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
    <img alt="Version" src="https://img.shields.io/badge/version-3.2.0-167a7f">
    <img alt="License" src="https://img.shields.io/badge/license-GPL--3.0--only-blue">
  </p>
</div>

---

Use FlowEncode on Windows x64 to encode video, transcode audio, extract Blu-ray or MKV tracks, and edit and compare VapourSynth scripts. Save frequently used settings as templates and run multiple jobs from one queue.

Get the installer or portable ZIP from [GitHub Releases](https://github.com/frankie1024/FlowEncode/releases). On first launch, follow the in-app guide to set up the tools you need.

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
| Video Encode | Encode with `x264`, `x265`, or `SVT-AV1`, import HDR settings, and preview and copy commands. |
| Auto Encode | Encode to a target quality with `Av1an`. Available metrics depend on the selected backend. |
| Audio Transcode | Convert to FLAC, AC3, DDP, AC4, AAC, TrueHD, or Opus, or measure loudness. With `deew 5.7`, convert DTS:X and Auro-3D tracks to Atmos or AC4 IMS. |
| Blu-ray / MKV Demux | Extract video, audio, subtitles, and chapters from Blu-ray folders or folders containing `.mkv` files. |
| VapourSynth | Edit `.vpy` and `.py` scripts in tabs with completion, diagnostics, and benchmarking. Compare previews side by side, with a wipe, or by difference, and use auto-alignment, auto-crop, waveform scopes, and batch frame export. |
| Queue and Templates | Manage jobs in batches, follow live progress and logs, and reuse `.profile` encoding templates. |
| Environment and Settings | Check tools, install supported components, import existing tools, and set your workspace, language, and theme. |

## Common Workflows

- **Start an encode or transcode.** Drop a video, audio file, or script onto the main window, choose the output and settings, then run the job or add it to the queue.
- **Extract tracks and chapters.** Open a Blu-ray folder with `DGDemux` or `eac3to`. For folders containing `.mkv` files, use `eac3to`.
- **Compare results.** Drop two videos or scripts onto the main window. In the preview, use `F` / `Shift+F` to cycle filters or export frames in batches from the comparison basket.

## Supported Toolchain

| Category | Components |
| --- | --- |
| Video encoders | `x264`, `x265`, `SVT-AV1` |
| Auto encoding | `Av1an`, `VMAF` |
| Quality metrics | `VMAF`, `SSIMULACRA2`, `XPSNR` / `XPSNR-Weighted`, `Butteraugli-INF` / `Butteraugli-3` (subject to the active `Av1an` backend) |
| Blu-ray / MKV demuxing | `DGDemux`, `eac3to` |
| Audio processing | `eac3to`, `deew`, `opusenc` / `Opus Encoder`, `FFmpeg`; DDP, AC4, TrueHD, and DEE AAC tasks require a user-supplied, properly licensed `Dolby Encoding Engine` / `DEE` |
| Media probing & pipelines | `FFmpeg`, `FFprobe` |
| Script runtime | `Python 3.14 x64` (recommended, minimum 3.13 x64), `VapourSynth R82`, `vsjetpack[full]`, `awsmfunc` |
| Input bridges | `VSPipe`, `Avs2PipeMod` |

Install or update `Python`, `VapourSynth`, `vsjetpack[full]`, `awsmfunc`, `FFmpeg`, and video encoders from the app. You can also install `Av1an` when a compatible package is available. Get `Avs2PipeMod`, `DGDemux`, `eac3to`, `deew`, `DEE`, and `opusenc` separately and import them in Settings.

The built-in VS editor templates run on the CPU and require no separate model downloads. When you install `awsmfunc`, the setup guide also installs `vapoursynth-fillborders` for Fill Borders and `vsfpng` for PNG export.

## Installation and Runtime Requirements

### Download

- **Installer** `FlowEncode_Setup_v<version>.exe`. Follow the setup wizard.
- **Portable ZIP** `FlowEncode_Portable_v<version>.zip`. Extract it and open `FlowEncode.exe`.
- **Checksums** `FlowEncode_Setup_v<version>.exe.sha256` and `SHA256SUMS.txt`. Use them to check the SHA256 of your download.

Transcoding with deew requires [deew 5.7.0](https://github.com/pcrlnd/deew/releases/tag/5.7.0) or newer and a user-supplied licensed copy of `DEE ≥6.7.0`. Select **Download other components** in the deew card in Settings to install `truehdd`, `harletty`, and `dee_shim`. DTS:X and Auro-3D transcoding requires `harletty ≥0.9.0`. You can keep your existing `config.toml` and DEE folder when upgrading deew.

For `deew 3.x` / `DEE 5.2.1`, use the [2.0.3 compatibility release](https://github.com/frankie1024/FlowEncode/releases/tag/v2.0.3).

### Runtime prerequisites

Requires Windows 10 version 1809 or later on x64. The installer checks for these components and prompts you to install any that are missing.

| Dependency | Purpose |
| --- | --- |
| Microsoft Visual C++ Redistributable x64 | Starts the desktop app. |
| Windows App Runtime x64 | Starts the desktop app. |
| Microsoft Edge WebView2 Runtime | Opens the built-in VapourSynth editor. |

### Workspace and system PATH

Choose a workspace in Settings to store downloads, tools, encoders, and templates. To run these tools from a terminal, select **Write System PATH** under **Settings → Environment & Dependencies**, approve the administrator prompt, and reopen the terminal.

## Local MCP Automation

Enable **Local MCP Automation** in desktop Settings to let an external AI client inspect media and tools, preview jobs, and submit them to the queue. Follow the [MCP setup guide](docs/MCP.md) to connect a client on the same computer.

## Local Data and Privacy

Source and output files are processed locally by default. App data lives in `%LocalAppData%\FlowEncode\data`. Custom `.profile` templates are stored in the workspace `Templates` folder.

## Development and Build

Developer resources are available in the [repository scripts](./scripts/) and [release guide](./.github/RELEASING.md).

## Feedback and License

- Issues: [GitHub Issues](https://github.com/frankie1024/FlowEncode/issues)
- Releases: [GitHub Releases](https://github.com/frankie1024/FlowEncode/releases)
- Website: [GitHub Pages](https://frankie1024.github.io/FlowEncode/)

This repository's source code is licensed under `GNU General Public License v3.0` (`GPL-3.0-only`). See [LICENSE](./LICENSE) for the full text.

External tools, runtimes, plugins, and packages invoked or used by FlowEncode remain under their own licenses. See [THIRD_PARTY_LICENSES.md](./docs/THIRD_PARTY_LICENSES.md).
