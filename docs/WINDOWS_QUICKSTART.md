# Windows 使用说明（v0.1.0）

本控制台用于四轴立柱升降式 SCARA 机械臂（`SC4I0001 v2` 运动卡 + 4×汇川 SV630N）。它不依赖旧博派 `GA_*` DLL，也不需要安装厂商驱动。

## 直接运行成品包

1. 将整个 Windows 成品文件夹完整解压到普通本地目录（需同时保留 `ScaraArm.Console.exe`、`ScaraArm.Core.dll`、`启动控制台.cmd` 与 `scripts/start-console.ps1`）。
2. 在装有 [.NET Framework 4.8](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48) 的 Windows 10/11 电脑上，双击 **`启动控制台.cmd`**（或直接运行 `ScaraArm.Console.exe`）。
3. 程序默认以**离线状态**启动，不自动连接控制卡；离线时所有使能与运动按钮均处于锁定状态。

入口脚本无需管理员权限，只对当前 PowerShell 进程生效，不修改系统执行策略、防火墙或网卡设置。

## 从源码构建

源码构建需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（用于编译 `net48` 控制台与运行 `net8.0` 测试）；首次构建需联网还原微软 `.NET Framework 4.8` 参考程序集包。

1. 在仓库 `main` 分支下载源码并完整解压到本地目录（支持中文和空格路径）。
2. 双击源码根目录的 **`启动控制台.cmd`**，将自动运行核心单测、编译控制台、执行离线 UI 冒烟检查，通过后打开主窗体。

常用命令行参数：

```bat
启动控制台.cmd -BuildOnly
启动控制台.cmd -VerifyOnly
```

- `-BuildOnly`：仅构建和验证，不打开日常操作窗口。
- `-VerifyOnly`：执行 `--ui-smoke-test` 离线界面检查并原样返回退出码。

手动构建打包到 `.artifacts\windows-console\`：

```powershell
.\scripts\build-windows.ps1
```

或分步执行：

```powershell
dotnet run --project tests\ScaraArm.Tests.csproj -c Release
dotnet build src\ScaraArm.Console\ScaraArm.Console.csproj -c Release -o .artifacts\windows-console
.\.artifacts\windows-console\ScaraArm.Console.exe --ui-smoke-test --screenshot .artifacts\windows-console\offline-ui.png
```

## 实机四轴与笛卡尔调试（SC4I0001 v2）

默认通信端点：电脑 `192.168.8.12:6000` ↔ 控制卡 `192.168.8.20:5000`（未连接时可在右上角 **`设置`** 修改）：

1. **连接与进 OP（不上使能）**：点击右上角 **`连接`**，完成 `0x01` 握手、会话获取、PRE-OP 四从站扫描、下发 528B 配置（RAM-only `0x605A=6`，不写驱动 EEPROM）并进入 OP；所有电机保持未使能，上方 4 张轴卡与坐标条实时显示各轴角度/高度与末端 `(X, Y, Z, R)`。
2. **关节单轴调试**：点击选中单张轴卡 → 点击 **`使能轴 N`** → 默认步长 `0.20` 圈、速度 `0.25 rev/s`，使用 **`− 反向` / `+ 正向`** 或 **`按住点动`** 做单轴微调。
3. **笛卡尔坐标控制（SCARA）**：切换 **`笛卡尔坐标 (SCARA)`** → **`使能全部四轴`** → 使用 **`±X / ±Y / ±Z / ±R`** 相对步进（默认 `10 mm / 5°`）或输入目标 **`(X, Y, Z, R)`** 绝对定位。系统自动执行工作空间与四轴软限位预检，并按**上升先提 Z、下降后降 Z**的安全顺序分步到位；默认不勾选“保持末端朝向 R”以保护轴 4 不触碰 `-55°` 顺时针软限位。
4. **标定与急停**：点击 **`设为零点`** 可将当前编码器计数与离地高度 `H0` 保存到同目录 `scara-calibration.ini`；点击 **`标定参数`** 可微调臂长、导程、速比与限位；任何时候点击红色 **`急停`** 立即减速停机并保持伺服持力（`0x605A=6`）。
5. **现场防干扰提醒**：请保持电脑网线、EtherCAT 网线与橙色三相伺服动力线物理分开（建议 ≥20 cm），避免高频 PWM 干扰引起丢包或驱动器 `Er.E08` 同步丢失。
