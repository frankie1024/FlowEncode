import json
import re
import struct
import subprocess
import tempfile
from collections import Counter
from pathlib import Path

import numpy as np
import vapoursynth as vs


def run_media_tool(path, arguments, *, data=None, cwd=None, timeout=30):
    process = subprocess.run([path, *arguments], input=data, capture_output=True, cwd=cwd, timeout=timeout,
                             creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if process.returncode:
        raise RuntimeError(process.stderr.decode("utf-8", "replace").strip() or f"Media tool exited with {process.returncode}.")
    return process.stdout


def read_chapters(ffprobe, source, clip):
    if not ffprobe or not source or not clip.fps:
        return [], False
    try:
        data = run_media_tool(ffprobe, ["-v", "error", "-show_chapters", "-show_streams", "-show_format",
                                        "-show_entries", "chapter=start_time:stream=codec_name,codec_type:format=duration",
                                        "-of", "json", "-i", source], timeout=12)
        metadata = json.loads(data)
        has_pgs = any(stream.get("codec_name") == "hdmv_pgs_subtitle" for stream in metadata.get("streams", []))
        seconds = clip.num_frames / float(clip.fps)
        if abs(float(metadata.get("format", {}).get("duration", -1)) - seconds) > max(2, seconds * 0.01):
            return [], has_pgs
        chapters = [round(float(item["start_time"]) * float(clip.fps)) for item in metadata.get("chapters", [])]
        return sorted({frame for frame in chapters if 0 < frame < clip.num_frames})[:1000], has_pgs
    except (OSError, ValueError, KeyError, RuntimeError, subprocess.TimeoutExpired):
        return [], False


def read_playlist_seams(path, clip):
    if not path or not clip.fps:
        return []
    try:
        with open(path, "rb") as file:
            data = file.read(1024 * 1024 + 1)
        if not data.startswith(b"MPLS") or len(data) > 1024 * 1024:
            return []
        section = struct.unpack_from(">I", data, 8)[0]
        size, _, count = struct.unpack_from(">IHH", data, section)
        section_end = section + 4 + size
        if section_end > len(data) or count < 2:
            return []
        position = section + 10
        elapsed = 0
        seams = []
        for _ in range(count):
            length = struct.unpack_from(">H", data, position)[0]
            position += 2
            if length < 20 or position + length > section_end or data[position + 5:position + 9] != b"M2TS":
                return []
            first, last = struct.unpack_from(">II", data, position + 12)
            if last <= first:
                return []
            if elapsed:
                seams.append(round(elapsed * clip.fps / 45000))
            elapsed += last - first
            position += length
        if abs(round(elapsed * clip.fps / 45000) - clip.num_frames) > max(2, round(float(clip.fps) * 0.5)):
            return []
        return sorted({frame for frame in seams if 0 < frame < clip.num_frames})
    except (OSError, struct.error):
        return []


def sample_windows(luma, chapters, progress=lambda: None):
    """Read short, evenly spaced windows and chapter seams, then select frames away from cuts."""
    total = luma.num_frames
    chapter_frames = set(chapters)
    fps = float(luma.fps) or 24.0
    count = min(80, total, max(4, int(np.ceil(total / fps / 90))))
    anchors = {int((2 * index + 1) * total / (2 * count)) for index in range(count)}
    boundaries = [0, *chapters, total]
    for start, end in zip(boundaries, boundaries[1:]):
        anchors.add((start + end) // 2)
    for chapter in chapters:
        anchors.add(chapter)
    radius = max(4, min(16, round(fps * 0.45)))
    intervals = merge_ranges((max(0, index - radius), min(total - 1, index + radius)) for index in anchors)
    small = luma.resize.Bilinear(width=256, height=144, format=vs.GRAY8, range_s="full")
    windows = []
    for start, end in intervals:
        cuts = [start]
        previous = None
        for index in range(start, end + 1):
            with small.get_frame(index) as frame:
                plane = np.array(frame[0], dtype=np.int16)
            if previous is not None and np.mean(np.abs(plane - previous)) >= 25:
                cuts.append(index)
            if start < index and index in chapter_frames and cuts[-1] != index:
                cuts.append(index)
            previous = plane
        for first, next_start in zip(cuts, [*cuts[1:], end + 1]):
            windows.append((first, next_start - 1, (first + next_start - 1) // 2))
        progress()
    frames = {middle for _, _, middle in windows}
    # Check both sides of chapter transitions even when the visual cut is subtle.
    frames.update(frame for chapter in chapters for frame in (chapter - 1, chapter))
    return sorted(frames), windows


def merge_ranges(ranges):
    merged = []
    for start, end in sorted(ranges):
        if merged and start <= merged[-1][1] + 1:
            merged[-1] = (merged[-1][0], max(merged[-1][1], end))
        else:
            merged.append((start, end))
    return merged


def occurrence_scope(frames, hits, total_frames):
    frames = set(frames)
    hits = frames.intersection(hits)
    if len(frames) < 6 or not hits:
        scope = "uncertain"
    else:
        section = lambda frame: min(3, frame * 4 // total_frames)
        sampled_sections = {section(frame) for frame in frames}
        hit_sections = {section(frame) for frame in hits}
        widespread = len(sampled_sections) >= 3 and hit_sections == sampled_sections and len(hits) >= len(frames) * 0.60
        concentrated = (len(hits) == 1 or (len(hit_sections) < len(sampled_sections) and len(hit_sections) <= 2))
        scope = "widespread" if widespread else "local" if concentrated else "uncertain"
    return {"scope": scope, "scopeHits": len(hits), "scopeSamples": len(frames)}


def read_gray(clip, indices):
    for index in indices:
        with clip.get_frame(index) as frame:
            yield np.asarray(frame[0]).tobytes()


def cambi_ffmpeg(ffmpeg, luma, indices, pq, *, heatmaps=False):
    feature = "name=cambi"
    if pq:
        feature += r"\:eotf=pq"
    if heatmaps:
        feature += r"\:heatmaps_path=."
    with tempfile.TemporaryDirectory(prefix="flowencode-cambi-") as path:
        graph = f"[0:v]split[a][b];[a][b]libvmaf=model='':feature='{feature}':n_threads=2:log_fmt=json:log_path=metrics.json"
        run_media_tool(ffmpeg, ["-v", "error", "-f", "rawvideo", "-pixel_format", "gray10le", "-video_size",
                                f"{luma.width}x{luma.height}", "-i", "pipe:0", "-filter_complex", graph,
                                "-an", "-f", "null", "-"], data=b"".join(read_gray(luma, indices)), cwd=path)
        metrics = json.loads((Path(path) / "metrics.json").read_text())
        scores = {}
        for index, row in zip(indices, metrics["frames"], strict=True):
            scores[index] = float(row["metrics"]["cambi_eotf_pq" if pq else "cambi"])
        region = None
        if heatmaps:
            for map_file in Path(path).glob("cambi_heatmap_scale_1_*_16b.gray"):
                dimensions = re.search(r"_(\d+)x(\d+)_16b", map_file.name)
                if not dimensions:
                    continue
                width, height = map(int, dimensions.groups())
                if map_file.stat().st_size != width * height * 2:
                    continue
                image = np.fromfile(map_file, dtype="<u2").reshape(height, width)
                if image.max() > 0:
                    yy, xx = np.where(image >= float(image.max()) * 0.65)
                    region = [int(xx.min()) * luma.width // width, int(yy.min()) * luma.height // height,
                              int(xx.max() + 1) * luma.width // width, int(yy.max() + 1) * luma.height // height]
                break
        return scores, region


def field_findings(ffmpeg, clip, luma, windows, progress=lambda: None):
    if not ffmpeg or clip.num_frames < 12:
        return []
    fps = float(clip.fps) or 24.0
    rows = []
    # Keep every source row so that resampling does not erase combing. Test continuous moving windows.
    reduced = luma.resize.Bilinear(width=min(640, luma.width), format=vs.GRAY8, range_s="full")
    for first, last, middle in windows:
        start, end = max(first, middle - 10), min(last, middle + 10)
        if end - start < 12:
            continue
        raw = b"".join(read_gray(reduced, range(start, end + 1)))
        pixels = np.frombuffer(raw, dtype=np.uint8).reshape(-1, reduced.height, reduced.width)
        small = pixels[:, ::8, ::8].astype(np.int16)
        movement = float(np.median(np.abs(np.diff(small, axis=0)).mean(axis=(1, 2))))
        if movement < 1.0:
            continue
        result = run_media_tool(ffmpeg, ["-v", "error", "-f", "rawvideo", "-pixel_format", "gray", "-video_size",
                                         f"{reduced.width}x{reduced.height}", "-i", "pipe:0", "-vf",
                                         "idet,metadata=mode=print:file=-", "-an", "-f", "null", "-"], data=raw).decode("utf-8", "replace")
        # Single-frame evidence is counted over consecutive frames; ignore the edges of each window.
        kinds = re.findall(r"lavfi.idet.single.current_frame=(\w+)", result)[2:-2]
        repeated = re.findall(r"lavfi.idet.repeated.current_frame=(\w+)", result)[2:-2]
        if not kinds:
            continue
        counts = Counter(kinds)
        positions = [index for index, field in enumerate(repeated) if field in ("top", "bottom")]
        cadence = Counter(index % 5 for index in positions)
        periodic = sum(count for _, count in cadence.most_common(2)) >= 4 and len(cadence) <= 2
        if 28 <= fps <= 31 and periodic and 0 < counts["progressive"] < len(kinds):
            order = "tff" if counts["tff"] >= counts["bff"] else "bff"
            method = "cadence"
            if hasattr(getattr(vs.core, "vivtc", None), "VFM"):
                try:
                    matched = clip[start:end + 1].vivtc.VFM(order=int(order == "tff"))
                    combed = []
                    for index in range(2, len(kinds) + 2):
                        with matched.get_frame(index) as frame:
                            combed.append(bool(frame.props["_Combed"]))
                    if sum(combed) < len(combed) * 0.25:
                        method = "cadence-vivtc"
                except (vs.Error, KeyError):
                    pass
            rows.append(("telecine", start + 2, end - 2, middle, len(positions), len(kinds), method))
        else:
            order = max(("tff", "bff"), key=counts.get) if counts["tff"] and counts["bff"] else ("tff" if counts["tff"] else "bff")
            if counts[order] >= len(kinds) * 0.80 and not positions:
                rows.append(("interlace", start + 2, end - 2, middle, counts[order], len(kinds), "idet-" + order))
        progress()
    return rows


def measure_blocks(ffmpeg, gray, frames, progress):
    measurements = []
    for start in range(0, len(frames), 6):
        indices = frames[start:start + 6]
        raw = b"".join(read_gray(gray, indices))
        result = run_media_tool(ffmpeg, ["-v", "error", "-f", "rawvideo", "-pixel_format", "gray", "-video_size",
                                         f"{gray.width}x{gray.height}", "-i", "pipe:0", "-vf",
                                         "blockdetect,metadata=mode=print:file=-", "-an", "-f", "null", "-"], data=raw)
        scores = re.findall(rb"lavfi.block=([0-9.]+)", result)
        if len(scores) != len(indices):
            raise RuntimeError("FFmpeg did not report a block score for every frame.")
        for index, score in enumerate(scores):
            pixels = np.frombuffer(raw, dtype=np.uint8, count=gray.width * gray.height,
                                   offset=index * gray.width * gray.height).reshape(gray.height, gray.width)
            measurements.append((float(score), grid_block_ratio(pixels)))
        progress()
    return measurements


def block_strength(value, grid, baseline):
    relative = value / baseline if value - baseline >= 5 else 0
    return max(relative, grid) if (relative >= 1.9 and grid >= 1.35) or (grid >= 2.0 and value >= 10) else 0


def block_findings(ffmpeg, luma, frames, windows, progress):
    if not ffmpeg or len(frames) < 4:
        return []
    gray = luma.resize.Point(format=vs.GRAY8, range_s="full")
    measurements = measure_blocks(ffmpeg, gray, frames, progress)
    # Compare each sample to its neighbors, avoiding a global threshold across different productions.
    findings = {}
    bounds = {middle: (start, end) for start, end, middle in windows}
    for position, (value, grid) in enumerate(measurements):
        nearby = measurements[max(0, position - 3):position] + measurements[position + 1:position + 4]
        baseline = max(1.0, float(np.median([item[0] for item in nearby])))
        strength = block_strength(value, grid, baseline)
        if strength:
            index = frames[position]
            findings[index] = max(strength, findings.get(index, 0))
            start, end = bounds.get(index, (index, index))
            neighbors = list(range(max(start, index - 4), min(end, index + 4) + 1))
            for frame, (value, grid) in zip(neighbors, measure_blocks(ffmpeg, gray, neighbors, progress), strict=True):
                hit = block_strength(value, grid, baseline)
                if hit:
                    findings[frame] = max(hit, findings.get(frame, 0))
    return list(findings.items())


def grid_block_ratio(pixels):
    ratios = []
    for plane in (pixels[::3], pixels.T[::3]):
        changes = np.abs(np.diff(plane.astype(np.int16), axis=1))
        for grid in (8, 16):
            if changes.shape[1] < grid * 5:
                continue
            phases = [float(np.mean(changes[:, phase::grid])) for phase in range(grid)]
            peak = max(phases)
            baseline = float(np.median(phases))
            if peak - baseline >= 3:
                ratios.append(peak / max(1.5, baseline))
    return max(ratios, default=0)


def native_candidate(clip, frames):
    if clip.height < 700 or len(frames) < 3:
        return None
    try:
        from nativeres import getnative
        from vskernels import Bilinear
    except ImportError:
        return None
    heights = [height for height in (480, 540, 720, 900, 1080, 1440, 1800) if 0.45 * clip.height <= height <= 0.9 * clip.height]
    if not heights:
        return None
    tested = sorted({height + delta for height in heights for delta in (-12, 0, 12)})
    dimensions = [(clip.width * height / clip.height, height) for height in tested]
    hits = []
    for index in (frames[len(frames) // 4], frames[len(frames) // 2], frames[len(frames) * 3 // 4]):
        results = {int(row.dim.height): row.error for row in getnative(clip, frame_num=index, dimensions=dimensions, kernel=Bilinear())}
        minima = [height for height in heights if all(height + delta in results for delta in (-12, 0, 12))
                  and results[height] > 0 and results[height] < 0.85 * min(results[height - 12], results[height + 12])]
        if minima:
            hits.append((index, min(minima, key=results.get)))
    if len(hits) >= 2 and len({height for _, height in hits}) == 1:
        return hits
    return None


def deep_scan(clip, luma, chapters, start, end, progress=lambda: None, playlist_seams=()):
    small = luma.resize.Bilinear(width=256, height=144, format=vs.GRAY8, range_s="full")
    checks = set(chapters) | set(playlist_seams)
    seams = set(playlist_seams)
    results = []
    recent = []
    frozen_start = None
    moved_into_freeze = False
    black_start = None
    black_after_content = False
    decode_start = None
    freeze_length = max(3, round((float(clip.fps) or 24) * 0.20))
    for index in range(start, end + 1):
        try:
            with small.get_frame(index) as frame:
                image = np.array(frame[0], dtype=np.int16)
                decoder_flag = frame.props.get("_DecodeError", 0) or frame.props.get("_Corrupt", 0)
                if decoder_flag:
                    raise vs.Error("Source marked the frame as corrupt")
        except vs.Error:
            if frozen_start is not None and index - frozen_start >= freeze_length:
                results.append(("freeze", frozen_start, index - 1, frozen_start + 1, 0.0))
            decode_start = index if decode_start is None else decode_start
            recent.clear()
            frozen_start = None
            black_start = None
            if index % 48 == 0:
                progress()
            continue
        if decode_start is not None:
            results.append(("decode", decode_start, index - 1, decode_start, 0.0))
            decode_start = None
        black = np.count_nonzero(image >= 12) < image.size * 0.02
        if black:
            if black_start is None:
                black_start = index
                black_after_content = bool(recent)
        elif black_start is not None:
            if black_after_content and index - black_start <= freeze_length:
                results.append(("black-run", black_start, index - 1, black_start, 0.0))
            black_start = None
        if recent:
            previous = recent[-1][1]
            difference = float(np.abs(image - previous).mean())
            if difference < 0.03 and image.max() >= 16:
                if frozen_start is None:
                    frozen_start = recent[-1][0]
                    moved_into_freeze = len(recent) == 2 and float(np.abs(previous - recent[0][1]).mean()) > 4
            else:
                if frozen_start is not None and index - frozen_start >= freeze_length:
                    results.append(("freeze", frozen_start, index - 1, frozen_start + 1, difference))
                elif frozen_start is not None and index - frozen_start >= 2 and difference > 4 and moved_into_freeze:
                    results.append(("duplicate", frozen_start, index - 1, frozen_start + 1, difference))
                frozen_start = None
            if len(recent) == 2:
                first_index, first = recent[0]
                continuity = float(np.abs(image - first).mean())
                isolated = min(float(np.abs(previous - first).mean()), difference)
                if continuity < 5 and isolated > max(13, continuity * 3):
                    if np.count_nonzero(previous >= 12) >= previous.size * 0.02:
                        results.append(("flash", first_index + 1, first_index + 1, first_index + 1, isolated))
            if index in checks and difference > 28:
                results.append(("playlist-seam" if index in seams else "seam", index - 1, index, index, difference))
        recent = [*recent[-1:], (index, image)]
        if index % 48 == 0:
            progress()
    if frozen_start is not None and end - frozen_start + 1 >= freeze_length:
        results.append(("freeze", frozen_start, end, frozen_start + 1, 0.0))
    if decode_start is not None:
        results.append(("decode", decode_start, end, decode_start, 0.0))
    return results
