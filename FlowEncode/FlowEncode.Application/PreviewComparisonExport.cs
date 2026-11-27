using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FlowEncode.Application;

public enum PreviewHtmlPackageMode
{
    SingleFileEmbedded,
    FolderLinked
}

public enum PreviewHtmlPackageModePreference
{
    Auto,
    SingleFileEmbedded,
    FolderLinked
}

public sealed record PreviewBookmarkedFrame(
    int FrameNumber,
    string Label = "");

public sealed record PreviewExportOutputSpec(
    int OutputIndex,
    string DisplayName,
    int FrameOffset = 0,
    double SubpixelOffsetX = 0.0,
    double SubpixelOffsetY = 0.0);

public sealed record PreviewExportFrameRequestItem(
    int PrimaryFrameNumber,
    string GroupName,
    int OutputIndex,
    string OutputDisplayName,
    int TargetFrameNumber,
    string FilePath,
    double SubpixelOffsetX = 0.0,
    double SubpixelOffsetY = 0.0);

public sealed record PreviewExportFramesResult(
    int ExportedCount,
    double ElapsedMs,
    string EncoderUsed);

public sealed record PreviewComparisonImageEntry(
    string OutputName,
    string RelativeOrAbsolutePath,
    string? Base64PngDataUri = null);

public sealed record PreviewComparisonGroup(
    string Name,
    int FrameNumber,
    IReadOnlyList<PreviewComparisonImageEntry> Images);

public sealed record SlowPicsUploadRequest(
    string CollectionName,
    bool IsPublic,
    bool OptimizeImages,
    IReadOnlyList<PreviewComparisonGroup> Groups);

public sealed record SlowPicsUploadResult(
    bool Succeeded,
    string? ComparisonUrl,
    string? Key,
    string? ErrorMessage);

public static class PreviewComparisonExportCoordinator
{
    public const int AutoSingleFileFrameThreshold = 10;

    public static PreviewHtmlPackageMode ResolveHtmlPackageMode(
        int frameCount,
        PreviewHtmlPackageModePreference preference = PreviewHtmlPackageModePreference.Auto)
    {
        return preference switch
        {
            PreviewHtmlPackageModePreference.SingleFileEmbedded => PreviewHtmlPackageMode.SingleFileEmbedded,
            PreviewHtmlPackageModePreference.FolderLinked => PreviewHtmlPackageMode.FolderLinked,
            _ => frameCount <= AutoSingleFileFrameThreshold
                ? PreviewHtmlPackageMode.SingleFileEmbedded
                : PreviewHtmlPackageMode.FolderLinked
        };
    }

    public static IReadOnlyList<int> BuildUniformSampleFrames(int totalFrames, int count, double skipEdgeRatio = 0.08)
    {
        if (totalFrames <= 0 || count <= 0)
        {
            return [];
        }

        if (totalFrames == 1 || count == 1)
        {
            return [Math.Clamp(totalFrames / 2, 0, Math.Max(0, totalFrames - 1))];
        }

        var clampedRatio = Math.Clamp(skipEdgeRatio, 0.0, 0.35);
        var start = (int)Math.Round((totalFrames - 1) * clampedRatio);
        var end = (int)Math.Round((totalFrames - 1) * (1.0 - clampedRatio));
        if (end <= start)
        {
            start = 0;
            end = totalFrames - 1;
        }

        var actualCount = Math.Min(count, end - start + 1);
        var frames = new List<int>(actualCount);
        var seen = new HashSet<int>();
        for (var i = 0; i < actualCount; i++)
        {
            var t = actualCount == 1 ? 0.5 : i / (double)(actualCount - 1);
            var f = (int)Math.Round(start + (end - start) * t);
            f = Math.Clamp(f, 0, totalFrames - 1);
            if (seen.Add(f))
            {
                frames.Add(f);
            }
        }

        return frames;
    }

    public static string SanitizeFileNameSegment(string? raw, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(source.Length);
        foreach (var ch in source)
        {
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        }

        var cleaned = sb.ToString().Trim().Trim('.');
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }
}

public static class PreviewOfflineHtmlBuilder
{
    private static readonly JsonSerializerOptions SafeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Default
    };

    public static string BuildHtml(
        string title,
        IReadOnlyList<PreviewComparisonGroup> groups,
        PreviewHtmlPackageMode mode)
    {
        var safeTitle = string.IsNullOrWhiteSpace(title) ? "FlowEncode Comparison" : title.Trim();
        var payloadGroups = groups.Select(group => new
        {
            name = group.Name,
            frameNumber = group.FrameNumber,
            images = group.Images.Select(img => new
            {
                name = img.OutputName,
                src = mode == PreviewHtmlPackageMode.SingleFileEmbedded && !string.IsNullOrWhiteSpace(img.Base64PngDataUri)
                    ? img.Base64PngDataUri
                    : img.RelativeOrAbsolutePath.Replace('\\', '/')
            }).ToArray()
        }).ToArray();

        var dataJson = JsonSerializer.Serialize(new
        {
            title = safeTitle,
            mode = mode.ToString(),
            generatedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            groups = payloadGroups
        }, SafeJsonOptions);

        // Prevent closing </script> injection inside JSON payload.
        dataJson = dataJson.Replace("</", "<\\/", StringComparison.Ordinal);

        return HtmlTemplate
            .Replace("__FLOWENCODE_TITLE__", HtmlEscape(safeTitle), StringComparison.Ordinal)
            .Replace("__FLOWENCODE_DATA_JSON__", dataJson, StringComparison.Ordinal);
    }

    public static string CreatePngDataUri(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        return "data:image/png;base64," + Convert.ToBase64String(bytes);
    }

    private static string HtmlEscape(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
    }

    private const string HtmlTemplate = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width,initial-scale=1" />
<title>__FLOWENCODE_TITLE__</title>
<style>
  :root {
    --bg: #0f1116;
    --panel: #171a22;
    --border: #2a2f3d;
    --text: #eef1f7;
    --muted: #98a2b3;
    --accent: #3b82f6;
  }
  * { box-sizing: border-box; user-select: none; }
  html, body { margin: 0; padding: 0; height: 100%; background: var(--bg); color: var(--text); font-family: "Segoe UI", system-ui, -apple-system, sans-serif; overflow: hidden; }
  .app { display: grid; grid-template-rows: auto 1fr auto; height: 100vh; }
  .topbar { display: flex; align-items: center; gap: 10px; padding: 8px 14px; background: var(--panel); border-bottom: 1px solid var(--border); flex-wrap: wrap; }
  .title { font-weight: 600; font-size: 14px; margin-right: 8px; }
  .btn-group { display: inline-flex; gap: 4px; align-items: center; }
  button, select { background: #222735; color: var(--text); border: 1px solid var(--border); border-radius: 6px; padding: 4px 10px; font-size: 12px; cursor: pointer; }
  button:hover { border-color: #4b556b; }
  button.active { background: var(--accent); border-color: var(--accent); color: #fff; }
  .spacer { flex: 1; }
  .stage { position: relative; overflow: hidden; background: #0a0c10; cursor: grab; }
  .stage.dragging { cursor: grabbing; }
  .canvas-wrap { position: absolute; left: 0; top: 0; transform-origin: 0 0; }
  .canvas-wrap img, .canvas-wrap canvas { display: block; max-width: none; }
  .pixelated img, .pixelated canvas { image-rendering: pixelated; }
  .wipe-overlay { position: absolute; inset: 0; overflow: hidden; pointer-events: none; }
  .wipe-line { position: absolute; top: 0; bottom: 0; width: 2px; background: rgba(255,255,255,.92); box-shadow: 0 0 8px rgba(0,0,0,.85); pointer-events: none; }
  .badge { position: absolute; top: 12px; left: 12px; background: rgba(12,14,18,.82); border: 1px solid rgba(255,255,255,.22); border-radius: 6px; padding: 5px 10px; font-size: 12px; pointer-events: none; z-index: 5; }
  .badge-right { left: auto; right: 12px; }
  .footer { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 6px 14px; background: var(--panel); border-top: 1px solid var(--border); font-size: 12px; color: var(--muted); }
  .frames { display: flex; gap: 6px; overflow-x: auto; max-width: 65vw; padding-bottom: 2px; }
</style>
</head>
<body>
<div class="app">
  <div class="topbar">
    <span class="title" id="docTitle"></span>
    <div class="btn-group" id="sourceButtons"></div>
    <div class="btn-group">
      <button id="modeFlipBtn" class="active">瞬切 (Flip)</button>
      <button id="modeWipeBtn">卷帘 (Wipe)</button>
      <button id="modeDiffBtn">差值</button>
      <button id="modeHeatBtn">热力图</button>
    </div>
    <div class="btn-group">
      <button id="zoomOutBtn">-</button>
      <button id="zoomFitBtn">适应</button>
      <button id="zoom100Btn">100%</button>
      <button id="zoomInBtn">+</button>
      <button id="pixelBtn">像素点放大</button>
    </div>
    <div class="spacer"></div>
    <span id="metaInfo" style="color:var(--muted);font-size:12px;"></span>
  </div>
  <div class="stage" id="stage">
    <div class="badge" id="badgeA"></div>
    <div class="badge badge-right" id="badgeB" style="display:none;"></div>
    <div class="canvas-wrap" id="wrap">
      <img id="imgA" alt="" draggable="false" />
      <div class="wipe-overlay" id="wipeOverlay" style="display:none;">
        <img id="imgB" alt="" draggable="false" />
      </div>
      <div class="wipe-line" id="wipeLine" style="display:none;"></div>
      <canvas id="diffCanvas" style="display:none;"></canvas>
    </div>
  </div>
  <div class="footer">
    <div class="frames" id="frameStrip"></div>
    <div>快捷键：← / → 切帧 · 1-9 / 鼠标点按 切版本 · 滚轮缩放 · 拖动平移</div>
  </div>
</div>
<script>
const DATA = __FLOWENCODE_DATA_JSON__;
let groupIdx = 0;
let activeSrcIdx = 0;
let mode = "flip"; // flip | wipe | diff | heat
let zoom = 1;
let panX = 0, panY = 0;
let wipeRatio = 0.5;
let pixelated = true;
let dragging = false, dragStartX = 0, dragStartY = 0, panStartX = 0, panStartY = 0;

const stage = document.getElementById("stage");
const wrap = document.getElementById("wrap");
const imgA = document.getElementById("imgA");
const imgB = document.getElementById("imgB");
const wipeOverlay = document.getElementById("wipeOverlay");
const wipeLine = document.getElementById("wipeLine");
const diffCanvas = document.getElementById("diffCanvas");
const badgeA = document.getElementById("badgeA");
const badgeB = document.getElementById("badgeB");
const sourceButtons = document.getElementById("sourceButtons");
const frameStrip = document.getElementById("frameStrip");
document.getElementById("docTitle").textContent = DATA.title;
document.getElementById("pixelBtn").classList.add("active");
wrap.classList.add("pixelated");

function currentGroup() { return DATA.groups[groupIdx] || { name: "-", frameNumber: 0, images: [] }; }

function renderFrameStrip() {
  frameStrip.innerHTML = "";
  DATA.groups.forEach((g, idx) => {
    const btn = document.createElement("button");
    btn.textContent = g.name || ("#" + g.frameNumber);
    if (idx === groupIdx) btn.classList.add("active");
    btn.onclick = () => { groupIdx = idx; loadGroup(); };
    frameStrip.appendChild(btn);
  });
}

function renderSourceButtons() {
  sourceButtons.innerHTML = "";
  const imgs = currentGroup().images;
  imgs.forEach((im, idx) => {
    const btn = document.createElement("button");
    btn.textContent = `${idx + 1}. ${im.name}`;
    if (idx === activeSrcIdx) btn.classList.add("active");
    btn.onclick = () => { activeSrcIdx = idx; updateView(); };
    sourceButtons.appendChild(btn);
  });
}

function applyTransform() {
  wrap.style.transform = `translate(${panX}px, ${panY}px) scale(${zoom})`;
  document.getElementById("metaInfo").textContent = `${Math.round(zoom * 100)}% · ${groupIdx + 1}/${DATA.groups.length}`;
}

function fitToStage() {
  const w = imgA.naturalWidth || 1;
  const h = imgA.naturalHeight || 1;
  const sw = stage.clientWidth || 1;
  const sh = stage.clientHeight || 1;
  zoom = Math.min(sw / w, sh / h, 1);
  panX = (sw - w * zoom) / 2;
  panY = (sh - h * zoom) / 2;
  applyTransform();
}

function turboColor(t) {
  const stops = [[0,0,0,0],[0.06,24,14,76],[0.24,30,92,220],[0.45,16,200,168],[0.68,240,224,30],[0.88,245,60,20],[1,255,245,235]];
  let s = 0;
  while (s < stops.length - 2 && t > stops[s + 1][0]) s++;
  const a = stops[s], b = stops[s + 1];
  const f = b[0] > a[0] ? Math.max(0, Math.min(1, (t - a[0]) / (b[0] - a[0]))) : 0;
  return [a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f, a[3] + (b[3] - a[3]) * f];
}

function computeDiffCanvas(heatmap) {
  if (!imgA.complete || !imgB.complete || !imgA.naturalWidth || !imgB.naturalWidth) return;
  const w = Math.max(imgA.naturalWidth, imgB.naturalWidth);
  const h = Math.max(imgA.naturalHeight, imgB.naturalHeight);
  diffCanvas.width = w;
  diffCanvas.height = h;
  const ctx = diffCanvas.getContext("2d");
  try {
    const c1 = document.createElement("canvas"); c1.width = w; c1.height = h;
    const ctx1 = c1.getContext("2d"); ctx1.drawImage(imgA, (w - imgA.naturalWidth) / 2, (h - imgA.naturalHeight) / 2);
    const c2 = document.createElement("canvas"); c2.width = w; c2.height = h;
    const ctx2 = c2.getContext("2d"); ctx2.drawImage(imgB, (w - imgB.naturalWidth) / 2, (h - imgB.naturalHeight) / 2);
    const d1 = ctx1.getImageData(0, 0, w, h).data;
    const d2 = ctx2.getImageData(0, 0, w, h).data;
    const out = ctx.createImageData(w, h);
    const od = out.data;
    for (let i = 0; i < d1.length; i += 4) {
      const dr = Math.abs(d1[i] - d2[i]);
      const dg = Math.abs(d1[i + 1] - d2[i + 1]);
      const db = Math.abs(d1[i + 2] - d2[i + 2]);
      if (heatmap) {
        const mag = Math.pow(Math.min(1, (dr + dg + db) / (3 * 255) * 4), 0.55);
        const [r, g, b] = turboColor(mag);
        od[i] = r; od[i + 1] = g; od[i + 2] = b; od[i + 3] = 255;
      } else {
        od[i] = Math.min(255, Math.pow(dr / 255, 0.45) * 255);
        od[i + 1] = Math.min(255, Math.pow(dg / 255, 0.45) * 255);
        od[i + 2] = Math.min(255, Math.pow(db / 255, 0.45) * 255);
        od[i + 3] = 255;
      }
    }
    ctx.putImageData(out, 0, 0);
  } catch (err) {
    ctx.fillStyle = "#171a22";
    ctx.fillRect(0, 0, w, h);
    ctx.fillStyle = "#eef1f7";
    ctx.font = "16px sans-serif";
    ctx.fillText("本地 file:// 文件夹模式下浏览器限制像素差值读取，请使用静态 HTTP 服务打开或改用单文件导出（≤10 帧）。", 24, 48);
  }
}

function updateWipe() {
  const w = imgA.naturalWidth || 1;
  const cut = Math.round(w * wipeRatio);
  wipeOverlay.style.clipPath = `inset(0 0 0 ${cut}px)`;
  wipeLine.style.left = `${cut}px`;
}

function updateView() {
  const g = currentGroup();
  const imgs = g.images;
  if (!imgs.length) return;
  if (activeSrcIdx >= imgs.length) activeSrcIdx = 0;
  renderSourceButtons();
  renderFrameStrip();

  ["modeFlipBtn","modeWipeBtn","modeDiffBtn","modeHeatBtn"].forEach(id => document.getElementById(id).classList.remove("active"));
  document.getElementById(mode === "flip" ? "modeFlipBtn" : mode === "wipe" ? "modeWipeBtn" : mode === "diff" ? "modeDiffBtn" : "modeHeatBtn").classList.add("active");

  imgA.onload = null;
  imgB.onload = null;

  if (mode === "flip") {
    imgA.style.display = "block";
    wipeOverlay.style.display = "none";
    wipeLine.style.display = "none";
    diffCanvas.style.display = "none";
    imgA.onload = () => { if (zoom === 1 && panX === 0 && panY === 0) fitToStage(); };
    imgA.src = imgs[activeSrcIdx].src;
    badgeA.textContent = `${imgs[activeSrcIdx].name} · ${g.name}`;
    badgeB.style.display = "none";
  } else {
    const idxA = 0;
    const idxB = imgs.length > 1 ? (activeSrcIdx === 0 ? 1 : activeSrcIdx) : 0;
    if (mode === "wipe") {
      imgA.style.display = "block";
      wipeOverlay.style.display = "block";
      wipeLine.style.display = "block";
      diffCanvas.style.display = "none";
      imgA.onload = () => { updateWipe(); if (zoom === 1 && panX === 0 && panY === 0) fitToStage(); };
      imgA.src = imgs[idxA].src;
      imgB.src = imgs[idxB].src;
      updateWipe();
    } else {
      imgA.style.display = "none";
      wipeOverlay.style.display = "none";
      wipeLine.style.display = "none";
      diffCanvas.style.display = "block";
      const runDiff = () => {
        computeDiffCanvas(mode === "heat");
        if (zoom === 1 && panX === 0 && panY === 0 && imgA.naturalWidth) fitToStage();
      };
      imgA.onload = runDiff;
      imgB.onload = runDiff;
      imgA.src = imgs[idxA].src;
      imgB.src = imgs[idxB].src;
      if (imgA.complete && imgB.complete && imgA.naturalWidth && imgB.naturalWidth) runDiff();
    }
    badgeA.textContent = `A: ${imgs[idxA].name} · ${g.name}`;
    badgeB.textContent = `B: ${imgs[idxB].name}`;
    badgeB.style.display = "block";
  }
  applyTransform();
}

function loadGroup() {
  imgA.onload = () => { if (mode === "wipe") updateWipe(); if (zoom === 1 && panX === 0 && panY === 0) fitToStage(); };
  updateView();
}

document.getElementById("modeFlipBtn").onclick = () => { mode = "flip"; updateView(); };
document.getElementById("modeWipeBtn").onclick = () => { mode = "wipe"; updateView(); };
document.getElementById("modeDiffBtn").onclick = () => { mode = "diff"; updateView(); };
document.getElementById("modeHeatBtn").onclick = () => { mode = "heat"; updateView(); };
document.getElementById("zoomFitBtn").onclick = fitToStage;
document.getElementById("zoom100Btn").onclick = () => { zoom = 1; panX = (stage.clientWidth - (imgA.naturalWidth||0))/2; panY = (stage.clientHeight - (imgA.naturalHeight||0))/2; applyTransform(); };
document.getElementById("zoomInBtn").onclick = () => { zoom = Math.min(16, zoom * 1.25); applyTransform(); };
document.getElementById("zoomOutBtn").onclick = () => { zoom = Math.max(0.1, zoom / 1.25); applyTransform(); };
document.getElementById("pixelBtn").onclick = (e) => { pixelated = !pixelated; wrap.classList.toggle("pixelated", pixelated); e.target.classList.toggle("active", pixelated); };

stage.addEventListener("wheel", (e) => {
  e.preventDefault();
  const rect = stage.getBoundingClientRect();
  const mx = e.clientX - rect.left, my = e.clientY - rect.top;
  const prev = zoom;
  zoom = Math.max(0.1, Math.min(16, zoom * (e.deltaY < 0 ? 1.15 : 1 / 1.15)));
  panX = mx - (mx - panX) * (zoom / prev);
  panY = my - (my - panY) * (zoom / prev);
  applyTransform();
}, { passive: false });

stage.addEventListener("mousedown", (e) => {
  if (e.button === 0 && mode === "flip" && !e.shiftKey && currentGroup().images.length > 1) {
    activeSrcIdx = (activeSrcIdx + 1) % currentGroup().images.length;
    updateView();
  }
  dragging = true; dragStartX = e.clientX; dragStartY = e.clientY; panStartX = panX; panStartY = panY;
  stage.classList.add("dragging");
});
window.addEventListener("mousemove", (e) => {
  if (mode === "wipe" && !dragging) {
    const rect = wrap.getBoundingClientRect();
    if (rect.width > 0) {
      wipeRatio = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
      updateWipe();
    }
  }
  if (!dragging) return;
  panX = panStartX + (e.clientX - dragStartX);
  panY = panStartY + (e.clientY - dragStartY);
  applyTransform();
});
window.addEventListener("mouseup", () => { dragging = false; stage.classList.remove("dragging"); });

window.addEventListener("keydown", (e) => {
  if (e.key === "ArrowRight") { groupIdx = (groupIdx + 1) % DATA.groups.length; loadGroup(); }
  else if (e.key === "ArrowLeft") { groupIdx = (groupIdx - 1 + DATA.groups.length) % DATA.groups.length; loadGroup(); }
  else if (e.key >= "1" && e.key <= "9") {
    const i = parseInt(e.key, 10) - 1;
    if (i < currentGroup().images.length) { activeSrcIdx = i; updateView(); }
  }
});

if (DATA.groups.length > 0) {
  imgA.onload = () => { fitToStage(); if (mode === "wipe") updateWipe(); };
  loadGroup();
}
</script>
</body>
</html>
""";
}
