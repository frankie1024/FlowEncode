# FlowEncode 2.0.3 工具链基线

2.0.3 是 2.0 维护线的兼容性修订版。此清单随主程序版本发布；安装、更新、修复与版本提示共用内置清单，不查询上游 latest。

| 组件 | 固定基线 |
| --- | --- |
| x264 | stable-20250608-b35605ace3dd |
| x265 | 4.2（保留 2.0 的工具包能力，不包含 3.0 的 multilib 改造） |
| SVT-AV1 | 4.1.0 |
| FFmpeg / FFprobe | Gyan 8.1 Full Shared，同包部署 |
| Av1an | 0.5.2-flowencode.2 |
| Python | 3.12.10 x64；自动安装 VS 包只选择 3.12 ABI |
| VapourSynth | R79 |
| vsrepo / awsmfunc / vsjetpack | 2.0.0 / 1.3.5 / 2.2.2 |
| VS 插件 | 内嵌 VsrepoBaseline.json：每个组件仅保留一个发布及其依赖和校验值 |
| deew / DEE | 用户自备 deew 3.x / DEE 5.2.1；拒绝将 deew 5.x 标为可用 |

编码器、FFmpeg 和 Av1an 的标签、资产名称、下载地址、SHA256 均固定在 `LegacyToolchainBaseline`。自动安装拒绝清单外的包，即使调用方保留了其他版本的缓存。Python 安装器保持固定地址、摘要和发布者签名检查；pip 安装使用精确包版本和完整传递依赖约束。vsrepo 在子进程中读取独立的固定数据库，不刷新或覆盖用户原有数据库。

主程序不能阻止用户或其他软件覆盖 PATH 中的工具。环境页对清单外版本提示未验证，并提供基线安装入口。已经安装的其他版本不会在启动时被自动覆盖。手工提供的 DGDemux、eac3to、opusenc 等工具无自动更新入口；deew 与 DEE 不随程序分发。

2.0.0–2.0.2 的已发布安装包不会因发布新版本而改变：用户须升级到 2.0.3，才能获得固定基线的更新策略。3.0 的工具链另行维护，不采用本清单。
