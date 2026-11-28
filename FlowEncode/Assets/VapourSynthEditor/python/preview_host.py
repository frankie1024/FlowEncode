import json
import mmap
import math
import importlib.util
import os
import sys
import struct
import time
import traceback
import zlib
from collections import OrderedDict
from contextlib import contextmanager

import numpy as np
import vapoursynth as vs

from repair_analysis import analyze_banding, analyze_repairs, attach_evidence, crop_samples, make_luma_clip, verify_crop_repair
from repair_scan import deep_scan, read_chapters, read_playlist_seams

try:
    sys.stderr.reconfigure(encoding="utf-8", errors="backslashreplace")
except Exception:
    pass

# Duplicate OS fd 1 for the JSON line protocol and redirect fd 1 -> fd 2 so native C/C++
# VapourSynth plugins printing to stdout never corrupt the host protocol stream.
try:
    _saved_stdout_fd = os.dup(1)
    os.dup2(2, 1)
    PROTOCOL_STDOUT = os.fdopen(_saved_stdout_fd, "w", encoding="utf-8", errors="backslashreplace")
    sys.stdout = sys.stderr
except Exception:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
    except Exception:
        pass
    PROTOCOL_STDOUT = sys.stdout


def emit(payload):
    PROTOCOL_STDOUT.write(json.dumps(payload, ensure_ascii=False) + "\n")
    PROTOCOL_STDOUT.flush()


def repair_progress(request_id):
    last_report = time.monotonic()

    def report():
        nonlocal last_report
        now = time.monotonic()
        if now - last_report >= 2:
            emit({"type": "repairProgress", "requestId": request_id})
            last_report = now

    return report


@contextmanager
def redirect_script_stdout():
    previous_stdout = sys.stdout

    try:
        sys.stdout = sys.stderr
        yield
    finally:
        sys.stdout = previous_stdout


def format_prop_value(value):
    if isinstance(value, bytes):
        try:
            return value.decode("utf-8", errors="replace")
        except Exception:
            return repr(value)

    if isinstance(value, (list, tuple)):
        return ", ".join(format_prop_value(item) for item in value)

    return str(value)


def matrix_prop_to_string(value):
    return {
        0: "rgb",
        1: "709",
        4: "fcc",
        5: "470bg",
        6: "170m",
        7: "240m",
        8: "ycgco",
        9: "2020ncl",
        10: "2020cl",
        12: "chromancl",
        13: "chromacl",
        14: "ictcp",
    }.get(int(value))


def range_prop_to_string(value):
    return {
        0: "limited",
        1: "full",
    }.get(int(value))


def transfer_prop_to_hdr_info(value):
    try:
        code = int(value)
    except Exception:
        return False, None
    if code == 16:
        return True, "PQ"
    if code == 18:
        return True, "HLG"
    return False, None


def transfer_prop_to_string(value):
    try:
        code = int(value)
    except Exception:
        return None
    return {
        1: "709",
        6: "601",
        8: "linear",
        13: "srgb",
        14: "2020_10",
        15: "2020_12",
        16: "st2084",
        18: "std-b67",
    }.get(code)


def primaries_prop_to_string(value):
    try:
        code = int(value)
    except Exception:
        return None
    return {
        1: "709",
        5: "470bg",
        6: "170m",
        9: "2020",
        12: "display-p3",
    }.get(code)


def write_frame_to_shared_memory(shared_memory_name, shared_memory_capacity, bgra):
    if not shared_memory_name:
        raise RuntimeError("Shared memory transport name is missing.")

    pixel_bytes = int(bgra.nbytes)
    shared_memory_capacity = int(shared_memory_capacity or 0)
    if shared_memory_capacity < pixel_bytes:
        raise RuntimeError(
            f"Shared memory transport capacity is too small. required={pixel_bytes} capacity={shared_memory_capacity}"
        )

    flat_pixels = np.ascontiguousarray(bgra, dtype=np.uint8).reshape(-1)
    with mmap.mmap(-1, shared_memory_capacity, tagname=shared_memory_name, access=mmap.ACCESS_WRITE) as shared_buffer:
        shared_buffer.seek(0)
        shared_buffer.write(memoryview(flat_pixels))
        shared_buffer.flush()

    return pixel_bytes


def _compute_luma_feature(gray_2d):
    arr = np.asarray(gray_2d, dtype=np.float32)
    mean = float(arr.mean())
    std = float(arr.std())
    norm_luma = (arr - mean) / (std + 1e-5)

    gx = np.zeros_like(arr)
    gy = np.zeros_like(arr)
    gx[:, 1:-1] = arr[:, 2:] - arr[:, :-2]
    gy[1:-1, :] = arr[2:, :] - arr[:-2, :]
    grad = np.hypot(gx, gy)
    g_mean = float(grad.mean())
    g_std = float(grad.std())
    norm_grad = (grad - g_mean) / (g_std + 1e-5)

    feat = np.concatenate((norm_luma.reshape(-1), norm_grad.reshape(-1)))
    norm = float(np.linalg.norm(feat))
    if norm > 1e-6:
        feat /= norm
    return feat


def _crop_active_region(luma_a, luma_b, max_dim=1024):
    h = min(luma_a.shape[0], luma_b.shape[0])
    w = min(luma_a.shape[1], luma_b.shape[1])
    ya = (luma_a.shape[0] - h) // 2
    xa = (luma_a.shape[1] - w) // 2
    yb = (luma_b.shape[0] - h) // 2
    xb = (luma_b.shape[1] - w) // 2
    a = luma_a[ya:ya + h, xa:xa + w].astype(np.float32, copy=False)
    b = luma_b[yb:yb + h, xb:xb + w].astype(np.float32, copy=False)

    # Trim dark uniform borders (letterbox/pillarbox) so black bars don't dominate FFT phase.
    combined = np.maximum(a, b)
    row_energy = combined.std(axis=1)
    col_energy = combined.std(axis=0)
    active_rows = np.where(row_energy > 2.5)[0]
    active_cols = np.where(col_energy > 2.5)[0]
    if active_rows.size >= 64 and active_cols.size >= 64:
        r0, r1 = int(active_rows[0]), int(active_rows[-1]) + 1
        c0, c1 = int(active_cols[0]), int(active_cols[-1]) + 1
        a = a[r0:r1, c0:c1]
        b = b[r0:r1, c0:c1]

    # Cap region around center for fast sub-200ms 2D FFT on 4K frames.
    ch, cw = a.shape
    if ch > max_dim:
        sy = (ch - max_dim) // 2
        a = a[sy:sy + max_dim, :]
        b = b[sy:sy + max_dim, :]
    if cw > max_dim:
        sx = (cw - max_dim) // 2
        a = a[:, sx:sx + max_dim]
        b = b[:, sx:sx + max_dim]
    return a, b


def _upsampled_dft_peak(cross_power, coarse_y, coarse_x, upsample_factor=20):
    h, w = cross_power.shape
    region_size = int(np.ceil(upsample_factor * 1.5))
    if region_size % 2 == 0:
        region_size += 1
    half = region_size // 2

    freqs_y = np.fft.fftfreq(h)
    freqs_x = np.fft.fftfreq(w)

    offsets_y = coarse_y + (np.arange(region_size) - half) / float(upsample_factor)
    offsets_x = coarse_x + (np.arange(region_size) - half) / float(upsample_factor)

    # 2D separable inverse DFT evaluated strictly on the local upsampled grid around (coarse_y, coarse_x).
    ker_y = np.exp(2j * np.pi * np.outer(offsets_y, freqs_y))
    ker_x = np.exp(2j * np.pi * np.outer(freqs_x, offsets_x))
    local_corr = np.real(ker_y @ cross_power @ ker_x) / float(h * w)

    peak_idx = int(np.argmax(local_corr))
    py, px = divmod(peak_idx, region_size)
    return float(offsets_y[py]), float(offsets_x[px]), float(local_corr[py, px])


def estimate_subpixel_shift_fft(luma_a, luma_b, upsample_factor=20):
    a, b = _crop_active_region(luma_a, luma_b)
    h, w = a.shape
    if h < 16 or w < 16:
        return 0.0, 0.0, 0.0

    # Zero-mean + 2D Hanning window suppresses border wrap-around artifacts.
    win_y = np.hanning(h).astype(np.float32)
    win_x = np.hanning(w).astype(np.float32)
    window = np.outer(win_y, win_x)

    wa = (a - float(a.mean())) * window
    wb = (b - float(b.mean())) * window

    fa = np.fft.fft2(wa)
    fb = np.fft.fft2(wb)
    cross = fb * np.conj(fa)
    denom = np.abs(cross)
    # Mild regularization prevents high-frequency noise blowup.
    eps = max(float(denom.mean()) * 0.02, 1e-6)
    cross_power = cross / (denom + eps)

    corr = np.real(np.fft.ifft2(cross_power))
    peak_idx = int(np.argmax(corr))
    py, px = divmod(peak_idx, w)
    coarse_y = py - h if py > h // 2 else py
    coarse_x = px - w if px > w // 2 else px

    shift_y, shift_x, refined_peak = _upsampled_dft_peak(
        cross_power, coarse_y, coarse_x, upsample_factor=upsample_factor
    )

    # Confidence normalized against background correlation noise floor.
    bg_std = float(np.std(corr)) + 1e-8
    snr = refined_peak / bg_std
    confidence = float(np.clip((snr - 3.0) / 18.0, 0.0, 1.0))
    return round(shift_x, 3), round(shift_y, 3), round(confidence, 4)


class PreviewSession:
    def __init__(self, startup):
        self.source_file_path = startup.get("sourceFilePath") or "<preview.vpy>"
        self.display_name = startup.get("displayName") or os.path.basename(self.source_file_path)
        self.content = startup.get("content") or ""
        self.working_directory = startup.get("workingDirectory") or os.getcwd()
        self.namespace = {}
        self.video_outputs = OrderedDict()
        self.render_clips = OrderedDict()
        self.align_luma_clips = {}
        self._repair_banding_inputs = None
        self._max_render_clip_cache = 24

        self._evaluate_script()

    def _evaluate_script(self):
        previous_cwd = os.getcwd()
        previous_sys_path = list(sys.path)

        source_directory = os.path.dirname(self.source_file_path) if self.source_file_path else ""
        namespace = {
            "__name__": "__flowencode_vapoursynth_preview__",
            "__file__": self.source_file_path,
            "__package__": None,
        }

        try:
            vs.clear_outputs()

            if self.working_directory and os.path.isdir(self.working_directory):
                os.chdir(self.working_directory)

            if source_directory and os.path.isdir(source_directory):
                sys.path.insert(0, source_directory)

            with redirect_script_stdout():
                exec(compile(self.content, self.source_file_path, "exec"), namespace)

            outputs = OrderedDict(sorted(vs.get_outputs().items()))
            for index, output in outputs.items():
                if isinstance(output, vs.VideoOutputTuple):
                    self.video_outputs[index] = output
        finally:
            os.chdir(previous_cwd)
            sys.path[:] = previous_sys_path

        if not self.video_outputs:
            raise RuntimeError("The script did not expose any video outputs.")

        self.namespace = namespace

    def describe_outputs(self):
        descriptions = []
        custom_names = self.namespace.get("__flowencode_output_names__")
        if not isinstance(custom_names, dict):
            custom_names = {}
        fe_before = self.namespace.get("_fe_before")
        fe_after = self.namespace.get("_fe_after")

        for index, output in self.video_outputs.items():
            clip = output.clip
            fmt = clip.format
            is_hdr = False
            hdr_transfer_name = None
            try:
                if clip.num_frames > 0:
                    with redirect_script_stdout():
                        props = clip.get_frame(0).props
                    if "_Transfer" in props:
                        is_hdr, hdr_transfer_name = transfer_prop_to_hdr_info(props["_Transfer"])
            except Exception:
                pass

            output_name = custom_names.get(int(index)) or custom_names.get(str(index))
            if not output_name:
                if fe_before is not None and clip is fe_before:
                    output_name = "Before"
                elif fe_after is not None and clip is fe_after:
                    output_name = "After"
                else:
                    output_name = f"Output {index}"

            descriptions.append(
                {
                    "index": int(index),
                    "name": str(output_name),
                    "width": int(clip.width),
                    "height": int(clip.height),
                    "totalFrames": int(clip.num_frames),
                    "fpsNumerator": int(clip.fps.numerator),
                    "fpsDenominator": int(clip.fps.denominator),
                    "formatName": getattr(fmt, "name", "Unknown") if fmt is not None else "Unknown",
                    "bitsPerSample": int(getattr(fmt, "bits_per_sample", 0) or 0) if fmt is not None else 0,
                    "isHdr": is_hdr,
                    "hdrTransferName": hdr_transfer_name,
                }
            )

        return descriptions

    def _apply_hdr_tonemap(self, clip, props, target_nits):
        core = vs.core
        transfer_code = int(props.get("_Transfer", 0)) if "_Transfer" in props else 0
        if transfer_code not in (16, 18):
            return clip

        prop_matrix = matrix_prop_to_string(props["_Matrix"]) if "_Matrix" in props else "2020ncl"
        prop_range = range_prop_to_string(props.get("_Range", 0)) or "limited"
        prop_transfer = transfer_prop_to_string(transfer_code) or "st2084"
        prop_primaries = primaries_prop_to_string(props["_Primaries"]) if "_Primaries" in props else "2020"
        target_peak = max(50.0, min(1000.0, float(target_nits or 203.0)))

        tonemap = getattr(getattr(core, "vszip", None), "Tonemap", None)
        if tonemap is not None and clip.format.color_family == vs.YUV:
            src_csp = 2 if transfer_code == 18 else 1  # HLG or HDR10 (PQ)
            try:
                yuv444 = clip.resize.Spline36(format=vs.YUV444P16, range_in_s=prop_range, range_s="limited")
                mapped = tonemap(yuv444, src_csp=src_csp, dst_csp=0, dst_max=target_peak, tone_mapping_function=3)
                # A filter can report errors on the first frame request.
                with mapped.get_frame(0):
                    pass
                return mapped
            except vs.Error:
                pass

        # Fallback: zimg linear conversion + smooth Reinhard compression to SDR BT.709.
        try:
            linear = clip.resize.Spline36(
                format=vs.RGBS,
                matrix_in_s=prop_matrix if clip.format.color_family == vs.YUV else None,
                range_in_s=prop_range if clip.format.color_family == vs.YUV else None,
                transfer_in_s=prop_transfer,
                primaries_in_s=prop_primaries,
                transfer_s="linear",
                primaries_s="709",
                nominal_luminance=target_peak,
            )
            scale = 1000.0 / target_peak
            compressed = core.std.Expr(
                linear,
                expr=f"x 0 max {scale:.6f} * dup 1 + /",
            )
            return compressed.resize.Spline36(
                format=vs.RGB24,
                transfer_in_s="linear",
                transfer_s="709",
                primaries_in_s="709",
                primaries_s="709",
            )
        except Exception:
            return clip

    def _prepare_render_clip(
        self,
        output_index,
        plane_mode="rgb",
        tone_map_hdr=False,
        tone_map_target_nits=203.0,
        separate_fields=False,
        subpixel_offset_x=0.0,
        subpixel_offset_y=0.0,
    ):
        normalized_plane = (plane_mode or "rgb").strip().lower()
        if normalized_plane not in ("rgb", "y", "u", "v"):
            normalized_plane = "rgb"

        target_nits = round(max(50.0, min(1000.0, float(tone_map_target_nits or 203.0))), 1)
        dx = round(float(subpixel_offset_x or 0.0), 4)
        dy = round(float(subpixel_offset_y or 0.0), 4)
        cache_key = (
            int(output_index),
            normalized_plane,
            bool(tone_map_hdr),
            target_nits if tone_map_hdr else 0.0,
            bool(separate_fields),
            dx,
            dy,
        )

        if cache_key in self.render_clips:
            self.render_clips.move_to_end(cache_key)
            return self.render_clips[cache_key]

        output = self.video_outputs[output_index]
        source_clip = output.clip
        fmt = source_clip.format
        if fmt is None:
            raise RuntimeError(f"Output {output_index} has no video format.")

        with redirect_script_stdout():
            props = source_clip.get_frame(0).props if source_clip.num_frames > 0 else {}

        working_clip = source_clip
        if separate_fields:
            try:
                with redirect_script_stdout():
                    fields = working_clip.std.SeparateFields()
                    top_fields = fields[0::2]
                    bottom_fields = fields[1::2]
                    working_clip = vs.core.std.StackVertical([top_fields, bottom_fields])
                    working_clip = working_clip.std.CopyFrameProps(source_clip)
            except Exception:
                working_clip = source_clip

        if normalized_plane in ("y", "u", "v"):
            plane_index = {"y": 0, "u": 1, "v": 2}[normalized_plane]
            with redirect_script_stdout():
                num_planes = working_clip.format.num_planes
                if num_planes > plane_index:
                    plane = working_clip.std.ShufflePlanes(plane_index, vs.GRAY)
                else:
                    plane = working_clip.std.ShufflePlanes(0, vs.GRAY)
                if plane.width != working_clip.width or plane.height != working_clip.height:
                    plane = plane.resize.Point(width=working_clip.width, height=working_clip.height)
                if abs(dx) >= 0.0001 or abs(dy) >= 0.0001:
                    plane = plane.resize.Spline36(src_left=dx, src_top=dy)
                plane8 = plane.resize.Point(format=vs.GRAY8) if plane.format.id != vs.GRAY8 else plane
                render_clip = vs.core.std.ShufflePlanes([plane8, plane8, plane8], [0, 0, 0], vs.RGB)
                render_clip = render_clip.std.CopyFrameProps(source_clip)
        else:
            if tone_map_hdr:
                with redirect_script_stdout():
                    working_clip = self._apply_hdr_tonemap(working_clip, props, target_nits)

            cur_fmt = working_clip.format
            has_subpixel = abs(dx) >= 0.0001 or abs(dy) >= 0.0001
            if cur_fmt.color_family == vs.RGB and cur_fmt.id == vs.RGB24 and not has_subpixel:
                render_clip = working_clip.std.CopyFrameProps(source_clip) if working_clip is not source_clip else working_clip
            else:
                with redirect_script_stdout():
                    work_props = working_clip.get_frame(0).props if working_clip.num_frames > 0 else props
                resize_attempts = []
                prop_matrix = matrix_prop_to_string(work_props["_Matrix"]) if "_Matrix" in work_props else None
                prop_range = range_prop_to_string(work_props["_Range"]) if "_Range" in work_props else None

                subpixel_kwargs = {"src_left": dx, "src_top": dy} if has_subpixel else {}
                if cur_fmt.color_family not in (vs.RGB, vs.GRAY):
                    resize_attempts.append({"matrix_in_s": prop_matrix or "709",
                                            "range_in_s": prop_range or "limited", **subpixel_kwargs})
                elif cur_fmt.color_family == vs.GRAY and prop_range:
                    resize_attempts.append({"range_in_s": prop_range, **subpixel_kwargs})

                resize_attempts.append({**subpixel_kwargs})

                last_error = None
                render_clip = None
                resizer = working_clip.resize.Spline36 if has_subpixel else working_clip.resize.Bicubic
                for resize_kwargs in resize_attempts:
                    try:
                        with redirect_script_stdout():
                            render_clip = resizer(format=vs.RGB24, **resize_kwargs)
                            render_clip = render_clip.std.CopyFrameProps(source_clip)
                        break
                    except Exception as error:
                        last_error = error

                if render_clip is None:
                    raise last_error or RuntimeError(f"Unable to convert output {output_index} to RGB24.")

        self.render_clips[cache_key] = render_clip
        while len(self.render_clips) > self._max_render_clip_cache:
            self.render_clips.popitem(last=False)
        return render_clip

    def render_frame(
        self,
        output_index,
        frame_number,
        transport_kind=None,
        shared_memory_name=None,
        shared_memory_capacity=None,
        raw_path=None,
        plane_mode="rgb",
        tone_map_hdr=False,
        tone_map_target_nits=203.0,
        separate_fields=False,
        fft_spectrum=False,
        subpixel_offset_x=0.0,
        subpixel_offset_y=0.0,
    ):
        if output_index not in self.video_outputs:
            raise RuntimeError(f"Output {output_index} does not exist.")

        with redirect_script_stdout():
            render_clip = self._prepare_render_clip(
                output_index,
                plane_mode=plane_mode,
                tone_map_hdr=tone_map_hdr,
                tone_map_target_nits=tone_map_target_nits,
                separate_fields=separate_fields,
                subpixel_offset_x=subpixel_offset_x,
                subpixel_offset_y=subpixel_offset_y,
            )
        if frame_number < 0 or frame_number >= render_clip.num_frames:
            raise RuntimeError(f"Frame {frame_number} is outside output {output_index}.")

        with redirect_script_stdout():
            frame = render_clip.get_frame(frame_number)
        red = np.asarray(frame[0])
        green = np.asarray(frame[1])
        blue = np.asarray(frame[2])

        if fft_spectrum:
            gray = (
                0.2126 * red.astype(np.float32)
                + 0.7152 * green.astype(np.float32)
                + 0.0722 * blue.astype(np.float32)
            )
            spectrum = np.fft.fftshift(np.fft.fft2(gray))
            magnitude = np.log1p(np.abs(spectrum))
            max_mag = float(magnitude.max())
            if max_mag > 0.0:
                norm = np.clip((magnitude / max_mag) * 255.0, 0.0, 255.0).astype(np.uint8)
            else:
                norm = np.zeros_like(red, dtype=np.uint8)
            red = norm
            green = norm
            blue = norm

        alpha = np.full(red.shape, 255, dtype=np.uint8)
        bgra = np.dstack((blue, green, red, alpha))
        pixel_bytes = int(bgra.nbytes)

        if transport_kind == "sharedMemory":
            pixel_bytes = write_frame_to_shared_memory(shared_memory_name, shared_memory_capacity, bgra)
        elif raw_path:
            os.makedirs(os.path.dirname(raw_path), exist_ok=True)
            bgra.tofile(raw_path)
        else:
            raise RuntimeError("Preview frame transport is not configured.")

        props = []
        for key in sorted(frame.props.keys()):
            try:
                props.append({"key": str(key), "value": format_prop_value(frame.props[key])})
            except Exception:
                props.append({"key": str(key), "value": "<unavailable>"})

        pict_type = frame.props.get("_PictType")
        frame_type = format_prop_value(pict_type) if pict_type is not None else None

        return {
            "type": "frame",
            "outputIndex": int(output_index),
            "frameNumber": int(frame_number),
            "width": int(frame.width),
            "height": int(frame.height),
            "pixelBytes": pixel_bytes,
            "frameType": frame_type,
            "properties": props,
        }

    def _get_small_luma_clip(self, output_index, width=160, height=90):
        key = (int(output_index), int(width), int(height))
        if key in self.align_luma_clips:
            return self.align_luma_clips[key]

        clip = self.video_outputs[output_index].clip
        with redirect_script_stdout():
            if clip.format.color_family == vs.RGB:
                luma = clip.resize.Bilinear(width=width, height=height, format=vs.GRAY8, matrix_s="709")
            else:
                plane0 = clip.std.ShufflePlanes(0, vs.GRAY)
                luma = plane0.resize.Bilinear(width=width, height=height, format=vs.GRAY8)
        self.align_luma_clips[key] = luma
        return luma

    def _get_full_luma_array(self, output_index, frame_number):
        clip = self.video_outputs[output_index].clip
        clamped = max(0, min(int(frame_number), clip.num_frames - 1))
        with redirect_script_stdout():
            if clip.format.color_family == vs.RGB:
                luma_clip = clip.resize.Bilinear(format=vs.GRAY16, matrix_s="709")
                frame = luma_clip.get_frame(clamped)
                return np.asarray(frame[0], dtype=np.float32) / 257.0
            plane0 = clip.std.ShufflePlanes(0, vs.GRAY)
            frame = plane0.get_frame(clamped)
            arr = np.asarray(frame[0], dtype=np.float32)
            bits = int(getattr(clip.format, "bits_per_sample", 8) or 8)
            if clip.format.sample_type == vs.FLOAT:
                return arr * 255.0
            if bits > 8:
                return arr * (255.0 / float((1 << bits) - 1))
            return arr

    def _search_best_offset_at_anchor(self, luma_a, luma_b, anchor_a, base_b, center_offset, radius):
        n_a = luma_a.num_frames
        n_b = luma_b.num_frames
        anchor_a = max(0, min(int(anchor_a), n_a - 1))
        with redirect_script_stdout():
            feat_a = _compute_luma_feature(np.asarray(luma_a.get_frame(anchor_a)[0]))

        min_off = max(-base_b, int(center_offset - radius))
        max_off = min((n_b - 1) - base_b, int(center_offset + radius))
        if min_off > max_off:
            return int(center_offset), 0.0

        step = 4 if (max_off - min_off) > 24 else 1
        scores = {}

        def eval_offset(off):
            if off in scores:
                return scores[off]
            fb = base_b + off
            if fb < 0 or fb >= n_b:
                scores[off] = -1.0
                return -1.0
            with redirect_script_stdout():
                feat_b = _compute_luma_feature(np.asarray(luma_b.get_frame(fb)[0]))
            score = float(np.dot(feat_a, feat_b))
            scores[off] = score
            return score

        coarse_offsets = list(range(min_off, max_off + 1, step))
        if max_off not in coarse_offsets:
            coarse_offsets.append(max_off)
        if center_offset not in coarse_offsets and min_off <= center_offset <= max_off:
            coarse_offsets.append(center_offset)

        for off in coarse_offsets:
            eval_offset(off)

        if step > 1:
            top_peaks = sorted(scores.items(), key=lambda kv: kv[1], reverse=True)[:3]
            for peak_off, _ in top_peaks:
                for off in range(max(min_off, peak_off - 6), min(max_off, peak_off + 6) + 1):
                    eval_offset(off)

        best_off, best_score = max(scores.items(), key=lambda kv: kv[1])
        confidence = float(np.clip((best_score + 0.1) / 1.1, 0.0, 1.0))
        return int(best_off), round(confidence, 4)

    def auto_align_temporal(
        self,
        output_index_a,
        output_index_b,
        anchor_frame_a,
        base_frame_b,
        initial_offset=0,
        search_radius=120,
        verify_multi_anchor=True,
    ):
        t0 = time.perf_counter()
        if output_index_a not in self.video_outputs or output_index_b not in self.video_outputs:
            raise RuntimeError("Invalid output index for temporal alignment.")

        luma_a = self._get_small_luma_clip(output_index_a)
        luma_b = self._get_small_luma_clip(output_index_b)
        radius = max(8, min(int(search_radius or 120), 1200))

        best_offset, confidence = self._search_best_offset_at_anchor(
            luma_a, luma_b, anchor_frame_a, base_frame_b, int(initial_offset or 0), radius
        )

        has_drift = False
        drift_summary = None
        if verify_multi_anchor and luma_a.num_frames >= 300 and luma_b.num_frames >= 300:
            clip_a = self.video_outputs[output_index_a].clip
            clip_b = self.video_outputs[output_index_b].clip
            fps_ratio = 1.0
            try:
                fps_a = clip_a.fps.numerator / float(clip_a.fps.denominator)
                fps_b = clip_b.fps.numerator / float(clip_b.fps.denominator)
                if fps_a > 0 and fps_b > 0:
                    fps_ratio = fps_b / fps_a
            except Exception:
                fps_ratio = 1.0

            check_a1 = int(luma_a.num_frames * 0.25)
            check_a2 = int(luma_a.num_frames * 0.75)
            base_b1 = int(round(check_a1 * fps_ratio))
            base_b2 = int(round(check_a2 * fps_ratio))
            off1, conf1 = self._search_best_offset_at_anchor(luma_a, luma_b, check_a1, base_b1, best_offset, 12)
            off2, conf2 = self._search_best_offset_at_anchor(luma_a, luma_b, check_a2, base_b2, best_offset, 12)
            if conf1 >= 0.45 and conf2 >= 0.45 and (abs(off1 - best_offset) > 1 or abs(off2 - best_offset) > 1):
                has_drift = True
                drift_summary = f"25%: {off1:+d}f · Current: {best_offset:+d}f · 75%: {off2:+d}f"

        elapsed_ms = round((time.perf_counter() - t0) * 1000.0, 1)
        return {
            "type": "temporalAlignment",
            "frameOffset": int(best_offset),
            "confidence": float(confidence),
            "hasDrift": bool(has_drift),
            "driftSummary": drift_summary,
            "elapsedMs": elapsed_ms,
        }

    def auto_align_spatial(self, output_index_a, output_index_b, frame_number_a, frame_number_b):
        t0 = time.perf_counter()
        if output_index_a not in self.video_outputs or output_index_b not in self.video_outputs:
            raise RuntimeError("Invalid output index for spatial alignment.")

        luma_a = self._get_full_luma_array(output_index_a, frame_number_a)
        luma_b = self._get_full_luma_array(output_index_b, frame_number_b)
        offset_x, offset_y, confidence = estimate_subpixel_shift_fft(luma_a, luma_b, upsample_factor=20)
        elapsed_ms = round((time.perf_counter() - t0) * 1000.0, 1)
        return {
            "type": "spatialAlignment",
            "offsetX": float(offset_x),
            "offsetY": float(offset_y),
            "confidence": float(confidence),
            "elapsedMs": elapsed_ms,
        }

    def sample_pixel(self, output_index, frame_number, x, y):
        if output_index not in self.video_outputs:
            raise RuntimeError(f"Output {output_index} does not exist.")

        clip = self.video_outputs[output_index].clip
        fmt = clip.format
        if fmt is None:
            raise RuntimeError(f"Output {output_index} has no video format.")

        clamped_frame = max(0, min(int(frame_number), clip.num_frames - 1))
        px = max(0, min(int(x), clip.width - 1))
        py = max(0, min(int(y), clip.height - 1))

        with redirect_script_stdout():
            frame = clip.get_frame(clamped_frame)

        bits = int(getattr(fmt, "bits_per_sample", 8) or 8)
        is_float = fmt.sample_type == vs.FLOAT
        denom = 1.0 if is_float else float((1 << bits) - 1)
        family_name = {
            vs.YUV: "YUV",
            vs.RGB: "RGB",
            vs.GRAY: "GRAY",
        }.get(fmt.color_family, "UNKNOWN")
        plane_names = {
            vs.YUV: ("Y", "U", "V"),
            vs.RGB: ("R", "G", "B"),
            vs.GRAY: ("Y", None, None),
        }.get(fmt.color_family, ("P0", "P1", "P2"))

        sub_w = int(getattr(fmt, "subsampling_w", 0) or 0)
        sub_h = int(getattr(fmt, "subsampling_h", 0) or 0)

        values = []
        for idx in range(fmt.num_planes):
            plane = np.asarray(frame[idx])
            if idx > 0 and fmt.color_family == vs.YUV:
                sx = min(px >> sub_w, plane.shape[1] - 1)
                sy = min(py >> sub_h, plane.shape[0] - 1)
            else:
                sx = min(px, plane.shape[1] - 1)
                sy = min(py, plane.shape[0] - 1)
            raw_val = float(plane[sy, sx])
            norm_val = raw_val if is_float else (raw_val / denom if denom > 0 else 0.0)
            values.append((plane_names[idx] or f"P{idx}", raw_val, round(norm_val, 5)))

        result = {
            "type": "pixelSample",
            "x": px,
            "y": py,
            "colorFamily": family_name,
            "bitsPerSample": bits,
            "plane0Name": values[0][0],
            "plane0Raw": values[0][1],
            "plane0Normalized": values[0][2],
            "plane1Name": values[1][0] if len(values) > 1 else None,
            "plane1Raw": values[1][1] if len(values) > 1 else None,
            "plane1Normalized": values[1][2] if len(values) > 1 else None,
            "plane2Name": values[2][0] if len(values) > 2 else None,
            "plane2Raw": values[2][1] if len(values) > 2 else None,
            "plane2Normalized": values[2][2] if len(values) > 2 else None,
        }
        return result

    @staticmethod
    def _write_png_rgb_fallback(file_path, red, green, blue):
        height, width = red.shape
        rgb = np.dstack((red, green, blue))
        raw_rows = b"".join(b"\x00" + rgb[y].tobytes() for y in range(height))
        compressed = zlib.compress(raw_rows, level=3)

        def png_chunk(chunk_type, data):
            crc = zlib.crc32(chunk_type + data) & 0xFFFFFFFF
            return struct.pack(">I", len(data)) + chunk_type + data + struct.pack(">I", crc)

        ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
        png_bytes = (
            b"\x89PNG\r\n\x1a\n"
            + png_chunk(b"IHDR", ihdr)
            + png_chunk(b"IDAT", compressed)
            + png_chunk(b"IEND", b"")
        )
        os.makedirs(os.path.dirname(file_path), exist_ok=True)
        with open(file_path, "wb") as f:
            f.write(png_bytes)

    def export_frames(
        self,
        items,
        plane_mode="rgb",
        tone_map_hdr=False,
        tone_map_target_nits=203.0,
        separate_fields=False,
    ):
        t0 = time.perf_counter()
        core = vs.core
        has_fpng = hasattr(core, "fpng") and hasattr(core.fpng, "Write")
        encoder_used = "fpng" if has_fpng else "zlib-png"
        exported = 0

        for item in items:
            out_idx = int(item["outputIndex"])
            frame_num = int(item["targetFrameNumber"])
            file_path = str(item["filePath"])
            dx = float(item.get("subpixelOffsetX") or 0.0)
            dy = float(item.get("subpixelOffsetY") or 0.0)

            if out_idx not in self.video_outputs:
                raise RuntimeError(f"Output {out_idx} does not exist.")

            with redirect_script_stdout():
                render_clip = self._prepare_render_clip(
                    out_idx,
                    plane_mode=plane_mode,
                    tone_map_hdr=tone_map_hdr,
                    tone_map_target_nits=tone_map_target_nits,
                    separate_fields=separate_fields,
                    subpixel_offset_x=dx,
                    subpixel_offset_y=dy,
                )
            clamped = max(0, min(frame_num, render_clip.num_frames - 1))
            os.makedirs(os.path.dirname(file_path), exist_ok=True)

            wrote_with_fpng = False
            if has_fpng:
                try:
                    with redirect_script_stdout():
                        single = render_clip[clamped]
                        writer = core.fpng.Write(single, filename=file_path, overwrite=True)
                        _ = writer.get_frame(0)
                    wrote_with_fpng = os.path.isfile(file_path) and os.path.getsize(file_path) > 0
                except Exception:
                    wrote_with_fpng = False

            if not wrote_with_fpng:
                encoder_used = "zlib-png"
                with redirect_script_stdout():
                    frame = render_clip.get_frame(clamped)
                self._write_png_rgb_fallback(
                    file_path,
                    np.asarray(frame[0], dtype=np.uint8),
                    np.asarray(frame[1], dtype=np.uint8),
                    np.asarray(frame[2], dtype=np.uint8),
                )

            exported += 1

        elapsed_ms = round((time.perf_counter() - t0) * 1000.0, 1)
        return {
            "type": "exportFrames",
            "exportedCount": exported,
            "elapsedMs": elapsed_ms,
            "encoderUsed": encoder_used,
        }

    def scan_worst_frames(
        self,
        output_index_a,
        output_index_b,
        sample_frames_a,
        sample_frames_b,
        metric="xpsnr",
        top_n=15,
        subpixel_offset_x=0.0,
        subpixel_offset_y=0.0,
    ):
        t0 = time.perf_counter()
        if output_index_a not in self.video_outputs or output_index_b not in self.video_outputs:
            raise RuntimeError("Invalid output index for worst-frame scan.")

        count = min(len(sample_frames_a), len(sample_frames_b))
        if count <= 0:
            raise RuntimeError("No sample frames were provided for worst-frame scan.")

        core = vs.core
        clip_a = self.video_outputs[output_index_a].clip
        clip_b = self.video_outputs[output_index_b].clip

        pairs = []
        for idx in range(count):
            fa = max(0, min(int(sample_frames_a[idx]), clip_a.num_frames - 1))
            fb = max(0, min(int(sample_frames_b[idx]), clip_b.num_frames - 1))
            pairs.append((fa, fb))

        with redirect_script_stdout():
            sub_a = core.std.Splice([clip_a[fa] for fa, _ in pairs])
            sub_b = core.std.Splice([clip_b[fb] for _, fb in pairs])
            dx = float(subpixel_offset_x or 0.0)
            dy = float(subpixel_offset_y or 0.0)
            if sub_b.width != sub_a.width or sub_b.height != sub_a.height or abs(dx) >= 0.0005 or abs(dy) >= 0.0005:
                sub_b = sub_b.resize.Spline36(
                    width=sub_a.width,
                    height=sub_a.height,
                    format=sub_a.format.id,
                    src_left=dx,
                    src_top=dy,
                )
            elif sub_b.format.id != sub_a.format.id:
                sub_b = sub_b.resize.Spline36(format=sub_a.format.id)

        req_metric = (metric or "xpsnr").strip().lower()
        metric_used = "psnr"
        backend_used = "numpy-luma"
        higher_is_worse = False
        scores = []

        zip_plugin = getattr(core, "vszip", None)
        cambi = getattr(getattr(core, "akarin", None), "Cambi", None)

        def collect_scores(metric_clip, property_name):
            result = []
            for idx, (fa, fb) in enumerate(pairs):
                with metric_clip.get_frame(idx) as frame:
                    score = float(frame.props[property_name])
                # Identical frames report infinite XPSNR; keep the result valid JSON.
                if not math.isfinite(score):
                    score = 99.0 if score > 0 else 0.0
                with sub_b.get_frame(idx) as frame:
                    pict_type = frame.props.get("_PictType")
                result.append((fa, fb, score, format_prop_value(pict_type) if pict_type is not None else None))
            return result

        if req_metric == "cambi":
            if cambi is None:
                raise RuntimeError("CAMBI requires the akarin plugin from vsjetpack[full].")
            with redirect_script_stdout():
                luma = (sub_b.resize.Bilinear(format=vs.GRAY10, matrix_s="709")
                        if sub_b.format.color_family == vs.RGB
                        else sub_b.std.ShufflePlanes(0, vs.GRAY).resize.Point(format=vs.GRAY10))
                scores = collect_scores(cambi(luma), "CAMBI")
            metric_used, backend_used, higher_is_worse = "CAMBI", "akarin-cambi", True
        elif req_metric == "ssimulacra2" and hasattr(zip_plugin, "SSIMULACRA2"):
            try:
                with redirect_script_stdout():
                    scores = collect_scores(zip_plugin.SSIMULACRA2(sub_a, sub_b), "SSIMULACRA2")
                metric_used, backend_used = "SSIMULACRA2", "vszip-cpu"
            except (vs.Error, KeyError):
                pass
        elif req_metric in ("xpsnr", "auto") and hasattr(zip_plugin, "XPSNR"):
            try:
                with redirect_script_stdout():
                    if sub_a.format.color_family == vs.YUV and sub_a.format.bits_per_sample in (8, 10):
                        reference, distorted = sub_a, sub_b
                    else:
                        def to_yuv10(clip):
                            color = {"matrix_s": "709"} if clip.format.color_family != vs.YUV else {}
                            return clip.resize.Bicubic(format=vs.YUV444P10, **color)

                        reference, distorted = to_yuv10(sub_a), to_yuv10(sub_b)
                    scores = collect_scores(zip_plugin.XPSNR(reference, distorted, verbose=False), "XPSNR_Y")
                metric_used, backend_used = "XPSNR", "vszip-cpu"
            except (vs.Error, KeyError):
                pass

        # 3. Ultra-fast NumPy Luma PSNR fallback (always works, ~2-3s for 80 frames)
        if not scores:
            with redirect_script_stdout():
                luma_a = sub_a.std.ShufflePlanes(0, vs.GRAY) if sub_a.format.color_family != vs.RGB else sub_a.resize.Bilinear(format=vs.GRAY8, matrix_s="709")
                luma_b = sub_b.std.ShufflePlanes(0, vs.GRAY) if sub_b.format.color_family != vs.RGB else sub_b.resize.Bilinear(format=vs.GRAY8, matrix_s="709")
                bits = int(getattr(luma_a.format, "bits_per_sample", 8) or 8)
                peak = 1.0 if luma_a.format.sample_type == vs.FLOAT else float((1 << bits) - 1)
                for idx, (fa, fb) in enumerate(pairs):
                    fra = luma_a.get_frame(idx)
                    frb = luma_b.get_frame(idx)
                    arr_a = np.asarray(fra[0], dtype=np.float32)
                    arr_b = np.asarray(frb[0], dtype=np.float32)
                    mse = float(np.mean((arr_a - arr_b) ** 2))
                    psnr = 99.0 if mse <= 1e-12 else float(10.0 * np.log10((peak * peak) / mse))
                    pt = frb.props.get("_PictType") or fra.props.get("_PictType")
                    scores.append((fa, fb, psnr, format_prop_value(pt) if pt is not None else None))
            metric_used = "PSNR"
            backend_used = "numpy-luma"

        vals = np.array([item[2] for item in scores], dtype=np.float64)
        median_score = round(float(np.median(vals)), 3)
        if higher_is_worse:
            p10_score = round(float(np.percentile(vals, 90)), 3)
            min_score = round(float(np.max(vals)), 3)
            worst_sorted = sorted(scores, key=lambda item: item[2], reverse=True)[: max(1, min(int(top_n or 15), 50))]
        else:
            p10_score = round(float(np.percentile(vals, 10)), 3)
            min_score = round(float(np.min(vals)), 3)
            worst_sorted = sorted(scores, key=lambda item: item[2])[: max(1, min(int(top_n or 15), 50))]
        worst_entries = [
            {
                "frameNumber": int(fa),
                "secondaryFrameNumber": int(fb),
                "score": round(float(sc), 3),
                "frameType": ftype,
            }
            for fa, fb, sc, ftype in worst_sorted
        ]

        elapsed_ms = round((time.perf_counter() - t0) * 1000.0, 1)
        return {
            "type": "worstFrames",
            "metricUsed": metric_used,
            "backendUsed": backend_used,
            "sampledCount": len(scores),
            "medianScore": median_score,
            "p10Score": p10_score,
            "minScore": min_score,
            "elapsedMs": elapsed_ms,
            "worstFrames": worst_entries,
        }

    def auto_detect_crop(self, output_index, sample_count=12):
        t0 = time.perf_counter()
        if output_index not in self.video_outputs:
            raise RuntimeError(f"Output {output_index} does not exist.")

        clip = self.video_outputs[output_index].clip
        total = clip.num_frames
        if total <= 0:
            raise RuntimeError("Clip has no frames for auto-crop detection.")

        n = max(3, min(int(sample_count or 12), total, 30))
        start = int(total * 0.10) if total > 20 else 0
        end = int(total * 0.90) if total > 20 else total - 1
        if end <= start:
            start, end = 0, total - 1

        sample_indices = sorted(set(int(round(start + (end - start) * (i / max(1, n - 1)))) for i in range(n)))
        with redirect_script_stdout():
            aligned, exact, bounds = crop_samples(clip, make_luma_clip(clip), sample_indices)
            repair = verify_crop_repair(clip, aligned, exact, [next(iter(bounds))]) if aligned != exact else None
        crop_left, crop_top, crop_right, crop_bottom = aligned
        elapsed_ms = round((time.perf_counter() - t0) * 1000.0, 1)

        return {
            "type": "autoCrop",
            "cropLeft": crop_left,
            "cropTop": crop_top,
            "cropRight": crop_right,
            "cropBottom": crop_bottom,
            "cropRepair": repair,
            "sampledCount": len(sample_indices),
            "elapsedMs": elapsed_ms,
        }

    def analyze_repairs(self, output_index, source=None, ffprobe=None, ffmpeg=None, progress=lambda: None):
        clip = self.video_outputs[output_index].clip
        if clip.format is None or not clip.width or not clip.height or not clip.num_frames:
            raise RuntimeError("Repair analysis needs a nonempty video with a fixed format and frame size.")
        with redirect_script_stdout():
            chapters, pgs = read_chapters(ffprobe, source, clip)
            result, self._repair_banding_inputs = analyze_repairs(clip, chapters=chapters, pgs=pgs, ffmpeg=ffmpeg, progress=progress)
        return result

    def deep_scan_repairs(self, output_index, source=None, ffprobe=None, progress=lambda: None, playlist=None):
        clip = self.video_outputs[output_index].clip
        if clip.format is None or not clip.width or not clip.height or not clip.num_frames:
            raise RuntimeError("Deep scan needs a nonempty video with a fixed format and frame size.")
        with redirect_script_stdout():
            chapters, _ = read_chapters(ffprobe, source, clip)
            seams = read_playlist_seams(playlist, clip)
            rows = deep_scan(clip, make_luma_clip(clip), chapters, 0, clip.num_frames - 1, progress, seams)
        suggestions = []
        rows.sort(key=lambda row: row[1])
        for kind, start, end, frame, score in rows[:500]:
            frames = [frame]
            if kind == "decode":
                frames = [n for n in (start - 1, end + 1) if 0 <= n < clip.num_frames]
            suggestions.append(attach_evidence({"kind": kind, "frames": frames, "edges": [], "statements": []},
                                               [(start, end)], kind, value=score))
        return {"type": "repairAnalysis", "sampledCount": clip.num_frames, "chapterCount": len(chapters),
                "playlistSeamCount": len(seams), "playlistMismatch": bool(playlist and not seams),
                "deep": True, "truncated": len(rows) > len(suggestions), "suggestions": suggestions}

    def analyze_repair_banding(self, progress):
        inputs = self._repair_banding_inputs
        self._repair_banding_inputs = None
        if inputs is None:
            raise RuntimeError("Run repair analysis before checking banding.")
        with redirect_script_stdout():
            return analyze_banding(*inputs, progress=progress)


def main():
    if len(sys.argv) < 2:
        emit({"type": "error", "message": "Preview host startup payload is missing."})
        return 1

    startup_path = sys.argv[1]

    try:
        with open(startup_path, "r", encoding="utf-8") as file:
            startup = json.load(file)

        session = PreviewSession(startup)
        has_vs_align = importlib.util.find_spec("vs_align") is not None
        has_cambi = hasattr(getattr(vs.core, "akarin", None), "Cambi")
        has_native_res = importlib.util.find_spec("nativeres") is not None
        emit(
            {
                "type": "ready",
                "outputs": session.describe_outputs(),
                "hasVsAlign": has_vs_align,
                "hasCambi": has_cambi,
                "hasNativeRes": has_native_res,
            }
        )
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        emit({"type": "error", "message": str(error)})
        return 1

    for line in sys.stdin:
        if not line:
            break

        line = line.strip()
        if not line:
            continue

        command = None
        try:
            command = json.loads(line)
            command_name = command.get("command")

            if command_name == "close":
                return 0

            if command_name == "renderFrame":
                request_id = int(command.get("requestId", 0))
                response = session.render_frame(
                    int(command["outputIndex"]),
                    int(command["frameNumber"]),
                    command.get("transportKind"),
                    command.get("sharedMemoryName"),
                    command.get("sharedMemoryCapacity"),
                    command.get("rawPath"),
                    command.get("planeMode") or "rgb",
                    bool(command.get("toneMapHdr", False)),
                    float(command.get("toneMapTargetNits") or 203.0),
                    bool(command.get("separateFields", False)),
                    bool(command.get("fftSpectrum", False)),
                    float(command.get("subpixelOffsetX") or 0.0),
                    float(command.get("subpixelOffsetY") or 0.0),
                )
                response["requestId"] = request_id
                emit(response)
                continue

            if command_name == "autoAlignTemporal":
                request_id = int(command.get("requestId", 0))
                response = session.auto_align_temporal(
                    int(command["outputIndexA"]),
                    int(command["outputIndexB"]),
                    int(command["anchorFrameA"]),
                    int(command["baseFrameB"]),
                    int(command.get("initialOffset", 0)),
                    int(command.get("searchRadius", 120)),
                    bool(command.get("verifyMultiAnchor", True)),
                )
                response["requestId"] = request_id
                emit(response)
                continue

            if command_name == "autoAlignSpatial":
                request_id = int(command.get("requestId", 0))
                response = session.auto_align_spatial(
                    int(command["outputIndexA"]),
                    int(command["outputIndexB"]),
                    int(command["frameNumberA"]),
                    int(command["frameNumberB"]),
                )
                response["requestId"] = request_id
                emit(response)
                continue

            if command_name == "samplePixel":
                request_id = int(command.get("requestId", 0))
                response = session.sample_pixel(
                    int(command["outputIndex"]),
                    int(command["frameNumber"]),
                    int(command["x"]),
                    int(command["y"]),
                )
                response["requestId"] = request_id
                emit(response)
                continue

            if command_name == "exportFrames":
                request_id = int(command.get("requestId", 0))
                response = session.export_frames(
                    command.get("items") or [],
                    command.get("planeMode") or "rgb",
                    bool(command.get("toneMapHdr", False)),
                    float(command.get("toneMapTargetNits") or 203.0),
                    bool(command.get("separateFields", False)),
                )
                response["requestId"] = request_id
                emit(response)
                continue

            if command_name == "scanWorstFrames":
                request_id = int(command.get("requestId", 0))
                response = session.scan_worst_frames(
                    int(command["outputIndexA"]),
                    int(command["outputIndexB"]),
                    command.get("sampleFramesA") or [],
                    command.get("sampleFramesB") or [],
                    command.get("metric") or "xpsnr",
                    int(command.get("topN", 15)),
                    float(command.get("subpixelOffsetX") or 0.0),
                    float(command.get("subpixelOffsetY") or 0.0),
                )
                response["requestId"] = request_id
                emit(response)
                continue

            if command_name == "analyzeRepairs":
                request_id = int(command.get("requestId", 0))
                report_repair_progress = repair_progress(request_id)
                args = (int(command["outputIndex"]), command.get("mediaSource"), command.get("ffprobePath"))
                if command.get("deep"):
                    response = session.deep_scan_repairs(*args, progress=report_repair_progress, playlist=command.get("playlistPath"))
                else:
                    response = session.analyze_repairs(*args, ffmpeg=command.get("ffmpegPath"), progress=report_repair_progress)
                response["requestId"] = request_id
                emit(response)
                continue

            if command_name == "analyzeRepairBanding":
                request_id = int(command.get("requestId", 0))
                response = session.analyze_repair_banding(repair_progress(request_id))
                response["requestId"] = request_id
                emit(response)
                continue

            if command_name == "autoDetectCrop":
                request_id = int(command.get("requestId", 0))
                response = session.auto_detect_crop(
                    int(command["outputIndex"]),
                    int(command.get("sampleCount", 12)),
                )
                response["requestId"] = request_id
                emit(response)
                continue

            emit({"type": "error", "message": f"Unsupported command: {command_name}"})
        except Exception as error:
            traceback.print_exc(file=sys.stderr)
            emit(
                {
                    "type": "error",
                    "requestId": int(command.get("requestId", 0)) if isinstance(command, dict) else 0,
                    "message": str(error),
                }
            )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
