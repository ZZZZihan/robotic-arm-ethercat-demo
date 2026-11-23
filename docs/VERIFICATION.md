# 验证记录

2026-10-26：本次验证限定新增demo，不运行原厂exe、不连接实体板卡、不配置网卡、不刷写固件。

## 本机已执行

- 官方.NET SDK一次性下载到临时目录，并与微软发布元数据SHA-512核对后使用。
- WinForms目标.NET Framework 4.8在macOS交叉编译通过，0警告、0错误；这只证明编译，不代表Windows窗体已实际运行。
- 协议手工向量与错误帧检查38项通过。
- 离线模型/UDP回环检查37项通过，涵盖单次请求、来源与旧挑战拒绝、超时、运行中取消，以及取消/超时后固定端口重用。加上38项协议断言，共75项核心检查。
- 第三位子Agent未参与实现，独立复核协议、运输、模拟模型与UI，当前静态版本未发现提交阻塞；建议的运行中取消与重用验证已补充。

## Windows集成验证

由 [PR #1 检查页](https://github.com/ZZZZihan/robotic-arm-ethercat-demo/pull/1/checks) 的 `Offline verification` 工作流执行。Windows任务编译Windows .exe，再以 `--ui-smoke-test` 实际打开窗体；Linux任务独立执行相同核心检查。

已完成的 [基线运行 c6aaf26](https://github.com/ZZZZihan/robotic-arm-ethercat-demo/actions/runs/40276329760) 中，Windows和Linux均通过75项核心检查，Windows通过15项离线界面检查。首次CI只检出一层历史，导致代码格式检查无法找到父提交；补足检出深度后该步骤已通过。

基线截图的反馈注释被较矮窗口裁切。当前版本已压缩布局、为注释单独保留空间并增加可见性断言；最新Windows检查共17项，另外保存 `hardware-ui.png` 供视觉复核。交付包的 `交付清单.json` 记录实际下载的CI运行、源码提交和文件SHA-256；各运行的成品与截图保存在工作流 `windows-demo` 产物中。当前提交是否通过，以PR检查页和对应产物为准。

界面检查覆盖默认离线、四轴显示、输入锁定、模拟移动与停止、轴独立性，以及全过程未调用回显。它不自动执行实体网络诊断。核心回环测试使用127.0.0.1上的合成回复，不代表与实卡通信成功。

## 未执行或不由本次证明

- 用户Windows电脑安装/启动、不同DPI/多屏、现场防火墙与网卡兼容。
- 回显过程中关窗、回显失败后UI控件恢复：已做静态复核，未通过自动化界面检查实测。核心层的取消/超时及端口释放已由回环测试覆盖。
- 实际运动卡回显、固件版本与驱动兼容、运行状态读取。
- 初始化、使能、回零、运动、SDO写入、参数保存、IO与磁吸控制。
- 四轴标定、正逆运动学、真实轨迹、安全停机和现场人工验收。

三个子Agent分别负责旧项目/Windows界面、固件/协议、独立技术复核，创建时均显式请求 `gpt-6.1-sol`、`ultra`。主Agent整合、实现模拟与传输、补恢复测试并统一构建。工具请求配置可见，不能据此宣称独立核验了服务端实际模型运行信息。

## 一键启动入口

2026-10-26新增 `启动Demo.cmd` 与 `scripts/start-demo.ps1`。Windows工作流新增 `tests/LauncherChecks.ps1`，使用系统自带Windows PowerShell 5.1，在含中文和空格的隔离目录执行12项流程检查：成品/源码构建准备与离线检查、真正无参数启动成品主窗体并正常关闭、缺少文件和无效项目失败，以及非零退出码传播。测试从其他工作目录调用，避免仅在仓库根目录成功。当前提交结果以PR检查页为准。

本轮另有两位子Agent分别完成公开内容/启动器独立复核和Windows检查实现，均显式请求 `gpt-6.1-sol / ultra`。公开前扫描了原有四个可达提交的25个文本对象，未发现疑似凭据或原厂二进制归档；保留Git作者元数据和既有历史。该扫描不替代许可证审定。仓库可见性已按用户要求改为Public。

## 从仓库下载后的启动检查

合并前补充了 GitHub ZIP 路径：Windows CI 下载本次精确提交的源码归档并解压，运行归档内的启动器检查，不依赖 Git 检出时的行尾转换。源码在新的中文及空格目录构建；新增通过源码 `启动Demo.cmd` 无参数构建、打开真实主窗口并正常关闭的检查，启动流程总数变为13项。原有缺文件、构建失败和退出码传播检查保留。

这条检查在 PR 和 `main` 推送时都会执行。PR 使用当前开发分支提交的归档，主分支使用本次合并提交的归档；具体完成结果以对应提交的 [Actions 运行记录](https://github.com/ZZZZihan/robotic-arm-ethercat-demo/actions) 为准。源码首次构建仍需要已安装 .NET 8 SDK、.NET Framework 4.8，并能访问 NuGet 还原参考程序集。

## 实板只读 OP 验证（2026-11-21，ThinkBook 现场）

- ThinkBook 专用网口 `以太网 3` 配置静态地址 `192.168.8.12/24`（无默认网关），`ping 192.168.8.20` 延迟 1–3 ms。
- 通过 J-Link V13 + 官方 `HC32F4A0_2M.FLM` 完整备份原始 Boot（64 KB）与 App（112 KB）至 `C:\RobotArmDemo\newcard-backup\`，仅刷写带 SV630N 适配补丁的 App（`0x00024000`），并双重 `verifybin` 校验通过（Boot 区域 100% 未修改）。
- 在 **零使能、零运动** 条件下完成完整实机只读链路：`0x01` Hello → `0xF2/0x01` 会话 → `0xF2/0x03` PRE-OP 扫描（4×汇川 SV630N）→ `0xF2/0x04` 528B 配置下发（`0x605A=6` RAM）→ `0xF2/0x05` 进入 OP → `0xF2/0x00` 实时遥测读回：
  - `bus_ready=1`，`pdo_ok=1`，`pdo_age=0ms`，`txpdo_bytes=52`，1 kHz 周期 PDO 计数正常递增。
  - 4 个从站均为 `SW=0x1650`（未使能就绪、无故障）、`CW=0x0000`、`Mode=8 (CSP)`、`Err=0x0000`、`valid=0x1F`，实时读取到四轴编码器位置。
- 尚未自动执行物理电机使能与机械运动；单轴低速测试与实体轴映射/方向确认交由现场操作者通过控制台分步完成，随后建立 SCARA 笛卡尔坐标系。

## 四轴 SCARA 标定与实机笛卡尔验收（2026-11-22，ThinkBook 现场）

- **单轴映射与右手系极性确认**：
  - `轴 1 (Slave 0)`：底座大臂旋转（`+cts` 俯视顺时针，右手系极性 `-1`），齿数比 `80/20 × 108/22 = 216/11 ≈ 19.6364`。
  - `轴 2 (Slave 1)`：立柱竖直丝杆升降（`+cts` 向上，右手系极性 `+1`），实测导程 `8.0 mm/rev`（`32,768 cts/mm`）。
  - `轴 3 (Slave 2)`：肘部小臂旋转（`+cts` 俯视顺时针，右手系极性 `-1`），齿数比 `80/20 × 92/22 = 184/11 ≈ 16.7273`。
  - `轴 4 (Slave 3)`：末端铁片架自转（`+cts` 俯视逆时针，右手系极性 `+1`），无打滑实测 `4:1`；顺时针硬限位位于约 `-67.5°`，软件加 `-55°` 软限位保护。
- **几何与最终零点**：`L1 = 250.0 mm`、`L2 = 145.0 mm`、`H0 = 108.0 mm`，零点多圈绝对值计数 `[-296968, -3112648, 928286, -117722]`（对应 `X = 395.0 mm, Y = 0.0 mm, Z = 108.0 mm, R = 0.0°`），标定写入 `scara-calibration.ini`。
- **离线单元测试与 UI 冒烟**：`tests/NewCard.Tests.csproj` 共 **187 项断言全通过**（`protocol=38, sc4i=63, scara=49, model/transport=37`）；`NewCardDemo.csproj` 在 Windows `.NET 8 SDK`（目标 `net48`）0 警告 0 错误编译通过，`--ui-smoke-test` 退出码 `0`（关节单轴与笛卡尔双模式界面均截图复核）。
- **实机联动与现场抗干扰排查记录**：
  - 完成四轴全使能（`en=0xF, auth=0xF`）下 `+Z / −X / +Y` 实机笛卡尔移动验证（`+Z` 实测编码器反馈 `+20.04 mm`，全程 `pdoAge=0 ms, lat=0x00, drvErr=0x0000`）。
  - 现场定位并记录了一起典型强电 EMI 问题：伺服使能时若上位机网线紧贴橙色电机动力线，会同时引发 PC↔W5500 丢包（界面闪烁“通信超时/已就绪”）与驱动器 `Er.E08` 同步丢失；将网线与动力线物理隔离并断电清 `Er.E08` 后恢复 0% 丢包。

## v0.1.0 架构重构与离线验证（2026-11-22）

- **死代码清理**：移除已不再使用的旧版 `OfflineArmModel`、`EchoClient`、`CardProtocol` 及对应旧测试。
- **高内聚低耦合分层**：
  - 统一项目命名为 `ScaraArm.Core`（`net48;net8.0` 独立类库）、`ScaraArm.Console`（`net48` WinForms）、`ScaraArm.Tests`（`net8.0` 测试），新增 `ScaraArm.sln` 与 `Directory.Build.props`（版本号 `0.1.0`）。
  - `ScaraArm.Core` 按单向依赖划分为 `Protocol`、`Transport`、`Kinematics`、`Motion` 四层，新增 `UdpFrameChannel` 与可离线单测的 `CartesianMotionPlanner` / `ISc4iMotionClient`，合并 `Sc4iClient` 中重复的运动前安全联锁。
  - `ScaraArm.Console` 将原 2,081 行 `MainForm.cs` 拆分为 `Theme/UiTheme`、`Controls/`（`AxisCard`、`JointControlPanel`、`CartesianControlPanel`）、`Dialogs/`（网络设置、设零点、标定参数）、`Diagnostics/UiSmokeTest`，并将主窗体按界面渲染（`MainForm.cs`）与硬件会话操作（`MainForm.Operations.cs`）分离。
- **本机验证（macOS）**：
  - `dotnet build ScaraArm.sln -c Release` 全量构建通过（`ScaraArm.Core` net48 + net8.0、`ScaraArm.Console` net48、`ScaraArm.Tests` net8.0），0 警告、0 错误。
  - `dotnet run --project tests/ScaraArm.Tests.csproj -c Release` 共 **134 项离线断言全部通过**（`protocol=35, client=28, scara=49, planner=22`），新增覆盖笛卡尔“上升先提 Z / 下降后降 Z”顺序、未使能拦截及急停中途终止。
  - Windows 实机 `--ui-smoke-test` 与 `tests/LauncherChecks.ps1` 需在 Windows 环境或 GitHub Actions `windows` 任务中执行验证。

## v0.2.0 示教与任务编排：Windows 冒烟与 ThinkBook 实机验收（2026-11-23）

- **新增分层与持久化**：
  - 在 `ScaraArm.Core.Tasks` 层新增 `TeachWaypoint` / `TeachProgram`、`TeachProgramStore`（UTF-8 INI 往返持久化）与 `TaskProgramRunner`（全路径启动前预检、门型安全提 Z 分段、固定/无限循环与停留期心跳保活），完全复用 `CartesianMotionPlanner`。
  - 在 `ScaraArm.Console` 新增 `示教与任务` 面板（`TeachTaskPanel`）与点位编辑弹窗（`WaypointEditDialog`），支持自动保存 `scara-program.ini`、任意任务文件导入导出、单步到位、预检、循环运行与随时安全停机。
- **实机暴露并修复的现场抗干扰问题**：
  - ThinkBook 实机在四轴伺服持力/运动 PWM 干扰下实测 `ping 192.168.8.20` 存在约 6% 单包丢包（15 发 14 收，RTT 1 ms），导致原单次发包的 `MoveAxisPtp` / `FaultReset` 在连续多点任务中偶发 3000 ms 超时。
  - 在 `Sc4iClient` 为 `MoveAxisPtp` 补充**同序列号（`seq`）防重放快速重传**（180 ms × 4 次，重传遇 `0x35` 说明首包已生效并用 `Heartbeat` 确认状态，杜绝重复运动）并为幂等的 `EnableAxes`/`FaultReset`/`Heartbeat` 优化重传策略，同时补充 2 项 UDP 丢包回环单测。
- **离线单测与 Windows UI 冒烟（ThinkBook + macOS）**：
  - `dotnet build ScaraArm.sln -c Release` 0 警告、0 错误；`ScaraArm.Tests` 共 **176 项断言全通过**（`protocol=35, client=30, scara=49, planner=22, tasks=40`）。
  - ThinkBook 执行 `scripts/build-windows.ps1` 产出 `ScaraArm.Console.exe`（`net48`）并实跑 `--ui-smoke-test`：**24 项 Windows 离线界面检查全通过**（含关节/笛卡尔/示教与任务三模式切换、离线安全锁定与视口边界检查），并对三模式截图完成人工/自动视觉复核，修正了 `门型安全提 Z` 输入框 3 px 垂直对齐细节。
  - 通过非交互 SSH 执行 `tests/LauncherChecks.ps1` 时，成品 `BuildOnly` 与 `VerifyOnly` 离线检查均通过，无参数实开桌面窗体检测受限于 SSH 非交互会话（需在 Windows 本地桌面或 GitHub Actions `windows-latest` runner 中执行）。
- **ThinkBook 四轴 SCARA 实机闭环验收**：
  - 使用现场 `scara-calibration.ini` 与 `scara-program.ini`（3 点位：启用 `P1 取料点 (360, 30, 105, R=0°)` 与 `P2 放料点 (360, -20, 105, R=0°)`，各停留 `400 ms`；禁用跳过点 `P_SKIP`；门型安全提 Z `120.0 mm`；速度 `0.35 圈/秒`；完后回零）。
  - 完整验证四轴使能（`EnMask=0xF, AuthMask=0xF`）→ 启动前预检 → 连续 2 轮门型循环取放（`P1 → P2 → P1 → P2`，重复定位 `360.00, ±20/30, 105.00–105.01 mm, R=0.00°`）与收尾 1 轮取放 + 完整回安全零点：`LIVE_TASK_PASS`（`Elapsed=84.13s, ProgressEvents=594, DwellHeartbeats=6, MaxPdoAge=0ms, LatchedFault=0x0, Estop=0, DrvErr=[0x0000×4]`），最终精准归位至 `X=395.00 mm, Y=0.00 mm, Z=108.01 mm, R=0.00°`。
