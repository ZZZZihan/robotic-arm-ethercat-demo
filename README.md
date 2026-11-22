# 四轴 SCARA 机械臂控制台（v0.1.0）

面向四轴立柱升降式 SCARA 机械臂的独立 Windows 控制台与核心控制库（`.NET Framework 4.8` / `.NET 8`），基于 **吴大拿团队（胡强视觉运动开发 / 源小群）** 的全开源 EtherCAT 运动卡硬件平台与 4× **汇川 SV630N** 伺服驱动器适配开发，支持**关节单轴低速调试**与**四轴 SCARA 笛卡尔坐标系 `(X, Y, Z, R)` 安全联动控制**。

---

## 硬件平台与参考资料来源

### 1. 运动控制卡与伺服硬件

| 组件 | 型号 / 规格 | 说明 |
| --- | --- | --- |
| **EtherCAT 运动控制卡** | **吴大拿团队 — 胡强视觉运动开发（源小群）** 全开源 EtherCAT 运动卡（`yxq006` / `EADP001` 系列） | 24V 供电，独立 PC 以太网口与 EtherCAT 主站网口；16 路低有效通用输入、14 路 NPN 开漏通用输出 |
| **主控与通信芯片** | 华大 **HC32F4A0**（Cortex-M4） + **W5500** 以太网 + **ECM-XF** EtherCAT 主站（`IP101GRI` PHY） | 上位机通过普通以太网 **UDP** 与卡通信（默认 `192.168.8.12:6000 ↔ 192.168.8.20:5000`），不依赖旧博派 `GA_*` SDK |
| **卡内固件** | 原厂 `Boot_017`（未修改） + 实机适配固件 **`SC4I0001 v2`** | 1 kHz 周期 PDO 同步，支持 `0x01` 握手与 `0xF2` 会话/总线/单轴指令；针对 SV630N 仅向 RAM 写入 `0x605A=6`（快停持力），不写驱动 EEPROM |
| **伺服驱动器** | 4× **汇川（Inovance）SV630N** EtherCAT 伺服 | `VID=0x00100000`, `PID=0x000C0112`, `Rev=0x00010000`；262,144 counts/rev 多圈绝对值编码器，CSP 模式（`8`） |

### 2. 四轴 SCARA 机械结构与标定参数

本控制台默认标定对应现场四轴立柱升降式 SCARA 机械臂（可通过界面“设为零点 / 标定参数”动态修改并保存到 `scara-calibration.ini`）：

- **连杆几何**：大臂 `L1 = 250.0 mm`，小臂 `L2 = 145.0 mm`，零点离地高度 `H0 = 108.0 mm`（对应零点坐标 `X = 395.0 mm, Y = 0.0 mm, Z = 108.0 mm, R = 0.0°`）。
- **关节映射与传动参数**：

| 软件轴 / 从站 | 实体关节 | 传动比 / 导程 | 右手系极性 | 默认软限位 |
| --- | --- | --- | --- | --- |
| 轴 1 (`Slave 0`) | 底座大臂旋转 (`J1`) | `80/20 × 108/22 = 216/11`（≈ `19.6364`） | `-1`（`+cts` 俯视顺时针） | `[-120°, +120°]` |
| 轴 2 (`Slave 1`) | 立柱竖直丝杆升降 (`Z`) | `8.0 mm/rev`（`32,768 cts/mm`） | `+1`（`+cts` 向上） | `[20 mm, 210 mm]` |
| 轴 3 (`Slave 2`) | 肘部小臂旋转 (`J3`) | `80/20 × 92/22 = 184/11`（≈ `16.7273`） | `-1`（`+cts` 俯视顺时针） | `[-135°, +135°]` |
| 轴 4 (`Slave 3`) | 末端执行器自转 (`J4`) | `4:1` | `+1`（`+cts` 俯视逆时针） | `[-55°, +120°]`（避开 `-67.5°` 顺时针硬限位） |

### 3. 参考资料与合规边界

本项目的硬件接口、W5500/HC32F4A0/ECM-XF 架构、Boot/App 协议核对参考了购买运动卡配套的以下原厂资料（来自 **吴大拿团队 — 胡强视觉运动开发 / 源小群**）：
- 《全开源 EtherCAT 运动卡学习教程 V1.92》（377 页）与《从单片机基础到程序框架》；
- 运动卡硬件资料包（原理图、华大 HC32F4A0 手册、ECM-XF 主站芯片资料）；
- 配套通讯协议报文 `v1.4` 及各机械结构（三轴/四轴/五轴龙门、SCARA、Delta、汇川三轴修改版）Boot/App/C# 演示源码。

完整原件目录与 SHA-256 指纹索引见 [docs/MATERIALS.md](docs/MATERIALS.md) 与 [docs/materials/manifest.json](docs/materials/manifest.json)。**本仓库不上传或重新分发任何原厂压缩包、DLL、安装包或第三方源码**；仓库中的上位机与核心控制库均为独立重写实现。

---

## 核心特性（v0.1.0）

- **默认安全离线**：启动时不自动连接控制卡、不初始化总线、不上电机使能；用户显式点击“连接”后完成握手与只读进 OP（电机保持未使能），急停与停止均保持伺服持力（`0x605A=6` 仅写 RAM）。
- **关节单轴调试**：选轴、单轴使能/重新授权、低速小步长相对定位、按住点动与保活监督、两步清故障/解锁。
- **四轴 SCARA 笛卡尔控制**：实时正运动学 `(X, Y, Z, R)` 与手系显示、逆运动学求解、工作空间与四轴软限位预检、安全运动顺序（**上升先提 Z、下降后降 Z**）、一键设零点与 `scara-calibration.ini` 持久化。
- **分层解耦架构**：核心协议、UDP 会话通信、运动学与标定、笛卡尔运动编排全部沉淀在零 UI 依赖的 `ScaraArm.Core` 类库中，可跨平台离线单测。

---

## 架构分层

依赖方向严格单向无环：

```text
ScaraArm.Console (WinForms UI, net48)
        │
        ▼
ScaraArm.Core.Motion      ── 笛卡尔安全规划与逐步到位编排（依赖接口，可离线单测）
    ├──▶ ScaraArm.Core.Kinematics  ── SCARA 标定参数、INI 持久化与正/逆运动学
    └──▶ ScaraArm.Core.Transport   ── UDP 通道、重试与 SC4I 会话/安全联锁客户端
            │
            ▼
         ScaraArm.Core.Protocol    ── SC4I0001 v2 帧编解码、528B 配置与错误码表
```

## 项目结构

```text
ScaraArm.sln                        解决方案入口
Directory.Build.props               统一版本号 (0.1.0) 与编译选项
启动控制台.cmd                       Windows 一键构建 / 启动入口
src/
  ScaraArm.Core/                    核心控制库（net48;net8.0，零 UI 依赖）
    Protocol/                       0x01 / 0xF2 协议编解码、遥测模型、528B 配置
    Transport/                      UDP 通道、SC4I 会话客户端与运动前安全联锁
    Kinematics/                     SCARA 标定、INI 读写与正/逆运动学
    Motion/                         笛卡尔安全序列规划与执行器
  ScaraArm.Console/                 Windows 控制台界面（net48 WinForms）
    Assets/                         多分辨率工业风应用图标 (app.ico)
    Theme/                          配色、字体与布局样式工具
    Controls/                       AxisCard、关节面板、笛卡尔面板
    Dialogs/                        网络设置、设零点、标定参数弹窗
    Diagnostics/                    离线 UI 冒烟检查 (--ui-smoke-test)
    MainForm.cs                     主窗体布局、弹窗入口与状态绑定
    MainForm.Operations.cs          会话生命周期、心跳轮询、点动与急停操作
tests/                              离线单元测试与 Windows 启动器校验（net8.0）
scripts/                            Windows 构建、一键启动与资料索引脚本
docs/                               使用说明、协议核对、验证记录与硬件资料索引
```

---

## 快速开始

### Windows 一键运行 / 构建

完整解压源码包或成品包后，直接双击根目录的 **`启动控制台.cmd`**（支持含中文或空格路径）：
- 成品包：检查 `.NET Framework 4.8` 后直接启动控制台；
- 源码包：自动运行核心测试、增量构建并完成离线 UI 冒烟检查，再打开控制台。

仅构建不启动日常窗口：

```bat
启动控制台.cmd -BuildOnly
```

或使用 PowerShell 手动构建与打包到 `.artifacts\windows-console\`：

```powershell
.\scripts\build-windows.ps1
```

### 运行核心单元测试（Windows / macOS / Linux）

```sh
dotnet run --project tests/ScaraArm.Tests.csproj -c Release
```

---

## 文档导航

| 目的 | 文件 |
| --- | --- |
| Windows 运行、源码构建与实机操作指南 | [Windows 使用说明](docs/WINDOWS_QUICKSTART.md) |
| `SC4I0001 v2` UDP 协议与原厂固件核对 | [协议核对](docs/PROTOCOL.md) |
| 离线单测、CI 与现场实机验收记录 | [验证记录](docs/VERIFICATION.md) |
| 原厂硬件/教程资料目录索引（参考） | [资料基本索引](docs/MATERIALS.md) |
| 原厂 12 份归档文件指纹清单（参考） | [资料清单](docs/materials/manifest.json) |

## 现场安全提示

- 请保持电脑网线、EtherCAT 网线与伺服橙色三相动力线物理分开（建议 ≥20 cm），避免强电 PWM 干扰引起丢包或驱动器 `Er.E08` 同步丢失。
- 本控制台软件急停通过 `0x605A=6` 减速停机并保持伺服持力，不等同于现场硬件急停 / STO 安全回路。
