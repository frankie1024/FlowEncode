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
    <img alt="Version" src="https://img.shields.io/badge/version-3.1.1-167a7f">
    <img alt="License" src="https://img.shields.io/badge/license-GPL--3.0--only-blue">
  </p>
</div>

---

在 Windows x64 上用 FlowEncode 压制视频、转码音频、提取蓝光或 MKV 轨道，也可以编辑 VapourSynth 脚本并对比预览。常用参数可以保存为模板，多个任务可以加入同一队列。

在 [GitHub Releases](https://github.com/frankie1024/FlowEncode/releases) 下载安装包或便携 ZIP。第一次打开时，按应用内引导配置所需工具。

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
| 视频压制 | 用 `x264`、`x265` 或 `SVT-AV1` 编码，导入 HDR 参数，预览并复制命令。 |
| 自动压制 | 用 `Av1an` 按目标画质压制，质量指标随所用后端而定。 |
| 音频转码 | 转为 FLAC、AC3、DDP、AC4、AAC、TrueHD 或 Opus，也可以只测响度。配合 `deew 5.7` 可将 DTS:X 和 Auro-3D 音轨转为 Atmos 或 AC4 IMS。 |
| 蓝光 / MKV 解复用 | 从蓝光目录或包含 `.mkv` 的目录提取视频、音频、字幕和章节。 |
| VapourSynth | 多标签编辑 `.vpy` 和 `.py` 脚本，支持补全、诊断和测速。预览可并排、卷帘或差值对比，支持自动对齐、自动切边、波形图和批量导出对比帧。 |
| 队列与模板 | 批量管理任务，查看实时进度和日志，复用 `.profile` 编码模板。 |
| 环境与设置 | 检查工具、安装支持的组件、导入已有工具，并设置工作目录、语言和主题。 |

## 常见工作流

- **开始压制或转码**。把视频、音频或脚本拖入主窗口，选择输出位置和参数，再运行或加入队列。
- **提取轨道和章节**。打开蓝光目录可选 `DGDemux` 或 `eac3to`。打开包含 `.mkv` 文件的目录，使用 `eac3to`。
- **检查压制效果**。把两个视频或脚本拖入主窗口开始对比。预览中可用 `F` / `Shift+F` 切换滤镜，或用对比篮批量导出画面。

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

可在应用内安装或更新 `Python`、`VapourSynth`、`vsrepo`、公开插件、`FFmpeg` 和视频编码器。`Av1an` 有兼容安装包时也可直接安装。`Avs2PipeMod`、`DGDemux`、`eac3to`、`deew`、`DEE` 和 `opusenc` 需自行获取，可在设置中导入已有工具。

## 安装与运行要求

### 获取发布包

- **安装包** `FlowEncode_Setup_v<version>.exe`，按向导安装。
- **便携包** `FlowEncode_Portable_v<version>.zip`，解压后打开 `FlowEncode.exe`。
- **校验文件** `FlowEncode_Setup_v<version>.exe.sha256` 和 `SHA256SUMS.txt`，用来核对下载文件的 SHA256。

使用 deew 转码需要 [deew 5.7.0](https://github.com/pcrlnd/deew/releases/tag/5.7.0) 或更新版本，以及用户自备的授权 `DEE ≥6.7.0`。在设置的 deew 卡片中点击 **下载其他组件**，即可安装 `truehdd`、`harletty` 和 `dee_shim`。DTS:X 和 Auro-3D 转码需要 `harletty ≥0.9.0`。升级 deew 时可保留现有 `config.toml` 和 DEE 目录。

如需继续使用 `deew 3.x` / `DEE 5.2.1`，请选择 [2.0.3 兼容维护版](https://github.com/frankie1024/FlowEncode/releases/tag/v2.0.3)。

### 运行前置依赖

需要 x64 版 Windows 10 1809 或更新版本。安装器会检查以下组件，缺少时会提示安装。

| 依赖 | 用途 |
| --- | --- |
| Microsoft Visual C++ Redistributable x64 | 启动桌面应用。 |
| Windows App Runtime x64 | 启动桌面应用。 |
| Microsoft Edge WebView2 Runtime | 打开内置 VapourSynth 编辑器。 |

### 工作目录与系统 PATH

在设置中选择工作目录，用来存放下载文件、工具、编码器和模板。如需在终端直接运行这些工具，进入「设置 → 环境与依赖」，点击「写入系统 PATH」，完成管理员授权后重新打开终端。

## 本地 MCP 自动化

在桌面端设置中开启「本地 MCP 自动化」，即可让外部 AI 客户端查询媒体和工具状态、预览任务并提交到队列。按 [MCP 接入说明](docs/MCP.md) 配置客户端即可在本机使用。

## 本地数据与隐私

源文件和输出文件默认在本机处理。应用数据保存在 `%LocalAppData%\FlowEncode\data`，自定义 `.profile` 模板保存在工作目录的 `Templates` 文件夹。

## 开发与构建

开发者资料见 [仓库脚本](./scripts/) 和 [发布约定](./.github/RELEASING.md)。

## 反馈与许可

- 问题反馈：[GitHub Issues](https://github.com/frankie1024/FlowEncode/issues)
- 版本下载：[GitHub Releases](https://github.com/frankie1024/FlowEncode/releases)
- 项目官网：[GitHub Pages](https://frankie1024.github.io/FlowEncode/)

本仓库源码采用 `GNU General Public License v3.0`（`GPL-3.0-only`），完整文本见 [LICENSE](./LICENSE)。

应用调用或协作的第三方工具、运行时、插件与依赖包各自遵循原许可证，说明见 [THIRD_PARTY_LICENSES.md](./docs/THIRD_PARTY_LICENSES.md)。
