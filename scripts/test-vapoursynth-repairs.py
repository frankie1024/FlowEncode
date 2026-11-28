"""Run with the Python/VapourSynth runtime that FlowEncode uses for preview."""

import json
from pathlib import Path
import shutil
import struct
import subprocess
import sys
from tempfile import TemporaryDirectory
import unittest

import numpy as np
import vapoursynth as vs


HELPER = Path(__file__).resolve().parents[1] / "FlowEncode" / "Assets" / "VapourSynthEditor" / "python"
sys.path.insert(0, str(HELPER))

from repair_analysis import analyze_banding, analyze_repairs, apply_statements, crop_samples, make_luma_clip
from repair_scan import deep_scan, occurrence_scope, read_chapters, read_playlist_seams, sample_windows


def make_clip(format_id, paint, width=512, height=288, full=False, length=6):
    base = vs.core.std.BlankClip(format=format_id, width=width, height=height, length=length).std.SetFrameProps(_Range=int(full))

    def modify(n, f):
        output = f.copy()
        for plane in range(3 if base.format.color_family == vs.RGB else 1):
            paint(n, np.asarray(output[plane]))
        return output

    return base.std.ModifyFrame(clips=base, selector=modify)


def letterbox(edges, *, black=16, content=110, noise=None, dark_edge=None, one_unbarred=False):
    left, top, right, bottom = edges

    def paint(index, pixels):
        h, w = pixels.shape
        if one_unbarred and index == 3:
            pixels[:] = content
            return
        pixels[:] = black
        pixels[top:h-bottom, left:w-right] = content
        if noise is not None:
            pixels[top-2:top, left+8:left+14] = noise
        if dark_edge is not None:
            pixels[top:top+2, left:w-right] = dark_edge
            pixels[h-bottom-2:h-bottom, left:w-right] = dark_edge

    return paint


class RepairTests(unittest.TestCase):
    def test_crop_ignores_sparse_noise_and_keeps_dark_picture(self):
        edges = (4, 8, 6, 10)
        cases = [
            (vs.YUV420P8, letterbox(edges, dark_edge=22), False),
            (vs.YUV420P8, letterbox(edges, black=0, dark_edge=12), True),
            (vs.YUV420P10, letterbox(edges, black=64, content=440, noise=112), False),
        ]
        for format_id, paint, full in cases:
            with self.subTest(format_id=format_id, full=full):
                clip = make_clip(format_id, paint, width=80, height=64, full=full)
                aligned, exact, _ = crop_samples(clip, make_luma_clip(clip), range(6))
                self.assertEqual((aligned, exact), (edges, edges))

    def test_uses_the_chroma_grid_in_each_direction(self):
        edges = (3, 7, 5, 9)
        for format_id, expected in [(vs.YUV420P8, (2, 6, 4, 8)), (vs.YUV422P8, (2, 7, 4, 9)),
                                    (vs.YUV444P8, edges), (vs.RGB24, edges)]:
            with self.subTest(format_id=format_id):
                clip = make_clip(format_id, letterbox(edges))
                aligned, exact, _ = crop_samples(clip, make_luma_clip(clip), range(6))
                self.assertEqual((aligned, exact), (expected, edges))

    def test_odd_border_recipe_removes_black_and_keeps_420(self):
        if not hasattr(getattr(vs.core, "fb", None), "FillBorders"):
            self.skipTest("FillBorders is unavailable.")
        for edges, expected_dims, expected_remainder in [
            ((0, 21, 0, 21), (512, 246), (0, 1, 0, 1)),
            ((0, 21, 0, 20), (512, 248), (0, 1, 0, 0)),
            ((1, 0, 0, 0), (512, 288), (1, 0, 0, 0)),
            ((3, 0, 5, 0), (504, 288), (1, 0, 1, 0)),
        ]:
            with self.subTest(edges=edges):
                clip = make_clip(vs.YUV420P8, letterbox(edges))
                result, _ = analyze_repairs(clip, range(6))
                crop = next(fix for fix in result["suggestions"] if fix["kind"] == "crop")
                self.assertNotIn("failure", crop)
                self.assertEqual(crop["edges"], edges)
                self.assertEqual(crop["remainder"], expected_remainder)
                self.assertIn("FillBorders", "\n".join(crop["statements"]))
                self.assertIn("src_width=", "\n".join(crop["statements"]))
                with apply_statements(clip, crop["statements"]).get_frame(0) as frame:
                    self.assertEqual((frame.width, frame.height), expected_dims)
                    self.assertEqual(frame.format.id, vs.YUV420P8)
                    self.assertTrue((np.asarray(frame[0]) == 110).all())

    def test_changing_aspect_keeps_the_widest_sample(self):
        clip = make_clip(vs.YUV420P8, letterbox((0, 20, 0, 22), one_unbarred=True))
        result, _ = analyze_repairs(clip, range(6))
        self.assertTrue(result["variableCrop"])
        self.assertTrue(any(fix["kind"] == "variable-crop" for fix in result["suggestions"]))
        self.assertFalse(any(fix["kind"] == "crop" for fix in result["suggestions"]))

    def test_scope_uses_distribution_and_sample_coverage(self):
        samples = [5, 15, 28, 39, 54, 68, 80, 90]
        self.assertEqual(occurrence_scope(samples, samples, 100)["scope"], "widespread")
        self.assertEqual(occurrence_scope(samples, samples[:4], 100)["scope"], "local")
        self.assertEqual(occurrence_scope(samples, samples[::2], 100)["scope"], "uncertain")
        self.assertEqual(occurrence_scope(samples[:2], samples[:2], 100)["scope"], "uncertain")

    def test_sample_windows_include_both_sides_of_a_chapter(self):
        clip = make_clip(vs.GRAY8, lambda n, pixels: pixels.fill(100 if n < 42 else 200), length=100)
        frames, windows = sample_windows(make_luma_clip(clip), [42])
        self.assertTrue({41, 42}.issubset(frames))
        self.assertFalse(any(first < 42 <= last for first, last, _ in windows))

    def test_subtitles_in_the_margin_block_global_crop(self):
        base = letterbox((0, 0, 0, 24))

        def paint(index, pixels):
            base(index, pixels)
            if 30 <= index <= 55:
                pixels[-14:-8, 180:206] = 190

        clip = make_clip(vs.GRAY8, paint, length=96)
        result, _ = analyze_repairs(clip, chapters=[48])
        findings = {suggestion["kind"]: suggestion for suggestion in result["suggestions"]}
        self.assertEqual(findings["crop"]["statements"], [])
        self.assertEqual(findings["subtitle"]["region"], [180, 274, 206, 280])

    def test_deep_scan_locates_freeze_and_isolated_black_frame(self):
        x = np.arange(512)

        def paint(index, pixels):
            if index == 30:
                pixels.fill(16)
                return
            position = 10 if 10 <= index <= 17 else index if index != 31 else 29
            pixels[:] = np.where((x + position * 12) % 200 < 80, 190, 60)

        clip = make_clip(vs.GRAY8, paint, length=48)
        findings = deep_scan(clip, make_luma_clip(clip), [], 0, 47)
        self.assertIn(("freeze", 10, 17), [finding[:3] for finding in findings])
        self.assertIn(("black-run", 30, 30), [finding[:3] for finding in findings])

    def test_play_item_timing_is_checked_against_video_length(self):
        clip = make_clip(vs.GRAY8, lambda n, pixels: pixels.fill(99), length=48)
        play_item = b"00001M2TS" + b"\x00" * 3 + struct.pack(">II", 90000, 135000)
        table = struct.pack(">HHH", 0, 2, 0) + (struct.pack(">H", len(play_item)) + play_item) * 2
        file_data = b"MPLS0200" + struct.pack(">I", 32) + b"\x00" * 20 + struct.pack(">I", len(table)) + table
        with TemporaryDirectory() as directory:
            playlist = Path(directory) / "00001.mpls"
            playlist.write_bytes(file_data)
            self.assertEqual(read_playlist_seams(playlist, clip), [24])
            self.assertEqual(read_playlist_seams(playlist, clip[:20]), [])

    def test_dirty_edge_after_cropping(self):
        if not (hasattr(vs.core, "edgefixer") or hasattr(vs.core, "fb")):
            self.skipTest("EdgeFixer and FillBorders are unavailable.")
        paint_borders = letterbox((4, 8, 6, 10))

        def paint(index, pixels):
            paint_borders(index, pixels)
            pixels[8, 4:-6] = 160

        clip = make_clip(vs.YUV420P8, paint)
        result, _ = analyze_repairs(clip, range(6))
        suggested = [fix for fix in result["suggestions"] if fix.get("method") != "manual"]
        self.assertEqual([fix["kind"] for fix in suggested], ["crop", "edge"])
        crop, edge = suggested
        self.assertNotIn("failure", edge)
        self.assertEqual(tuple(edge["edges"]), (0, 1, 0, 0))
        fixed = apply_statements(clip, crop["statements"] + edge["statements"])
        with fixed.get_frame(0) as frame:
            luma = np.asarray(frame[0])
            self.assertLess(abs(float(luma[0, 100]) - float(luma[1, 100])), 10)

    def test_diagonal_staircases_yield_review_frames(self):
        y, x = np.indices((288, 512))

        def paint(index, pixels):
            pixels[:] = np.where((x - y // 2) % 100 < 45, 192, 80)

        clip = make_clip(vs.YUV420P8, paint)
        result, _ = analyze_repairs(clip, range(6))
        aa = next(fix for fix in result["suggestions"] if fix["kind"] == "aa")
        self.assertEqual(len(aa["frames"]), 3)
        if hasattr(vs.core, "znedi3"):
            self.assertNotIn("failure", aa)

    def test_cambi_sample_yields_deband_suggestion(self):
        if not hasattr(getattr(vs.core, "akarin", None), "Cambi"):
            self.skipTest("CAMBI is unavailable.")

        def paint(index, pixels):
            pixels[:] = 128 + 2 * (np.arange(pixels.shape[1]) // 8)

        clip = make_clip(vs.GRAY10, paint, width=1280, height=720)
        result = analyze_banding(clip, clip, [0, 1, 2])
        self.assertEqual([fix["kind"] for fix in result["suggestions"]], ["deband"])
        if hasattr(vs.core, "vszip"):
            self.assertNotIn("failure", result["suggestions"][0])

    def test_ffmpeg_pq_cambi_reports_region_and_local_range(self):
        ffmpeg = shutil.which("ffmpeg")
        if not ffmpeg:
            self.skipTest("FFmpeg is unavailable.")

        def paint(index, pixels):
            pixels[:] = 128 + 2 * (np.arange(pixels.shape[1]) // 8) if 3 <= index <= 8 else 200

        clip = make_clip(vs.GRAY10, paint, width=640, height=360, length=16).std.SetFrameProps(_Transfer=16)
        result = analyze_banding(clip, clip, [5], ffmpeg, [(0, 10, 5)])
        self.assertFalse(result.get("bandingScanFailure"), result)
        finding = result["suggestions"][0]
        self.assertEqual(finding["evidence"], "cambi-pq")
        self.assertEqual(finding["ranges"], [{"start": 3, "end": 8}])
        self.assertEqual(len(finding["region"]), 4)

    def test_preview_host_reports_exact_and_aligned_odd_crop(self):
        if not hasattr(getattr(vs.core, "fb", None), "FillBorders"):
            self.skipTest("FillBorders is unavailable.")
        script = """import numpy as np
import vapoursynth as vs
base = vs.core.std.BlankClip(format=vs.YUV420P8, width=80, height=102, length=3).std.SetFrameProps(_Range=0)
def paint(n, f):
    output = f.copy()
    pixels = np.asarray(output[0])
    pixels[:] = 16
    pixels[21:-21] = 110
    return output
base.std.ModifyFrame(clips=base, selector=paint).set_output(0)
"""
        with TemporaryDirectory() as path:
            startup = Path(path) / "startup.json"
            startup.write_text(json.dumps({"content": script}), encoding="utf-8")
            result = subprocess.run([sys.executable, HELPER / "preview_host.py", startup],
                                    input='{"command":"autoDetectCrop","outputIndex":0,"requestId":1}\n{"command":"close"}\n',
                                    text=True, capture_output=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        ready, crop = map(json.loads, result.stdout.splitlines())
        self.assertEqual(ready["type"], "ready")
        self.assertEqual(crop["type"], "autoCrop", result.stderr)
        self.assertEqual((crop["cropTop"], crop["cropBottom"]), (20, 20))
        self.assertEqual(crop["cropRepair"]["edges"], [0, 21, 0, 21])
        self.assertNotIn("failure", crop["cropRepair"])


if __name__ == "__main__":
    unittest.main()
