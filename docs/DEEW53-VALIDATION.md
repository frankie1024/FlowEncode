# deew 5.3 适配与验收

日期：2026-11-14（Asia/Shanghai）。本文保留 deew 5.3.0 的适配与验收记录；后续 3.0.3 适配了 [deew 5.3.1](DEEW531-VALIDATION.md)。此前 3.0.0 发布版的 5.1 基线保留在 [原验收记录](DEEW5-VALIDATION.md)。

## 工具基线与升级

- 最低 deew **5.3.0**；仍按具体任务检测授权 DEE **≥6.7.0** 的所需组件。
- 本次实测：deew 5.3.0、DEE 6.7.0、truehdd 0.6.3、dee_shim 0.2.0+g396caf4、FFmpeg / ffprobe 9.0.2。
- 推荐 truehdd ≥0.6.3、dee_shim ≥0.2.0。Windows 下较旧的 shim 不支持 DEE 命名管道 URI，deew 会在相关 DDP 任务回退到临时 WAV。
- 将 CLI 版 `deew.exe` 替换到应用实际固定的路径即可；保留原 `config.toml` 和完整 DEE 目录，无需用 `-gc` 重置用户配置。工具不随项目分发。
- 5.1/5.2 会明确提示升级，避免新默认值或新参数被旧执行器错误解释。此最低版本同步用于工具检测、运行前检查、事件校验、设置提示和 MCP 能力查询。

上游依据：[5.2.0](https://github.com/pcrlnd/deew/releases/tag/5.2.0)、[5.3.0](https://github.com/pcrlnd/deew/releases/tag/5.3.0)、[5.1→5.3 源码差异](https://github.com/pcrlnd/deew/compare/5.1.0...5.3.0)、[truehdd 0.6.3](https://github.com/truehdd/truehdd/releases/tag/0.6.3)、[dee-shim 0.2.0](https://github.com/truehdd/dee-shim/releases/tag/v0.2.0)。

## 已适配行为

| 范围 | 行为 |
| --- | --- |
| 环绕相移 | 新增 `noPhaseShift` 面板选项与 MCP 参数；支持 `-nps` / `--no-phase-shift`，仅 DD、DDP、DDP Atmos 编码可用。 |
| AC4 Atmos DRC | 支持并默认使用 `none`；普通 AC4 继续默认 `music_light`，拒绝 `none`。默认预览与执行一致。 |
| 只测量 | 测量原始输入，忽略编码格式及 downmix、DRC、码率、dialnorm、相移、LFE、编码档位。界面允许 Atmos 格式下选择只测量，并隐藏编码专用面板。 |
| Atmos 测量依赖 | 按输入真实 codec/profile 判断；TrueHD Atmos 使用 `truehdd` + `dee_ddpjoc_encoder`，普通 7.0/7.1 使用 `dee_ddp_encoder`，其余支持声道使用 `dee_measure_loudness`。MKV/MKA/M2TS 可用于只测量。 |
| TrueHD 依赖 | 所有实际编码为 96 kHz 的普通 TrueHD 均要求 shim，无论 DRC；包括 ≥72 kHz、按 deew 规则重采样到 96 kHz 的输入。预览、MCP 计划及执行共用输入元数据。 |
| 扩展响度 | 接收门控方式、对白占比、节目响度（LKFS）、响度范围（LU）、真峰值（dBTP）；增量事件保留有效字段，忽略缺失和无效扩展值。AC4 编码目标不覆盖源测量结果。 |
| 结果与历史 | 指标进入运行中 telemetry、可复制的桌面结果面板、MCP `audioTelemetry.loudnessMetrics` 和持久化历史；旧历史缺少新字段时仍可读取。 |
| 管道与配置 | 保留可选 `pipe`、`surround_phase_shift` 布尔配置及注释；旧配置缺项时跟随上游默认 true。管道格式不再按完整临时 PCM 误报空间需求，`pipe=false` 继续走临时文件。 |

上游的 truehdd 损坏提示、格式变化错误和子工具错误继续经过现有 warning/error、退出码和新产物检查传递；`done` 仍不能单独证明任务成功。

## 真实短样本

通过项目运行器完成 **22 / 22** 个真实任务：

- 复跑原有 17 组：DD、DDP、AAC、TrueHD、AC4 指定 dialnorm、只测量、7.1/7.0 standard/bluray、DTS:X 兼容准备和保留临时文件。
- 新增 5 组：DD/DDP 关闭相移各一组；输出选择为 TrueHD Atmos 时测量普通 DTS 源一组；96 kHz TrueHD + `film_light` 和 88.2 kHz→96 kHz TrueHD + `film_light` 各一组。
- 96 kHz 使用 24 位双声道短样本。TrueHD 解码为原始 PCM 后，与源 PCM **长度相同、逐字节一致**；SHA-256 同为 `8EA808F5259C6B192C7E3DB091FBA11E103546908B8C3C9667C2DF4619255BB7`。
- 新响度字段实际返回：dialogue gating、对白 22%、节目响度 −12.5 LKFS、响度范围 7.9 LU、真峰值 −1 dBTP。各任务均检查终态，原有 17 组还用 ffprobe 核对输出；每组配置均恢复为原始字节。

本地证据位于 Git 忽略的 `artifacts/validation-deew53/`：工具版本、普通回归 TRX、真实烟测 TRX、两组结果 JSON、日志、媒体和 96 kHz PCM 比较文件。没有处理长片，也没有替换用户正式工具目录内的二进制。

## 构建、回归与范围

普通回归 **622 / 622 通过**，无失败或跳过。桌面 Release、Domain/Application/Infrastructure/Tests 和 MCP 均编译通过，零警告、零错误。MCP stdio 初始化及 16 个工具 schema 验证通过，`noPhaseShift` 为可选布尔参数、默认 false。

新增契约与子进程回归覆盖相移格式限制、AC4/Atmos DRC 区分、测量参数忽略、源驱动工具选择、高采样率 TrueHD 依赖、可选配置保留、扩展事件容错、实时 telemetry 和重启后的历史指标。Windows 测试进程使用仓库内临时目录；仅在生成的测试 runtimeconfig 关闭父进程查询，以避开本机 testhost 的父进程访问限制，未修改应用运行配置。

**真实 TrueHD Atmos 测量和 Atmos 编码尚无媒体验收。** 这部分由参数、依赖与子进程测试覆盖，不能把上述普通 DTS 测量案例视为 Atmos 实测。损坏/变格式 Atmos 输入也未做本地真实素材复现；错误处理依据上游实现和现有失败终态检查。
