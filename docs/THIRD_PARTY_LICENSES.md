# FlowEncode Third-Party Licenses

This document records the third-party components and tools that FlowEncode may bundle, invoke, detect, guide users to install, or allow users to import manually.

FlowEncode's own source code is distributed under `GPL-3.0-only`. That license applies to this repository's source code, not to independent runtimes, NuGet packages, encoders, media tools, VapourSynth plugins, or user-installed tools. Each third-party component remains governed by its own license.

## Bundled Or Build-Time Components

| Component | Role | License / Terms |
| --- | --- | --- |
| Monaco Editor | Bundled editor assets under `FlowEncode/Assets/VapourSynthEditor/vendor/monaco` | MIT |
| CommunityToolkit.Mvvm | MVVM helpers | MIT |
| Microsoft.Extensions.Hosting | Application hosting and dependency injection | MIT |
| SharpCompress | Archive handling | MIT |
| Microsoft.WindowsAppSDK | Windows app framework package/runtime | Microsoft license terms for the package and runtime |
| Microsoft.Windows.SDK.BuildTools | Windows SDK build tooling | Microsoft license terms |
| WebView2 Runtime / SDK surface | Runtime used by the editor host through Windows App SDK / WebView2 APIs | Microsoft license terms |
| MSTest packages | Test-only packages | MIT |

## Public External Toolchain

| Component | Role | License / Terms |
| --- | --- | --- |
| Python 3.12 | Script runtime | Python Software Foundation License |
| VapourSynth / VSPipe | Script runtime and pipe output | LGPL-2.1 |
| vsrepo | VapourSynth package helper | MIT |
| awsmfunc | VapourSynth helper package | MIT |
| vs-jetpack | VapourSynth helper package | MIT |
| VapourSynth plugins | User-installed processing plugins | Follow each plugin's own license |
| x264 | Video encoder | GPL-2.0-or-later |
| x265 | Video encoder | GPL-2.0 or commercial license, depending on distribution |
| SVT-AV1 | Video encoder | BSD 3-Clause Clear |
| FFmpeg / FFprobe | Media processing and probing tools | LGPL/GPL, depending on build configuration |
| Av1an | Target-quality encoding orchestration | GPL-3.0 |
| Avs2PipeMod | `.avs` input bridge | GPL-3.0 |

FlowEncode's setup guide currently supports automatic installation or updates for Python 3.12, VapourSynth / vsrepo related Python packages, the public VapourSynth plugin bundle, awsmfunc, vs-jetpack, FFmpeg / FFprobe, x264, x265, SVT-AV1, and Av1an. Other command-line tools are detected, pinned, or manually imported only.

## Optional User-Supplied Tools

These tools are optional external binaries. FlowEncode may detect them from configured paths, the local tools folder, or `PATH`, and may allow users to copy a local binary into the app workspace. FlowEncode does not bundle them in the installer, download them automatically, or grant a license to use them.

| Component | Role | License / Terms |
| --- | --- | --- |
| DGDemux | Blu-ray / UHD Blu-ray demux backend | Upstream/distributor terms; user-supplied only |
| eac3to | Blu-ray demux and audio conversion backend | Upstream/distributor terms; user-supplied only |
| deew | Wrapper used by the DDP workflow | MIT |
| Dolby Encoding Engine / DEE | Dolby audio encoding backend used by deew | Proprietary Dolby component; user-supplied and separately licensed |
| Opus Encoder / opusenc | Opus command-line encoder | opus-tools terms; opusenc is covered by the three-clause BSD-style Opus tools license |

## Release Checklist

- Release assets do not bundle the repository `LICENSE` or this `THIRD_PARTY_LICENSES.md` file; these notices are maintained in the public repository.
- README and release notes must clearly distinguish automatically installable public toolchain components from optional user-supplied or manually imported binaries.
- README and release notes must not present locally supplied, private, non-open, unauthorized, proprietary, or license-unclear tools as bundled, auto-installed, publicly licensed, or granted-by-FlowEncode dependencies.
- Release notes must not include UI details, internal implementation details, build commands, validation output, test results, SHA256 values, checksum logs, or internal QA notes.
