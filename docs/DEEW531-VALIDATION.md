# deew 5.3.1 适配与验收

日期：2026-11-18（Asia/Shanghai）。本文记录当前源码的工具要求和验证范围；5.3.0 的测试结果保留在 [上一份记录](DEEW53-VALIDATION.md)。

## 上游变化与适配

deew 5.3.1 于 2026-11-14 21:21（北京时间）发布。CLI 的主要变化是：TrueHD Atmos 只测量改用 `dee_measure_loudness` 测量 Atmos 母版的多声道响度，跳过双耳响度结果。JSONL 事件字段保持兼容。GUI/Web 新增缩放和系统字体设置，由上游界面使用。

依据：[5.3.1 发布说明](https://github.com/pcrlnd/deew/releases/tag/5.3.1)、[5.3.0→5.3.1 源码差异](https://github.com/pcrlnd/deew/compare/5.3.0...5.3.1)。

- 当前源码最低要求 **deew 5.3.1**。工具检测、执行检查、事件版本检查、MCP 描述和设置提示使用同一要求，避免旧版仍走另一套 Atmos 测量流程。
- TrueHD Atmos 只测量需要 `truehdd` 和 `dee_measure_loudness`，不再要求 `dee_ddpjoc_encoder`。桌面预览、MCP 计划和执行共用此规则。普通 7.0/7.1 输入仍用 `dee_ddp_encoder` 测量。
- 修复低响度输入被误判为测量失败的问题：`measureOnly` 接收低于 −31 dB 的有限测量值，不再套用编码 dialnorm 的 −31..0 范围。运行中进度、结果和重启后的任务历史均保留该值。
- 保留 5.3.0 已有的相移开关、AC4 Atmos DRC、96 kHz TrueHD shim 检查和扩展响度指标。
- 升级时替换 CLI 版 `deew.exe`，保留原 `config.toml` 和完整 DEE 工具集。仍要求授权 DEE ≥6.7.0，推荐 truehdd ≥0.6.3、dee_shim ≥0.2.0。

## 验证状态

- 桌面 Release、Domain/Application/Infrastructure/Tests 和 MCP 编译通过，零警告、零错误。
- 普通回归 **629 / 629 通过**，无失败或跳过。覆盖 Atmos 测量工具选择、5.3.0 版本拒绝、低于 −31 dB 的事件读取、实时 telemetry 和重启后的任务历史。
- 使用项目运行器执行 **23 / 23 组真实短样本**：复跑此前 22 组，再增加低响度源的只测量任务。该样本返回 −52 dB，节目响度 −52.1 LKFS，任务正常完成且不生成媒体文件。
- 96 kHz TrueHD + `film_light` 再次通过无损往返检查：解码后的 PCM 与源 PCM 长度相同、逐字节一致，SHA-256 均为 `8EA808F5259C6B192C7E3DB091FBA11E103546908B8C3C9667C2DF4619255BB7`。88.2 kHz→96 kHz TrueHD 也完成编码。
- MCP stdio 初始化和 16 个工具 schema 检查通过，音频计划描述要求 deew ≥5.3.1，`noPhaseShift` 仍是默认 false 的可选布尔参数。
- 实测工具为 deew 5.3.1、DEE 6.7.0、truehdd 0.6.3、dee_shim 0.2.1+gc1141a0、FFmpeg / ffprobe 9.0.2。下载的 CLI SHA-256 与 GitHub 发布资产摘要一致：`34e42802a74dd440e8d219caa3f2229f7b4c16f850ca72d93c855806e3645b48`。
- 测试使用独立的 deew 目录，所有任务结束后 `config.toml` 均恢复为原始字节；没有替换用户正式目录内的工具。证据保存在 Git 忽略的 `artifacts/validation-deew531/`，包含上游差异、工具版本、TRX、结果 JSON、日志及 PCM 文件。

本机测试使用仓库内的临时目录；生成的测试 runtimeconfig 沿用关闭 `MSTest.EnableParentProcessQuery` 的配置，以避开 testhost 的父进程访问限制，应用配置不受影响。

真实 TrueHD Atmos 测量和 Atmos 编码仍缺少本地素材验收。依赖及子进程测试不能替代真实 Atmos 媒体验证。
