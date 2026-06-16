# Av1an Parallel CRF Video Encoding Plan

## 背景

FlowEncode 目前有两条视频相关执行路径：

- 普通视频压制：由 `LocalEncodingJobRunner` 直接组织 `source -> encoder -> output` 管线，支持 x264、x265、SVT-AV1，并支持 CRF/CQ/QP/ABR/VBR/two-pass 等模式。
- 自动压制：由 `StructuredAv1anRunner` / `LegacyAv1anCliFallbackRunner` 调用 Av1an，已经具备 `--workers`、临时目录、取消、进度、输出落盘和失败清理能力。

本计划只在普通视频压制中新增一个可选的 Av1an 并行执行模式。它复用 Av1an 的 chunk planning、worker 调度和拼接能力，不在 FlowEncode 内部自研 chunk scheduler。

## 目标

新增“Av1an 并行 CRF 视频压制”能力：

- 支持编码器：x264、x265、SVT-AV1。
- 支持码控：仅 CRF。
- 支持输入：沿用普通视频压制现有视频输入能力。
- 支持输出：仅视频输出，不处理音频、字幕、章节或复杂 mux。
- 支持并行度：`Workers = 0` 表示 Av1an 自动决定，`Workers > 0` 显式传给 Av1an。
- 支持命令预览、实时日志、取消、失败清理和完成落盘。

## 非目标

第一版明确不做：

- two-pass。
- ABR/VBR/CQ/QP 的 Av1an 并行普通压制模式。
- 音频复制、音频转码、字幕、章节、附件或多轨 mux。
- FlowEncode 自研切分、worker 调度、chunk 拼接或 resume 逻辑。
- 失败后从已完成 chunk 自动续跑。
- 跨机器分布式编码。
- 对任意高级 encoder 参数做完全兼容保证。
- 对 VapourSynth 脚本边界伪影做自动修复。

## 用户体验

普通视频压制页面增加一个高级开关：

- `使用 Av1an 分块并行`

开启后：

- 码控模式强制或限制为 CRF。
- 编码器仍可选择 x264、x265、SVT-AV1。
- 显示 `Workers (0 = Auto)`。
- 命令预览显示 Av1an 命令，而不是裸 encoder 命令。
- 输出仍按普通视频压制任务显示进度、日志、完成状态和失败状态。

关闭后：

- 现有普通压制行为保持不变。
- two-pass、ABR/VBR/CQ/QP 等现有能力不受影响。

## 数据模型

新增或扩展普通压制请求模型：

- `UseAv1anParallelVideoEncoding: bool`
- `Av1anWorkers: int?`

推荐新增一个专用请求，而不是把 Auto Encode 的 `AutoCompressionRequest` 扩成万能对象：

```csharp
public sealed record ParallelVideoEncodingRequest(
    Guid JobId,
    string SourcePath,
    string OutputPath,
    EncoderKind EncoderKind,
    double Crf,
    string Preset,
    string Tune,
    string Profile,
    string VideoParameters,
    int? Workers,
    InputPipelineKind PipelineKind,
    EncoderArchitecture PreferredArchitecture);
```

原因：

- Auto Encode 是 target-quality 语义，包含 metric、target quality、probe、search profile。
- 并行普通压制是 fixed-CRF 语义，不应伪装成 target-quality。
- 两者可以共享 Av1an 进程运行基础设施，但领域请求应分离。

## Av1an 参数映射

Av1an 命令基本结构：

```text
av1an -i <source> -o <output> -y --keep --temp <temp>
  --encoder <x264|x265|svt-av1>
  --video-params <encoder args>
  [--workers <n>]
  [--progress-format jsonl]
```

`--video-params` 由普通压制参数生成：

- x264: `--crf <value> --preset <preset> ...`
- x265: `--crf <value> --preset <preset> ...`
- SVT-AV1: `--rc 0 --crf <value> --preset <preset> ...`

同时复用现有逻辑：

- preset/tune/profile 归一化。
- HDR / color metadata 参数生成。
- 用户 Additional Arguments 追加。

禁止或拦截以下用户参数：

- 输出控制：`-o`、`--output`、`-b`。
- 输入控制：`-i`、`--input`、`--stdin`、`--y4m`、`--demuxer`。
- two-pass / stats：`--pass`、`--stats`、`--slow-firstpass`。
- 会破坏 Av1an chunk 输入的 raw source 参数，除非后续专门验证。

## 架构落点

### 1. 新增并行普通视频 runner

新增基础设施类：

- `ParallelVideoEncodingAv1anRunner`

职责：

- 构建 fixed-CRF Av1an 参数。
- 复用 Av1an 可用性检测。
- 启动 Av1an 进程。
- 解析 jsonl 进度，必要时退回 legacy 文本进度。
- 处理取消、失败清理、输出 finalize。

建议抽取共享组件：

- `Av1anProcessRunner`
- `Av1anArgumentBuilder`
- `Av1anProgressAdapter`

但第一版不要过度抽象。可以先让新 runner 复用 `LegacyAv1anCliFallbackRunner` 中已经稳定的静态 helper，再做小范围提取。

### 2. 普通压制 runner 路由

`LocalEncodingJobRunner.RunAsync` 前置分流：

- 如果 `UseAv1anParallelVideoEncoding == false`：走现有逻辑。
- 如果为 true：校验 CRF + 支持编码器 + Av1an 可用，然后委托 `ParallelVideoEncodingAv1anRunner`。

也可以在 ViewModel 层直接选择 runner，但建议在应用服务层保持普通压制任务入口一致，减少 UI 分支泄漏。

### 3. 命令预览

`BuildDisplayCommand` 应跟随执行模式：

- 普通模式：保持现有裸 encoder 命令。
- Av1an 并行模式：显示 Av1an 命令。

命令预览必须准确反映：

- `--encoder`
- `--video-params`
- `--workers`
- `--temp`
- `--progress-format jsonl`

## 三步实施计划

### Step 1: 垂直打通后端

目标：不改 UI 或只用临时内部入口，先让 fixed-CRF Av1an 视频任务可运行。

工作项：

- 新增 `ParallelVideoEncodingRequest` 或扩展 `EncodingJobRequest`。
- 新增参数构建器，把 x264/x265/SVT-AV1 CRF 参数映射到 Av1an。
- 新增危险参数校验。
- 新增 runner，复用 Av1an temp、progress、abort、cleanup、finalize。
- 添加单元测试：
  - x264 CRF 参数映射。
  - x265 CRF 参数映射。
  - SVT-AV1 CRF 参数映射。
  - `Workers = null` 不输出 `--workers`。
  - `Workers = 4` 输出 `--workers 4`。
  - 禁止 `--pass`、`--stats`、输出参数和输入参数。

完成标准：

- 后端可构建正确 Av1an 命令。
- runner 可以启动并接收进度。
- 失败和取消会清理临时输出。
- 现有普通压制测试不受影响。

#### Step 1.1: 请求模型与验证

建议文件：

- `FlowEncode/FlowEncode.Domain/ParallelVideoEncodingRequest.cs`
- `FlowEncode/FlowEncode.Domain/RequestValidation.cs`
- `FlowEncode/FlowEncode.Domain.Tests/ParallelVideoEncodingRequestTests.cs`

实现细节：

- 新增 `ParallelVideoEncodingRequest`，表达 fixed-CRF 并行视频压制。
- `Crf` 必须是有限且非负数。
- `Workers` 为 `null` 表示 Av1an 自动；显式值必须大于 0。
- `EncoderKind` 只允许 `X264`、`X265`、`SvtAv1`。
- `VideoParameters` 和后续可能的 backend args 必须是单行。

测试：

- 接受 x264/x265/SVT-AV1。
- 拒绝未知编码器。
- 接受 `Workers = null`。
- 拒绝 `Workers <= 0`。
- 拒绝换行参数。
- 拒绝 NaN/Infinity CRF。

验收：

- 领域模型能独立表达需求，不借用 target-quality 的 `AutoCompressionRequest`。

#### Step 1.2: Av1an 参数构建器

建议文件：

- `FlowEncode/FlowEncode.Infrastructure/ParallelVideoAv1anArgumentBuilder.cs`
- `FlowEncode/FlowEncode.Domain.Tests/ParallelVideoAv1anArgumentBuilderTests.cs`

实现细节：

- 构建 Av1an 外层参数：
  - `-i <source>`
  - `-o <output>`
  - `-y`
  - `--keep`
  - `--temp <temp>`
  - `--encoder <x264|x265|svt-av1>`
  - `--video-params <resolved encoder args>`
  - `--progress-format jsonl`
  - 可选 `--workers <n>`
- 构建 encoder 内层参数：
  - x264: `--crf <crf> --preset <preset>`
  - x265: `--crf <crf> --preset <preset>`
  - SVT-AV1: `--rc 0 --crf <crf> --preset <preset>`
- 复用或迁移现有 helper：
  - `EncoderArgumentValueNormalizer`
  - `CommandArgumentTokenizer`
  - `CommandLineDisplay`
  - `EncodingCommandBuilder.BuildEncoderColorMetadataArguments`
- 先不支持外层 Av1an backend args，避免用户绕过保护。

危险参数 deny-list：

- 输入类：`-i`、`--input`、`--stdin`、`--y4m`、`--demuxer`。
- 输出类：`-o`、`--output`、`-b`。
- two-pass 类：`--pass`、`--stats`、`--slow-firstpass`。
- stdin/raw 管线类：第一版不允许用户手动覆盖 chunk 输入来源。

测试：

- x264 命令包含 `--encoder x264` 和 `--video-params` 内的 `--crf`。
- x265 命令包含 `--encoder x265`。
- SVT-AV1 命令包含 `--encoder svt-av1`、`--rc 0`、`--crf`。
- `Workers = null` 不输出 `--workers`。
- `Workers = 4` 输出 `--workers 4`。
- 危险参数被拒绝，并返回具体参数名。

验收：

- 构建器只负责纯参数转换，无进程启动副作用。
- 命令预览和实际执行复用同一套参数构建结果。

#### Step 1.3: Runner 最小实现

建议文件：

- `FlowEncode/FlowEncode.Application/IParallelVideoEncodingRunner.cs`
- `FlowEncode/FlowEncode.Infrastructure/ParallelVideoEncodingAv1anRunner.cs`
- `FlowEncode/FlowEncode.Domain/ParallelVideoEncodingResult.cs`
- `FlowEncode/FlowEncode.Domain/ParallelVideoEncodingProgress.cs`

实现细节：

- 解析 Av1an 路径：复用 `ExternalToolLocator.ResolveAv1an()`。
- 预检 Av1an：复用 `LegacyAv1anCliFallbackRunner.EnsureAv1anRuntimeReadyAsync()`。
- temp 目录：
  - `<output dir>/.flowencode-temp/av1an-parallel/<jobId>`
- staged output：
  - temp 目录下同名输出文件。
- 成功后：
  - 使用 `ExecutionOutputStaging` 或等价 finalizer 移动到最终输出。
  - 清理 temp 目录。
- 失败/取消后：
  - 第一版不做续跑。
  - 清理 staged output 和 temp 目录。
- 进度：
  - 优先使用 `--progress-format jsonl` 和 `JsonlEventParser`。
  - 如果 Av1an 不支持 structured protocol，可退回文本进度解析，或直接失败提示当前 Av1an 版本不支持。

测试：

- 可用 fake process runner 最好；如果现有代码没有抽象，先测试参数和失败消息，runner 做较少单测。
- 取消时会调用 active execution terminate。
- 成功 finalize 失败时返回 failed result。

验收：

- 后端可由单元测试覆盖参数、验证、清理策略。
- 不影响现有 Auto Encode runner。

### Step 2: 接入普通视频压制 UI

目标：用户可以在普通视频压制页面开启并行模式。

工作项：

- ViewModel 增加：
  - `UseAv1anParallelVideoEncoding`
  - `Av1anParallelWorkers`
  - `CanUseAv1anParallelVideoEncoding`
- XAML 增加高级开关和 worker NumberBox。
- 开启并行模式时限制码控为 CRF。
- 命令预览跟随模式刷新。
- 队列任务保留该模式配置。
- 添加 UI 相关 ViewModel 测试：
  - 开启模式后非 CRF 请求被拒绝。
  - CRF 请求可生成 Av1an 命令预览。
  - 关闭模式后仍生成原裸 encoder 命令。

完成标准：

- 用户能从普通视频压制页面创建 Av1an 并行 CRF 视频任务。
- 普通模式完全保持原行为。
- 错误提示明确说明第一版只支持 CRF 和视频。

#### Step 2.1: ViewModel 状态接入

建议文件：

- `FlowEncode/ViewModels/MainWindowViewModel.cs`
- `FlowEncode/ViewModels/MainWindowViewModel.Modules.cs`
- `FlowEncode/ViewModels/EncodingJobItemViewModel.cs`
- `FlowEncode/ViewModels/AppText.cs`

需要先定位普通视频压制属性所在 partial 文件；本计划不假设最终文件名。

实现细节：

- 增加开关：
  - `UseAv1anParallelVideoEncoding`
- 增加 worker 输入：
  - `Av1anParallelWorkers`
- 增加派生状态：
  - `CanUseAv1anParallelVideoEncoding`
  - `Av1anParallelVideoHint`
- 创建普通压制请求时：
  - 开关关闭：保持现有 `EncodingJobRequest`。
  - 开关开启：要求 `Profile.RateControl == RateControlMode.Crf`。
  - 开关开启：构造并行请求或设置 `EncodingJobRequest` 的并行字段。

约束：

- 不自动修改用户的非 CRF 配置，优先给明确错误。
- 如果后续体验需要，也可以在开启开关时把码控切到 CRF，但第一版建议保守。

测试：

- 开关关闭时请求不带并行模式。
- 开关开启 + CRF 时请求有效。
- 开关开启 + two-pass/ABR/VBR/CQ/QP 时返回错误。
- workers 0 映射为 `null`。
- workers 2 映射为 `2`。

验收：

- UI 状态只影响普通视频压制，不影响 Auto Encode。
- 现有队列任务和状态显示不破坏。

#### Step 2.2: XAML 控件接入

建议文件：

- `FlowEncode/Controls/.../VideoEncodingView.xaml` 或当前普通视频压制对应 XAML。
- `FlowEncode/Controls/.../VideoEncodingView.xaml.cs` 如有响应式布局代码。
- `FlowEncode/ViewModels/AppText.cs`

实现细节：

- 在高级参数区域增加 ToggleSwitch 或 CheckBox：
  - 中文：`使用 Av1an 分块并行`
  - 英文：`Use Av1an chunked parallel encoding`
- 开启后显示 NumberBox：
  - `Workers (0 = Auto)`
  - Minimum `0`
  - Maximum 可先用 `64`
- 显示简短提示：
  - 仅 CRF。
  - 仅视频。
  - 不处理音频/字幕/章节。

验收：

- 控件布局在常见窗口宽度下不挤压已有编码参数。
- 开关关闭时页面视觉负担低。

#### Step 2.3: 命令预览接入

建议文件：

- `FlowEncode/FlowEncode.Application/IEncodingJobRunner.cs`
- `FlowEncode/FlowEncode.Infrastructure/LocalEncodingJobRunner.cs`
- 新增 runner 或参数构建器所在文件。

实现细节：

- 普通模式继续显示裸 encoder 命令。
- Av1an 并行模式显示 Av1an 命令。
- 命令预览不应提前创建 temp 目录；只需要稳定显示拟用路径。
- 预览路径中的 staged output 可以显示最终 output，避免让用户困惑。

测试：

- 普通模式预览不含 `av1an`。
- Av1an 并行模式预览含 `av1an`、`--encoder`、`--video-params`。

验收：

- 用户复制命令能看懂实际后端执行计划。

### Step 3: 验证、保护和文档

目标：降低真实压制风险，避免用户误以为这是完整 mux 或 two-pass 替代。

工作项：

- 完成后执行轻量输出校验：
  - 输出文件存在。
  - 输出文件非零。
  - `ffprobe` 可读取视频流。
  - 可选：源/输出时长差在合理范围内。
- 日志中记录 Av1an 命令、worker、temp 目录、最终输出路径。
- README 或帮助文本说明：
  - 该模式只处理视频。
  - 不支持 two-pass。
  - 不处理音频/字幕/章节。
  - worker 过高可能变慢。
- 增加 smoke test 或手动验证清单。

完成标准：

- 成功任务生成可读取的视频输出。
- 失败任务有清晰日志。
- 用户不会把该模式误解为完整封装 mux。

#### Step 3.1: 输出校验

建议文件：

- `FlowEncode/FlowEncode.Infrastructure/ParallelVideoEncodingOutputVerifier.cs`
- `FlowEncode/FlowEncode.Domain.Tests/ParallelVideoEncodingOutputVerifierTests.cs`

实现细节：

- 成功 exit code 后检查：
  - staged output 存在。
  - staged output 非零。
  - `ffprobe` 可读到至少一个 video stream。
- 可选检查：
  - 输出 duration 与源 duration 差值小于阈值。
  - 如果源 total frames 可探测，输出 total frames 接近源。

第一版建议只做存在、非零、video stream 可读，避免 frame count 在 VFR、滤镜裁剪、脚本变帧率场景误报。

验收：

- Av1an exit code 为 0 但输出不可读时，任务标记 failed。

#### Step 3.2: 日志与错误消息

建议文件：

- `FlowEncode/ViewModels/AppText.cs`
- `FlowEncode/FlowEncode.Infrastructure/EncodingJobLogWriter.cs`
- 新 runner 文件。

实现细节：

- 任务开始日志包含：
  - Av1an path。
  - encoder。
  - workers。
  - temp directory。
  - output path。
- 参数冲突错误：
  - 明确指出冲突参数。
  - 提示该模式只支持 Av1an 管理输入/输出。
- 非 CRF 错误：
  - 明确指出“Av1an 并行视频模式第一版仅支持 CRF”。
- 缺少 Av1an 错误：
  - 复用现有 tool locator 文案。

验收：

- 用户可从日志判断任务走的是 Av1an 并行模式。
- 用户可根据错误提示修改配置。

#### Step 3.3: 项目文档与测试脚本

建议文件：

- `README.md`
- `README.en.md`
- `docs/index.html`
- `docs/en/index.html`
- `FlowEncode/FlowEncode.Domain.Tests/...`

注意：本计划书是本地开发文档，不要求 push 到远端。产品文档是否更新由发布策略决定。

实现细节：

- README 只需一句能力描述，不展开内部实现。
- 官网文案可后续发布前再更新。
- `scripts/test.ps1` 应能跑过新增测试。

验收：

- `scripts/test.ps1` 通过。
- 没有把本地计划书误纳入发布说明，除非维护者明确决定提交。

## 实施任务清单

### 阶段 A: 后端参数与领域模型

- [ ] 新增 `ParallelVideoEncodingRequest`。
- [ ] 新增 request validation。
- [ ] 新增 Av1an 参数构建器。
- [ ] 新增危险参数检测。
- [ ] 增加领域与参数构建单元测试。

阶段 A 完成后可以开评审，因为它不依赖 UI。

### 阶段 B: Runner 与执行

- [ ] 新增 `ParallelVideoEncodingAv1anRunner`。
- [ ] 接入 Av1an 预检。
- [ ] 接入 jsonl 进度。
- [ ] 接入取消。
- [ ] 接入 output finalize。
- [ ] 接入失败/取消清理。
- [ ] 增加 runner 可测部分的测试。

阶段 B 完成后应能通过临时集成测试或内部入口执行一次短视频压制。

### 阶段 C: 普通压制入口分流

- [ ] 扩展普通压制请求或任务配置。
- [ ] `BuildDisplayCommand` 分流。
- [ ] `RunAsync` 分流。
- [ ] 确保普通模式代码路径无行为变化。
- [ ] 补现有普通压制回归测试。

阶段 C 完成后，后端能力可被 UI 调用。

### 阶段 D: UI 接入

- [ ] 增加开关。
- [ ] 增加 workers 输入。
- [ ] 增加提示文案。
- [ ] 开启模式时校验 CRF。
- [ ] 命令预览跟随刷新。
- [ ] 队列任务保存该配置。

阶段 D 完成后，用户可完整使用该功能。

### 阶段 E: 输出校验与发布前 QA

- [ ] 增加 output verifier。
- [ ] 增加 ffprobe 视频流校验。
- [ ] 补错误文案。
- [ ] 跑单元测试。
- [ ] 手动压制 smoke test。
- [ ] 检查 README/官网是否需要更新。

阶段 E 完成后可进入发布候选。

## 风险评估

### 参数兼容风险

风险：用户 Additional Arguments 中包含输入、输出、pass、stats 等参数，会和 Av1an 的 chunk 输入输出冲突。

控制：

- 第一版使用 deny-list 阻断高风险参数。
- 错误提示列出冲突参数。
- 不承诺所有裸 encoder 小参都可在 Av1an chunk 模式下工作。

### 质量边界风险

风险：chunk 边界可能降低压缩效率或产生边界异常，特别是非 scene cut 切分或强时域滤镜场景。

控制：

- 借 Av1an 的 chunk method 和 scene split，不自研切分。
- 不额外插手帧边界。
- 文档中说明高级 VapourSynth 脚本需要用户自行验证。

### 性能反噬风险

风险：`workers * encoder threads` 过高会导致 CPU cache 抖动、内存占用升高、磁盘 IO 压力上升，实际速度变慢。

控制：

- 默认 `Workers = 0`。
- UI 文案提示 worker 不等于越大越快。
- 后续可增加推荐值：`min(物理核心数 / 每 worker 线程数, 8)`，但第一版先交给 Av1an auto。

### 输出语义风险

风险：用户以为该模式会保留音频、字幕、章节。

控制：

- 模式名称使用“视频压制”而不是“封装”。
- 输出校验只要求视频流。
- 文档和错误提示明确“不处理音频/字幕/章节”。

### 维护风险

风险：重复实现 Auto Encode runner 的 Av1an 调用逻辑，后续修 bug 需要改两处。

控制：

- 第一版只复用 helper，避免大重构。
- 第二轮再抽取共享 `Av1anProcessRunner`。
- 保持 Auto Encode target-quality 语义和 Parallel CRF video 语义分离。

## 推荐实现顺序

1. 后端参数构建和测试。
2. 后端 runner 最小可用。
3. 命令预览。
4. UI 开关和 worker 输入。
5. 输出校验。
6. README / 帮助文案。

## 手动验证清单

- x264 CRF + workers auto。
- x264 CRF + workers 2。
- x265 CRF + HDR metadata。
- SVT-AV1 CRF。
- VapourSynth 输入。
- Y4M / 普通视频输入。
- 取消任务。
- Av1an 不存在。
- 用户参数包含 `--pass`。
- 用户参数包含输出参数。
- 输出目录已有同名文件。

## 最终判断

该功能值得做，但必须保持第一版范围窄：

- 只做 CRF。
- 只做视频。
- 只借 Av1an。
- 不做 two-pass。
- 不做音频、字幕、章节。
- 不自研分块调度。

这样可以把收益集中在多核利用和长任务加速上，同时把质量、拼接、mux、时间戳和维护风险控制在可接受范围内。
