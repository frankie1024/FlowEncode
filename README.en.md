<div align="center">
  <img src="./docs/assets/flowencode-hammer.png" alt="FlowEncode" width="96" height="96">

  <h1>FlowEncode</h1>

  <p><strong>A Windows x64 desktop frontend for video encoding, audio transcoding, and VapourSynth workflows</strong></p>

  <p>
    <a href="./README.md">中文</a> ·
    <a href="https://frankie1024.github.io/FlowEncode/">Website</a> ·
    <a href="https://github.com/frankie1024/FlowEncode/releases">Download</a> ·
    <a href="https://github.com/frankie1024/FlowEncode/issues">Issues</a>
  </p>

  <p>
    <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%20x64-0078d4">
    <img alt="Version" src="https://img.shields.io/badge/version-3.1.0-167a7f">
    <img alt="Framework" src="https://img.shields.io/badge/.NET-8.0-512bd4">
    <img alt="UI" src="https://img.shields.io/badge/UI-WinUI%203-0d6efd">
    <img alt="License" src="https://img.shields.io/badge/license-GPL--3.0--only-blue">
  </p>
</div>

---

FlowEncode is a Windows x64 desktop frontend that coordinates external tools such as `x264`, `x265`, `SVT-AV1`, `Av1an`, `FFmpeg`, `VapourSynth`, and `VSPipe` for Blu-ray and MKV demuxing, script editing and preview, video encoding, audio transcoding, and target-quality auto encoding.

It is not a standalone encoder and does not bundle every external tool. Instead, it brings configuration forms, copyable command previews, templates, job queues, and a local MCP server into one interface to cut down on repetitive setup.

> [!IMPORTANT]
> FlowEncode supports Windows x64 only. Run the in-app first-run environment guide before daily use. If you still need `deew 3.x` / `DEE 5.2.1`, use the [2.0.3 compatibility release](https://github.com/frankie1024/FlowEncode/releases/tag/v2.0.3).

> [!NOTE]
> A large portion of this project is written with AI assistance. Actual behavior, dependency requirements, and limitations are defined by the source code, release packages, and documentation.

![FlowEncode dashboard](./docs/assets/flowencode-dashboard-en.png)

## Table of Contents

- [Feature Overview](#feature-overview)
- [Common Workflows](#common-workflows)
- [Supported Toolchain](#supported-toolchain)
- [Installation and Runtime Requirements](#installation-and-runtime-requirements)
- [Local MCP Automation](#local-mcp-automation)
- [Local Data and Privacy](#local-data-and-privacy)
- [Development and Build](#development-and-build)
- [Feedback and License](#feedback-and-license)

## Feature Overview

| Module | Description |
| --- | --- |
| Dashboard | Entry point to all modules with active task status; drop video, audio, script files, or Blu-ray folders onto the main window to route straight into the matching workflow. |
| Blu-ray / MKV Demux | Scan Blu-ray folders (`DGDemux` / `eac3to`) or folders containing `.mkv` files (`eac3to`), pick playlists and tracks, and export video, audio, subtitles, and chapters. |
| VapourSynth | Embedded `.vpy` / `.py` editor with drag-and-drop open, multiple tabs, side-by-side comparison, chained completion, static diagnostics with Quick Fixes, smart filter insertion, and script benchmarking. The standalone preview window provides multi-output comparison (wipe, side-by-side, difference, heatmap, flicker), automatic temporal and subpixel alignment, auto-crop, worst-frame scanning, Histogram/Waveform/Vectorscope scopes, a pixel pipette, comparison-basket export, and 9 preview filters (Original, Luma, Chroma, `Solar ×1`, `Solar ×2`, Residual, Shadow Boost, Legal Range Alert, Edge Highlight; `F` / `Shift+F` to cycle). |
| Video Encode | Configure standard `x264`, `x265`, and `SVT-AV1` jobs with input/output paths, HDR argument import, copyable command previews, job queues, batch controls, keyboard shortcuts, and live logs. |
| Audio Transcode | Run `eac3to`, `deew`, and `FFmpeg` / `opusenc` for FLAC, AC3, DDP, AC4 / `ac4-ims`, AAC, TrueHD, Opus, and related outputs, with custom output naming and templates, piping controls, and loudness measurement. Tasks using `deew` require `deew 5.5.3` or newer and a user-supplied, properly licensed `Dolby Encoding Engine` / `DEE ≥6.7.0`. |
| Auto Encode | Run target-quality encoding through `Av1an`, probing available quality metrics and encoders from the active backend, with probing options, extra arguments, execution plans, and staged live logs. |
| Template Library | Save, load, import, export, pin, and overwrite `.profile` templates to reuse encoding settings. |
| Environment Guide & Settings | Check local and remote tool readiness, install or update supported public tools, and manually import, pin, or remove selected external binaries. Manage workspace folders, theme, language, system `PATH`, app updates, and post-queue actions. |

## Common Workflows

```mermaid
flowchart LR
  A["Prepare source files, MKV, or Blu-ray folders"] --> B["Demux / edit and preview VapourSynth scripts"]
  B --> C["Choose video, audio, or auto-encode workflow"]
  C --> D["Select encoder, template, and options"]
  D --> E["Inspect and copy the resolved command"]
  E --> F["Queue or run immediately"]
  F --> G["Monitor progress, logs, and outputs"]
```

- Drop video, audio, script files, or Blu-ray folders onto the main window to jump straight into the matching module.
- Scan and extract tracks and chapters with `DGDemux` (Blu-ray folders) or `eac3to` (Blu-ray and MKV folders).
- Edit `.vpy` / `.py` scripts in the VapourSynth workspace and inspect frames, frame properties, preview filters, chapters, and crop settings in the preview window; installed builds also register an empty `.vpy` template in the Windows Explorer New menu.
- Configure regular `x264`, `x265`, or `SVT-AV1` jobs, or run target-quality `Av1an` workflows, reviewing or copying the exact command before execution.
- Start, cancel, remove, and reorder queued tasks in batches, and optionally sleep or shut down when the queue succeeds and the system is idle.
- Save stable configurations as `.profile` templates for reuse.

## Supported Toolchain

| Category | Components |
| --- | --- |
| Video encoders | `x264`, `x265`, `SVT-AV1` |
| Auto encoding | `Av1an`, `VMAF` |
| Quality metrics | `VMAF`, `SSIMULACRA2`, `XPSNR` / `XPSNR-Weighted`, `Butteraugli-INF` / `Butteraugli-3` (subject to the active `Av1an` backend) |
| Blu-ray / MKV demuxing | `DGDemux`, `eac3to` |
| Audio processing | `eac3to`, `deew`, `opusenc` / `Opus Encoder`, `FFmpeg`; DDP, AC4, TrueHD, and DEE AAC tasks require a user-supplied, properly licensed `Dolby Encoding Engine` / `DEE` |
| Media probing & pipelines | `FFmpeg`, `FFprobe` |
| Script runtime | `Python 3.14` (new installs) / `Python 3.12` (retained environments), `VapourSynth R79`, `vsrepo`, public VapourSynth plugins |
| Input bridges | `VSPipe`, `Avs2PipeMod` |
| Application framework | `.NET 8`, `WinUI 3`, `Windows App SDK` |

In-app installation and updates cover `Python 3.14`, `VapourSynth` / `vsrepo` and public plugin packages, `awsmfunc`, `vsjetpack`, `FFmpeg`, `x264`, `x265`, `SVT-AV1`, and `Av1an` when a managed compatible package is available (existing compatible `Python 3.12` environments can be kept).

`Avs2PipeMod`, `DGDemux`, `eac3to`, `deew`, `Dolby Encoding Engine` / `DEE`, and `opusenc` / `Opus Encoder` are user-supplied external tools governed by their own licenses. FlowEncode detects, imports, pins, and invokes them; it does not distribute them or grant rights to use them.

## Installation and Runtime Requirements

### Download

Download the latest Windows x64 release from [GitHub Releases](https://github.com/frankie1024/FlowEncode/releases). Each stable release provides:

```text
FlowEncode_Setup_v<version>.exe          # Windows x64 installer
FlowEncode_Setup_v<version>.exe.sha256   # Installer SHA256 checksum
FlowEncode_Portable_v<version>.zip       # Portable ZIP
SHA256SUMS.txt                           # Combined SHA256 checksums
```

> **deew requirements**: Audio tasks using `deew` require [deew 5.5.3](https://github.com/pcrlnd/deew/releases/tag/5.5.3) or newer and licensed `DEE ≥6.7.0` (`truehdd ≥0.6.3` and `dee_shim ≥0.2.0` are recommended when applicable). To upgrade `deew`, replace the CLI `deew.exe` while keeping your existing `config.toml` and DEE folder. For `deew 3.x` / `DEE 5.2.1`, use the [2.0.3 compatibility release](https://github.com/frankie1024/FlowEncode/releases/tag/v2.0.3).

### Runtime prerequisites

The installer checks for the following components and prompts to install any that are missing. Skipping them still installs the app, but startup or specific features may fail:

| Dependency | Purpose |
| --- | --- |
| Microsoft Visual C++ Redistributable x64 | Required by the WinUI desktop runtime. |
| Windows App Runtime x64 | Required by the unpackaged Windows App SDK host. |
| Microsoft Edge WebView2 Runtime | Required by the embedded VapourSynth editor. |

### Workspace and system PATH

FlowEncode stores larger dependencies and user templates in a configurable workspace directory:

- `downloads`
- `tools`
- `encoders`
- `Templates`

Click **Write System PATH** under **Settings → Environment & Dependencies** to register installed and detected encoder, tool, and Python directories on the system `PATH` (requires administrator elevation). Repeated clicks do not duplicate entries; running it again after installing tools or moving the workspace updates entries and cleans up obsolete ones previously added by this feature while preserving preexisting `PATH` items. Reopen terminals or apps afterward.

## Local MCP Automation

Both the installer and portable ZIP include a local stdio MCP server (`mcp/FlowEncode.Mcp.exe`) so external AI clients can inspect capabilities and media, build execution plans, and submit jobs to the desktop queue.

- Automation is disabled by default; enable **Local MCP Automation** in desktop Settings before connecting a client.
- The desktop app owns all tasks, concurrency limits, and output locks. Communication uses a per-user Windows named pipe with no network port exposed.

See [MCP Setup](docs/MCP.md) for client configuration and available tools.

## Local Data and Privacy

FlowEncode works locally by default:

- It does not actively upload source files, outputs, templates, or local paths.
- Lightweight runtime state is stored under `%LocalAppData%\FlowEncode\data`.
- The Windows Explorer New-menu `.vpy` template installed by Setup lives at `Assets\VapourSynthScript.vpy` inside the installation directory.
- User templates live in the workspace `Templates` folder, where `.profile` files are loaded automatically.
- `downloads`, `tools`, and `encoders` live under the configured workspace directory.
- Review and redact logs, screenshots, or cache files before sharing them publicly.

## Development and Build

Solution projects:

| Project | Description |
| --- | --- |
| `FlowEncode` | WinUI 3 desktop entry point and UI layer. |
| `FlowEncode.Application` | Application service abstractions and shared task coordination. |
| `FlowEncode.Domain` | Encoding configuration, job models, and domain rules. |
| `FlowEncode.Infrastructure` | Local tool discovery, runners, caching, and external process integration. |
| `FlowEncode.Mcp` | Local stdio MCP server. |
| `FlowEncode.Domain.Tests` | Unit tests. |

Build notes:

- Target framework: `net8.0-windows10.0.26100.0` (minimum OS version: `Windows 10.0.17763.0`)
- Platform: `x64`
- UI stack: `WinUI 3` / `Windows App SDK`
- Build and packaging scripts live in `scripts/`; installer script lives in `installer/`

Common commands:

```powershell
./scripts/build.ps1 -Configuration Release
./scripts/test.ps1 -Configuration Release
./scripts/build.ps1 -Configuration Release -RunTests
./scripts/build-release-assets.ps1
```

## Feedback and License

- Issues: [GitHub Issues](https://github.com/frankie1024/FlowEncode/issues)
- Releases: [GitHub Releases](https://github.com/frankie1024/FlowEncode/releases)
- Website: [GitHub Pages](https://frankie1024.github.io/FlowEncode/)

This repository's source code is licensed under `GNU General Public License v3.0` (`GPL-3.0-only`). See [LICENSE](./LICENSE) for the full text.

External tools, runtimes, plugins, and packages invoked or used by FlowEncode remain under their own licenses. See [THIRD_PARTY_LICENSES.md](./docs/THIRD_PARTY_LICENSES.md).
