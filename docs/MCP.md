# FlowEncode MCP / 本地 AI 接入

FlowEncode 3.0 ships a stdio MCP server in `mcp/FlowEncode.Mcp.exe`. The desktop owns all tasks, settings, tools and process lifetimes. Manual and MCP tasks share the same concurrency limit and output claims.

## 接入 / Connect

1. 安装或解压完整的 FlowEncode 包，保留 `mcp` 与主程序的相对位置。
2. 在桌面应用的设置中开启“本地 MCP 自动化”（默认关闭）。关闭后不再接受新的自动化调用，已接收的任务仍会继续执行。
3. 运行 `mcp\FlowEncode.Mcp.exe --check`，或将设置页中的配置复制到 MCP 客户端。

```json
{
  "mcpServers": {
    "flowencode": {
      "command": "C:\\Program Files\\FlowEncode\\mcp\\FlowEncode.Mcp.exe"
    }
  }
}
```

Use your actual installation path. `FLOWENCODE_DESKTOP_EXE` can select a development desktop executable. The pipe is scoped to the same Windows user and logon session. The bridge starts the desktop when a task tool needs it; `get_app_status` never starts it. No network server or remote access is opened.

## 工作流 / Workflow

Call `get_capabilities`, `probe_media`, and optionally `list_templates` / `get_default_profile`. Create a plan with `create_video_plan`, `create_audio_plan`, or `create_task_plan`. Inspect the resolved command, effective request, warnings and expected outputs. Call `submit_plan(planId, idempotencyKey)` once and retain both values. Track with `get_task(taskId)`. Use `cancel_task(taskId)` and `retry_task(taskId, newIdempotencyKey)` for the exact task.

计划的有效期为 15 分钟。源文件、工具、配置或编码设置变化后，请重新创建计划。若提交后未收到响应，用相同的 planId 和幂等键再次提交，会返回原任务；任务已完成或桌面应用重启后也一样。重新执行失败或已取消的任务时，会生成新 ID。桌面应用退出后，未完成的任务会在历史中标为中断失败，不会自动重跑。

Accepted work continues after the MCP client disconnects. The shared task service tracks manual and MCP tasks; use `list_tasks` and `get_task` to inspect IDs, logs and actual artifacts. Media, diagnostics and retained temporary files are distinct artifact types. Logs are bounded in API responses (`get_task.logLines`: 0–2000).

`overwriteOutput` defaults to false at every MCP entry point. DDP/deew uses an output **directory**; video, Opus and eac3to use an output **filename**. Ordinary video encoding produces a video elementary stream; it does not automatically mux source audio or subtitles.

## deew 示例 / Example

`create_audio_plan` arguments:

```json
{
  "sourcePath": "D:\\media\\sample.flac",
  "outputPath": "D:\\output\\sample-ddp",
  "mode": "Ddp",
  "deew": { "format": "ddp", "trackIndex": 0, "keepLog": true },
  "overwriteOutput": false
}
```

Track indexes count audio streams from zero. Omitted options follow the portable deew configuration. `measureOnly` measures the original source without media and ignores encoding format, downmix, bitrate, DRC, dialnorm, phase shift and LFE settings. TrueHD Atmos sources use truehdd and dee_measure_loudness to measure the multichannel Atmos master; the DDP Atmos encoder is not required for measurement. `noPhaseShift: true` disables surround phase shift for DD, DDP and DDP Atmos. FlowEncode sends `-sps off` to deew. Set `keepTemp` to retain task intermediates and `keepLog` to save diagnostics. Current source requires deew ≥5.4.0 and the licensed DEE ≥6.7 components for the selected task. We recommend truehdd ≥0.6.3 and dee_shim ≥0.2.0. With deew 5.4.0, plans warn when the config applies 3 dB of surround attenuation to a 5.1 or 7.1 TrueHD output. AC4 Atmos supports DRC `none` by default; plain AC4 uses `music_light` and rejects `none`. TrueHD encoded at 96 kHz requires dee_shim regardless of DRC, including inputs resampled to that rate. Genuine Atmos media acceptance remains pending; Atmos output is experimental.

Audio task snapshots expose `audioTelemetry`, including `loudnessMetrics` when available: `gating` (`dialogue` / `level`), `speechPercent`, `programLoudnessLkfs`, `loudnessRangeLu` and `truePeakDbtp`. These optional measurements remain available in completed task history after reconnecting or restarting. Units are LKFS, LU and dBTP respectively; they are separate from the rounded `deewDb` value. Measure-only loudness can be below −31 dB; it is not restricted to the encoder dialnorm range. Missing fields remain null.

## Tool list

`get_app_status`, `launch_desktop_app`, `get_capabilities`, `probe_media`, `list_templates`, `get_default_profile`, `create_video_plan`, `create_audio_plan`, `create_task_plan`, `submit_plan`, `list_tasks`, `get_task`, `cancel_task`, `retry_task`, `scan_bluray_disc`, `scan_bluray_playlist`.

The generic plan accepts a typed `TaskWorkload` for Video, Audio, BluRay or AutoCompression. For BluRay, obtain actual playlist and track records using the scan tools before creating the request. Full Av1an and Blu-ray media workflows still require suitable local dependencies and source fixtures; the beta real MCP acceptance covers video and audio.

## Diagnostics

An `automation_disabled` error means the desktop switch is off. `not_found` identifies a missing task/plan. Stale plans, missing dependencies and output conflicts are rejected before execution or at the final queued preflight. The bridge writes diagnostics to stderr and reserves stdout for MCP JSON-RPC.

In restricted development hosts that deny a freshly built apphost access to named pipes, a diagnostic client may use `dotnet FlowEncode.Mcp.dll` with .NET 8 installed. This was the local acceptance harness fallback; it does not change the desktop pipe ACL or bypass the automation switch.
