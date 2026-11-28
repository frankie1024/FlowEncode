from collections import Counter
import subprocess

import numpy as np
import vapoursynth as vs

from repair_scan import block_findings, cambi_ffmpeg, field_findings, merge_ranges, native_candidate, occurrence_scope, sample_windows


def make_luma_clip(clip, format_id=vs.GRAYS, pixel_range="full"):
    """Convert range/bit depth while keeping the original pixel grid."""
    if clip.format.color_family == vs.RGB:
        return clip.resize.Point(format=format_id, matrix_s="709", range_s=pixel_range)
    return clip.std.ShufflePlanes(0, vs.GRAY).resize.Point(format=format_id, range_s=pixel_range)


def crop_samples(clip, luma_clip, sample_indices):
    edges_by_frame = {}
    for frame_idx in sample_indices:
        with luma_clip.get_frame(frame_idx) as frame:
            above_black = np.asarray(frame[0]) > (5.0 / 255.0)
        h, w = above_black.shape
        # Require coverage across the line so isolated bright pixels in the bars don't stop detection.
        rows = np.flatnonzero(np.count_nonzero(above_black, axis=1) >= w * 0.10)
        cols = np.flatnonzero(np.count_nonzero(above_black, axis=0) >= h * 0.10)
        if rows.size and cols.size:
            edges_by_frame[frame_idx] = (int(cols[0]), int(rows[0]), w - 1 - int(cols[-1]), h - 1 - int(rows[-1]))

    if not edges_by_frame:
        return (0, 0, 0, 0), (0, 0, 0, 0), edges_by_frame

    # Use the widest sampled active picture, then round down only where required by chroma subsampling.
    left, top, right, bottom = map(min, zip(*edges_by_frame.values()))
    if left + right >= clip.width - 16 or top + bottom >= clip.height - 16:
        return (0, 0, 0, 0), (0, 0, 0, 0), edges_by_frame
    step_x = 1 << clip.format.subsampling_w
    step_y = 1 << clip.format.subsampling_h
    exact = (left, top, right, bottom)
    aligned = tuple(value // step * step for value, step in zip(exact, (step_x, step_y, step_x, step_y)))
    return aligned, exact, edges_by_frame


def edge_args(edges):
    return dict(zip(("left", "top", "right", "bottom"), edges))


def edge_arguments(edges):
    return ", ".join(f"{name}={pixels}" for name, pixels in edge_args(edges).items())


def corroborate_crop(luma, bounds, frames):
    if not hasattr(getattr(vs.core, "acrop", None), "CropValues"):
        return []

    # A single stray pixel can stop CropValues. Use agreeing frames as evidence for the crop suggestion.
    limits = dict(zip(("left", "top", "right", "bottom"),
                      (max(side) + 2 for side in zip(*(bounds[index] for index in frames)))))
    try:
        detected = luma.acrop.CropValues(**limits, ref_color=[0.0], max_color_deviation=[5.0 / 255.0])
        corroborated = []
        for index in frames:
            with detected.get_frame(index) as frame:
                edges = tuple(int(frame.props[f"Crop{side.title()}Value"]) for side in limits)
            if edges == bounds[index]:
                corroborated.append(index)
        return corroborated
    except (vs.Error, KeyError):
        return []


def stable_scene_samples(luma, frames):
    if len(frames) < 4 or not hasattr(getattr(vs.core, "scxvid", None), "Scxvid"):
        return frames

    # Scxvid reads sequentially. Restart it in a short window around each sparse sample.
    try:
        small = luma.resize.Bilinear(width=320, height=180, format=vs.YUV420P8, matrix_s="709")
        stable = []
        for index in frames:
            start = max(0, index - 3)
            end = min(small.num_frames, index + 2)
            window = small[start:end].scxvid.Scxvid()
            for nearby in range(max(index, start + 1), end):
                with window.get_frame(nearby - start) as frame:
                    if frame.props.get("_SceneChangePrev", 0):
                        break
            else:
                stable.append(index)
        if len(stable) >= max(3, len(frames) // 2):
            return stable
    except vs.Error:
        pass
    return frames


def dirty_line_width(edge):
    # Ignore the corners and look for a 1–2 px line with a consistent signed luma error.
    margin = edge.shape[1] // 10
    edge = edge[:4, margin:edge.shape[1] - margin]
    if edge.shape[0] < 4 or edge.shape[1] < 16:
        return 0
    for width in (2, 1):
        interior = edge[width]
        delta = edge[:width] - interior
        baseline = edge[width + 1] - interior
        for sign in (-1, 1):
            bright_or_dark = np.min(delta * sign, axis=0) > 6.0 / 255.0
            nearby_agrees = np.abs(baseline) < 4.0 / 255.0
            if np.mean(bright_or_dark & nearby_agrees) >= 0.80:
                return width
    return 0


def persistent_edges(samples):
    return tuple(width if count >= len(samples) * 0.75 else 0 for width, count in
                 (Counter(side).most_common(1)[0] for side in zip(*samples)))


def staircase_count(luma):
    # Look for sharp, mostly monotone one-pixel steps along diagonal edges, in both orientations.
    size = 24
    height, width = luma.shape
    if height < size or width < size:
        return 0
    rows = np.linspace(0, height - size, min(height // size, 18), dtype=int)
    cols = np.linspace(0, width - size, min(width // size, 30), dtype=int)
    pixels = np.arange(size)
    tiles = luma[(rows[:, None] + pixels)[:, None, :, None], (cols[:, None] + pixels)[None, :, None, :]]
    count = 0
    for oriented in (tiles, tiles.swapaxes(-1, -2)):
        gradient = np.diff(oriented[..., 3:-3, :], axis=-1)
        strength = np.abs(gradient)
        peaks = np.argmax(strength, axis=-1)
        peak_strength = np.take_along_axis(gradient, peaks[..., None], axis=-1)[..., 0]
        adjacent = np.take_along_axis(strength, np.maximum(0, peaks - 1)[..., None], axis=-1)[..., 0]
        adjacent += np.take_along_axis(strength, np.minimum(size - 2, peaks + 1)[..., None], axis=-1)[..., 0]
        sign = np.sign(np.median(peak_strength, axis=-1))[..., None]
        sharp = (peak_strength * sign > 0.12) & (adjacent < np.abs(peak_strength) * 0.38)
        steps = np.diff(peaks, axis=-1) * np.sign(peaks[..., -1:] - peaks[..., :1])
        travel = np.abs(peaks[..., -1] - peaks[..., 0])
        valid = (travel >= 4) & (travel <= 13) & (np.count_nonzero(sharp, axis=-1) >= 15)
        valid &= np.count_nonzero((steps == 0) | (steps == 1), axis=-1) >= 16
        valid &= (np.max(steps, axis=-1) <= 2) & (np.count_nonzero(steps == 1, axis=-1) >= travel * 0.65)
        count += int(np.count_nonzero(valid))
    return count


def apply_statements(clip, statements):
    scope = {"_fe_repair_clip": clip}
    exec("\n".join(statements), scope)
    return scope["_fe_repair_clip"]


def verify_suggestion(kind, clip, statements, frames, edges=None, expected_size=None):
    result = {"kind": kind, "frames": frames, "statements": statements, "edges": edges or []}
    try:
        candidate = apply_statements(clip, statements)
        if (candidate.width, candidate.height) != (expected_size or (clip.width, clip.height)) or candidate.format != clip.format:
            raise RuntimeError("The filter changed the clip size or format unexpectedly.")
        if candidate.num_frames != clip.num_frames or candidate.fps != clip.fps:
            raise RuntimeError("The filter changed the frame count or rate.")
        for index in frames:
            with candidate.get_frame(index):
                pass
    except Exception as error:
        result["failure"] = str(error)
    return result


def spread_sample(frames):
    return list(dict.fromkeys([frames[0], frames[len(frames) // 2], frames[-1]]))


def verify_crop_repair(clip, aligned, exact, frames):
    statements = []
    if any(aligned):
        statements.append(f"_fe_repair_clip = _fe_repair_clip.std.Crop({edge_arguments(aligned)})")
    active_width = clip.width - exact[0] - exact[2]
    active_height = clip.height - exact[1] - exact[3]
    step_x = 1 << clip.format.subsampling_w
    step_y = 1 << clip.format.subsampling_h
    width = (active_width + step_x // 2) // step_x * step_x
    height = (active_height + step_y // 2) // step_y * step_y
    remainder = tuple(a - b for a, b in zip(exact, aligned))
    if any(remainder):
        left, top, _, _ = remainder
        statements.extend([
            f"_fe_repair_clip = _fe_repair_clip.fb.FillBorders({edge_arguments(remainder)}, mode='fillmargins')",
            f"_fe_repair_clip = _fe_repair_clip.resize.Spline36(width={width}, height={height}, "
            f"src_left={left}, src_top={top}, src_width={active_width}, src_height={active_height}, dither_type='error_diffusion')",
        ])

    suggestion = verify_suggestion("crop", clip, statements, frames, exact, expected_size=(width, height))
    suggestion["remainder"] = remainder
    suggestion["alphaStatements"] = [f"_fe_repair_alpha = _fe_repair_alpha.std.Crop({edge_arguments(exact)})"]
    if (width, height) != (active_width, active_height):
        suggestion["alphaStatements"].append(f"_fe_repair_alpha = _fe_repair_alpha.resize.Bilinear(width={width}, height={height})")
    return suggestion


def verify_edge_repair(clip, edges, frames, plane=0, kind="edge"):
    def luma_only(filter_call):
        if clip.format.color_family != vs.YUV:
            return [f"_fe_repair_clip = _fe_repair_clip{filter_call}"]
        sources = ["_fe_repair_clip"] * 3
        sources[plane] = "_fe_repair_plane"
        planes = list(range(3))
        planes[plane] = 0
        return ["import vapoursynth as _fe_vs",
                f"_fe_repair_plane = _fe_repair_clip.std.ShufflePlanes({plane}, _fe_vs.GRAY)",
                f"_fe_repair_plane = _fe_repair_plane{filter_call}",
                f"_fe_repair_clip = _fe_vs.core.std.ShufflePlanes([{', '.join(sources)}], {planes}, _fe_vs.YUV)"]

    args = edge_arguments(edges)
    if clip.format.color_family != vs.RGB and hasattr(getattr(vs.core, "edgefixer", None), "Continuity"):
        edgefixer = verify_suggestion(kind, clip, luma_only(f".edgefixer.Continuity({args}, radius=16)"), frames, edges)
        if not edgefixer.get("failure"):
            return edgefixer

    return verify_suggestion(kind, clip, luma_only(f".fb.FillBorders({args}, mode='fixborders')"), frames, edges)


def manual_comparisons(clip, frames):
    if not frames:
        return []
    suggestions = []
    frames = spread_sample(frames)
    if hasattr(getattr(vs.core, "cas", None), "CAS"):
        suggestions.append(verify_suggestion("cas", clip,
            ["_fe_repair_clip = _fe_repair_clip.cas.CAS(sharpness=0.3)"], frames))
    fmt = clip.format
    if (fmt.color_family in (vs.YUV, vs.GRAY) and fmt.sample_type == vs.INTEGER and clip.num_frames >= 5
            and hasattr(getattr(vs.core, "dedot", None), "Dedot")):
        suggestions.append(verify_suggestion("dedot", clip,
            ["_fe_repair_clip = _fe_repair_clip.dedot.Dedot()"], frames))
    return [dict(suggestion, method="manual") for suggestion in suggestions if not suggestion.get("failure")]


def subtitle_region(luma, frame_index, edges):
    # Detect small bright, text-like components in otherwise dark top/bottom margins.
    with luma.get_frame(frame_index) as frame:
        pixels = np.asarray(frame[0])
        for offset, margin in ((0, pixels[:edges[1]]), (pixels.shape[0] - edges[3], pixels[pixels.shape[0] - edges[3]:])):
            if margin.shape[0] < 3:
                continue
            side = margin.shape[1] // 10
            bright = margin[:, side:margin.shape[1] - side] > 0.32
            rows = np.count_nonzero(bright, axis=1)
            if np.count_nonzero((rows >= 3) & (rows < margin.shape[1] * 0.40)) >= 3:
                y, x = np.where(bright)
                return [int(x.min()) + side, int(y.min()) + offset, int(x.max()) + side + 1, int(y.max()) + offset + 1]
    return None


def attach_evidence(suggestion, ranges, method, region=None, value=None, total=None):
    suggestion.update(ranges=[{"start": a, "end": b} for a, b in ranges], evidence=method)
    if region:
        suggestion["region"] = region
    if value is not None:
        suggestion["value"] = value
    if total is not None:
        suggestion["total"] = total
    return suggestion


def detected(kind, frames, ranges, evidence, region=None, value=None):
    return attach_evidence({"kind": kind, "frames": frames, "statements": [], "edges": []}, ranges, evidence, region, value)


def optical_signals(luma, color):
    # Use the original grid. Subsample the number of edge profiles, not the pixels along each profile.
    strength = []
    shifts = []
    regions = []
    for axis in (0, 1):
        rows = (luma if axis == 0 else luma.T)[::4]
        if rows.shape[1] < 12:
            continue
        vector = rows[:, 6:-5] - rows[:, 5:-6]
        sign = np.sign(vector)
        left = (rows[:, 5:-6] - (rows[:, 2:-9] + rows[:, 1:-10]) * 0.5) * sign
        right = (rows[:, 6:-5] - (rows[:, 9:-2] + rows[:, 10:-1]) * 0.5) * sign
        near_left = (rows[:, 4:-7] - (rows[:, 2:-9] + rows[:, 1:-10]) * 0.5) * sign
        near_right = (rows[:, 7:-4] - (rows[:, 9:-2] + rows[:, 10:-1]) * 0.5) * sign
        flat = (np.abs(rows[:, 2:-9] - rows[:, 1:-10]) < 0.02) & (np.abs(rows[:, 10:-1] - rows[:, 9:-2]) < 0.02)
        halo = (np.abs(vector) > 0.23) & (left < -0.035) & (right > 0.035) & flat
        halo &= (near_left < -0.008) & (near_right > 0.008)
        yy, xx = np.where(halo)
        strength.append(len(xx))
        if len(xx):
            positions = (xx + 5, yy * 4) if axis == 0 else (yy * 4, xx + 5)
            regions.append([max(0, int(np.percentile(positions[0], 10)) - 3), max(0, int(np.percentile(positions[1], 10)) - 3),
                            min(luma.shape[1], int(np.percentile(positions[0], 90)) + 4),
                            min(luma.shape[0], int(np.percentile(positions[1], 90)) + 4)])
        if color is not None:
            # Measure a consistent luma/chroma lag across many high-contrast edges.
            cols = (color if axis == 0 else color.T)[::4]
            tests = [np.abs(cols[:, 6 + shift:cols.shape[1] - 5 + shift] - cols[:, 5 + shift:cols.shape[1] - 6 + shift])
                     for shift in range(-3, 4)]
            peaks = np.argmax(tests, axis=0) - 3
            good = (np.abs(vector) > 0.16) & (np.max(tests, axis=0) > 0.05)
            selected = peaks[good]
            if selected.size >= 75:
                lag, count = Counter(selected).most_common(1)[0]
                if abs(lag) >= 2 and count >= selected.size * 0.55:
                    shifts.append((axis, int(lag)))
    region = None if not regions else [min(r[0] for r in regions), min(r[1] for r in regions),
                                       max(r[2] for r in regions), max(r[3] for r in regions)]
    return sum(strength), region, shifts


def confirm_local_ranges(windows, hits, check, progress):
    confirmed = set(hits)
    for first, last, middle in windows:
        if middle not in hits:
            continue
        for index in range(max(first, middle - 5), min(last, middle + 5) + 1):
            if index not in confirmed and check(index):
                confirmed.add(index)
        progress()
    return merge_ranges((index, index) for index in confirmed)


def analyze_repairs(clip, sample_indices=None, *, chapters=(), pgs=False, ffmpeg=None, progress=lambda: None):
    luma_clip = make_luma_clip(clip)
    windows = []
    if sample_indices is None:
        sample_indices, windows = sample_windows(luma_clip, chapters, progress)
    aligned, exact, bounds = crop_samples(clip, luma_clip, sample_indices)
    variable_crop = any(max(side) - min(side) >= 8 for side in zip(*bounds.values()))
    processed = clip
    active_luma = luma_clip.std.Crop(**edge_args(exact)) if any(exact) else luma_clip
    suggestions = []
    crop_usable = True
    subtitle_frames = []
    if any(exact) and windows:
        if exact[1] or exact[3]:
            subtitle_samples = {}
            for index in sample_indices:
                region = subtitle_region(luma_clip, index, exact)
                if region:
                    subtitle_samples[index] = region
                progress()
            subtitle_frames = list(subtitle_samples)
        if subtitle_frames:
            ranges = confirm_local_ranges(windows, set(subtitle_frames), lambda index: subtitle_region(luma_clip, index, exact), progress)
            suggestion = detected("subtitle", spread_sample(subtitle_frames), ranges, "bright-margin", subtitle_samples[subtitle_frames[0]])
            suggestions.append(dict(suggestion, **occurrence_scope(sample_indices, subtitle_frames, clip.num_frames)))
    if any(exact):
        closest = sorted(bounds, key=lambda frame: sum(a - b for a, b in zip(bounds[frame], exact)))
        candidate_frames = sorted(closest[:max(3, len(closest) // 2)])
        candidate_frames = spread_sample(candidate_frames)
        confirmed = corroborate_crop(luma_clip, bounds, candidate_frames)
        suggestion = verify_crop_repair(clip, aligned, exact, confirmed or candidate_frames)
        if subtitle_frames:
            suggestion["statements"] = []
        if confirmed:
            suggestion["method"] = "autocrop"
        suggestions.append(attach_evidence(suggestion, [(0, clip.num_frames - 1)], "border-consensus", value=len(bounds)))
        suggestion.update(occurrence_scope(bounds, [index for index, edges in bounds.items() if edges == exact], clip.num_frames))
        crop_usable = bool(suggestion["statements"]) and not suggestion.get("failure")
        if crop_usable:
            processed = apply_statements(clip, suggestion["statements"])
    if variable_crop:
        widest = min(bounds, key=lambda index: sum(bounds[index]))
        narrowest = max(bounds, key=lambda index: sum(bounds[index]))
        alternates = list(dict.fromkeys([widest, narrowest]))
        suggestions.append(attach_evidence({"kind": "variable-crop", "frames": alternates, "statements": [], "edges": []},
                                           [(index, index) for index in alternates], "aspect-change",
                                           value=sum(bounds[narrowest]) - sum(bounds[widest])))

    scene_frames = set(stable_scene_samples(active_luma, list(bounds)))
    dirty_by_frame = {}
    alias_scores = []
    ring_scores = []
    chroma_shifts = []
    chroma_lines = []
    active_source = processed if crop_usable else clip
    chroma_clip = None
    if windows and active_source.format.color_family == vs.YUV:
        try:
            chroma_clip = active_source.resize.Bilinear(format=vs.YUV444PS)
        except vs.Error:
            pass
    for index in bounds:
        with active_luma.get_frame(index) as frame:
            pixels = np.asarray(frame[0])
            if not variable_crop and crop_usable:
                dirty_by_frame[index] = tuple(map(dirty_line_width, (pixels.T, pixels, pixels.T[::-1], pixels[::-1])))
            if index in scene_frames and not frame.props.get("_FieldBased", 0):
                score = staircase_count(pixels)
                if score >= 2:
                    alias_scores.append((score, index))
                if windows:
                    color = None
                    if chroma_clip is not None:
                        with chroma_clip.get_frame(index) as chroma:
                            planes = [np.asarray(chroma[plane]) for plane in (1, 2)]
                            dominant = max(planes, key=lambda plane: float(np.std(plane[::8, ::8])))
                            if dominant.shape == pixels.shape:
                                color = dominant.copy()
                        if not variable_crop and crop_usable:
                            with active_source.get_frame(index) as original:
                                scale = 1 if active_source.format.sample_type == vs.FLOAT else (1 << active_source.format.bits_per_sample) - 1
                                planes = [np.asarray(original[plane], dtype=np.float32) / scale for plane in (1, 2)]
                                chroma_lines.append((index, [tuple(map(dirty_line_width, (plane.T, plane, plane.T[::-1], plane[::-1])))
                                                             for plane in planes]))
                    ringing, region, shifts = optical_signals(pixels, color)
                    if ringing >= max(40, pixels.size // 20000):
                        if any(exact):
                            region = [region[0] + exact[0], region[1] + exact[1], region[2] + exact[0], region[3] + exact[1]]
                        ring_scores.append((index, ringing, region))
                    chroma_shifts.extend((index, axis, lag) for axis, lag in shifts)
        progress()

    if len(dirty_by_frame) >= 3:
        dirty_edges = persistent_edges(dirty_by_frame.values())
        if any(dirty_edges):
            frames = [index for index, sides in dirty_by_frame.items()
                      if all(width == 0 or sides[i] == width for i, width in enumerate(dirty_edges))]
            if frames:
                suggestion = attach_evidence(verify_edge_repair(processed, dirty_edges, spread_sample(frames)),
                                             [(0, clip.num_frames - 1)], "edge-consensus", value=len(frames), total=len(dirty_by_frame))
                suggestion.update(occurrence_scope(dirty_by_frame, frames, clip.num_frames))
                if windows and suggestion["scope"] != "widespread":
                    suggestion["statements"] = []
                    suggestion["ranges"] = [{"start": a, "end": b} for a, b in merge_ranges((n, n) for n in frames)]
                suggestions.append(suggestion)
        elif windows:
            groups = Counter(edges for edges in dirty_by_frame.values() if any(edges))
            for edges, count in groups.most_common(2):
                if count < 2:
                    continue
                hits = {index for index, found in dirty_by_frame.items() if found == edges}

                def confirm_edges(index):
                    with active_luma.get_frame(index) as frame:
                        pixels = np.asarray(frame[0])
                        return tuple(map(dirty_line_width, (pixels.T, pixels, pixels.T[::-1], pixels[::-1]))) == edges

                ranges = confirm_local_ranges(windows, hits, confirm_edges, progress)
                frames = spread_sample(sorted(hits))
                suggestion = verify_edge_repair(processed, edges, frames, kind="local-edge")
                attach_evidence(suggestion, ranges, "edge-consensus", value=count, total=len(dirty_by_frame))
                suggestions.append(dict(suggestion, **occurrence_scope(dirty_by_frame, hits, clip.num_frames)))

    if ring_scores:
        ranked = sorted(ring_scores, key=lambda row: row[1], reverse=True)
        hits = {index for index, _, _ in ring_scores}

        def confirm_ringing(index):
            with active_luma.get_frame(index) as frame:
                pixels = np.asarray(frame[0])
                count, _, _ = optical_signals(pixels, None)
                return count >= max(40, pixels.size // 20000)

        ranges = confirm_local_ranges(windows, hits, confirm_ringing, progress)
        suggestion = detected("ring", [index for index, _, _ in ranked[:3]], ranges,
                              "ring", region=ranked[0][2], value=ranked[0][1])
        suggestions.append(dict(suggestion, **occurrence_scope(scene_frames, hits, clip.num_frames)))
    if chroma_shifts:
        result, votes = Counter((axis, lag) for _, axis, lag in chroma_shifts).most_common(1)[0]
        if votes >= 2:
            hits = sorted({index for index, axis, lag in chroma_shifts if (axis, lag) == result})
            suggestion = detected("chroma", spread_sample(hits), merge_ranges((n, n) for n in hits), "chroma", value=abs(result[1]))
            suggestions.append(dict(suggestion, **occurrence_scope(scene_frames, hits, clip.num_frames)))
    if len(chroma_lines) >= 3:
        x, y = 1 << active_source.format.subsampling_w, 1 << active_source.format.subsampling_h
        for plane in (1, 2):
            plane_edges = [(index, edges[plane - 1]) for index, edges in chroma_lines]
            chroma_edges = persistent_edges([edges for _, edges in plane_edges])
            if not any(chroma_edges):
                continue
            hits = [index for index, edges in plane_edges if all(not width or width == edges[i] for i, width in enumerate(chroma_edges))]
            if not hits:
                continue
            scope = occurrence_scope([index for index, _ in plane_edges], hits, clip.num_frames)
            frames = spread_sample(hits)
            if scope["scope"] == "widespread":
                suggestion = verify_edge_repair(active_source, chroma_edges, frames, plane, "chroma-edge")
                attach_evidence(suggestion, [(0, clip.num_frames - 1)], "chroma-edge" + str(plane), value=len(hits))
            else:
                suggestion = detected("chroma-edge", frames, merge_ranges((n, n) for n in hits), "chroma-edge" + str(plane), value=len(hits))
            suggestion["edges"] = [width * step for width, step in zip(chroma_edges, (x, y, x, y))]
            suggestions.append(dict(suggestion, **scope))

    if alias_scores:
        ranked = sorted(alias_scores, reverse=True)
        frames = [index for _, index in ranked[:3]]
        fix = ["from vsaa import based_aa as _fe_based_aa, NNEDI3 as _fe_NNEDI3",
               "_fe_repair_clip = _fe_based_aa(_fe_repair_clip, rfactor=2.0, supersampler=_fe_NNEDI3(), antialiaser=_fe_NNEDI3(), postfilter=False)"]
        anchors = {index for _, index in alias_scores}

        def confirm_aliasing(index):
            with active_luma.get_frame(index) as frame:
                return not frame.props.get("_FieldBased", 0) and staircase_count(np.asarray(frame[0])) >= 2

        local_ranges = confirm_local_ranges(windows, anchors, confirm_aliasing, progress)
        suggestion = attach_evidence(verify_suggestion("aa", clip, fix, frames), local_ranges,
                                     "staircase", value=max(score for score, _ in alias_scores))
        suggestions.append(dict(suggestion, **occurrence_scope(scene_frames, [index for _, index in alias_scores], clip.num_frames)))

    field_failure = None if ffmpeg or not windows else "FFmpeg is unavailable."
    try:
        fields = field_findings(ffmpeg, clip, luma_clip, windows, progress)
        field_centers = [middle for start, end, middle in windows if end - start >= 12]
        for kind, method in sorted({(row[0], row[6]) for row in fields}):
            matching = [row for row in fields if row[0] == kind and row[6] == method]
            ranges = merge_ranges((start, end) for _, start, end, _, _, _, _ in matching)
            hits = [row[3] for row in matching]
            preview_frames = spread_sample(hits)
            statements = []
            if kind == "interlace" and clip.format.color_family != vs.RGB:
                # Bwdif field 0/1 produces one output frame per source frame with bottom/top field first.
                statements = [f"_fe_repair_clip = _fe_repair_clip.bwdif.Bwdif(field={int(method == 'idet-tff')})"]
            finding = verify_suggestion(kind, clip, statements, preview_frames) if statements else {
                "kind": kind, "frames": preview_frames, "statements": [], "edges": []}
            finding = attach_evidence(finding, ranges, method, value=sum(row[4] for row in matching), total=sum(row[5] for row in matching))
            suggestions.append(dict(finding, **occurrence_scope(field_centers, hits, clip.num_frames)))
    except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
        field_failure = str(error)
    if windows:
        try:
            blocks = block_findings(ffmpeg, active_luma, sorted(scene_frames), windows, progress)
            if blocks:
                hits = [index for index, _ in blocks]
                ranked = sorted(blocks, key=lambda row: row[1], reverse=True)
                suggestion = detected("block", [index for index, _ in ranked[:3]], merge_ranges((n, n) for n in hits),
                                      "block", value=ranked[0][1])
                suggestions.append(dict(suggestion, **occurrence_scope(scene_frames, hits, clip.num_frames)))
        except (OSError, RuntimeError, subprocess.TimeoutExpired):
            pass
        try:
            native = native_candidate(active_source, sorted(scene_frames))
            if native:
                hits = [index for index, _ in native]
                suggestions.append(detected("native", hits, [(n, n) for n in hits], "native", value=native[0][1]))
        except (vs.Error, ValueError, RuntimeError, OSError, TypeError, AttributeError):
            pass

    result = {"type": "repairAnalysis", "sampledCount": len(sample_indices), "variableCrop": variable_crop,
              "chapterCount": len(chapters), "windowCount": len(windows), "hasPgs": pgs and any(exact),
              "fieldScanFailure": field_failure, "suggestions": suggestions}
    frames = sorted(scene_frames)
    result["suggestions"].extend(manual_comparisons(clip, frames))
    return result, (clip, processed, frames, ffmpeg, windows, (exact[0], exact[1]) if crop_usable else (0, 0))


def analyze_banding(clip, active_clip, frames, ffmpeg=None, windows=(), offset=(0, 0), *, progress=lambda: None):
    result = {"type": "repairBanding", "suggestions": []}
    if not frames:
        return result

    try:
        with active_clip.get_frame(frames[0]) as frame:
            transfer = frame.props.get("_Transfer", 2)
        pq = transfer == 16
        if transfer == 18 or (pq and not ffmpeg):
            raise RuntimeError("This CAMBI backend cannot analyze the source transfer function.")
        if min(active_clip.width, active_clip.height) < 216:
            raise RuntimeError("CAMBI needs at least 216 pixels in each dimension.")

        # CAMBI uses 10-bit luma code values. Keep the current resolution for the measurement.
        luma = make_luma_clip(active_clip, vs.GRAY10, "limited")
        cambi = None if ffmpeg else luma.akarin.Cambi()
        scores = {}

        def measure(indices):
            if ffmpeg:
                for start in range(0, len(indices), 6):
                    group = indices[start:start + 6]
                    try:
                        measured, _ = cambi_ffmpeg(ffmpeg, luma, group, pq)
                    except (OSError, RuntimeError, ValueError, KeyError, subprocess.TimeoutExpired):
                        if pq:
                            raise
                        fallback = luma.akarin.Cambi()
                        measured = {}
                        for index in group:
                            with fallback.get_frame(index) as frame:
                                measured[index] = float(frame.props["CAMBI"])
                    scores.update(measured)
                    progress()
            else:
                for index in indices:
                    with cambi.get_frame(index) as frame:
                        scores[index] = float(frame.props["CAMBI"])
                    progress()

        measure(frames)
        seed_frames = [index for index, score in scores.items() if score >= 5.0]
        refinements = {nearby for index in seed_frames for a, b, mid in windows if mid == index
                       for nearby in range(max(a, index - 5), min(b, index + 5) + 1)}
        measure(sorted(refinements - scores.keys()))
        banding_scores = [(score, index) for index, score in scores.items() if score >= 5.0]
        if banding_scores:
            worst = [index for _, index in sorted(banding_scores, reverse=True)[:3]]
            fix = ["from vsdeband import f3k_deband as _fe_f3k_deband",
                   "_fe_repair_clip = _fe_f3k_deband(_fe_repair_clip, radius=16, thr=32, grain=0)"]
            region = None
            if ffmpeg:
                try:
                    _, region = cambi_ffmpeg(ffmpeg, luma, worst[:1], pq, heatmaps=True)
                except (OSError, RuntimeError, ValueError, KeyError, subprocess.TimeoutExpired):
                    pass
            if region:
                x, y = offset
                region = [region[0] + x, region[1] + y, region[2] + x, region[3] + y]
            ranges = merge_ranges((index, index) for _, index in banding_scores)
            suggestion = attach_evidence(verify_suggestion("deband", clip, fix, worst), ranges,
                                         "cambi-pq" if pq else "cambi-sdr", region, value=max(banding_scores)[0])
            result["suggestions"].append(dict(suggestion, **occurrence_scope(frames, seed_frames, clip.num_frames)))
    except Exception as error:
        result["bandingScanFailure"] = str(error)
    return result
