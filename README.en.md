<div align="center">
  <img src="./docs/assets/flowencode-hammer.png" alt="FlowEncode" width="96" height="96">

  <h1>FlowEncode</h1>

  <p><strong>A Windows x64 frontend for video encoding, transcoding and VapourSynth workflows</strong></p>

  <p>
    <a href="./README.md">中文</a> ·
    <a href="https://frankie1024.github.io/FlowEncode/">Website</a> ·
    <a href="https://github.com/frankie1024/FlowEncode/releases">Download</a> ·
    <a href="https://github.com/frankie1024/FlowEncode/issues">Issues</a>
  </p>

  <p>
    <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%20x64-0078d4">
    <img alt="Version" src="https://img.shields.io/badge/version-1.9.5-167a7f">
    <img alt="Framework" src="https://img.shields.io/badge/.NET-8.0-512bd4">
    <img alt="UI" src="https://img.shields.io/badge/UI-WinUI%203-0d6efd">
    <img alt="License" src="https://img.shields.io/badge/license-GPL--3.0--only-blue">
  </p>
</div>

---

FlowEncode is a Windows x64 desktop frontend for video encoding workflows. It coordinates `x264`, `x265`, `SVT-AV1`, `Av1an`, `FFmpeg`, `VapourSynth`, `VSPipe` and related public toolchains through one interface for Blu-ray demuxing, script editing and preview, video encoding, audio transcoding, auto encoding, queue execution, log inspection, template reuse, and drag-and-drop entry points for video, audio, and Blu-ray sources.

It is designed for users who want a graphical Windows video encoding GUI while keeping the flexibility of command-line encoders, VapourSynth scripts and Av1an automation.

> [!IMPORTANT]
> FlowEncode currently supports Windows x64 only. New installations should complete the in-app first-run environment guide before production use.

> [!NOTE]
> Transparency note: this project is heavily assisted by AI-generated code. The actual behavior, dependency boundaries and limitations are defined by the source code, release artifacts and this documentation.

![FlowEncode dashboard](./docs/assets/flowencode-dashboard-en.png)

## Table of Contents

- [Project Scope](#project-scope)
- [Feature Overview](#feature-overview)
- [Workflow](#workflow)
- [Supported Toolchain](#supported-toolchain)
- [Installation and Runtime Requirements](#installation-and-runtime-requirements)
- [Local Data and Privacy](#local-data-and-privacy)
- [Development and Build](#development-and-build)
- [Feedback and License](#feedback-and-license)

## Project Scope

FlowEncode is not a standalone encoder and does not bundle every dependency into a single distribution. It is closer to a workflow console for video encoding:

| Goal | Description |
| --- | --- |
| Unified configuration | Keep input/output paths, encoder parameters, script state, job queues and logs in one desktop interface. |
| Toolchain flexibility | Coordinate `x264`, `x265`, `SVT-AV1`, `Av1an`, `FFmpeg` and `VapourSynth` instead of replacing them. |
| Less repeated setup | Reduce daily encoding setup through templates, command previews, queue execution and environment guidance. |
| Local-first operation | Work with local files, local dependencies and a local workspace by default. |

## Feature Overview

| Module | Capabilities |
| --- | --- |
| Dashboard | Entry point for demux, VapourSynth, video encode, audio transcode, auto encode, templates and settings, with active task indicators and direct drag-and-drop routing for video files, audio files, and Blu-ray folders. |
| Blu-ray Demux | Scan Blu-ray folders, choose playlists and tracks, then export video, audio and subtitles through detected or imported `DGDemux` / `eac3to`. |
| VapourSynth | Edit `.vpy` / `.py` scripts, drop files to open them, manage tabs, compare scripts side by side, inspect diagnostics, preview frames, view frame props, save snapshots, manage chapters, use crop helpers and read preview logs. |
| Video Encode | Configure regular `x264`, `x265` and `SVT-AV1` jobs with input/output paths, HDR argument import, copyable command previews, job queues, batch actions, keyboard shortcuts and real-time logs. |
| Audio Transcode | Run standalone `eac3to`, `deew`, `FFmpeg` / `opusenc` audio workflows for FLAC, AC3, DDP, Opus and related outputs; DDP workflows require a user-supplied, properly licensed `Dolby Encoding Engine` / `DEE`. |
| Auto Encode | Run target-quality workflows through a structured `Av1an` backend, probe backend-supported metrics and encoders, and tune probing options, backend arguments, execution plans, and staged live progress. |
| Template Library | Save, load, import, export, pin and overwrite `.profile` templates for reusable encoding presets. |
| Environment Guide | Show grouped collapsible dependency cards, check local and remote readiness, guide installation or updates for publicly supported runtimes, plugins, encoders, `FFmpeg` and `Av1an`, and allow manual import, pinning or removal of app-local copies for selected demuxers and audio tools. |
| Settings and Updates | Manage workspace path, theme, language, dependency status, application updates, queue completion actions and first-run guide access. |

## Workflow

```mermaid
flowchart LR
  A["Prepare source files or Blu-ray folders"] --> B["Demux / write or load VapourSynth scripts"]
  B --> C["Choose video, audio or auto-encode workflow"]
  C --> D["Choose encoder, template and parameters"]
  D --> E["Preview and copy the resolved command"]
  E --> F["Queue or run directly"]
  F --> G["Inspect progress, logs and output"]
```

Common usage patterns:

- Scan Blu-ray folders, choose playlists and tracks, then demux through prepared `DGDemux` or `eac3to` binaries.
- Drop video files, audio files, or Blu-ray folders onto the main window to jump straight into the matching workflow.
- Maintain `.vpy` / `.py` scripts in the VapourSynth editor and inspect output frames, frame properties, chapters and crop parameters in the preview window.
- Drop scripts onto the main window to open them, or create empty `.vpy` scripts from the Windows Explorer new-file menu.
- Configure regular encoding tasks with `x264`, `x265` or `SVT-AV1`, then copy the resolved command for review or debugging.
- Run standalone audio transcode jobs for FLAC, AC3, DDP, Opus and related formats; DDP output depends on a user-supplied, properly licensed `Dolby Encoding Engine` / `DEE`.
- Run target-quality automation through `Av1an`, with metrics, probing options, and execution details exposed according to the active backend capabilities.
- Start, cancel, delete and reorder queued jobs in batches, use keyboard shortcuts for faster queue work, and optionally sleep or shut down after a successful queue drain and system idle.
- Save stable settings as `.profile` templates and reuse them in future jobs.

## Supported Toolchain

| Type | Components |
| --- | --- |
| Video encoders | `x264`, `x265`, `SVT-AV1` |
| Auto encoding | `Av1an`, `VMAF` |
| Quality metrics | `VMAF`, `SSIMULACRA2`, `XPSNR` / `XPSNR-Weighted`, `Butteraugli-INF` / `Butteraugli-3` (subject to the active `Av1an` backend capabilities) |
| Blu-ray demuxing | `DGDemux`, `eac3to` |
| Audio processing | `eac3to`, `deew`, `opusenc` / `Opus Encoder`, `FFmpeg`; DDP output requires a user-supplied, properly licensed `Dolby Encoding Engine` / `DEE` |
| Media probing and pipelines | `FFmpeg`, `FFprobe` |
| Script runtime | `Python 3.12`, `VapourSynth`, `vsrepo`, public VapourSynth plugins |
| Input bridge | `VSPipe`, `Avs2PipeMod` |
| Application framework | `.NET 8`, `WinUI 3`, `Windows App SDK` |

Support depth depends on upstream versions, local runtime state, plugin availability and user environment configuration. FlowEncode provides detection and guidance where possible, but it does not replace upstream installation instructions or licenses.

In-app automatic installation or updates mainly cover `Python 3.12`, `VapourSynth` / `vsrepo` and public plugin packages, `awsmfunc`, `vsjetpack`, `FFmpeg`, `x264`, `x265`, `SVT-AV1`, and `Av1an` when a managed compatible package is available. `Avs2PipeMod`, `DGDemux`, `eac3to`, `deew`, `Dolby Encoding Engine` / `DEE`, and `opusenc` / `Opus Encoder` are user-supplied external tools governed by their own licenses; FlowEncode only detects them, imports local binaries, pins paths and invokes them. It does not distribute those tools in the installer or grant rights to use them.

## Installation and Runtime Requirements

### Download

Download the latest Windows x64 installer from [GitHub Releases](https://github.com/frankie1024/FlowEncode/releases).

Current release assets are installer-based:

```text
FlowEncode_Setup_v<version>.exe
```

### Runtime prerequisites

The installer checks for the following components:

| Dependency | Purpose |
| --- | --- |
| Microsoft Visual C++ Redistributable x64 | Required by the WinUI desktop runtime. |
| Windows App Runtime x64 | Required by the unpackaged Windows App SDK host. |
| Microsoft Edge WebView2 Runtime | Required by the embedded VapourSynth editor surface. |

If a prerequisite is missing, the installer prompts the user to install it. Users may skip those steps and continue installing the main application, but the app may fail to start or related features may not work.

### Workspace

FlowEncode supports a separate workspace directory for:

- `downloads`
- `tools`
- `encoders`
- `Templates`

The installation directory is intended for application binaries and static resources. Runtime state, large toolchain files and user templates live under local application data or the configured workspace.

## Local Data and Privacy

FlowEncode is local-first by default:

- It does not actively upload source files, output files, templates or local paths.
- Lightweight runtime state is stored under `%LocalAppData%\FlowEncode\data`.
- The current-user `.vpy` new-file menu template is stored under `%LocalAppData%\FlowEncode\data\Templates`.
- User templates are stored in the workspace `Templates` folder and `.profile` files are loaded automatically.
- Large runtime directories such as `downloads`, `tools` and `encoders` are kept under the configured workspace.
- Review and redact logs, screenshots or cache files before sharing them publicly.

## Development and Build

The main application is a WinUI 3 desktop app:

| Project | Description |
| --- | --- |
| `FlowEncode` | WinUI 3 desktop entry point and UI layer. |
| `FlowEncode.Application` | Application service abstractions. |
| `FlowEncode.Domain` | Encoding configuration, job models and domain rules. |
| `FlowEncode.Infrastructure` | Local tool discovery, runners, cache and external integrations. |
| `FlowEncode.Domain.Tests` | Domain-layer tests. |

Build notes:

- Target framework: `net8.0-windows10.0.26100.0`
- Minimum target platform: `Windows 10.0.17763.0`
- Platform: `x64`
- UI: `WinUI 3` / `Windows App SDK`
- Release scripts: `scripts/`
- Installer script: `installer/`

Official entry points:

```powershell
./scripts/build.ps1
./scripts/test.ps1
```

Common commands:

```powershell
./scripts/build.ps1 -Configuration Release
./scripts/build.ps1 -Configuration Release -RunTests
./scripts/build-release-assets.ps1
```

## Feedback and License

- Issues: [GitHub Issues](https://github.com/frankie1024/FlowEncode/issues)
- Downloads: [GitHub Releases](https://github.com/frankie1024/FlowEncode/releases)
- Website: [GitHub Pages](https://frankie1024.github.io/FlowEncode/)

This repository is licensed under `GNU General Public License v3.0` and is treated as `GPL-3.0-only`. See [LICENSE](./LICENSE) for the full license text.

Third-party tools, runtimes, plugins and packages keep their own licenses. See [THIRD_PARTY_LICENSES.md](./docs/THIRD_PARTY_LICENSES.md) for the public support scope and license notes.
