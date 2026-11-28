# 项目 VS 编辑器诊断

2026-11-28 · 核对项目提交 `1557854`，以上游稳定版 `vsjetpack 3.0.0` 为参照。

重构前，两套智能模板中的 Placebo 去色带和 BM3D 使用了 JET 3.0.0 未公开导出的名称，生成的脚本会在导入时报错。本次改为共用滤镜定义，以可在标准 CPU 环境出帧的模板作为默认入口。 [共享模板](FlowEncode/Assets/VapourSynthEditor/filter-templates.json)

## VapourSynth 与 VS 编辑器重构计划

推荐 **Windows x64、Python 3.14 x64**（最低 Python 3.13 x64），以 **Python 3.14.10 和 VapourSynth R82** 作为本轮验收基线。VS 工具链必装 `vsjetpack[full]`、`awsmfunc`、`vapoursynth-fillborders` 和 `vsfpng`。JET 版本交给 pip 按当前 Python 和平台解析，本方案中的 JET 3.0.0 用于核对接口及依赖。新模板和预览按 API 4 实现，旧版 VapourSynth、API 3 插件及旧脚本的兼容不在验收范围内。 [Python 3.14.10](https://www.python.org/downloads/release/python-31410/) · [VapourSynth R82](https://github.com/vapoursynth/vapoursynth/releases/tag/R82) · [JET 3.0.0 包声明](https://github.com/Jaded-Encoding-Thaumaturgy/vs-jetpack/blob/v3.0.0/pyproject.toml)

安装引导已切换到 VapourSynth R82 和不锁版本的 JET `full`，并移除 `vsrepo` 和旧「VS 插件包」入口。默认滤镜和预览使用 CPU 后端，无需另下载模型。 [当前工具链策略](FlowEncode/FlowEncode.Infrastructure/VapourSynthToolchainPolicy.cs)

### 上游依赖与应用安装范围

| 项目 | 本次采用的方式 |
| --- | --- |
| Python 与 VapourSynth | 推荐常规的 Python 3.14 x64 解释器。JET 3.0.0 声明 Python `>=3.13`、VapourSynth `>=78`。FlowEncode 选 R82 作为迁移目标，并用同一环境提供的 `vapoursynth.exe` 和 `vspipe.exe`。VapourSynth 从 R80 起移除 API 3。 [JET 包声明](https://github.com/Jaded-Encoding-Thaumaturgy/vs-jetpack/blob/v3.0.0/pyproject.toml) · [VapourSynth 变更记录](https://github.com/vapoursynth/vapoursynth/blob/R82/ChangeLog) |
| JET `full` | 由所选的 `python.exe -m pip` 从 PyPI 和 VSWheels 解析，应用安装和更新一律请求 `vsjetpack[full]`。按已安装版本的 `Requires-Dist` 与 `Provides-Extra` 检查对应平台的依赖，再用实际滤镜出帧验收。 [JET 仓库与安装说明](https://github.com/Jaded-Encoding-Thaumaturgy/vs-jetpack#installation) |
| `full` 的能力边界 | JET 3.0.0 的 `full` 包括 `basic`、`source`、`kernels`、`expr`、`rg`、`mask`、`scale`、`aa`、`denoise`、`dehalo`、`deinterlace`、`deband`。它包含 BestSource、FFMS2、L-SMASH、缩放、反交错和 CPU 降噪等依赖。`gpu`、`cl`、`vulkan`、`nvidia`、`amd` 是单独的 extras。FlowEncode 的内置模板选用 CPU 后端，`full` 仍由 pip 按上游声明安装。 [JET 3.0.0 依赖定义](https://github.com/Jaded-Encoding-Thaumaturgy/vs-jetpack/blob/v3.0.0/pyproject.toml#L65) |
| `awsmfunc` | 保留现有安装、检测、升级和功能入口。`vs-rekt` 由 pip 按 `awsmfunc` 的依赖声明处理。设置引导安装 `awsmfunc` 时一并安装 `vapoursynth-fillborders` 和 `vsfpng`，并检查两个插件是否已加载。补边缺少后端时提示缺项；PNG 后端不可用时仍可使用内置写入。 [awsmfunc 包声明](https://github.com/OpusGang/awsmfunc/blob/1.3.5/pyproject.toml) · [FillBorders](https://pypi.org/project/vapoursynth-fillborders/) · [vsfpng](https://pypi.org/project/vsfpng/) |
| 旧安装项 | `vsrepo`、旧「VS 插件包」从设置引导和环境判断移除。现有用户环境里的软件包和插件文件由用户管理。重构后的模板和诊断移除 `mvsfunc`、`havsfunc` 的调用和依赖。 |

## 分阶段落地计划

### 阶段 0 · 建立可复现的验收环境

- [ ] 在 Windows x64 用 Python 3.14 新建隔离环境。安装 VapourSynth R82 后，运行该环境的 `vapoursynth.exe config`。安装 JET `full` 和 `awsmfunc`，执行 `pip check`，记录实际解析版本、Python 路径及索引，便于复现。 [VapourSynth R82 安装说明](https://github.com/vapoursynth/vapoursynth/blob/R82/doc/installation.rst) · [JET 安装说明](https://jaded-encoding-thaumaturgy.github.io/JET-guide/master/basics/installation/)
- [ ] 分别准备干净安装环境、只有 JET 基础包的环境，以及缺少必装插件的修复场景。干净环境跑通固定格式测试脚本和短 MKV 源，确认 `vspipe --info` 和实际取帧均成功。用绝对路径记录 Python 和 `vspipe` 来源。
- [ ] 建立短样片集，覆盖 BT.709 Limited、Full、未标记范围、HDR10（PQ）、HLG、交错素材及带多个 `set_output()` 的脚本。保存每个样片的预期帧数、帧率、格式和色彩信息。

在 Python 3.14 x64 创建的测试环境中执行下列命令。所有命令使用该环境的可执行文件。

```powershell
python -m pip install 'VapourSynth==82'
vapoursynth config
python -m pip install 'vsjetpack[full]' --extra-index-url https://jaded-encoding-thaumaturgy.github.io/vs-wheels/simple
python -m pip install awsmfunc vapoursynth-fillborders vsfpng
python -m pip check
vspipe --version
vspipe --info .\smoke.vpy
vspipe --start 0 --end 0 .\smoke.vpy --
```

`smoke.vpy` 内容如下。

```python
import vapoursynth as vs

clip = vs.core.std.BlankClip(width=32, height=32, length=2, format=vs.YUV420P8)
clip.std.SetFrameProps(_Range=0).set_output()
```

**通过条件**　干净环境可以在无旧 VS 插件目录的情况下安装并出帧。手动验收锁定的 VapourSynth 版本、实际解析的 JET 版本和缺失的可选后端都有记录。

### 阶段 1 · 切换设置引导和运行时选择

- [ ] 设置引导保留 `Python`、`VapourSynth`、`awsmfunc` 和 `vsjetpack[full]`。安装顺序为 Python → VapourSynth → `awsmfunc` 与 JET。JET 安装和手动更新统一使用 `full` 与 VSWheels，显示已装版本，更新时由 pip 解析。旧 `vsrepo` 和「VS 插件包」移出首次启动、设置卡片和就绪报告。
- [ ] 更新 `VapourSynthToolchainPolicy`、`ToolchainBaseline` 和 Python 发现逻辑，将已验证的 R82 接入推荐安装。默认优先匹配 Python 3.14 x64，保留用户显式选择路径的机制。验证设置引导、`VapourSynthRuntimePathResolver`、语言服务、Python 预览进程和编码实际使用配对的 Python 与 `vspipe`。检查旧缓存失效后会重新检测当前环境。 [当前工具链策略](FlowEncode/FlowEncode.Infrastructure/VapourSynthToolchainPolicy.cs) · [运行时路径解析](FlowEncode/FlowEncode.Infrastructure/VapourSynthRuntimePathResolver.cs)
- [ ] 先核对 `full` 所需包及版本，再检查常用源滤镜、`awsmfunc` 导入和叠字所需的 `core.sub.Subtitle`。只装基础包时显示依赖不完整，说明缺失项并允许补装。测试安装中断、索引不可用和重试。设置引导用能力检测区分包可导入、核心插件已加载以及滤镜能出帧。
- [ ] 核对预期保留的安装记录、卸载权限和外部 Python 路径。测试同时存在两套 Python 时，卡片状态、安装目标和出帧进程使用同一个解释器。清理已失去入口的旧清单、探测与测试夹具，保留仍被持久化格式使用的标识值。

**通过条件**　移除两张旧卡片后，标准安装使用同一套 Python 与 VapourSynth。只装 JET 基础包时显示缺项，补齐 `full` 后能在同一入口更新、探测并出帧。

### 阶段 2 · 统一帧属性与色彩转换

- [ ] 在 `preview_host.py` 的 SDR 转换、HDR 色调映射、`VapourSynthFramePropertyProbe.py`、`SourceVideoInfoProbe.cs` 和两套 BT.709 模板统一使用 `_Range`。取值是 **`0 = Limited`、`1 = Full`**。BT.709 模板写入 `_Range=0`，此操作只设置标签；实际转换在缩放或色彩转换阶段完成。 [R82 帧属性定义](https://github.com/vapoursynth/vapoursynth/blob/R82/doc/apireference.rst#reserved-frame-properties)
- [ ] 源属性探测将 `0` 映射为 `tv`、`1` 映射为 `pc`，缺失或未知返回未标记。预览的显示默认值单独处理，保留用户手动选择的输入范围和矩阵，逐项验收黑位、白位和 HDR 转 SDR。

**通过条件**　Limited、Full、未标记范围均按各自规则显示，源属性报告与预览默认值可区分。打开、跳帧、HDR 映射与导出均无 `_ColorRange` 弃用警告。

### 阶段 3 · 迁移 VS 编辑器的智能代码

- [ ] 让「智能插入」和快捷补全复用同一套模板定义。模板描述导入项、参数、依赖和要插入的代码，两端各自处理界面交互。验证别名复用、重复导入、选中目标变量、已有输出编号和撤销。覆盖默认 `.vpy`、插入源文件、参数补全与诊断快捷修复。
- [ ] 去色带使用 CPU 版 F3K 和细节保护模板。迁移 BM3D、源加载、位深、缩放、IVTC、QTGMC、时域降噪、DFTTest、加颗粒和 NLM。按下方滤镜清单选用公开接口。每个模板用当前 JET 稳定版实测签名、默认参数、适用格式与后端。QTGMC 验收场序及单倍、双倍输出帧率，IVTC 验收场匹配与去重后的帧率。
- [ ] 两个入口都保留 `awsmfunc.bbmod()`、`awsmfunc.fb()`、`awsmfunc.FrameInfo()`。补边在缺后端时提示用户，叠字验收标题、帧号和帧类型。移除旧模板中的 `mvsfunc`、`havsfunc` 及相关快捷修复。先跑通已有能力，再按下方优先级添加可验证的新滤镜。

**通过条件**　每个内置滤镜从菜单和补全分别生成一次脚本，两边导入、参数和依赖提示一致。默认模板实际取首帧，时域滤镜补测中段与末尾。缺少可选后端时提示具体依赖。

### 阶段 4 · 适配预览、对比与编码

- [ ] 预览界面沿用现有设计。在 `preview_host.py`、`VapourSynthPreviewService` 和 `PreviewCompareScriptBuilder` 验证脚本执行、输出索引、帧读取、缓存命中、跳帧、多输出切换及关闭后的资源释放。对比脚本与编辑器源加载使用同一套经过验证的源滤镜策略。
- [ ] 验收 SDR 和 HDR 映射路径，覆盖 `vszip.Tonemap` 和插件不可用时的内置转换。用固定时间点对比预览画面、像素取样和 PNG 的结果。默认用 `vsfpng` 导出，同时覆盖插件不可用时的内置 PNG 写入。
- [ ] 保留边线修复脚本生成，回归自动裁切、帧对齐和质量扫描。CAMBI 等有独立后端的功能按检测结果启用。对同一 `.vpy` 做单源和双源预览，并用配对的 `vspipe` 向编码器送短片段，核对帧数、尺寸、帧率与色彩参数。 [预览服务](FlowEncode/FlowEncode.Infrastructure/VapourSynth/VapourSynthPreviewService.cs) · [对比脚本生成](FlowEncode/FlowEncode.Application/PreviewCompareScriptBuilder.cs)

**通过条件**　预览、截图和短片段编码在同一环境实际运行。缺少可选后端时，相关功能给出具体提示，基础预览和编码仍可使用。

### 阶段 5 · 集成验收与发布

- [ ] 运行现有 .NET 测试和 Windows x64 构建，重点覆盖安装参数、依赖探测、路径选择、模板生成、源属性和预览。分别在干净环境、只有 JET 基础包和已装 JET `full` 的环境验收。另测从现有 FlowEncode 设置升级后，用户显式选择的有效路径仍可使用。
- [ ] 对 Windows 安装包走一次完整流程，从首次设置、安装依赖、打开 `.vpy`、插入滤镜到预览、导出和编码。核对中英文 README、官网与发行说明。给用户写清 Python 3.14 推荐版本、必装依赖、可选后端和旧脚本涉及的手动替换。发布前核对 [发布流程](.github/RELEASING.md)。

**通过条件**　干净安装和升级验收均通过，验收记录包含运行时版本、滤镜版本、样片结果和缺失后端的表现。用户文档只写已经可用的能力。

### 验收标准

| 场景 | 通过条件 |
| --- | --- |
| 全新 Windows x64 环境 | 设置引导安装新版运行时、JET 稳定版、`awsmfunc` 和补边、PNG 后端。`pip check` 通过，默认源滤镜能加载文件，`vspipe` 和应用预览都能出帧。 |
| 智能插入和快捷补全 | 每项内置模板从两个入口各运行一次。导入、参数和依赖提示一致。验证 `awsmfunc` 边线修复和帧信息叠加能出帧；补边在后端缺失时提示缺项，通过设置引导修复后能出帧。 |
| 色彩范围与预览 | BT.709 模板写入 `_Range=0`。Limited、Full、未标记和 HDR 样片的预览与源属性结果符合各自预期。正常操作日志中的 `_ColorRange` 弃用警告为零。 |
| 预览与编码 | 单源、多输出、双源对比、跳帧、取样和截图均通过。用短片段确认 `vspipe` 能向编码器送帧。 |
| 旧依赖清理 | 安装、就绪检查、模板生成和当前用户文档均已切换到新版。搜索命中仅限历史记录及新版升级说明中对已移除依赖的说明。 |

## 本轮验证记录

- Windows x64 隔离环境以 Python 3.14.10、VapourSynth R82、JET 3.0.0 和 awsmfunc 1.3.5 验证。28 个默认 CPU 模板通过出帧，QTGMC、BWDIF 和 IVTC 核对了输出帧率。
- 验证了 Limited、Full、未标记范围、PQ、HLG、双源预览、共享内存取帧及 CPU 质量指标。内置 PNG 回退可用，`vspipe` 向 x264 送入 12 帧并完成编码。
- Windows x64 Release 构建及 152 项针对性 .NET 测试通过。安装包的首次启动界面和发布流程留待发版验收。

## VS 智能滤镜调整清单

范围是 FlowEncode 的「智能插入」菜单和快捷补全模板。菜单和快捷补全使用同一份定义。 [现有菜单](FlowEncode/Controls/VapourSynth/VapourSynthWorkspaceView.xaml.cs#L752) · [现有补全](FlowEncode/Assets/VapourSynthEditor/editor.js#L1308)

### 移除旧入口

| 移除的模板或分支 | 处理方式 |
| --- | --- |
| `havsfunc.QTGMC`、`havsfunc.SMDegrain` | 保留反交错和时域降噪，分别改用 `QTempGaussMC().deinterlace(...)` 和 `vsdenoise.mc_degrain()`。菜单里的 SMDegrain 改称「运动补偿时域降噪」。 [QTGMC](https://jaded-encoding-thaumaturgy.github.io/vs-jetpack/api/vsdeinterlace/qtgmc/) · [时域降噪](https://jaded-encoding-thaumaturgy.github.io/vs-jetpack/api/vsdenoise/funcs/#vsdenoise.funcs.mc_degrain) |
| `havsfunc.FineDehalo` 菜单项及补全分支 | 合并为一个 `vsdehalo.fine_dehalo()` 条目。 |
| `core.placebo.Deband` 与失效的 `vsdeband.Placebo.deband` | 移除两个 Placebo 快捷项。去色带使用 CPU 版 `vsdeband.f3k_deband()`，另提供细节保护模板。 [去色带接口](https://jaded-encoding-thaumaturgy.github.io/vs-jetpack/api/vsdeband/debanders/) |
| `vsdenoise.BM3D.denoise` 和补全里的原生 `bm3dcuda`／`bm3d` 分支 | 合并为 `vsdenoise.bm3d()`。由模板按实际后端能力提供参数。 [BM3D 接口](https://jaded-encoding-thaumaturgy.github.io/vs-jetpack/api/vsdenoise/blockmatch/) |
| `core.neo_f3kdb.Deband`、`core.grain.Add`、`core.dfttest.DFTTest`、`core.knlm.KNLMeansCL` | 功能继续提供，分别改用 `vsdeband.f3k_deband()`、`vsdeband.Grainer`、`vsdenoise.DFTTest().denoise(...)` 和 `vsdenoise.nl_means()`。NLM 默认验证 JET 的 CPU 后端。 [颗粒接口](https://jaded-encoding-thaumaturgy.github.io/vs-jetpack/api/vsdeband/noise/) · [DFTTest](https://jaded-encoding-thaumaturgy.github.io/vs-jetpack/api/vsdenoise/fft/) · [NLM](https://github.com/Jaded-Encoding-Thaumaturgy/vs-jetpack/blob/v3.0.0/vsdenoise/nlm.py) |

仓库的智能菜单和补全中没有独立的 `mvsfunc` 滤镜条目。它随旧插件包、探测和诊断引用一起清理。

### 保留并更新的入口

- 视频源、位深转换、Spline36、逆缩放和 IVTC 继续提供，迁移至 `vssource`、`vstools`、`vskernels` 及 `vsdeinterlace.vfm()`／`vdecimate()` 的对应接口。 [源滤镜](https://github.com/Jaded-Encoding-Thaumaturgy/vs-jetpack/blob/v3.0.0/vssource/indexers/misc.py) · [缩放接口](https://jaded-encoding-thaumaturgy.github.io/vs-jetpack/api/vskernels/) · [IVTC](https://jaded-encoding-thaumaturgy.github.io/vs-jetpack/api/vsdeinterlace/ivtc/)
- 保留 `based_aa()`、`fine_dehalo()`、`replace_ranges()`、裁切、遮罩合并、BT.709 帧属性、HDR 转 SDR 和双路输出对比。BT.709 使用 `_Range=0`，HDR 转 SDR 使用 `vszip.Tonemap`，其中源色彩 `1` 为 PQ、`2` 为 HLG。
- 保留 `awsmfunc.bbmod()` 边线修复、`awsmfunc.fb()` 补边和 `awsmfunc.FrameInfo()` 帧信息叠加。帧信息继续使用 `FrameInfo()` 显示标题、帧号和帧类型。 [awsmfunc 1.3.5 源码](https://github.com/OpusGang/awsmfunc/blob/1.3.5/src/awsmfunc/base.py)

### 本次新增的滤镜

| 优先级 | 条目 | 用途 |
| --- | --- | --- |
| 首批 | BWDIF 快速反交错 · `vsaa.BWDIF` | 为交错素材提供快速反交错，支持单倍或双倍帧率。 [BWDIF](https://jaded-encoding-thaumaturgy.github.io/vs-jetpack/api/vsaa/deinterlacers/) |
| 首批 | 色度降噪 · `core.zsmooth.CCD()` | 单独处理色斑和彩色噪点。 [JET 依赖声明](https://github.com/Jaded-Encoding-Thaumaturgy/vs-jetpack/blob/v3.0.0/pyproject.toml) |
| 首批 | 压缩块修复 · `vsdenoise.deblock_qed()` | 处理低码率素材中明显的块状边界。 [Deblock QED](https://jaded-encoding-thaumaturgy.github.io/vs-jetpack/api/vsdenoise/deblock/#vsdenoise.deblock.deblock_qed) |
| 进阶 | 细节保护去色带 · `vsdeband.deband_detail_mask()` 配合去色带滤镜 | 生成细节遮罩，让平坦区域去色带，减少对纹理和线条的影响。 [细节遮罩](https://jaded-encoding-thaumaturgy.github.io/vs-jetpack/api/vsdeband/mask/) |
| 进阶 | 边缘亮度修正 · `vsdehalo.FixBorderBrightness` | 按指定行列校正亮边或暗边。 [边缘亮度接口](https://jaded-encoding-thaumaturgy.github.io/vs-jetpack/api/vsdehalo/border/) |
| 进阶 | Rescale · `vsscale.Rescale` | 按指定源高度逆缩放，再用 CPU 的 Spline36 重建为输入分辨率。 [Rescale](https://jaded-encoding-thaumaturgy.github.io/vs-jetpack/api/vsscale/rescale/) |

## 重构前的模板诊断

| 滤镜 | 重构前插入的调用 | 核对过的 JET 3.0.0 接口 |
| --- | --- | --- |
| Placebo 去色带 | `from vsdeband import Placebo`，再调用 `Placebo.deband(...)` | `from vsdeband import placebo_deband`，再调用 `placebo_deband(...)`。 [JET 源码](https://github.com/Jaded-Encoding-Thaumaturgy/vs-jetpack/blob/v3.0.0/vsdeband/debanders.py#L297) |
| BM3D 降噪 | `from vsdenoise import BM3D`，再调用 `BM3D.denoise(...)` | `from vsdenoise import bm3d`，再调用 `bm3d(...)`。 [JET 源码](https://github.com/Jaded-Encoding-Thaumaturgy/vs-jetpack/blob/v3.0.0/vsdenoise/blockmatch.py#L554) |
| QTGMC、SMDegrain | `havsfunc.QTGMC(...)`、`havsfunc.SMDegrain(...)` | `vsdeinterlace.QTempGaussMC`、`vsdenoise.mc_degrain()`。参数和默认行为有差异，迁移时需重新核对。 [QTGMC 指南](https://jaded-encoding-thaumaturgy.github.io/JET-guide/master/filtering/situational/qtgmc/) · [mc_degrain 接口](https://jaded-encoding-thaumaturgy.github.io/vs-jetpack/api/vsdenoise/funcs/#vsdenoise.funcs.mc_degrain) |
| DFTTest、F3KDB、加颗粒、KNLMeansCL | `core.dfttest.DFTTest`、`core.neo_f3kdb.Deband`、`core.grain.Add`、`core.knlm.KNLMeansCL` | JET 提供 `vsdenoise.DFTTest`、`vsdeband.f3k_deband()`、`vsdeband.Grainer`、`vsdenoise.nl_means()`。旧调用是否可用取决于对应的 DLL，即使已安装 `vsjetpack[full]` 也要逐项检查。 [JET 降噪接口](https://jaded-encoding-thaumaturgy.github.io/vs-jetpack/api/vsdenoise/) · [JET 去色带接口](https://jaded-encoding-thaumaturgy.github.io/vs-jetpack/api/vsdeband/debanders/) · [Grainer](https://jaded-encoding-thaumaturgy.github.io/vs-jetpack/api/vsdeband/noise/) |

抗锯齿 `based_aa`、去光晕 `fine_dehalo` 和区间替换 `replace_ranges` 已使用 JET 接口。重构前模板中的 `core.placebo.Deband`、`core.fmtc.bitdepth` 和 `core.vivtc` 依赖实际加载的 DLL，纳入本次模板迁移。 [菜单模板](FlowEncode/Controls/VapourSynth/VapourSynthWorkspaceView.xaml.cs#L807) · [区间替换补全](FlowEncode/Assets/VapourSynthEditor/editor.js#L1524)

## 依赖检查与待验证项

菜单和快捷补全使用运行时返回的 Python 函数和插件函数判断依赖。比如缺少 `core.fb.FillBorders` 时，补边项会提示缺失后端。 [菜单依赖检查](FlowEncode/Controls/VapourSynth/VapourSynthWorkspaceView.xaml.cs#L709) · [模块探测](FlowEncode/Assets/VapourSynthEditor/python/introspect_runtime.py#L137)
