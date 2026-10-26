# Windows 使用说明

这是新卡的独立演示台。原先的博派操作台仍对应旧卡，不能混用它的 DLL、轴单位或网络参数。

## 直接运行成品

1. 将整个 Windows 成品文件夹解压到普通本地目录。
2. 在装有 .NET Framework 4.8 的 Windows 10/11 电脑上，双击 `启动Demo.cmd`。它会检查运行条件并打开 `NewCardDemo.exe`；也可以直接打开exe。程序不要求博派 DLL，不需要安装厂商驱动即可离线演示。
3. 默认进入“四轴离线演示”。选择轴，输入位移和速度，点击“相对 + / −”；点击“停止模拟”停止模拟。输入回车不会发起运动。
4. 所有位置和速度均为演示计数，与实际电机圈数、角度、丝杆高度无换算关系。模拟范围 ±1000、速度上限100也不是机械安全限值。

如果 Windows 因文件来自另一台电脑而显示提示，先核对本次交付的 SHA-256。不要为了运行本程序修改全局防火墙、网络设置或系统执行策略。

`启动Demo.cmd` 和 `scripts/start-demo.ps1` 必须与成品一起保留，不要仅复制cmd到别处。无需管理员权限。入口只对本次PowerShell进程设置执行策略，不改变电脑的持久设置；缺依赖或启动失败时会显示原因并停留供查看。

## 硬件回显诊断

本次开发未连接实体卡，也未更改电脑网卡。回显功能供后续操作者在硬件条件确认后使用。

先确认板卡正在运行的 App 固件与网络参数。原厂 App 自身上电会初始化 EtherCAT，并可能写驱动参数；不能因为 demo 没有运动命令就认为上电本身没有影响。需先核对驱动与固件、负载保持、现场急停/STO和接线条件。

“硬件回显诊断”页默认电脑 `192.168.8.12:6000`、卡 `192.168.8.20:5000`。这是资料默认值，不是探测到的实机配置。卡的回复目的地址通常在固件中固定；只在界面改电脑端口并不能改变卡的回包目的端口。

- 点击“发送回显 0x01”才发送一次100字节查询；无重试、无后台轮询。
- 通过源地址/端口、帧长、校验和、命令、令牌及72字节随机回显全部校验后，显示本次 App 回显通过和窗口ID。
- 回显不读取轴状态，不证明伺服就绪、位置正确或机构已停止。窗口ID不是固件版本号。
- Boot回复、超时、错误校验、错误来源和旧包不会显示成功。错误后可修正参数，再手动查询；关窗取消等待并释放端口。
- 没有使能、回零、清零、清错、SDO写入、输出控制、原始发包、固件升级或真实运动入口。“停止模拟”不能停止真实机械臂。

电脑网口与EtherCAT驱动网口是不同接口，参见教程第14页接线图。不要通过批量尝试地址或随意刷写固件来排障。

## 从源码构建

需要 .NET 8 SDK（构建工具）；生成程序运行目标为 .NET Framework 4.8。项目用微软的参考程序集包进行编译，依据 [Microsoft Reference Assemblies](https://learn.microsoft.com/en-us/dotnet/framework/migration-guide/reference-assemblies)。

1. 首次使用先安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)，并确认电脑已安装 [.NET Framework 4.8](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48)。SDK 与程序运行环境是两个依赖。
2. 在仓库 `main` 分支点击 **Code → Download ZIP**，或[直接下载源码包](https://github.com/ZZZZihan/robotic-arm-ethercat-demo/archive/refs/heads/main.zip)，完整解压到普通本地文件夹。无需 Git。
3. 打开解压后的源码根目录，双击 `启动Demo.cmd`。首次构建需要联网下载微软参考程序集包；检查通过后自动打开离线窗口。

**解压完整源码包，双击 `启动Demo.cmd` 即可构建并启动。** 源码模式每次先做检查和增量构建，避免打开与当前代码不一致的旧程序；若同一路径的程序正在运行，请先正常关闭它。构建失败不会继续启动旧成品。

可选命令：

```bat
启动Demo.cmd -BuildOnly
启动Demo.cmd -VerifyOnly
```

`-BuildOnly` 完成构建准备后退出，不打开日常操作窗口；源码构建过程会短暂打开离线检查窗口。`-VerifyOnly` 运行离线界面检查后退出，退出码会原样返回给调用方。这两个参数不能同时使用。成品包的 `-BuildOnly` 只检查启动条件，不重新编译。

手动构建也可在仓库根目录执行：

```powershell
.\scripts\build-windows.ps1
```

脚本先跑核心测试，再编译并短暂打开离线窗体进行检查，输出到 `.artifacts\windows-demo\`。没有实体卡通信。它不修改系统执行策略；若脚本执行受现有策略限制，也可按顺序运行：

```powershell
dotnet run --project tests\NewCard.Tests.csproj -c Release
dotnet build src\NewCardDemo\NewCardDemo.csproj -c Release -o .artifacts\windows-demo
.\.artifacts\windows-demo\NewCardDemo.exe --ui-smoke-test --screenshot .artifacts\windows-demo\offline-ui.png
```

`--ui-smoke-test` 只运行界面离线检查，退出码0表示通过。日常使用双击 `启动Demo.cmd` 或exe，不传检查参数。
