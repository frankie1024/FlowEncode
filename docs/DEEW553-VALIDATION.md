# deew 5.5.3 适配与验收

日期：2026-11-22（Asia/Shanghai）。本文记录当前源码对 deew 5.5.3 的适配与验证范围；此前记录保留在 [deew 5.3.1 验收](DEEW531-VALIDATION.md) 与 [3.0.0 验收基线](DEEW5-VALIDATION.md)。

## 上游变化与适配

从 deew 5.4.0 到 5.5.3（5.5.0、5.5.1、5.5.2、5.5.3），上游 CLI 的主要变化包括：

- 5.5.0：新增 TrueHD Atmos 解码参数 `-wm`/`--warp-mode`（`normal`/`warping`/`prologiciix`/`loro`，配置项 `truehdd_warp_mode`）与 `-bc`/`--bed-conform`（`on`/`off`，配置项 `truehdd_bed_conform`）；新增 `-on`/`--output-name` 与 `-ot`/`--output-template`（配置项 `[output].template`，默认 `{stem}`），并在执行前检查输出重名与覆盖源文件；精简 `-h` 帮助输出。
- 5.5.1：修正低于 88 kbps 的 AAC 编码器延迟裁剪（≤71 kbps 为 1710 样本，72–87 kbps 为 1702 样本），以及 DEE 侧下混增加的 32 样本延迟。
- 5.5.2：管道模式编码（DD、DDP、AC4、AAC 与 `-mo`）输出真实阶段进度；源时长未知时先显示不确定脉冲，读取结束后按实际时长估算后续编码进度。
- 5.5.3：新增单次管道覆盖参数 `-p`/`--pipe`（`auto`/`always`/`never`）；新生成的 `[gui]` 配置包含 `theme = 'system'`。

依据：[5.5.3 发布说明](https://github.com/pcrlnd/deew/releases/tag/5.5.3)、[5.4.0→5.5.3 源码差异](https://github.com/pcrlnd/deew/compare/5.4.0...5.5.3)。

FlowEncode 的适配内容：

- 当前源码最低与推荐版本均更新为 **deew 5.5.3**，同步用于工具探测、执行检查、事件版本检查、MCP 能力查询与设置提示。仍要求授权 DEE ≥6.7.0，推荐 truehdd ≥0.6.3、dee_shim ≥0.2.0。升级时替换 CLI 版 `deew.exe`，保留原有 `config.toml` 和完整 DEE 目录。
- 新增 `ac4-ims` 格式与 `--ac4-ims-mode`（`general`/`music`），按需校验 `dee_ac4ims_encoder`（TrueHD Atmos 输入额外要求 `truehdd`），在 `music` 模式下限定 DRC 为 `music_light`，拒绝单声道/立体声与 `-dm`/`-dl`/`-dn`。
- 支持 `-sps`/`-lpf`/`-s3a` 开关参数，继续兼容旧配置和高级参数中的 `-nps`/`--no-phase-shift`；`-s3a off` 可覆盖配置中的 `surround_3db_attenuation = true` 并消除 5.1/7.1 TrueHD 衰减提示。
- 支持 `-wm`/`--warp-mode`、`-bc`/`--bed-conform`（适用于 Atmos 格式、`ac4-ims` 与 `-mo`），以及单次管道覆盖 `-p`/`--pipe`（同步作用于 PCM 临时空间估算）。
- 完整实现 deew 5.5 的输出命名规则（`-on`、`-ot` 与 `[output].template`，含 9 项占位符、`DELAY` 标签剥离、空值与分隔符清理、非法字符替换和扩展名校验），在 `ffprobe` 探测中读取音轨 `language`/`title` 标签；桌面输出预览、覆盖确认与 MCP 计划均按真实输出文件名预测与校验。
- `DeewConfigEditor` 校验并原样保留 `low_pass_filter`、`truehdd_warp_mode`、`truehdd_bed_conform`、`[output].template`、`[default_bitrates].ac4_ims`、`[default_modes].ac4_ims` 与 `[gui].theme`。
- 修复 `deew 5.5.2+` 进度条与共享任务进度适配：`DeewEventsReader` 改为按实际执行阶段（是否管道、是否含 `decode` / `measure`、是否 `-mo`）连续分配 `0%–100%` 区间，消除管道编码直接跳至 35% / 45% 及 Atmos 只测量在 `measure` 前 35% 停顿的问题；`DeewAudioExecution` 与 `TaskCoordinator` 收到 `percent: null` 脉冲事件时保留已有进度，避免共享任务快照与 MCP 查询回退为 `null`。

## 验证状态

- `FlowEncode.Domain`、`FlowEncode.Application`、`FlowEncode.Infrastructure`、`FlowEncode.ToolStub`、`FlowEncode.Domain.Tests`、`FlowEncode.Mcp` 与 WinUI 桌面 Release x64 均编译通过，零警告、零错误。
- 非烟测回归测试通过 **667 / 668**（因隔离测试目录位于 `%TEMP%`，`VersionMetadataScriptTests` 未定位仓库根目录；在仓库根目录直接执行 `scripts/sync-version-metadata.ps1 -Check` 通过）。全部 deew 契约、子进程执行、输出命名、配置租约和 MCP 计划测试均通过。
- 使用项目运行器与独立隔离目录执行 **27 / 27 组真实短样本**：
  - 17 组常规覆盖：DD、DDP、DDP Blu-ray、指定 dialnorm、只测量、AAC、TrueHD、AC4 指定 dialnorm，以及 7.1/7.0/DTS:X 组合，均通过 FFprobe 编码格式核对。
  - 10 组扩展覆盖：DD/DDP 关闭相移（`-sps off`）、`-p always` + `-on custom-ddp-output`（实际生成 `custom-ddp-output.ec3` 并收到 7 次实时进度更新）、`-p never` + `-ot "{stem}.{format}.{channels}"`（实际生成 `odyssey-20s.ddp.5.1.ec3`）、`ac4-ims` 默认档位与 `music` 档位 + `-on custom-ims-music`（实际生成 `.ac4` 与 `custom-ims-music.ac4`）、非 Atmos 源在 Atmos 格式下的只测量（−15 dB）、低响度只测量（−52 dB）、96 kHz TrueHD + `film_light` 与 88.2 kHz→96 kHz TrueHD。
  - 96 kHz TrueHD 解码后的 PCM 与源 PCM 逐字节一致。每次运行后 `config.toml` 均恢复为原始字节。
- 实测工具：deew 5.5.3、DEE 6.7.0（含 `dee_ac4ims_encoder`）、truehdd 0.6.3、dee_shim 0.2.1+gc1141a0、FFmpeg / ffprobe 9.0.2。下载的 CLI 版 `deew.exe` SHA-256 与 GitHub 发布摘要一致：`5bacc9fc5f13a2e5de8ceaae8f73976b28e3d61eba8abc7aec77a6c60ba3e6cb`。

真实 TrueHD Atmos 测量、Atmos 编码以及 `-wm`/`-bc` 在真实 Atmos 素材上的解码结果仍待素材验收；当前由参数校验、依赖检查与子进程测试桩覆盖。
