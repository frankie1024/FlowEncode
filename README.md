<div align="center">
  <img src="./docs/assets/flowencode-hammer.png" alt="FlowEncode" width="96" height="96">

  <h1>FlowEncode</h1>

  <p><strong>面向 Windows x64 的视频压制、转码与 VapourSynth 工作流前端</strong></p>

  <p>
    <a href="./README.en.md">English</a> ·
    <a href="https://frankie1024.github.io/FlowEncode/">官网</a> ·
    <a href="https://github.com/frankie1024/FlowEncode/releases">下载</a> ·
    <a href="https://github.com/frankie1024/FlowEncode/issues">问题反馈</a>
  </p>

  <p>
    <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%20x64-0078d4">
    <img alt="Version" src="https://img.shields.io/badge/version-1.9.8-167a7f">
    <img alt="Framework" src="https://img.shields.io/badge/.NET-8.0-512bd4">
    <img alt="UI" src="https://img.shields.io/badge/UI-WinUI%203-0d6efd">
    <img alt="License" src="https://img.shields.io/badge/license-GPL--3.0--only-blue">
  </p>
</div>

---

FlowEncode 是一个面向 Windows x64 的桌面端编码工作流前端，围绕 `x264`、`x265`、`SVT-AV1`、`Av1an`、`FFmpeg`、`VapourSynth`、`VSPipe` 与相关公开工具链，提供蓝光解复用、脚本编辑与预览、视频压制、音频转码、自动压制、队列执行、日志查看、模板复用，以及面向视频、音频和蓝光目录的拖放入口。

它适合需要图形化管理日常压制流程，同时仍希望保留命令行编码器、VapourSynth 脚本和 Av1an 自动压制灵活性的用户。

> [!IMPORTANT]
> FlowEncode 当前仅支持 Windows x64。首次使用前建议先完成应用内的首启环境引导。

> [!NOTE]
> 透明说明：本项目大量使用 AI 辅助开发。实际功能、依赖边界与已知限制以源码、发布包和本文档描述为准。

![FlowEncode dashboard](./docs/assets/flowencode-dashboard.png)

## 目录

- [核心定位](#核心定位)
- [功能概览](#功能概览)
- [工作流](#工作流)
- [支持的工具链](#支持的工具链)
- [安装与运行要求](#安装与运行要求)
- [本地数据与隐私](#本地数据与隐私)
- [开发与构建](#开发与构建)
- [反馈与许可](#反馈与许可)

## 核心定位

FlowEncode 不是单一编码器，也不是封装所有依赖的全家桶发行版。它更接近一个面向视频压制场景的工作流控制台：

| 目标 | 说明 |
| --- | --- |
| 统一配置 | 将输入输出、编码器参数、脚本环境、任务队列与日志集中到一个桌面界面。 |
| 保留灵活性 | 不替代 `x264`、`x265`、`SVT-AV1`、`Av1an`、`FFmpeg` 或 `VapourSynth`，而是协调它们参与工作流。 |
| 降低重复劳动 | 通过模板库、命令预览、队列执行和环境引导减少日常压制中的重复配置。 |
| 面向本地使用 | 默认围绕本机文件、本机依赖和本机工作目录运行，不主动上传源文件或模板。 |

## 功能概览

| 模块 | 能力 |
| --- | --- |
| 工作台 | 按流程进入解复用、VapourSynth、视频压制、音频转码、自动压制、模板库和设置，并显示活跃任务状态；支持将视频、音频文件和蓝光目录直接拖放到对应流程。 |
| 蓝光解复用 | 扫描蓝光目录，调用已检测或导入的 `DGDemux` / `eac3to` 选择播放列表与轨道，导出视频、音频和字幕。 |
| VapourSynth | 提供 `.vpy` / `.py` 脚本编辑、拖放打开、标签页、左右对比、基础诊断、预览、帧属性、截图、章节、裁剪辅助和预览日志。 |
| 视频压制 | 配置 `x264`、`x265`、`SVT-AV1` 常规编码任务，统一管理输入输出、HDR 参数导入、命令预览复制、任务队列、批量操作、键盘快捷键和实时日志。 |
| 音频转码 | 独立处理 `eac3to`、`deew`、`FFmpeg` / `opusenc` 音频流程，支持 FLAC、AC3、DDP、Opus 等输出路径；DDP 路径需要用户自行提供已授权的 `Dolby Encoding Engine` / `DEE`。 |
| 自动压制 | 基于 `Av1an` 的结构化后端组织目标质量流程，按后端能力探测可用指标与编码器，支持探测参数、后端附加参数、执行计划和分阶段实时日志。 |
| 模板库 | 保存、载入、导入、导出、置顶和覆盖 `.profile` 模板，沉淀常用参数集。 |
| 环境引导 | 按分组展示可折叠依赖卡片，检测本地与远端状态；可引导安装或更新公开支持的运行时、插件、编码器、`FFmpeg` 与 `Av1an`，并允许手动导入、固定或移除部分解复用器和音频工具的本地副本。 |
| 设置与更新 | 管理工作目录、主题、语言、依赖状态、应用更新、队列完成后动作和首启引导入口。 |

## 工作流

```mermaid
flowchart LR
  A["准备源文件或蓝光目录"] --> B["解复用 / 编写或载入 VapourSynth 脚本"]
  B --> C["选择视频、音频或自动压制流程"]
  C --> D["选择编码器、模板与参数"]
  D --> E["预览并复制实际命令"]
  E --> F["加入队列或直接执行"]
  F --> G["查看进度、日志与输出结果"]
```

常见使用方式：

- 扫描蓝光目录，选择播放列表与轨道后用已准备好的 `DGDemux` 或 `eac3to` 解复用。
- 将视频、音频文件或蓝光目录直接拖放到主窗口，快速进入对应工作流。
- 使用 VapourSynth 编辑器维护 `.vpy` / `.py` 脚本，通过预览窗口检查输出帧、帧属性、章节和裁剪参数。
- 拖放脚本到主窗口打开，或使用 Windows 资源管理器的新建菜单创建空 `.vpy` 脚本。
- 使用 `x264`、`x265` 或 `SVT-AV1` 配置常规编码任务，并复制实际命令用于复查或调试。
- 独立处理 FLAC、AC3、DDP、Opus 等音频转码任务；DDP 输出依赖用户自行准备的授权 `Dolby Encoding Engine` / `DEE`。
- 使用 `Av1an` 进行目标质量自动压制，并根据当前后端能力选择指标、探测参数与执行计划。
- 批量启动、取消、删除和重排队列任务，配合键盘快捷键快速处理队列，并可在队列成功完成且系统空闲后自动睡眠或关机。
- 将稳定参数保存为 `.profile` 模板，在后续任务中快速复用。

## 支持的工具链

| 类型 | 组件 |
| --- | --- |
| 视频编码器 | `x264`、`x265`、`SVT-AV1` |
| 自动压制 | `Av1an`、`VMAF` |
| 质量指标 | `VMAF`、`SSIMULACRA2`、`XPSNR` / `XPSNR-Weighted`、`Butteraugli-INF` / `Butteraugli-3`（取决于当前 `Av1an` 后端能力） |
| 蓝光解复用 | `DGDemux`、`eac3to` |
| 音频处理 | `eac3to`、`deew`、`opusenc` / `Opus Encoder`、`FFmpeg`；DDP 输出需要用户自行提供已授权的 `Dolby Encoding Engine` / `DEE` |
| 媒体探测与管线 | `FFmpeg`、`FFprobe` |
| 脚本环境 | `Python 3.12`、`VapourSynth`、`vsrepo`、公开可用的 VapourSynth 插件 |
| 输入桥接 | `VSPipe`、`Avs2PipeMod` |
| 应用框架 | `.NET 8`、`WinUI 3`、`Windows App SDK` |

对具体工具的支持深度取决于上游版本、本机运行时状态、插件完整性和用户环境配置。FlowEncode 会尽量提供检测和引导，但不替代上游项目自身的安装说明和许可证。

其中，应用内自动安装或更新主要覆盖 `Python 3.12`、`VapourSynth` / `vsrepo` 及公开插件包、`awsmfunc`、`vsjetpack`、`FFmpeg`、`x264`、`x265`、`SVT-AV1`，以及在托管兼容包可用时的 `Av1an`。`Avs2PipeMod`、`DGDemux`、`eac3to`、`deew`、`Dolby Encoding Engine` / `DEE`、`opusenc` / `Opus Encoder` 属于用户自行获取并遵守各自许可证的外部工具；FlowEncode 只负责检测、手动导入、固定路径和调用，不随安装包分发，也不授予这些工具的使用许可。

## 安装与运行要求

### 获取安装包

前往 [GitHub Releases](https://github.com/frankie1024/FlowEncode/releases) 下载最新的 Windows x64 安装包。

当前发布资产以安装版为主：

```text
FlowEncode_Setup_v<version>.exe
```

### 运行前置依赖

安装器会检测以下组件：

| 依赖 | 作用 |
| --- | --- |
| Microsoft Visual C++ Redistributable x64 | WinUI 桌面应用运行依赖。 |
| Windows App Runtime x64 | 未打包 Windows App SDK 应用运行依赖。 |
| Microsoft Edge WebView2 Runtime | 内置 VapourSynth 编辑器界面依赖。 |

如果缺失，安装器会提示安装。用户可以跳过并继续安装主程序，但这可能导致应用无法启动或部分功能不可用。

### 工作目录

FlowEncode 支持单独配置工作目录，用于承载：

- `downloads`
- `tools`
- `encoders`
- `Templates`

程序安装目录默认只放程序本体和静态资源。运行期可写数据、大体积工具链和用户模板会放在应用数据目录或用户指定的工作目录中。

## 本地数据与隐私

FlowEncode 默认面向本地工作流：

- 不主动上传源文件、输出文件、模板内容或本地路径。
- 运行期轻量状态保存在 `%LocalAppData%\FlowEncode\data`。
- 当前用户的 `.vpy` 新建菜单模板保存在 `%LocalAppData%\FlowEncode\data\Templates`。
- 用户模板保存在工作目录的 `Templates` 文件夹，并自动加载其中的 `.profile` 文件。
- `downloads`、`tools`、`encoders` 等目录位于独立工作目录，可在设置中调整。
- 对外分享日志、截图或缓存文件前，建议先人工检查并脱敏。

## 开发与构建

项目主体是 WinUI 3 桌面应用：

| 项目 | 说明 |
| --- | --- |
| `FlowEncode` | WinUI 3 桌面入口与界面层。 |
| `FlowEncode.Application` | 应用服务抽象。 |
| `FlowEncode.Domain` | 编码配置、任务模型和领域规则。 |
| `FlowEncode.Infrastructure` | 本地工具探测、运行器、缓存和外部集成。 |
| `FlowEncode.Domain.Tests` | 领域层测试。 |

构建注意事项：

- 目标框架：`net8.0-windows10.0.26100.0`
- 最低目标平台：`Windows 10.0.17763.0`
- 平台：`x64`
- UI：`WinUI 3` / `Windows App SDK`
- 发布脚本位于 `scripts/`
- 安装器脚本位于 `installer/`

官方构建入口：

```powershell
./scripts/build.ps1
./scripts/test.ps1
```

常用命令：

```powershell
./scripts/build.ps1 -Configuration Release
./scripts/build.ps1 -Configuration Release -RunTests
./scripts/build-release-assets.ps1
```

## 反馈与许可

- 问题反馈：[GitHub Issues](https://github.com/frankie1024/FlowEncode/issues)
- 版本下载：[GitHub Releases](https://github.com/frankie1024/FlowEncode/releases)
- 项目官网：[GitHub Pages](https://frankie1024.github.io/FlowEncode/)

本仓库源码采用 `GNU General Public License v3.0`，按 `GPL-3.0-only` 处理。完整许可证文本见 [LICENSE](./LICENSE)。

程序调用或协作的第三方工具、运行时、插件与包分别遵循其各自许可证条款。公开支持范围与许可证说明见 [THIRD_PARTY_LICENSES.md](./docs/THIRD_PARTY_LICENSES.md)。
