<div align="center">
  <img src="./docs/assets/flowencode-hammer.png" alt="FlowEncode" width="96" height="96">

  <h1>FlowEncode</h1>

  <p><strong>面向 Windows x64 的视频压制、音频转码与 VapourSynth 桌面前端</strong></p>

  <p>
    <a href="./README.en.md">English</a> ·
    <a href="https://frankie1024.github.io/FlowEncode/">官网</a> ·
    <a href="https://github.com/frankie1024/FlowEncode/releases">下载</a> ·
    <a href="https://github.com/frankie1024/FlowEncode/issues">问题反馈</a>
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

FlowEncode 是 Windows x64 桌面前端，用来组织和调用 `x264`、`x265`、`SVT-AV1`、`Av1an`、`FFmpeg`、`VapourSynth`、`VSPipe` 等外部工具，完成蓝光与 MKV 解复用、脚本编辑与预览、常规视频压制、音频转码和目标质量自动压制。

它本身不是编码器，安装包也不附带全部外部工具；通过统一的参数界面、命令预览、模板库、任务队列和本地 MCP 服务，减少日常压制中的重复配置。

> [!IMPORTANT]
> FlowEncode 仅支持 Windows x64。首次使用建议先完成应用内的首启环境引导。如需继续使用旧版 `deew 3.x` / `DEE 5.2.1`，请选择 [2.0.3 兼容维护版](https://github.com/frankie1024/FlowEncode/releases/tag/v2.0.3)。

> [!NOTE]
> 本项目包含大量 AI 辅助编写的代码。实际功能、依赖要求与使用限制以源码、发布包和文档为准。

![FlowEncode dashboard](./docs/assets/flowencode-dashboard.png)

## 目录

- [功能概览](#功能概览)
- [常见工作流](#常见工作流)
- [支持的工具链](#支持的工具链)
- [安装与运行要求](#安装与运行要求)
- [本地 MCP 自动化](#本地-mcp-自动化)
- [本地数据与隐私](#本地数据与隐私)
- [开发与构建](#开发与构建)
- [反馈与许可](#反馈与许可)

## 功能概览

| 模块 | 说明 |
| --- | --- |
| 工作台 | 集中进入各功能页并查看活跃任务；支持把视频、音频、脚本或蓝光目录直接拖入主窗口进入对应流程。 |
| 蓝光 / MKV 解复用 | 扫描蓝光目录（`DGDemux` / `eac3to`）或包含 `.mkv` 的目录（`eac3to`），选择播放列表与轨道，导出视频、音频、字幕和章节。 |
| VapourSynth | 内置 `.vpy` / `.py` 编辑器，支持拖放打开、多标签、左右对比、链式补全、静态诊断与快速修复、智能滤镜插入及脚本测速；独立预览窗口提供多路对比（卷帘/并排/差值/热力图/闪烁）、自动帧对齐与亚像素对齐、自动切边、最差帧扫描、直方图/波形图/矢量示波器、像素吸管、对比篮导出与 9 种预览滤镜（原图、亮度、色度、`Solar ×1`、`Solar ×2`、残差、暗场提亮、范围越界、边缘高亮，`F` / `Shift+F` 切换）。 |
| 视频压制 | 配置 `x264`、`x265`、`SVT-AV1` 常规编码任务，支持输入输出管理、HDR 参数导入、实际命令预览复制、任务队列、批量操作、快捷键与实时日志。 |
| 音频转码 | 调用 `eac3to`、`deew`、`FFmpeg` / `opusenc` 处理音频，支持 FLAC、AC3、DDP、AC4 / `ac4-ims`、AAC、TrueHD、Opus 等输出，支持自定义输出命名与模板、管道模式和响度测量。使用 `deew` 的任务需要 `deew 5.7.0` 或更新版本及用户自备的授权 `Dolby Encoding Engine` / `DEE ≥6.7.0`。 |
| 自动压制 | 调用 `Av1an` 按目标质量自动压制，根据当前后端能力检测可用质量指标与编码器，支持探测参数、后端附加参数、执行计划与分阶段日志。 |
| 模板库 | 保存、载入、导入、导出、置顶和覆盖 `.profile` 模板，复用常用编码参数。 |
| 环境引导与设置 | 检测本地与远端依赖状态，引导安装或更新公开工具链，支持手动导入、固定或移除部分外部工具；管理工作目录、主题、语言、系统 `PATH`、应用更新和队列完成后动作。 |

## 常见工作流

```mermaid
flowchart LR
  A["准备源文件、MKV 或蓝光目录"] --> B["解复用 / 编写并预览 VapourSynth 脚本"]
  B --> C["进入视频、音频或自动压制流程"]
  C --> D["选择编码器、模板与参数"]
  D --> E["预览并复制实际命令"]
  E --> F["加入队列或直接运行"]
  F --> G["查看进度、日志与输出文件"]
```

- 将视频、音频、脚本文件或蓝光目录直接拖入主窗口，快速进入对应模块。
- 使用 `DGDemux`（蓝光目录）或 `eac3to`（蓝光目录与 MKV 目录）扫描并提取轨道和章节。
- 在 VapourSynth 编辑器中维护 `.vpy` / `.py` 脚本，通过预览窗口检查输出帧、帧属性、预览滤镜、章节和裁剪参数；也可在 Windows 资源管理器右键新建空白 `.vpy` 脚本。
- 使用 `x264`、`x265`、`SVT-AV1` 配置常规视频压制，或使用 `Av1an` 按目标质量自动压制，并在执行前核对或复制实际命令。
- 批量启动、取消、删除和重排队列任务，必要时设置队列完成且系统空闲后自动睡眠或关机。
- 将常用参数保存为 `.profile` 模板，在后续任务中直接载入。

## 支持的工具链

| 类型 | 组件 |
| --- | --- |
| 视频编码器 | `x264`、`x265`、`SVT-AV1` |
| 自动压制 | `Av1an`、`VMAF` |
| 质量指标 | `VMAF`、`SSIMULACRA2`、`XPSNR` / `XPSNR-Weighted`、`Butteraugli-INF` / `Butteraugli-3`（取决于当前 `Av1an` 后端能力） |
| 蓝光 / MKV 解复用 | `DGDemux`、`eac3to` |
| 音频处理 | `eac3to`、`deew`、`opusenc` / `Opus Encoder`、`FFmpeg`；DDP、AC4、TrueHD、DEE AAC 等任务需要用户自备授权 `Dolby Encoding Engine` / `DEE` |
| 媒体探测与管线 | `FFmpeg`、`FFprobe` |
| 脚本环境 | `Python 3.14`（新安装）/ `Python 3.12`（保留已有环境）、`VapourSynth R79`、`vsrepo`、公开 VapourSynth 插件 |
| 输入桥接 | `VSPipe`、`Avs2PipeMod` |
| 应用框架 | `.NET 8`、`WinUI 3`、`Windows App SDK` |

应用内自动安装和更新覆盖 `Python 3.14`、`VapourSynth` / `vsrepo` 及公开插件包、`awsmfunc`、`vsjetpack`、`FFmpeg`、`x264`、`x265`、`SVT-AV1`，以及托管兼容包可用时的 `Av1an`（已有可用的 `Python 3.12` 环境可继续保留）。

`Avs2PipeMod`、`DGDemux`、`eac3to`、`deew`、`Dolby Encoding Engine` / `DEE`、`opusenc` / `Opus Encoder` 属于用户自行获取并遵守各自许可证的外部工具。FlowEncode 仅提供检测、手动导入、固定路径和调用，不随安装包分发，也不授予这些工具的使用许可。

## 安装与运行要求

### 获取发布包

前往 [GitHub Releases](https://github.com/frankie1024/FlowEncode/releases) 下载最新的 Windows x64 正式版。每次正式发布提供以下文件：

```text
FlowEncode_Setup_v<version>.exe          # Windows x64 安装包
FlowEncode_Setup_v<version>.exe.sha256   # 安装包校验文件
FlowEncode_Portable_v<version>.zip       # 便携 ZIP
SHA256SUMS.txt                           # 总 SHA256 校验清单
```

> **deew 音频工具要求**。使用 `deew` 的音频任务需要 [deew 5.7.0](https://github.com/pcrlnd/deew/releases/tag/5.7.0) 或更新版本，以及授权 `DEE ≥6.7.0`。可在设置的 deew 卡片中点击 **下载其他组件**，将 `truehdd`、`harletty` 和 `dee_shim` 安装到 `deew.exe` 所在目录。DTS:X 和 Auro-3D 输入可用于 `ddp-atmos`、`thd-atmos`、`ac4-atmos` 和 `ac4-ims`，需要 `harletty ≥0.9.0`。升级时可保留原有 `config.toml` 和 DEE 目录。如需继续使用 `deew 3.x` / `DEE 5.2.1`，请使用 [2.0.3 兼容维护版](https://github.com/frankie1024/FlowEncode/releases/tag/v2.0.3)。

### 运行前置依赖

安装器会自动检测以下组件；如有缺失会提示安装。跳过安装仍可完成主程序部署，但可能导致应用无法启动或部分功能不可用：

| 依赖 | 用途 |
| --- | --- |
| Microsoft Visual C++ Redistributable x64 | WinUI 桌面应用运行依赖。 |
| Windows App Runtime x64 | 未打包 Windows App SDK 应用运行依赖。 |
| Microsoft Edge WebView2 Runtime | 内置 VapourSynth 编辑器运行依赖。 |

### 工作目录与系统 PATH

FlowEncode 支持自定义工作目录，集中存放体积较大的依赖与模板：

- `downloads`
- `tools`
- `encoders`
- `Templates`

在“设置 → 环境与依赖”中点击“写入系统 PATH”，可将已安装和已检测到的编码器、工具与 Python 命令目录写入系统 `PATH`（需要管理员授权）。重复点击不会写入重复项；新增工具或迁移工作目录后再次点击会同步更新并清理此前由该功能写入的失效目录，原有系统 `PATH` 项会保留。完成后重新打开终端或相关程序即可生效。

## 本地 MCP 自动化

安装包与便携包都包含本地 stdio MCP 服务 `mcp/FlowEncode.Mcp.exe`，可接入外部 AI 客户端查询环境与媒体信息、生成执行计划并提交到桌面端队列。

- 该功能默认关闭，需在桌面端设置中开启“本地 MCP 自动化”后才接受调用。
- 任务、并发上限与输出占用由桌面端统一管理，仅通过当前 Windows 用户会话的本地命名管道通信，不开放网络端口。

接入配置与可用工具列表见 [MCP 接入说明](docs/MCP.md)。

## 本地数据与隐私

FlowEncode 默认全部在本地运行：

- 不主动上传源文件、输出文件、模板内容或本机路径。
- 运行期轻量状态保存在 `%LocalAppData%\FlowEncode\data`。
- 安装版右键新建 `.vpy` 菜单使用的空白模板位于安装目录的 `Assets\VapourSynthScript.vpy`。
- 用户模板保存在工作目录的 `Templates` 文件夹，自动加载其中的 `.profile` 文件。
- `downloads`、`tools`、`encoders` 位于工作目录，可在设置中随时更改位置。
- 对外分享日志、截图或缓存文件前，建议先人工检查并脱敏。

## 开发与构建

解决方案结构：

| 项目 | 说明 |
| --- | --- |
| `FlowEncode` | WinUI 3 桌面入口与界面层。 |
| `FlowEncode.Application` | 应用服务抽象与共享任务协调。 |
| `FlowEncode.Domain` | 编码配置、任务模型与领域规则。 |
| `FlowEncode.Infrastructure` | 工具探测、运行器、缓存与外部进程集成。 |
| `FlowEncode.Mcp` | 本地 stdio MCP 服务。 |
| `FlowEncode.Domain.Tests` | 单元测试。 |

环境与构建要点：

- 目标框架：`net8.0-windows10.0.26100.0`（最低目标平台：`Windows 10.0.17763.0`）
- 平台：`x64`
- UI 框架：`WinUI 3` / `Windows App SDK`
- 构建与打包脚本位于 `scripts/`，安装器脚本位于 `installer/`

常用命令：

```powershell
./scripts/build.ps1 -Configuration Release
./scripts/test.ps1 -Configuration Release
./scripts/build.ps1 -Configuration Release -RunTests
./scripts/build-release-assets.ps1
```

## 反馈与许可

- 问题反馈：[GitHub Issues](https://github.com/frankie1024/FlowEncode/issues)
- 版本下载：[GitHub Releases](https://github.com/frankie1024/FlowEncode/releases)
- 项目官网：[GitHub Pages](https://frankie1024.github.io/FlowEncode/)

本仓库源码采用 `GNU General Public License v3.0`（`GPL-3.0-only`），完整文本见 [LICENSE](./LICENSE)。

应用调用或协作的第三方工具、运行时、插件与依赖包各自遵循原许可证，说明见 [THIRD_PARTY_LICENSES.md](./docs/THIRD_PARTY_LICENSES.md)。
