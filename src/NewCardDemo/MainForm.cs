using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using NewCard.Core;

namespace NewCardDemo
{
    internal sealed class MainForm : Form
    {
        private static readonly Color Accent = Color.FromArgb(8, 115, 185);
        private static readonly Color Ink = Color.FromArgb(22, 57, 76);
        private static readonly Color Muted = Color.FromArgb(97, 117, 129);
        private static readonly Color Surface = Color.FromArgb(240, 247, 251);
        private static readonly Color Border = Color.FromArgb(220, 231, 237);

        private readonly OfflineArmModel arm = new OfflineArmModel();
        private readonly Button[] axisCards = new Button[4];
        private readonly System.Windows.Forms.Timer simulationTimer;
        private readonly TextBox history = new TextBox();
        private readonly NumericUpDown amount = new NumericUpDown();
        private readonly NumericUpDown speed = new NumericUpDown();
        private readonly Label selectedTitle = new Label();
        private readonly Label modeBadge = new Label();
        private readonly Label actualFeedback = new Label();
        private readonly Label targetFeedback = new Label();
        private readonly Label motionFeedback = new Label();
        private readonly Label axisNote = new Label();
        private readonly Button forward = new Button();
        private readonly Button reverse = new Button();
        private readonly Button stop = new Button();
        private readonly TextBox localIp = new TextBox();
        private readonly TextBox cardIp = new TextBox();
        private readonly NumericUpDown localPort = new NumericUpDown();
        private readonly NumericUpDown cardPort = new NumericUpDown();
        private readonly NumericUpDown timeout = new NumericUpDown();
        private readonly Button echo = new Button();
        private readonly Label echoState = new Label();
        private readonly TextBox echoDetails = new TextBox();
        private readonly EchoClient echoClient = new EchoClient();
        private readonly List<Control> endpointControls = new List<Control>();
        private CancellationTokenSource echoCancellation;
        private int selectedAxis;
        private int echoProbeCount;
        private readonly Stopwatch simulationClock = Stopwatch.StartNew();
        private double previousSimulationTime;

        internal MainForm()
        {
            Text = "新卡四轴 Demo · 离线演示与回显诊断";
            Font = new Font("Microsoft YaHei UI", 10F);
            ForeColor = Ink;
            BackColor = Surface;
            ClientSize = new Size(1160, 820);
            MinimumSize = new Size(1060, 800);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;

            BuildLayout();
            simulationTimer = new System.Windows.Forms.Timer { Interval = 50 };
            simulationTimer.Tick += delegate
            {
                double now = simulationClock.Elapsed.TotalSeconds;
                double seconds = Math.Max(0, Math.Min(0.25, now - previousSimulationTime));
                previousSimulationTime = now;
                AdvanceSimulation(seconds);
            };
            previousSimulationTime = simulationClock.Elapsed.TotalSeconds;
            simulationTimer.Start();
            FormClosing += delegate
            {
                simulationTimer.Stop();
                if (echoCancellation != null) echoCancellation.Cancel();
            };
            RefreshSimulation();
            AddLog("启动：离线模拟。没有打开控制卡、初始化总线或发送网络数据。");
        }

        private static Label LabelText(string text, float size, bool bold)
        {
            return new Label
            {
                Text = text, Dock = DockStyle.Fill, AutoSize = false,
                Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = Ink, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0)
            };
        }

        private static TableLayoutPanel Grid(int columns, int rows)
        {
            return new TableLayoutPanel
            {
                ColumnCount = columns, RowCount = rows, Dock = DockStyle.Fill,
                Margin = new Padding(0), Padding = new Padding(0), BackColor = Color.Transparent
            };
        }

        private static void StyleButton(Button button, string text, bool primary)
        {
            button.Text = text;
            button.Dock = DockStyle.Fill;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.BackColor = primary ? Accent : Color.White;
            button.ForeColor = primary ? Color.White : Ink;
            button.Cursor = Cursors.Hand;
            button.Margin = new Padding(0, 0, 12, 0);
            button.UseVisualStyleBackColor = false;
        }

        private static void ConfigureNumber(NumericUpDown input, decimal minimum, decimal maximum, decimal value)
        {
            input.Minimum = minimum;
            input.Maximum = maximum;
            input.Value = value;
            input.Dock = DockStyle.Fill;
            input.ThousandsSeparator = true;
            input.Font = new Font("Microsoft YaHei UI", 12F);
            input.Margin = new Padding(0, 4, 18, 4);
        }

        private void BuildLayout()
        {
            TableLayoutPanel root = Grid(1, 4);
            root.Padding = new Padding(24, 14, 24, 14);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            Controls.Add(root);

            TableLayoutPanel header = Grid(2, 2);
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.Controls.Add(LabelText("机械臂 / 新卡四轴 Demo", 22, true), 0, 0);
            modeBadge.Text = "● 离线模拟 · 手动回显诊断";
            modeBadge.Dock = DockStyle.Fill;
            modeBadge.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            modeBadge.ForeColor = Accent;
            modeBadge.TextAlign = ContentAlignment.MiddleRight;
            header.Controls.Add(modeBadge, 1, 0);
            Label subtitle = LabelText("同一台四轴机构；新卡的轴映射、比例与机械行程仍待确认。", 10, false);
            subtitle.ForeColor = Muted;
            header.Controls.Add(subtitle, 0, 1);
            header.SetColumnSpan(subtitle, 2);
            root.Controls.Add(header, 0, 0);

            TabControl tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(18, 9) };
            TabPage offline = new TabPage("四轴离线演示") { BackColor = Color.White, Padding = new Padding(18) };
            TabPage hardware = new TabPage("硬件回显诊断") { BackColor = Color.White, Padding = new Padding(22) };
            tabs.TabPages.Add(offline);
            tabs.TabPages.Add(hardware);
            BuildOfflinePage(offline);
            BuildHardwarePage(hardware);
            root.Controls.Add(tabs, 0, 1);

            TableLayoutPanel log = Grid(1, 2);
            log.Padding = new Padding(0, 10, 0, 0);
            log.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            log.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Label logTitle = LabelText("操作记录", 10, true);
            log.Controls.Add(logTitle, 0, 0);
            history.Multiline = true;
            history.ReadOnly = true;
            history.ScrollBars = ScrollBars.Vertical;
            history.Dock = DockStyle.Fill;
            history.BackColor = Color.White;
            history.BorderStyle = BorderStyle.FixedSingle;
            history.Font = new Font("Microsoft YaHei UI", 9F);
            log.Controls.Add(history, 0, 1);
            root.Controls.Add(log, 0, 2);
            Label footer = LabelText("离线位置使用演示计数；回显成功仅证明收到响应，不代表总线、伺服或机械运动已就绪。", 9, false);
            footer.ForeColor = Muted;
            root.Controls.Add(footer, 0, 3);
        }

        private void BuildOfflinePage(TabPage page)
        {
            TableLayoutPanel layout = Grid(1, 5);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(layout);

            TableLayoutPanel cards = Grid(4, 1);
            for (int index = 0; index < 4; index++)
            {
                cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
                int capturedIndex = index;
                Button card = new Button { TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 6, 8, 6) };
                StyleButton(card, String.Empty, false);
                card.Margin = new Padding(index == 0 ? 0 : 6, 0, index == 3 ? 0 : 6, 10);
                card.Click += delegate { SelectAxis(capturedIndex); };
                axisCards[index] = card;
                cards.Controls.Add(card, index, 0);
            }
            layout.Controls.Add(cards, 0, 0);
            selectedTitle.Dock = DockStyle.Fill;
            selectedTitle.Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
            selectedTitle.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(selectedTitle, 0, 1);

            TableLayoutPanel inputs = Grid(2, 2);
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            inputs.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            inputs.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            inputs.Controls.Add(LabelText("相对位移 / 演示计数", 10, true), 0, 0);
            inputs.Controls.Add(LabelText("速度 / 演示计数每秒", 10, true), 1, 0);
            ConfigureNumber(amount, 1, 1000, 100);
            ConfigureNumber(speed, 1, 100, 50);
            inputs.Controls.Add(amount, 0, 1);
            inputs.Controls.Add(speed, 1, 1);
            layout.Controls.Add(inputs, 0, 2);

            TableLayoutPanel actions = Grid(3, 1);
            actions.Padding = new Padding(0, 4, 0, 10);
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            StyleButton(reverse, "相对 −", true);
            StyleButton(forward, "相对 +", true);
            StyleButton(stop, "停止模拟", false);
            stop.Margin = new Padding(0);
            reverse.Click += delegate { StartSimulation(-1); };
            forward.Click += delegate { StartSimulation(1); };
            stop.Click += delegate { StopSimulation(); };
            actions.Controls.Add(reverse, 0, 0);
            actions.Controls.Add(forward, 1, 0);
            actions.Controls.Add(stop, 2, 0);
            layout.Controls.Add(actions, 0, 3);

            TableLayoutPanel feedback = Grid(3, 3);
            feedback.BackColor = Surface;
            feedback.Padding = new Padding(16, 8, 16, 8);
            feedback.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            feedback.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
            feedback.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            feedback.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            feedback.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            feedback.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            feedback.Controls.Add(LabelText("模拟位置", 10, false), 0, 0);
            feedback.Controls.Add(LabelText("模拟目标", 10, false), 1, 0);
            feedback.Controls.Add(LabelText("模拟状态", 10, false), 2, 0);
            Label[] values = { actualFeedback, targetFeedback, motionFeedback };
            for (int index = 0; index < values.Length; index++)
            {
                values[index].Dock = DockStyle.Fill;
                values[index].Font = new Font("Microsoft YaHei UI", index == 2 ? 14F : 20F, FontStyle.Bold);
                values[index].ForeColor = Accent;
                values[index].TextAlign = ContentAlignment.MiddleLeft;
                feedback.Controls.Add(values[index], index, 1);
            }
            axisNote.Dock = DockStyle.Fill;
            axisNote.ForeColor = Muted;
            axisNote.TextAlign = ContentAlignment.MiddleLeft;
            feedback.Controls.Add(axisNote, 0, 2);
            feedback.SetColumnSpan(axisNote, 3);
            layout.Controls.Add(feedback, 0, 4);
        }

        private void BuildHardwarePage(TabPage page)
        {
            TableLayoutPanel layout = Grid(1, 6);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            page.Controls.Add(layout);
            layout.Controls.Add(LabelText("只发送 0x01 回显", 17, true), 0, 0);
            Label description = LabelText("只有点击下方按钮才会通信。此页不执行总线初始化、使能、回零、运动或 IO 写入。\r\n请先确认电脑已有合适的专用网卡地址；程序不会修改网卡或自动探测设备。", 10, false);
            description.ForeColor = Muted;
            layout.Controls.Add(description, 0, 1);

            TableLayoutPanel endpoints = Grid(4, 4);
            endpoints.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            endpoints.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            endpoints.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            endpoints.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            for (int row = 0; row < 3; row++) endpoints.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            localIp.Text = "192.168.8.12";
            cardIp.Text = "192.168.8.20";
            localIp.Dock = DockStyle.Fill;
            cardIp.Dock = DockStyle.Fill;
            localIp.Margin = new Padding(0, 4, 20, 4);
            cardIp.Margin = new Padding(0, 4, 20, 4);
            ConfigureNumber(localPort, 1, 65535, 6000);
            ConfigureNumber(cardPort, 1, 65535, 5000);
            ConfigureNumber(timeout, 100, 10000, 1500);
            localPort.Font = cardPort.Font = timeout.Font = Font;
            endpoints.Controls.Add(LabelText("电脑 IP", 10, false), 0, 0);
            endpoints.Controls.Add(localIp, 1, 0);
            endpoints.Controls.Add(LabelText("电脑端口", 10, false), 2, 0);
            endpoints.Controls.Add(localPort, 3, 0);
            endpoints.Controls.Add(LabelText("控制卡 IP", 10, false), 0, 1);
            endpoints.Controls.Add(cardIp, 1, 1);
            endpoints.Controls.Add(LabelText("卡端口", 10, false), 2, 1);
            endpoints.Controls.Add(cardPort, 3, 1);
            endpoints.Controls.Add(LabelText("超时 / ms", 10, false), 0, 2);
            endpoints.Controls.Add(timeout, 1, 2);
            layout.Controls.Add(endpoints, 0, 2);
            endpointControls.AddRange(new Control[] { localIp, cardIp, localPort, cardPort, timeout });

            TableLayoutPanel action = Grid(2, 1);
            action.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
            action.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            StyleButton(echo, "发送回显 0x01", true);
            echo.Click += async delegate { await ProbeEchoAsync(); };
            action.Controls.Add(echo, 0, 0);
            Label caution = LabelText("回显不证明 EtherCAT 总线或驱动状态。", 10, false);
            caution.ForeColor = Muted;
            action.Controls.Add(caution, 1, 0);
            layout.Controls.Add(action, 0, 3);
            echoState.Text = "尚未通信";
            echoState.Dock = DockStyle.Fill;
            echoState.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
            echoState.TextAlign = ContentAlignment.MiddleLeft;
            echoState.ForeColor = Muted;
            layout.Controls.Add(echoState, 0, 4);
            echoDetails.Multiline = true;
            echoDetails.ReadOnly = true;
            echoDetails.Dock = DockStyle.Fill;
            echoDetails.BackColor = Surface;
            echoDetails.BorderStyle = BorderStyle.None;
            echoDetails.ScrollBars = ScrollBars.Vertical;
            echoDetails.Text = "响应中的标记、窗口 ID 与 App/Boot 信息将在这里显示。\r\n未知响应将保留为诊断信息，不会据此解锁硬件运动。";
            layout.Controls.Add(echoDetails, 0, 5);
        }

        private bool SelectAxis(int index)
        {
            if (index < 0 || index >= arm.AxisCount || AnySimulationRunning()) return false;
            selectedAxis = index;
            RefreshSimulation();
            return true;
        }

        private bool AnySimulationRunning()
        {
            return arm.IsMoving;
        }

        private bool StartSimulation(int direction)
        {
            if ((direction != -1 && direction != 1) || AnySimulationRunning()) return false;
            try { arm.MoveRelative(selectedAxis, direction * (double)amount.Value, (double)speed.Value); }
            catch (ArgumentException error)
            {
                AddLog("模拟启动已拒绝：" + error.Message);
                return false;
            }
            catch (InvalidOperationException error)
            {
                AddLog("模拟启动已拒绝：" + error.Message);
                return false;
            }
            previousSimulationTime = simulationClock.Elapsed.TotalSeconds;
            AddLog(String.Format(CultureInfo.InvariantCulture,
                "模拟轴{0}：相对 {1:+0;-0} 演示计数，目标 {2:0.##}，速度 {3:0} 演示计数/秒。",
                selectedAxis + 1, direction * amount.Value, arm.GetTarget(selectedAxis), speed.Value));
            RefreshSimulation();
            return true;
        }

        private void AdvanceSimulation(double seconds)
        {
            if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds < 0) throw new ArgumentOutOfRangeException("seconds");
            bool wasMoving = arm.IsMoving;
            arm.Advance(seconds);
            if (wasMoving && !arm.IsMoving)
            {
                AddLog("模拟轴" + (selectedAxis + 1) + "：已到达模拟目标。没有向硬件发送运动指令。");
            }
            RefreshSimulation();
        }

        private void StopSimulation()
        {
            bool wasRunning = AnySimulationRunning();
            arm.StopAll();
            AddLog(wasRunning ? "停止模拟：保留当前模拟位置。此按钮不发送硬件停止指令。" : "当前没有模拟运动。此按钮不发送硬件停止指令。");
            RefreshSimulation();
        }

        private void RefreshSimulation()
        {
            bool running = AnySimulationRunning();
            for (int index = 0; index < arm.AxisCount; index++)
            {
                axisCards[index].Text = String.Format(CultureInfo.InvariantCulture,
                    "软件轴 {0}  ·  模拟\r\n{1:0.##} 演示计数\r\n{2}", index + 1, arm.GetPosition(index), arm.IsAxisMoving(index) ? "运行中" : "静止");
                axisCards[index].BackColor = selectedAxis == index ? Surface : Color.White;
                axisCards[index].FlatAppearance.BorderColor = selectedAxis == index ? Accent : Border;
                axisCards[index].FlatAppearance.BorderSize = selectedAxis == index ? 2 : 1;
                axisCards[index].Enabled = !running;
            }
            selectedTitle.Text = "软件轴 " + (selectedAxis + 1) + " · 有限相对定位演示";
            actualFeedback.Text = arm.GetPosition(selectedAxis).ToString("0.##", CultureInfo.InvariantCulture);
            targetFeedback.Text = arm.GetTarget(selectedAxis).ToString("0.##", CultureInfo.InvariantCulture);
            motionFeedback.Text = arm.IsAxisMoving(selectedAxis) ? "模拟运行中" : "模拟静止";
            axisNote.Text = selectedAxis == 1
                ? "旧操作台的软件轴2对应升降机构；新卡的轴映射和方向尚未确认。\r\n模拟位置范围 ±1000 演示计数；不代表电机圈数、毫米、角度或真实机构位置。"
                : "新卡的软件轴与实体关节映射、比例和行程尚未确认。\r\n模拟位置范围 ±1000 演示计数；不代表电机圈数、毫米、角度或真实机构位置。";
            forward.Enabled = reverse.Enabled = amount.Enabled = speed.Enabled = !running;
            stop.Enabled = true;
        }

        private async System.Threading.Tasks.Task ProbeEchoAsync()
        {
            if (echoCancellation != null) return;
            echoCancellation = new CancellationTokenSource();
            CancellationTokenSource request = echoCancellation;
            foreach (Control input in endpointControls) input.Enabled = false;
            echo.Enabled = false;
            echoState.Text = "等待回显响应…";
            echoState.ForeColor = Accent;
            echoDetails.Text = "已由用户点击发送回显请求；不执行其他硬件命令。";
            echoProbeCount++;
            AddLog("用户请求 0x01 回显：电脑 " + localIp.Text.Trim() + ":" + localPort.Value + " → 控制卡 " + cardIp.Text.Trim() + ":" + cardPort.Value + "。");
            try
            {
                EchoResult result = await echoClient.ProbeAsync(localIp.Text.Trim(), (int)localPort.Value,
                    cardIp.Text.Trim(), (int)cardPort.Value, (int)timeout.Value, request.Token);
                if (IsDisposed || Disposing) return;
                echoState.Text = "已收到回显响应";
                echoState.ForeColor = Accent;
                echoDetails.Text = String.Format(CultureInfo.InvariantCulture,
                    "标记：0x{0:X2}\r\n窗口 ID：0x{1:X8}（{1}）\r\n{2}\r\n耗时：{3:0.##} ms\r\n\r\n仅为回显诊断结果；总线、驱动使能和机械状态仍未知。",
                    result.Marker, result.WindowId, result.Message, result.ElapsedMilliseconds);
                AddLog("回显完成：" + result.Message + "；不据此判断总线或运动就绪。");
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed && !Disposing)
                {
                    echoState.Text = "回显已取消";
                    AddLog("回显取消；没有执行后续硬件命令。");
                }
            }
            catch (Exception error)
            {
                if (!IsDisposed && !Disposing)
                {
                    echoState.Text = "回显未完成";
                    echoState.ForeColor = Color.FromArgb(180, 65, 40);
                    echoDetails.Text = error.Message + "\r\n\r\n保留当前配置。程序不会自动重试、改网卡、初始化总线或清除故障。";
                    AddLog("回显失败：" + error.Message);
                }
            }
            finally
            {
                if (!IsDisposed && !Disposing)
                {
                    foreach (Control input in endpointControls) input.Enabled = true;
                    echo.Enabled = true;
                }
                echoCancellation = null;
                request.Dispose();
            }
        }

        private void AddLog(string message)
        {
            history.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine);
        }

        internal bool RunSmokeTest(string screenshotPath, out string report)
        {
            simulationTimer.Stop();
            int checks = 0;
            Action<bool, string> check = delegate(bool condition, string description)
            {
                if (!condition) throw new InvalidOperationException("界面离线检查失败：" + description);
                checks++;
            };
            check(echoProbeCount == 0, "启动不发回显");
            check(modeBadge.Text.Contains("离线模拟"), "显示明确的离线模式标签");
            check(arm.AxisCount == 4 && axisCards.Length == 4, "显示四个软件轴");
            check(!AnySimulationRunning() && arm.GetPosition(0) == 0, "初始离线静止");
            check(localIp.Text == "192.168.8.12" && cardIp.Text == "192.168.8.20" && localPort.Value == 6000 && cardPort.Value == 5000, "诊断默认端点");
            check(SelectAxis(1), "选择软件轴2");
            check(axisNote.Text.Contains("尚未确认"), "旧升降映射不视为新卡映射");
            amount.Value = 12;
            speed.Value = 20;
            forward.PerformClick();
            check(arm.IsAxisMoving(1) && arm.GetTarget(1) == 12, "相对正向模拟启动");
            check(!SelectAxis(2) && !forward.Enabled && !amount.Enabled, "运行期间锁定选轴和重复启动");
            AdvanceSimulation(0.25);
            check(arm.GetPosition(1) == 5 && arm.IsAxisMoving(1), "模拟反馈推进");
            stop.PerformClick();
            check(!arm.IsAxisMoving(1) && arm.GetPosition(1) == 5 && arm.GetTarget(1) == 5, "停止保留当前位置");
            amount.Value = 2;
            reverse.PerformClick();
            AdvanceSimulation(1);
            check(arm.GetPosition(1) == 3 && !arm.IsAxisMoving(1), "停止后从当前位置反向相对定位");
            check(SelectAxis(3), "停止后可选择其他软件轴");
            amount.Value = 10;
            forward.PerformClick();
            AdvanceSimulation(1);
            check(arm.GetPosition(3) == 10 && arm.GetPosition(1) == 3, "四轴模拟位置互相独立");
            check(echoProbeCount == 0 && echoCancellation == null, "所有离线操作均不通信");
            SelectAxis(1);
            amount.Value = 100;
            speed.Value = 50;
            AddLog("离线界面检查通过：" + checks + " 项；未打开控制卡、未发送 UDP。");
            RefreshSimulation();
            Update();
            if (!String.IsNullOrWhiteSpace(screenshotPath))
            {
                string fullPath = Path.GetFullPath(screenshotPath);
                string directory = Path.GetDirectoryName(fullPath);
                if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                using (Bitmap bitmap = new Bitmap(Width, Height))
                {
                    DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                    bitmap.Save(fullPath, ImageFormat.Png);
                }
            }
            report = "PASS: " + checks + " offline UI checks; 0 echo probes; no hardware operations.";
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && simulationTimer != null) simulationTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
