import contextlib
import json
import os
import runpy
import sys

import vapoursynth as vs


def normalize(value):
    if isinstance(value, bytes):
        return value.decode("utf-8", "replace")

    try:
        return int(value)
    except Exception:
        return str(value)


def main():
    source_path = sys.argv[1]

    with open(os.devnull, "w", encoding="utf-8") as sink:
        with contextlib.redirect_stdout(sink):
            runpy.run_path(source_path, run_name="__vapoursynth_probe__")

    output = vs.get_output(0)
    frame = output.clip.get_frame(0)
    props = frame.props
    result = {}

    for key in (
        "_Matrix",
        "_Primaries",
        "_Transfer",
        "_Range",
        "_ColorRange",
        "_ChromaLocation",
    ):
        if key in props:
            result[key] = normalize(props[key])

    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    main()
