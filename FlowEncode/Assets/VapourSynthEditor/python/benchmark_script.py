import json
import os
import sys
import time
import traceback
from contextlib import contextmanager


@contextmanager
def redirect_stdout_to_stderr():
    prev = sys.stdout
    try:
        sys.stdout = sys.stderr
        yield
    finally:
        sys.stdout = prev


def main():
    payload = json.load(sys.stdin)
    file_path = payload.get("filePath") or "<untitled.vpy>"
    content = payload.get("content") or ""
    working_dir = payload.get("workingDirectory") or os.getcwd()
    sample_frames = max(10, min(int(payload.get("sampleFrames") or 100), 500))
    output_index = int(payload.get("outputIndex") or 0)

    try:
        import vapoursynth as vs

        prev_cwd = os.getcwd()
        prev_sys_path = list(sys.path)
        source_dir = os.path.dirname(file_path) if file_path and os.path.isabs(file_path) else ""
        namespace = {
            "__name__": "__flowencode_benchmark__",
            "__file__": file_path,
            "__package__": None,
        }

        try:
            vs.clear_outputs()
            if working_dir and os.path.isdir(working_dir):
                os.chdir(working_dir)
            if source_dir and os.path.isdir(source_dir):
                sys.path.insert(0, source_dir)

            with redirect_stdout_to_stderr():
                exec(compile(content, file_path, "exec"), namespace)

            outputs = sorted(vs.get_outputs().items())
            video_outputs = {idx: out for idx, out in outputs if isinstance(out, vs.VideoOutputTuple)}
        finally:
            os.chdir(prev_cwd)
            sys.path[:] = prev_sys_path

        if not video_outputs:
            raise RuntimeError("The script did not expose any video outputs.")

        target_idx = output_index if output_index in video_outputs else next(iter(video_outputs.keys()))
        clip = video_outputs[target_idx].clip
        total = int(clip.num_frames)
        if total <= 0:
            raise RuntimeError("Output clip has 0 frames.")

        count = min(sample_frames, total)
        if total > count * 2:
            # Start at 25% into the clip to avoid black/static intros and reflect realistic mid-clip filter cost.
            start = max(0, min(int(total * 0.25), total - count))
        else:
            start = 0

        # Warm up 1 frame so one-time indexer / shader compilation doesn't skew steady-state FPS.
        with redirect_stdout_to_stderr():
            _ = clip.get_frame(start)

        t0 = time.perf_counter()
        with redirect_stdout_to_stderr():
            for frame_idx in range(start, start + count):
                _ = clip.get_frame(frame_idx)
        elapsed = max(1e-6, time.perf_counter() - t0)
        fps = count / elapsed

        fmt = clip.format
        result = {
            "succeeded": True,
            "outputIndex": int(target_idx),
            "framesRendered": int(count),
            "startFrame": int(start),
            "totalFrames": total,
            "elapsedSeconds": round(elapsed, 3),
            "fps": round(fps, 2),
            "width": int(clip.width),
            "height": int(clip.height),
            "formatName": getattr(fmt, "name", "Unknown") if fmt is not None else "Unknown",
            "bitsPerSample": int(getattr(fmt, "bits_per_sample", 0) or 0) if fmt is not None else 0,
            "errorMessage": None,
        }
    except Exception as ex:
        traceback.print_exc(file=sys.stderr)
        result = {
            "succeeded": False,
            "outputIndex": output_index,
            "framesRendered": 0,
            "startFrame": 0,
            "totalFrames": 0,
            "elapsedSeconds": 0.0,
            "fps": 0.0,
            "width": 0,
            "height": 0,
            "formatName": "",
            "bitsPerSample": 0,
            "errorMessage": str(ex),
        }

    json.dump(result, sys.stdout, ensure_ascii=False)


if __name__ == "__main__":
    main()
