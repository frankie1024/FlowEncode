# FlowEncode 3.0 工具链兼容性审计

检查日期：2026-11-12（Asia/Shanghai）。源码基线：`a5aa34bab66e08412852acec817a761159232d5b`，2.0.2。

本文作为 [3.0 路线图](ROADMAP-3.0.md) 的补充。**后续已按用户要求实施运行时修复、固定基线和 2.0 升级保护；最终策略与验证见第 8 节。第 1–7 节保留最初审计证据，描述的是修复前的状态。** 测试工具隔离在仓库 `artifacts/compatibility-audit/`，没有替换用户已安装的工具。

## 1. 结论与优先级

FFmpeg 已进入 9.0 系列。FlowEncode 当前使用的主要 FFmpeg/FFprobe 参数，在本次测试的 8.1、9.0 分支构建上仍可执行，未发现需要全面重写命令行的变化。但目前不能据此认定 3.0 工具链已通过发布验收。

| 级别 | 已确认的问题 | 3.0 必须采取的动作 |
| --- | --- | --- |
| P1 | FFmpeg 自动更新实际上选择 master 每日构建，升级没有稳定分支边界 | 建立推荐版本清单，明确 release/master 通道；默认只采用经过回归的分支与包摘要 |
| P1 | 当前托管 x265 4.3 包仅支持 8 位输出；显式要求 10 位仍退回 8 位，退出码为 0 | 修复发布源的 multilib/10-bit 构建；安装及执行前验证位深能力，产物不符合计划时不得判成功 |
| P1 | SVT-AV1 QP 模式会生成 `--qp 32.0`，4.2.0、4.3.0 均直接报错 | 按已安装版本和模式生成合法数值；同步修改实际执行、模板预览和校验 |
| P1 | 只把 QP 改成整数仍不能保证 QP 语义：`--rc 0 --qp 32` 实际使用 CRF 默认 AQ | 旧参数路径显式使用 `--aq-mode 0`；支持 `--cqp` 的版本可走新版路径，并校验范围和步进 |
| P1 | SVT IQ tune 及 high/professional profile 可选，但部分常规视频组合会失败 | 按预测结构、色度、位深和版本约束组合；在提交前说明不可用原因 |
| P1 | “能输出视频”与“保留输入位深”没有统一契约；本机 x264 默认也把 10 位输入编码为 8 位 | 将目标位深、色度和允许的转换写入执行计划，并探测实际产物 |
| P2 | FFmpeg/FFprobe 就绪与安装验证主要检查 `-version`，没有检查具体组件和参数；两个工具也可被分别固定到不同目录 | 增加按工作流的能力检测，记录实际路径/构建身份，并报告版本配对状态 |

上表前六项均有源码或实际 CLI 证据，不是仅根据大版本号推测。当前的“参数冲突校验”主要检查重复值和相反开关，不能替代编码器自身的版本、范围和组合校验。

## 2. 当前版本与变化

| 组件 | 上游/发布源核对 | 本机原有工具 | 与 FlowEncode 有关的影响 |
| --- | --- | --- | --- |
| FFmpeg | 官方稳定版 9.0.2；8.1 分支为 8.1.3 | `N-126175-g0056dd32fd-20260816` | 自动更新来源同时提供 master 和 release 构建，不能把 GitHub latest 当作稳定 FFmpeg 版本 |
| x264 | 项目托管源 `stable-20250608-b35605ace3dd` | `0.165.3222 b35605a` | 基础 CLI 和两遍编码可用；默认输出位深仍需显式约束 |
| x265 | 项目托管源 4.3 | 4.3，8bit 构建 | 当前包缺少可用的 10 位编码实现，属于构建能力问题 |
| SVT-AV1 | 官方及项目托管源均为 4.3.0 | 4.2.0 | 4.x 有库 API 变化，但 FlowEncode 调用独立 EncApp；更直接的风险是 CLI 数值及模式组合 |
| VapourSynth | R82 | 本次未升级或做渲染回归 | R74 起插件装载和色彩范围属性变化；R80 移除 API3，旧插件需要检查 |
| Opus | libopus 1.6.1 | opusenc 使用 libopus 1.3 | 编码库版本与 opus-tools 版本需分别记录；本次已有 opusenc 基础管线可执行 |
| deew / Av1an | deew 5.x 迁移见主路线图；Av1an 使用项目协议兼容包 | deew 3.2.2；Av1an 0.5.2-unstable | 本机旧 deew 不能作为 3.0 新 DEE 工作流验收环境；Av1an 继续以协议/能力探测决定支持 |

版本来源：[FFmpeg 下载页](https://ffmpeg.org/download.html)、[x264 托管发布](https://github.com/frankie1024/x264-windows-builds/releases/tag/stable-20250608-b35605ace3dd)、[x265 托管发布](https://github.com/frankie1024/x265-windows-builds/releases/tag/4.3)、[SVT-AV1 托管发布](https://github.com/frankie1024/svt-av1-windows-builds/releases/tag/v4.3.0)、[Opus 官方新闻](https://opus-codec.org/news/)。托管源版本经 GitHub API 直接核对。

### 2.1 FFmpeg 变化不能直接等同于当前应用故障

FFmpeg 9.0 删除了过时 NVENC 选项及对较旧 SDK 的支持；7.0 已删除 `-map_channel`、`-psnr`。当前常规视频路径调用独立 x264/x265/SVT-AV1，仓库相关命令没有使用这些已删除的选项，所以这些变更不构成本次发现的直接故障。未来引入硬件编码或导入外部 FFmpeg 模板时，须另建能力与迁移规则。

当前源码保留的 `-strict -1 -f yuv4mpegpipe`，以及音频的 `-progress`、`-stats_period`、`-mapping_family 1` 等，在本次两个 FFmpeg 分支上仍可执行。不要因为版本号更新就删除 high-bit-depth Y4M 所需的兼容选项。

新管线如果需要控制同步，应使用明确的 `-fps_mode` 策略，并检测 VFR 输入。Y4M 不携带逐帧可变时间戳；单纯加 passthrough 不能保证 VFR 经独立编码器后时长正确。这一项本次没有使用 VFR 样本验证，应保留为发布矩阵项目。

依据：[FFmpeg Changelog](https://github.com/FFmpeg/FFmpeg/blob/master/Changelog)、[FFmpeg 同步模式文档](https://ffmpeg.org/ffmpeg.html#Advanced-options)。FlowEncode 主要通过进程调用 FFmpeg，本次没有发现直接绑定 libav* ABI 的调用；库 API 变化与 CLI 兼容性应分开评估。

## 3. FFmpeg 自动更新与能力校验

源码位置：`LocalExternalToolService.GetFfmpegPackageAsync`、`ValidateStagedToolDirectory`、`ProcessToolProbeService.ProbeWithProcessVersion`、`ExternalToolLocator.ResolveFfmpeg/ResolveFfprobe`。

当前选择逻辑为：

1. 请求 `BtbN/FFmpeg-Builds/releases/latest`。
2. 筛选名称包含 `win64` 和 `shared` 的 ZIP，优先 `gpl-shared`。
3. 按名称排序取第一个，没有限定 `n8.1` 或 `n9.0`。

对 2026-11-11 发布的真实资产列表重放该逻辑，结果是 **`ffmpeg-master-latest-win64-gpl-shared.zip`**。同一个发布实际上还包含 8.1、9.0 分支。BtbN 的 latest 是浮动每日构建集合，release 分支构建也会随分支提交移动，不能当作不可变的点版本安装包。

来源：[BtbN 构建规则](https://github.com/BtbN/FFmpeg-Builds/blob/master/README.md)、[实际构建发布集合](https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/tag/latest)。

建议分两步处理：

- **进入 3.0 开发基线时：** 明确默认 release 分支，不允许包名排序造成隐式切换。master 作为可见的测试通道。清单保存来源、包名、构建标识、SHA256 和验证日期；不能只写“latest”。
- **发布前：** 固定回归通过的推荐包。继续使用现有暂存验证和替换机制，但把验证扩展到实际能力；能力缺失时保留原有可用版本。需要可回退的已验证版本，不能只保留最近一次下载。

能力检查应服务于工作流，避免让“缺少某个可选组件”导致所有 FFmpeg 功能都不可用：

| 工作流 | 最小检测内容 |
| --- | --- |
| 视频输入桥接 | 所需解码器、Y4M muxer、输入像素格式、实际管线短样本 |
| Opus 多声道 | `libopus`、`mapping_family`、声道映射过滤器，以及目标声道输出探测 |
| ffmpeg → opusenc | PCM/WAV 管线、`--ignorelength`，实际二进制管道 |
| DTS:X → DDP 准备 | DCA decoder 的输入 `-downmix` 能力；真实 DTS:X 样本的声道布局 |
| 媒体探测 | 实际使用的 JSON 字段、缺失值处理、ffmpeg/ffprobe 解析到的完整路径与版本 |
| Av1an | 后端协议、质量指标及其所用编码器/FFmpeg 路径，不能只依赖 FlowEncode 主进程的 PATH |

目前安装验证会运行 ffmpeg 和 ffprobe 的 `-version`，但不核对二者是否同一构建；运行时也允许各自手工指定路径。3.0 应优先提供同包工具，并对外部独立配置给出可核对的配对状态。

## 4. 编码器问题的复现与修复要求

### 4.1 x265：当前托管包不能完成 10 位输出

当前本机 x265.exe 与本次新下载的项目 4.3 发布包内 x265.exe 的 SHA256 完全一致：

```text
025289256214b39818f5b149b2c5d16f8143d8c773dac3b65f4d8e674cc8f78d
```

包内 `BUILD_INFO.txt` 显示只执行普通 `ENABLE_CLI=ON`、`ENABLE_SHARED=OFF` 构建，没有 multilib/高位深合并步骤。本机输出也明确标识 `8bit`。

使用真实 10 位 Y4M 输入测试：

```text
x265 --preset ultrafast --output-depth 10 --y4m --input source10.y4m -o out.hevc
```

进程退出码为 0，但出现 `falling back to default bit-depth`，ffprobe 确认产物为 **`yuv420p`**。仅加 `--profile main10` 的样本也没有得到 10 位产物。8 位视频、两遍编码仍可执行，不能把整个 x265 工具标成不可用，也不能把它标成支持所有 HEVC 工作流。

施工要求：

- 在托管编码器构建仓库提供并验证 10 位或 multilib 包；FlowEncode 导入/更新时探测可用位深。
- 计划明确目标位深和 profile；不支持时提交前拒绝，不能允许编码器回退后静默成功。
- 完成后探测实际 codec、pix_fmt、帧数与计划一致性；HDR10 还需要独立 HDR 元数据样本验收。

### 4.2 x264：10 位输入不代表 10 位输出

本机 x264 可接收 10 位 Y4M，但在当前默认参数形态下输出 8 位 H.264。额外明确 `--output-depth 10` 后，产物才为 `yuv420p10le`。这与“程序报错”不同，但同样是 3.0 要避免的结果偏离。

`EncodingCommandBuilder.BuildEncoderCommand` 当前没有针对 x264/x265 自动确定输出位深。修复时应尊重用户显式目标和 profile，不能无条件把所有输出改成输入位深，例如 baseline/high 的目标可能就是 8 位。预览与实际执行必须来自同一份已解析计划。

### 4.3 SVT-AV1：QP 数值与模式语义

源码位置：`EncodingCommandBuilder.BuildRateControlArguments` 和 `EncodingCommandPreviewBuilder.BuildRateControlArguments`。

当前 `FormatNumber` 强制至少一位小数，所以质量值 32 会变为：

```text
--rc 0 --qp 32.0
```

4.2.0 和 4.3.0 均返回非零退出码，提示 qp 值无效。

4.3.0 对照测试结果：

| 参数 | 实际结果 |
| --- | --- |
| `--rc 0 --qp 32.0` | 拒绝，退出码 1 |
| `--rc 0 --qp 32` | 可以运行，但配置日志显示 CRF / 32.00 |
| `--rc 0 --aq-mode 0 --qp 32` | 可以运行，配置日志显示 CQP / 32.00 |
| `--cqp 32.25` | 可以运行，配置日志显示 CQP / 32.25 |

因此不能只修改数字格式：旧版兼容路径需要整数和显式 AQ 设置；新版 `--cqp` 支持及范围/步进应按版本能力使用。不要在未知旧版 EncApp 上无条件发送新选项，也不要静默四舍五入用户质量值。修复还要覆盖手动附加参数对 AQ/CRF/CQP 的覆盖和冲突。

依据：[SVT-AV1 4.3.0 固定版本参数表](https://github.com/AOMediaCodec/SVT-AV1/blob/v4.3.0/Docs/Parameters.md)。

### 4.4 SVT-AV1：tune、profile 与旧模板

当前 `EncoderManifestCatalog` 静态提供 IQ、main/high/professional 等选项，未随输入组合限制：

- 4.2.0、4.3.0 的普通随机访问视频使用 `--tune 3` 会失败；本次日志指出 IQ 只支持特定预测结构，不能当普通视频通用 tune。
- 4.3.0 的 8 位 4:2:0 输入使用 `--profile 1` 会失败，要求 4:4:4；`--profile 2` 也会失败，提示低于 10 位时要求 4:2:2。应校验组合，不能简单认为 profile 名称存在就可选。
- 4.2 引入 VMAF tune（5）；当前 UI 没列出。这是可按能力新增的选项，不是本次阻断项。
- 旧模板中的 `--lp 20` 在 4.3 不再表示用 20 个逻辑处理器。实测它警告后限制为 parallelism level 6，仍返回 0。3.0 导入旧模板时应解释这一语义变化，不能把它继续显示成线程数。
- 本机 4.2 与测试 4.3 对某些高 preset 存在重映射提示。preset/tune 的同名值不能保证跨版本画质与速度完全相同，应保存编码器版本以支持复现。

来源：[SVT-AV1 4.3.0 参数与约束](https://github.com/AOMediaCodec/SVT-AV1/blob/v4.3.0/Docs/Parameters.md)、[固定版本变更记录](https://github.com/AOMediaCodec/SVT-AV1/blob/v4.3.0/CHANGELOG.md)。

## 5. VapourSynth 和其他依赖的边界

VapourSynth R74 引入 `_Range` 并改变插件装载机制；R80 移除 API3 插件支持，R82 修复部分 NVIDIA GPU 内存路径及范围常量问题。这些变化可能影响脚本/插件兼容性，即使编码器 CLI 本身没有变化。

当前 `SourceVideoInfoProbe.MapVapourSynthRange` 已优先读取 `_Range`、再回退 `_ColorRange`，不能把这项已存在的兼容逻辑当作待补缺口。仍需回归预览、第三方滤镜、vsrepo/PyPI 安装及 GPU 脚本。Python 的构建版本要求与最终用户可运行版本要区分，不应仅看到上游用 Python 3.14 构建就判定所有用户必须升级至 3.14。

来源：[VapourSynth 官方 ChangeLog](https://github.com/vapoursynth/vapoursynth/blob/master/ChangeLog)、[R82 发布说明](https://www.vapoursynth.com/2026/11/02/r82-todays-patch-release-is-brought-to-you-by-nvidia/)。

deew 5.x / DEE 6.7 是独立的大版本迁移，沿用主路线图已确定的新版本边界。本次没有授权 DEE 新工具链及真实 Atmos/DTS:X 素材验收，不改变既有迁移结论。

## 6. 本次验证记录

### 6.1 隔离下载和版本身份

以下 ZIP 下载后均核对 GitHub API 提供的 SHA256，再在仓库 artifacts 中解压运行：

| 包 | SHA256 |
| --- | --- |
| `ffmpeg-n8.1-latest-win64-gpl-shared-8.1.zip` | `1e38a41a1dd859f11a2b4529f20ecf28e657d057ce5bdbad40fd2ec18515cfff` |
| `ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip` | `f7fe240cfc7312e40e8686982f73fbf2b377b8cdc3bd52795e157d5b7036ebbf` |
| `svt-av1-4.3.0-x64.zip` | `caf4df0ac5393436e4e63455d1be09b935bb8bfe7acd013653cd34fa228c05bb` |
| `x265-4.3-x64.zip` | `7e6599de84d20498482a54ddec5c5d0d6b32afe1992d86180e1220a5f698f375` |

FFmpeg 实际版本分别为 `n8.1.3-35-gf1acb0a266-20261111` 和 `n9.0.2-49-gc179411bbb-20261111`。它们是 release 分支快照，**不是精确的 8.1.3 / 9.0.2 官方点版本二进制包**。

### 6.2 真实 CLI 样本结果

测试使用 128×128、24 帧、24fps 的合成 Y4M/FFV1 样本，以及短静音音频。二进制管线使用 Python subprocess 字节流，未通过 PowerShell 文本管道传输。

| 项目 | FFmpeg 8.1 分支 | FFmpeg 9.0 分支 |
| --- | --- | --- |
| 当前视频 FFprobe JSON 查询 | 通过 | 通过 |
| 当前 FFmpeg Y4M 源命令，8/10 位输入 | 可执行 | 可执行 |
| Y4M → x264/x265/SVT，8 位，输出帧数 | 均为 24 帧 | 均为 24 帧 |
| Y4M → x264/x265，10 位输入，未强制有效输出能力 | 返回成功，但均输出 8 位 | 返回成功，但均输出 8 位 |
| Y4M → SVT 4.3，10 位 | 24 帧，10 位 AV1 | 24 帧，10 位 AV1 |
| libopus stereo / 5.1 / 7.1 与 FFprobe JSON | 成功，2/6/8 声道 | 成功，2/6/8 声道 |
| 5.1(side) channelmap → libopus | 可执行 | 可执行 |
| FFmpeg WAV → 原有 opusenc | 可执行 | 可执行 |
| DCA decoder 的 `-downmix` 参数查询 | 存在 | 存在 |

`matrix-results.json` 共记录 61 次进程调用：54 次 FFmpeg 相关链路/探测，以及 7 次 SVT 版本/参数对照；后者包含两次预期复现的失败。额外 12 次调用验证了 profile 失败、显式位深及 x264/x265/SVT 的分步两遍编码；两遍 8 位短样本均可执行。退出码为 0 的调用仍可能出现上表中的位深偏离。

原始命令、日志、媒体和临时探测脚本保存在 `artifacts/compatibility-audit/`（被 Git 忽略）；关键结果已固化在本文。它们属于 CLI 审计，未通过桌面 UI 提交，也不代表完整应用或真实长片验收。

### 6.3 仓库测试与环境限制

Domain/Application/Infrastructure/Tests 编译成功。普通 `dotnet test` 初次被 testhost 监控父进程的访问权限阻断；仅在生成的 runtimeconfig 中关闭 `MSTest.EnableParentProcessQuery`，并把 TEMP/TMP 指向本仓库 artifacts 后，运行了排除 ExternalToolSmoke 的测试：**365 通过，9 失败，共 374**。

9 个失败均出现目录访问拒绝：8 个为 `VapourSynthPreviewServiceTests` 的默认用户工作目录，1 个为并行编码日志测试使用的 `D:\work`。没有修改源码、用户目录权限或用户工作目录配置来规避它们。不能将本次结果记录为“全套测试通过”。

本次未覆盖：真实 DTS:X/Atmos、HDR10/HDR10+/Dolby Vision、VFR、4:2:2/4:4:4/12 位、复杂多轨长片、VapourSynth 插件/GPU 渲染、Av1an 完整任务、新 DEE、安装与回滚 UI。应使用这些样本验证后再确定最终推荐工具包。

## 7. 并入 3.0 的施工与发布门槛

1. **P0 工具基线：** 修复 x265 发布包位深能力；确定 FFmpeg release 通道及推荐包身份；保留本次失败样本作为真实回归用例。
2. **P1 共享能力与计划：** `IToolCapabilityService` 记录路径、版本、构建身份、位深/色度、参数支持及约束；计划确定输出属性。UI/MCP 共用验证结果。
3. **参数适配：** 修复 SVT QP 数值与 AQ 语义，约束 IQ/profile/旧模板；保证模板预览和实际执行一致；未知手工参数应能给出具体诊断，不能把完整 help 当作任意组合均合法的证据。
4. **执行后验证：** 退出码、产物存在性和媒体属性共同决定成功；不匹配计划时保留恢复文件并说明原因，不发布错误属性的最终产物。
5. **发布矩阵：** 每个推荐 FFmpeg/编码器组合跑基础 8/10 位、两遍、音频声道、取消与错误参数回归，再加真实 HDR/DTS:X/VFR/脚本样本。升级后复用用户模板也必须经过校验。

完成这些门槛后，才可把相应组合标记为“3.0 推荐工具链”。本次测试说明 FFmpeg 8.1/9.0 的基础 CLI 路径可继续使用，不等于可以直接开放任意 latest 自动升级。

## 8. 后续实施：固定基线与 2.0 环境保留

实施日期：2026-11-12。默认安装/修复只取以下固定版本，不跟随每日构建。更新入口仍存在，但放在“更多 → 更新工具（不推荐）”；警告明确指出其他正式版本也可能引起脚本、插件和参数不兼容，默认操作为“保留当前环境”。核对基线不提示用户追新，不自动升级已就绪的工具。

### 8.1 版本组合

| 组件 | 默认基线 | 说明 |
| --- | --- | --- |
| Python | **3.14.10 Windows x64，普通 GIL 构建** | 新安装使用；安装器 URL、SHA256 和 PSF 签名固定；已有 3.12 环境继续保留 |
| VapourSynth | **R79** | 保留 API3 + API4；R80 起移除 API3，因此本轮不默认升级到 R82 |
| vsjetpack / vsrepo / awsmfunc | **2.2.2 / 2.0.0 / 1.3.5** | 与 R79 配套；pip 约束同时固定 30 个直接及间接依赖，防止安装脚本包时自动带入新 VS |
| akarin / vszip / vsstubs | **1.4.1 / 22.0.0 / 2.2.0** | 已在新旧 Python 上核对安装与基本渲染 |
| VS 插件集 | **2026-11-12 快照，37 个包的依赖闭包** | 内嵌 `Resources/VsrepoBaseline.json`，固定单个版本、下载地址及文件 SHA256 |
| FFmpeg + FFprobe | **9.0.2 Gyan Full Shared** | 来自精确正式标签；不采用 BtbN master，也不采用每日重建的 release 分支 ZIP |
| x264 | **stable-20250608-b35605ace3dd，0.165.3222** | stable 标签和 ZIP 摘要固定 |
| x265 | **4.3，8+10 位 multilib，优化构建** | 修复包 CI 已通过；默认安装固定使用本项目 v3.0.0-beta.1 附属资产与精确摘要，旧 8 位包被拒绝 |
| SVT-AV1 | **4.3.0** | 正式标签与 ZIP 摘要固定 |
| Av1an | **v0.5.2-flowencode.2** | 项目协议兼容的正式托管包，固定标签和摘要；不是自动跟随上游 nightly |

插件集中 ffms2 为 5.0、fpng 为 1.0、libp2p 为 r2、lsmas 为 1296.0.0.0、placebo 为 v3.3.1。四个旧脚本包没有使用浮动分支：mvsfunc 固定 `865c7486ca860d323754ec4774bc4cca540a7076`、havsfunc 固定 `5baf9c2a1dac9da4de182cf3a4e12041c3e0d48b`，其依赖 adjust 和 nnedi3_resample 同样固定提交与文件摘要。这是保留旧脚本生态的明确例外；其余插件优先采用正式发布标签。vsrepo 的私有快照存放在工作区下载目录，正常核对不会刷新或覆盖用户原有的包定义数据库。

选择依据：[R79 正式发布](https://github.com/vapoursynth/vapoursynth/releases/tag/R79)、[VS 官方变更记录](https://github.com/vapoursynth/vapoursynth/blob/master/ChangeLog)、[Python 3.14.10 正式发布](https://www.python.org/downloads/release/python-31410/)、[vsjetpack 2.2.2 元数据](https://pypi.org/pypi/vsjetpack/2.2.2/json)。新版 vsjetpack 3.0 的 Python 下限已提高到 3.13，配套新版 vsstubs 又提高 VS 下限；不通过无约束的 pip upgrade 混用两个插件栈。

### 8.2 从 2.0 升级

1. 保留原有工作区、设置、手动工具路径和插件目录，不在启动或版本核对时卸载或迁移它们。
2. Python 选择依次尊重手动 Python 路径、手动 vspipe 所属环境、已有环境绑定；没有明确绑定时优先保留已安装 VS 的解释器。仅安装了更高版本 Python，不构成切换理由。
3. 可用的旧 Python 3.12 环境继续显示就绪，可主动点击“安装推荐版本”并排安装新 Python。只有当前解释器已是推荐版本且检测正常时，主按钮才置灰显示“已是推荐版本”。Python 卡片提供解释器选择、依赖安装、脚本验证和回退步骤。Python/module/pip/vspipe 探测尽量使用同一绑定，避免把不同解释器中的组件拼成假就绪状态。
4. 用户主动安装新的 Python 次版本时采用并排安装，不修改系统 PATH、`py` 启动器或文件关联，也不清除原 Python 手动绑定；后续 CLI 同步继续采用旧绑定。该操作不会自动把 VS、插件或用户脚本迁移到新环境。
5. 真正切换时，用户先在新解释器中安装基线，再验证自己的脚本，最后通过路径设置主动切换；旧环境仍可用于回退。相同 Python 次版本的补丁升级可能由官方安装器原地执行，不能作为环境备份手段。

Python 3.14.10 的选择用于新安装和后续维护；**不要求已有可用的 3.12 用户为了运行 FlowEncode 3.0 立即更换解释器**。本轮实际验证覆盖 3.12.10 与 3.14.10；其他自行安装的版本不会因此自动获得“已实测”标记。

### 8.3 已实施的运行时修复

- x264/x265 的 QP 参数不再生成编码器拒绝的 `32.0`；SVT 整数 QP 明确 `--rc 0 --aq-mode 0 --qp 32`，四分之一步进或大于 63 的扩展 CQP 值使用经能力检测的 `--cqp`。预览、实际执行和提交前校验一致。
- SVT 校验 IQ/all-intra、Main/YUV420 组合及附加参数对 QP/AQ/RC 的冲突；旧 `--lp 20` 不再被误当成新版本线程数量静默执行。
- x264/x265 明确目标输出位深；执行前确认编码器支持。旧 x265 的 10 位请求提前失败，不再接受其退出码 0 后退回 8 位的行为。
- 产物验证检查 codec、位深、色度及可确定的帧数，音频 Opus 检查编码格式和声道；失败时保留恢复文件。FFprobe 根据时长推算的帧数不作为精确判错条件，避免误伤 VFR。
- FFmpeg/FFprobe 配对及按工作流所需的滤镜/编码器能力先检查；Av1an 子进程优先使用已选编码器和 FFmpeg 所在目录。稳定 FFmpeg 包必须匹配固定摘要；摘要改变时停止默认自动安装。

### 8.4 新增验证结果与发布资产

- **54 项兼容性专项检查全部通过**：含策略/参数/产物校验回归、环境选择迁移规则，以及 9 项真实 runner/能力检查。真实 runner 覆盖 x264、优化版 x265、SVT 的 8/10 位、SVT 小数 CQP、旧 x265 拦截；FFmpeg 使用精确 9.0.2 包。
- Python **3.12.10 与 3.14.10** 均在独立目录安装同一组固定依赖，核心模块导入和 10 位 BlankClip → SetFrameProps → akarin 渲染通过。3.14.10 加载现有 API3 CTMF 后实际取帧通过。
- 私有 vsrepo 快照的 `installed → install → upgrade → installed` 已在隔离环境运行通过。仅保留快照列出的插件后，ffms2、lsmas、mvsfunc.Depth、havsfunc.SMDegrain、libp2p.Pack、placebo.Resample 和 fpng.Write 均成功实际取帧。
- WinUI Release 构建通过，0 警告、0 错误。未用桌面交互执行安装或更新确认按钮；UI 路径经过 XAML/C# 编译检查。
- 结合最新主线重新编译并运行全量普通回归：**497 / 497 通过**。此前 deew 临时目录位置的失败已修复，本轮重新验证通过。该结果不替代工具安装、升级回滚及发布包验收。

x265 源构建修复：[PR #1](https://github.com/frankie1024/x265-windows-builds/pull/1)，[优化构建与位深验证 CI](https://github.com/frankie1024/x265-windows-builds/actions/runs/43147776097)。CI 内使用 NASM 构建 10 位静态库和 8 位 CLI，并通过 LINKED_10BIT 链接；验证失败即停止打包。修复 ZIP 已下载复核，其 SHA256 为 `499913e1e898b64078a795c9edcd0ea8805fe05417091ce9abad0572f70f0b3d`，基线已锁定这个精确资产。**PR 尚未合并，也未覆盖公开的旧 4.3 发布资产**；在发布修复包前，默认安装不会退回旧 8 位包。若重新编译打包，必须重新验收并同步摘要，不能将新产物当成同一个已验证包。

**纳入 beta 的状态：** 基线策略、参数修复、旧环境保护、提示文案和测试均随 3.0.0-beta.1 提交。默认 x265 来源已固定为本仓库的 [v3.0.0-beta.1 / x265-4.3-x64.zip](https://github.com/frankie1024/FlowEncode/releases/download/v3.0.0-beta.1/x265-4.3-x64.zip)，继续核对精确 SHA256 `499913e1e898b64078a795c9edcd0ea8805fe05417091ce9abad0572f70f0b3d`。对应源码及许可证作为独立资产同发布提供。外部 x265 仓库原有 4.3 资产保持原样，普通工具更新通道也保持独立；默认安装不会退回它的旧 8 位包。

测试日志与隔离环境位于 `artifacts/compatibility-audit/`，包括 `integration-unit-tests.log`、`final-focused-tests.log`、`python312-vs79-pinned.json`、`python314-vs79-legacy.json`、`fresh-pinned-plugin-render.json` 和 `x265-ci-verified/bit-depth-validation.json`。真实长片、HDR/DV/VFR、复杂 GPU 脚本、完整 Av1an 任务和安装器端到端回滚仍属于 3.0 发布矩阵，不能由短样本替代。

### 8.5 x265 发布附属材料

核对精确二进制 ZIP：其中只有 `x265.exe`、`BUILD_INFO.txt` 和 `bit-depth-validation.json`；已有上游源码提交信息，但没有附带 GPL 许可证正文。已准备独立附件 **`x265-4.3-multilib-source-license.zip`**，没有重打或修改二进制 ZIP。

- 本地文件：`D:\codex\FlowEncode\artifacts\compatibility-audit\x265-4.3-multilib-source-license.zip`，2,076,280 字节。
- 附件 SHA256：`db428fc8cdba3110ead30155207e7e529fcaacf0a1f8fc5e23ea698092b83077`。
- 包含 x265 提交 `e9b88125dc21393b3fd8d68e98083bdfb89778a8` 的完整已跟踪源码树（494 项），以及构建自动化提交 `ad1d751cb8ce02e8a24618adc8d784449f32e3a8` 的完整脚本与工作流。
- 包含未修改的上游 `COPYING`、构建自动化 MIT `LICENSE`、版权与源码说明、构建步骤、原始 CI 验证报告和文件摘要清单。x265 源文件声明 GPL v2 或以后版本；构建仓库的 MIT 许可不会改变编码器许可。
- 上游已跟踪的 `x265Version.txt` 仍写着 4.2；源码归档失去 Git 元数据时会报告旧版本。附件提供单独的 `Prepare-Source-Version.ps1`，在提取的源码中恢复 CI 使用的 4.3 / tag distance 0 / 精确提交信息。已通过 CMake 实际检查，版本结果为 4.3。归档内的原始源码保持原样。

两个 ZIP 可以作为 FlowEncode beta 的附属发布资产一同提供，保留外部 x265 仓库旧资产；发布时需要把应用的固定下载来源指向实际 beta release/asset，并继续校验二进制原摘要 `499913e1e898b64078a795c9edcd0ea8805fe05417091ce9abad0572f70f0b3d`。这两份精确附件随本项目 v3.0.0-beta.1 发布；固定基线的 HTTP/摘要选择有回归测试覆盖。
