# 四轴 SCARA 机械臂控制台（v0.1.0）

面向四轴立柱升降式 SCARA 机械臂（HC32F4A0 `SC4I0001 v2` 运动卡 + 4×汇川 SV630N 伺服驱动器）的独立 Windows 控制台与核心控制库，支持**关节单轴低速调试**与**四轴 SCARA 笛卡尔坐标系 `(X, Y, Z, R)` 安全联动控制**。

## 核心特性（v0.1.0）

- **默认安全离线**：启动时不自动连接控制卡、不初始化总线、不上电机使能；用户显式点击“连接”后完成握手与只读进 OP（电机保持未使能），急停与停止均保持伺服持力（`0x605A=6` 仅写 RAM，不写驱动 EEPROM）。
- **关节单轴调试**：选轴、单轴使能/重新授权、低速小步长相对定位、按住点动与保活监督、两步清故障/解锁。
- **四轴 SCARA 笛卡尔控制**：实时正运动学 `(X, Y, Z, R)` 与手系显示、逆运动学求解、工作空间与四轴软限位预检、安全运动顺序（**上升先提 Z、下降后降 Z**）、一键设零点与 `scara-calibration.ini` 持久化。
- **分层解耦架构**：核心协议、UDP 会话通信、运动学与标定、笛卡尔运动编排全部沉淀在零 UI 依赖的 `ScaraArm.Core` 类库中，可跨平台离线单测。

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

## 文档导航

| 目的 | 文件 |
| --- | --- |
| Windows 运行、源码构建与实机操作指南 | [Windows 使用说明](docs/WINDOWS_QUICKSTART.md) |
| `SC4I0001 v2` UDP 协议与原厂固件核对 | [协议核对](docs/PROTOCOL.md) |
| 离线单测、CI 与现场实机验收记录 | [验证记录](docs/VERIFICATION.md) |
| 原厂硬件/教程资料目录索引（参考） | [资料基本索引](docs/MATERIALS.md) |
| 原厂 12 份归档文件指纹清单（参考） | [资料清单](docs/materials/manifest.json) |

## 安全提示

- 主机接口为普通以太网 UDP（默认 `192.168.8.12:6000 ↔ 192.168.8.20:5000`），与旧博派 `GA_*` SDK 不通用。
- 请保持电脑网线、EtherCAT 网线与伺服三相动力线物理分开（建议 ≥20 cm），避免强电干扰引发丢包或驱动器 `Er.E08` 同步丢失。
- 仓库不分发原厂闭源源码、DLL、安装包或压缩资料；所有控制代码与协议编解码均为独立实现。
