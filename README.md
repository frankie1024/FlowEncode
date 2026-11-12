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
    <img alt="Version" src="https://img.shields.io/badge/version-2.0.1-167a7f">
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
- [AI 调用（MCP 服务）](#ai-调用mcp-服务)
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

在“设置 → 环境与依赖”中点击“写入系统 PATH”，可将已安装和已检测到的编码器、工具及 Python 命令目录一键加入系统 PATH，供所有用户使用。需要时会弹出 Windows 管理员授权。重复点击不会重复添加目录；安装新工具或迁移工作目录后，可再次点击更新，并清理此前由该功能添加的失效目录。原先已有的系统 PATH 项会保留。完成后请重新打开终端或相关应用。

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
| `FlowEncode.Mcp` | 面向 AI 客户端的 MCP 服务（stdio）。 |

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

## AI 调用（MCP 服务）

`FlowEncode.Mcp` 是一个基于 [Model Context Protocol](https://modelcontextprotocol.io/) 的 stdio 服务，让 Claude、Cursor 等 AI 客户端用自然语言操作 FlowEncode：视频编码队列、音频转码、蓝光解复用、目标质量自动压制、VapourSynth 脚本与预览、模板、设置、依赖维护和应用更新。

**统一任务**：MCP 本身不运行任务。编码、音频、蓝光、自动压制任务以及设置修改、依赖安装和应用更新都通过本机命名管道（仅限当前 Windows 用户）交给 FlowEncode 桌面端执行；桌面端未运行时会自动启动。因此 AI 发起的任务会出现在桌面端队列中，与手动添加的任务共用“最大并发编码任务数”和“队列完成后动作”，并且可以在界面上查看和取消；AI 也能查看、取消、重排和删除用户在桌面端添加的任务。探测、命令预览、模板文件和 VapourSynth 诊断/渲染等只读操作由 MCP 进程直接完成，读写的是同一份 `%LOCALAPPDATA%\FlowEncode` 设置和工作区。

### 接入

安装包会把服务放在安装目录的 `mcp` 子目录（默认 `C:\Program Files\FlowEncode\mcp\FlowEncode.Mcp.exe`）。先运行自检，它会确认能找到桌面端，并打印可直接粘贴的客户端配置：

```powershell
& "C:\Program Files\FlowEncode\mcp\FlowEncode.Mcp.exe" --check
```

在 Claude Code 中注册：

```powershell
claude mcp add flowencode -- "C:\Program Files\FlowEncode\mcp\FlowEncode.Mcp.exe"
```

或在 Claude Desktop 等客户端的 `mcpServers` 配置中加入：

```json
{
  "mcpServers": {
    "flowencode": {
      "command": "C:\\Program Files\\FlowEncode\\mcp\\FlowEncode.Mcp.exe"
    }
  }
}
```

从源码构建时运行 `dotnet publish FlowEncode/FlowEncode.Mcp -c Release --self-contained -o artifacts/mcp`；此时服务找不到相邻的 `FlowEncode.exe`，请设置环境变量 `FLOWENCODE_DESKTOP_EXE` 指向桌面端可执行文件。

### 工具

| 范围 | 工具 |
| --- | --- |
| 应用与设置 | `get_app_status`、`launch_desktop_app`、`get_settings`、`update_settings`（编码器偏好、主题、语言、工作区、并发数、队列完成后动作）、`check_app_update`、`install_app_update` |
| 视频编码队列 | `preview_encoding_command`、`start_encoding_job`、`get_encoding_job`、`list_encoding_jobs`、`cancel_encoding_job`、`start_queued_encoding_job`、`restart_encoding_job`、`remove_encoding_job`、`move_encoding_job` |
| 音频 | `probe_audio_source`、`preview_audio_command`、`start_audio_task`（DDP / Opus / FLAC / AC3） |
| 蓝光 | `scan_bluray_disc`、`preview_bluray_demux_command`、`start_bluray_demux`（按轨道导出视频、音频、字幕、章节） |
| 目标质量自动压制 | `preview_auto_compression_command`、`start_auto_compression`（VMAF / SSIMULACRA2 / Butteraugli / XPSNR，探测次数、采样率、插值、统计方式） |
| 模块任务 | `get_task_status`、`cancel_task`（audio / bluray / autocompression） |
| VapourSynth | `vapoursynth_new_script`、`vapoursynth_read_script`、`vapoursynth_save_script`、`vapoursynth_diagnose_script`、`vapoursynth_list_functions`、`vapoursynth_get_outputs`、`vapoursynth_render_frame`（PNG 截图、帧属性、按时间码定位、裁剪预览与 `CropAbs/CropRel` 命令）、`vapoursynth_compare_frames`、`read_chapters`、`write_chapters` |
| 模板 | `list_templates`、`get_default_profile`、`save_template`、`set_template_pinned`、`delete_template`、`import_template`、`export_template` |
| 环境与依赖 | `get_environment_status`、`list_encoders`、`probe_video_source`、`list_dependencies`、`manage_dependency`（安装/更新、卸载、导入、手动固定、取消固定）、`refresh_environment`、`get_operation`、`list_operations` |

说明：

- 所有路径都必须是绝对路径；默认不覆盖已存在的输出文件（可传 `overwrite: true`）。同一输出路径已有排队或运行中的任务时会被拒绝，而不是像界面那样自动改名。
- 音频、蓝光、自动压制模块与界面一样，每个模块同一时间只运行一个任务。
- 依赖安装和应用更新耗时较长，会返回 `operationId`，用 `get_operation` 查询结果。应用更新会在没有任务运行时启动安装程序并关闭桌面端，安装程序同时会替换 MCP 服务，客户端需要重新连接。
- 关闭 MCP 客户端不会中止已提交给桌面端的任务。

## 反馈与许可

- 问题反馈：[GitHub Issues](https://github.com/frankie1024/FlowEncode/issues)
- 版本下载：[GitHub Releases](https://github.com/frankie1024/FlowEncode/releases)
- 项目官网：[GitHub Pages](https://frankie1024.github.io/FlowEncode/)

本仓库源码采用 `GNU General Public License v3.0`，按 `GPL-3.0-only` 处理。完整许可证文本见 [LICENSE](./LICENSE)。

程序调用或协作的第三方工具、运行时、插件与包分别遵循其各自许可证条款。公开支持范围与许可证说明见 [THIRD_PARTY_LICENSES.md](./docs/THIRD_PARTY_LICENSES.md)。
