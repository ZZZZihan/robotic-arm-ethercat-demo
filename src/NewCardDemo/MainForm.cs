using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NewCard.Core;

namespace NewCardDemo
{
    internal sealed class MainForm : Form
    {
        private const string WindowTitle = "四轴机械臂控制";
        private const double DefaultAccelerationRevPerSec2 = 1.0;

        private static readonly Color Bg = Color.FromArgb(245, 247, 250);
        private static readonly Color CardBg = Color.White;
        private static readonly Color SelectedBg = Color.FromArgb(238, 246, 253);
        private static readonly Color SuccessBg = Color.FromArgb(236, 248, 241);
        private static readonly Color DangerBg = Color.FromArgb(253, 240, 239);
        private static readonly Color BorderColor = Color.FromArgb(221, 227, 234);
        private static readonly Color Accent = Color.FromArgb(22, 119, 255);
        private static readonly Color Success = Color.FromArgb(22, 148, 82);
        private static readonly Color Danger = Color.FromArgb(214, 48, 49);
        private static readonly Color Ink = Color.FromArgb(30, 41, 59);
        private static readonly Color Muted = Color.FromArgb(100, 116, 139);
        private static readonly Color DisabledBg = Color.FromArgb(226, 232, 240);

        private readonly Font fontTitle = new Font("Microsoft YaHei UI", 13.5F, FontStyle.Bold);
        private readonly Font fontSection = new Font("Microsoft YaHei UI", 11.5F, FontStyle.Bold);
        private readonly Font fontAxisValue = new Font("Microsoft YaHei UI", 14.5F, FontStyle.Bold);
        private readonly Font fontBold = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        private readonly Font fontRegular = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular);
        private readonly Font fontSmall = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular);
        private readonly Font fontInput = new Font("Microsoft YaHei UI", 11F, FontStyle.Regular);

        private readonly Sc4iClient sc4iClient = new Sc4iClient();
        private readonly System.Windows.Forms.Timer pollTimer;
        private readonly System.Windows.Forms.Timer jogTimer;

        private string localIp = "192.168.8.12";
        private int localPort = 6000;
        private string cardIp = "192.168.8.20";
        private int cardPort = 5000;

        private readonly Label connectionBadge = new Label();
        private readonly Button connectButton = new Button();
        private readonly Button clearFaultButton = new Button();
        private readonly Button estopButton = new Button();
        private readonly Button settingsButton = new Button();

        private readonly AxisCard[] axisCards = new AxisCard[4];
        private readonly Panel cartesianBanner = new Panel();
        private readonly Label cartesianCoordsLabel = new Label();
        private readonly Label cartesianBranchLabel = new Label();
        private readonly Button jointModeButton = new Button();
        private readonly Button cartModeButton = new Button();
        private readonly Button setZeroButton = new Button();
        private readonly Button calDialogButton = new Button();

        private readonly Panel controlPanel = new Panel();
        private readonly TableLayoutPanel jointControlContainer = CreateGrid(1, 3);
        private readonly TableLayoutPanel cartControlContainer = CreateGrid(1, 4);
        private readonly Label selectedAxisLabel = new Label();
        private readonly Button enableButton = new Button();
        private readonly NumericUpDown stepInput = new NumericUpDown();
        private readonly NumericUpDown velInput = new NumericUpDown();
        private readonly Button moveNegButton = new Button();
        private readonly Button movePosButton = new Button();
        private readonly Button jogNegButton = new Button();
        private readonly Button jogPosButton = new Button();

        private readonly Button enableAllAxesButton = new Button();
        private readonly Button goHomeButton = new Button();
        private readonly CheckBox keepWorldRCheck = new CheckBox();
        private readonly NumericUpDown cartStepMmInput = new NumericUpDown();
        private readonly NumericUpDown cartStepDegInput = new NumericUpDown();
        private readonly NumericUpDown cartVelRevInput = new NumericUpDown();
        private readonly ComboBox elbowCombo = new ComboBox();
        private readonly Button[] cartJogButtons = new Button[8];
        private readonly NumericUpDown targetXInput = new NumericUpDown();
        private readonly NumericUpDown targetYInput = new NumericUpDown();
        private readonly NumericUpDown targetZInput = new NumericUpDown();
        private readonly NumericUpDown targetRInput = new NumericUpDown();
        private readonly Button fillCurrentTargetButton = new Button();
        private readonly Button moveTargetButton = new Button();
        private readonly Label statusLabel = new Label();

        private readonly string calibrationPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scara-calibration.ini");
        private ScaraCalibration calibration = new ScaraCalibration();
        private bool cartesianMode;
        private int selectedAxis;
        private bool busy;
        private bool pollInFlight;
        private bool jogActive;
        private bool jogKeepaliveInFlight;
        private int jogGeneration;
        private int heartbeatFailCount;
        private bool sessionLost;
        private bool formClosing;
        private Sc4iStatusReply liveStatus;
        private CancellationTokenSource activeOpCts;
        private CancellationTokenSource pollCts;
        private CancellationTokenSource jogCts;

        internal MainForm()
        {
            Text = WindowTitle;
            Font = fontRegular;
            ForeColor = Ink;
            BackColor = Bg;
            ClientSize = new Size(760, 620);
            MinimumSize = new Size(720, 580);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            DoubleBuffered = true;

            try { calibration = ScaraCalibration.LoadOrDefault(calibrationPath); }
            catch { calibration = new ScaraCalibration(); }

            BuildLayout();

            pollTimer = new System.Windows.Forms.Timer { Interval = 150 };
            pollTimer.Tick += async delegate { await PollStatusAsync(); };

            jogTimer = new System.Windows.Forms.Timer { Interval = 80 };
            jogTimer.Tick += async delegate { await SendJogKeepaliveAsync(); };

            Deactivate += async delegate
            {
                if (jogActive) await StopJogAsync("窗口失焦，已停止点动");
            };

            FormClosing += delegate
            {
                formClosing = true;
                pollTimer.Stop();
                jogTimer.Stop();
                bool shouldStop = jogActive || (liveStatus != null && liveStatus.MotionState != 0);
                jogActive = false;
                jogGeneration++;
                CancelAllInFlightOps();
                if (sc4iClient.IsConnected && shouldStop)
                {
                    try
                    {
                        using (CancellationTokenSource cts = new CancellationTokenSource(500))
                        {
                            sc4iClient.StopHoldAsync(cts.Token).GetAwaiter().GetResult();
                        }
                    }
                    catch { }
                }
                sc4iClient.Disconnect();
            };

            SetStatus("未连接，点击右上角“连接”开始。", Muted);
            RefreshUi();
        }

        private void BuildLayout()
        {
            TableLayoutPanel root = CreateGrid(1, 6);
            root.Padding = new Padding(20, 14, 20, 12);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            Controls.Add(root);

            root.Controls.Add(BuildTopBar(), 0, 0);
            root.Controls.Add(BuildAxisRow(), 0, 1);
            root.Controls.Add(BuildCartesianBanner(), 0, 2);
            root.Controls.Add(BuildModeBar(), 0, 3);
            root.Controls.Add(BuildControlCard(), 0, 4);

            statusLabel.Dock = DockStyle.Fill;
            statusLabel.Font = fontSmall;
            statusLabel.ForeColor = Muted;
            statusLabel.TextAlign = ContentAlignment.BottomLeft;
            statusLabel.AutoEllipsis = true;
            root.Controls.Add(statusLabel, 0, 5);
        }

        private Control BuildCartesianBanner()
        {
            cartesianBanner.Dock = DockStyle.Fill;
            cartesianBanner.BackColor = CardBg;
            cartesianBanner.Margin = new Padding(0, 0, 0, 8);
            cartesianBanner.Padding = new Padding(14, 0, 14, 0);
            cartesianBanner.Resize += delegate { cartesianBanner.Invalidate(); };
            cartesianBanner.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(BorderColor, 1))
                    e.Graphics.DrawRectangle(pen, 0, 0, cartesianBanner.Width - 1, cartesianBanner.Height - 1);
            };

            TableLayoutPanel row = CreateGrid(2, 1);
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            cartesianCoordsLabel.Dock = DockStyle.Fill;
            cartesianCoordsLabel.Font = fontBold;
            cartesianCoordsLabel.ForeColor = Ink;
            cartesianCoordsLabel.TextAlign = ContentAlignment.MiddleLeft;
            cartesianCoordsLabel.Text = "X: --   Y: --   Z: --   R: --";

            cartesianBranchLabel.Dock = DockStyle.Fill;
            cartesianBranchLabel.Font = fontSmall;
            cartesianBranchLabel.ForeColor = Muted;
            cartesianBranchLabel.TextAlign = ContentAlignment.MiddleRight;
            cartesianBranchLabel.Text = "L1=250 L2=145 mm";

            row.Controls.Add(cartesianCoordsLabel, 0, 0);
            row.Controls.Add(cartesianBranchLabel, 1, 0);
            cartesianBanner.Controls.Add(row);
            return cartesianBanner;
        }

        private Control BuildModeBar()
        {
            TableLayoutPanel bar = CreateGrid(5, 1);
            bar.Padding = new Padding(0, 0, 0, 8);
            bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 174));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));

            StyleButton(jointModeButton, "关节单轴", Accent, Color.White, 0);
            StyleButton(cartModeButton, "笛卡尔坐标 (SCARA)", CardBg, Ink, 1);
            StyleButton(setZeroButton, "设为零点", CardBg, Ink, 1);
            StyleButton(calDialogButton, "标定参数", CardBg, Muted, 1);
            jointModeButton.Font = fontBold;
            cartModeButton.Font = fontBold;
            jointModeButton.Margin = new Padding(0, 0, 8, 0);
            cartModeButton.Margin = new Padding(0, 0, 8, 0);
            setZeroButton.Margin = new Padding(0, 0, 8, 0);
            calDialogButton.Margin = new Padding(0);

            jointModeButton.Click += delegate { SetControlMode(false); };
            cartModeButton.Click += delegate { SetControlMode(true); };
            setZeroButton.Click += delegate { CaptureCurrentAsZero(); };
            calDialogButton.Click += delegate { ShowCalibrationDialog(); };

            bar.Controls.Add(jointModeButton, 0, 0);
            bar.Controls.Add(cartModeButton, 1, 0);
            bar.Controls.Add(setZeroButton, 3, 0);
            bar.Controls.Add(calDialogButton, 4, 0);
            return bar;
        }

        private void SetControlMode(bool useCartesian)
        {
            if (jogActive) return;
            cartesianMode = useCartesian;
            jointControlContainer.Visible = !cartesianMode;
            cartControlContainer.Visible = cartesianMode;
            if (cartesianMode) FillTargetFromCurrentIfConnected();
            RefreshUi();
        }

        private Control BuildTopBar()
        {
            TableLayoutPanel bar = CreateGrid(6, 1);
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));

            Label title = new Label
            {
                Text = WindowTitle,
                AutoSize = true,
                Font = fontTitle,
                ForeColor = Ink,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 4, 8, 0)
            };
            bar.Controls.Add(title, 0, 0);

            connectionBadge.Dock = DockStyle.Fill;
            connectionBadge.Font = fontBold;
            connectionBadge.TextAlign = ContentAlignment.MiddleLeft;
            connectionBadge.Margin = new Padding(2, 6, 8, 0);
            bar.Controls.Add(connectionBadge, 1, 0);

            StyleButton(connectButton, "连接", Accent, Color.White, 0);
            StyleButton(clearFaultButton, "清故障", CardBg, Ink, 1);
            StyleButton(estopButton, "急停", Danger, Color.White, 0);
            StyleButton(settingsButton, "设置", CardBg, Muted, 1);

            connectButton.Font = fontBold;
            estopButton.Font = fontBold;
            connectButton.Margin = new Padding(0, 2, 8, 2);
            clearFaultButton.Margin = new Padding(0, 2, 8, 2);
            estopButton.Margin = new Padding(0, 2, 8, 2);
            settingsButton.Margin = new Padding(0, 2, 0, 2);

            connectButton.Click += async delegate { await ToggleConnectAsync(); };
            clearFaultButton.Click += async delegate { await ClearFaultsAsync(); };
            estopButton.Click += async delegate { await TriggerStopHoldAsync("已触发急停，保持伺服持力"); };
            settingsButton.Click += delegate { ShowSettingsDialog(); };

            bar.Controls.Add(connectButton, 2, 0);
            bar.Controls.Add(clearFaultButton, 3, 0);
            bar.Controls.Add(estopButton, 4, 0);
            bar.Controls.Add(settingsButton, 5, 0);
            return bar;
        }

        private Control BuildAxisRow()
        {
            TableLayoutPanel row = CreateGrid(4, 1);
            row.Padding = new Padding(0, 12, 0, 12);
            for (int i = 0; i < 4; i++)
            {
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
                int captured = i;
                AxisCard card = new AxisCard(i + 1, fontBold, fontAxisValue, fontSmall);
                card.Margin = new Padding(i == 0 ? 0 : 5, 0, i == 3 ? 0 : 5, 0);
                card.CardClicked += delegate
                {
                    if (jogActive) return;
                    SelectAxis(captured);
                };
                axisCards[i] = card;
                row.Controls.Add(card, i, 0);
            }
            return row;
        }

        private Control BuildControlCard()
        {
            controlPanel.Dock = DockStyle.Fill;
            controlPanel.BackColor = CardBg;
            controlPanel.Padding = new Padding(16, 12, 16, 14);
            controlPanel.Resize += delegate { controlPanel.Invalidate(); };
            controlPanel.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(BorderColor, 1))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, controlPanel.Width - 1, controlPanel.Height - 1);
                }
            };

            // Joint single-axis control panel
            jointControlContainer.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            jointControlContainer.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            jointControlContainer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            controlPanel.Controls.Add(jointControlContainer);

            // Row 0: Selected axis title + Enable button
            TableLayoutPanel header = CreateGrid(2, 1);
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            selectedAxisLabel.Dock = DockStyle.Fill;
            selectedAxisLabel.Font = fontSection;
            selectedAxisLabel.ForeColor = Ink;
            selectedAxisLabel.TextAlign = ContentAlignment.MiddleLeft;
            StyleButton(enableButton, "使能轴 1", Success, Color.White, 0);
            enableButton.Font = fontBold;
            enableButton.Margin = new Padding(8, 2, 0, 2);
            enableButton.Click += async delegate { await EnableSelectedAxisAsync(); };
            header.Controls.Add(selectedAxisLabel, 0, 0);
            header.Controls.Add(enableButton, 1, 0);
            jointControlContainer.Controls.Add(header, 0, 0);

            // Row 1: Step & Speed inputs
            TableLayoutPanel inputs = CreateGrid(2, 2);
            inputs.Padding = new Padding(0, 12, 0, 6);
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            inputs.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            inputs.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            inputs.Controls.Add(CreateCaption("步长 (圈)"), 0, 0);
            inputs.Controls.Add(CreateCaption("速度 (圈/秒)"), 1, 0);
            ConfigureDecimalInput(stepInput, 0.01m, 10.00m, 0.20m, 0.05m, 2);
            ConfigureDecimalInput(velInput, 0.02m, 2.00m, 0.25m, 0.05m, 2);
            stepInput.Margin = new Padding(0, 2, 14, 2);
            velInput.Margin = new Padding(0, 2, 0, 2);
            inputs.Controls.Add(stepInput, 0, 1);
            inputs.Controls.Add(velInput, 1, 1);
            jointControlContainer.Controls.Add(inputs, 0, 1);

            // Row 2: Move & Jog buttons
            TableLayoutPanel actions = CreateGrid(4, 1);
            actions.Padding = new Padding(0, 12, 0, 2);
            for (int i = 0; i < 4; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

            StyleButton(moveNegButton, "− 反向", Accent, Color.White, 0);
            StyleButton(movePosButton, "+ 正向", Accent, Color.White, 0);
            StyleButton(jogNegButton, "◀ 按住点动", CardBg, Ink, 1);
            StyleButton(jogPosButton, "▶ 按住点动", CardBg, Ink, 1);
            moveNegButton.Font = fontBold;
            movePosButton.Font = fontBold;
            moveNegButton.Margin = new Padding(0, 0, 10, 0);
            movePosButton.Margin = new Padding(0, 0, 10, 0);
            jogNegButton.Margin = new Padding(0, 0, 10, 0);
            jogPosButton.Margin = new Padding(0);

            moveNegButton.Click += async delegate { await MoveSelectedAxisAsync(-1); };
            movePosButton.Click += async delegate { await MoveSelectedAxisAsync(1); };
            jogNegButton.MouseDown += async delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) await StartJogAsync(-1);
            };
            jogPosButton.MouseDown += async delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) await StartJogAsync(1);
            };
            jogNegButton.MouseUp += async delegate { await StopJogAsync("已停止点动并持力"); };
            jogPosButton.MouseUp += async delegate { await StopJogAsync("已停止点动并持力"); };
            jogNegButton.MouseCaptureChanged += async delegate
            {
                if (jogActive) await StopJogAsync("已停止点动并持力");
            };
            jogPosButton.MouseCaptureChanged += async delegate
            {
                if (jogActive) await StopJogAsync("已停止点动并持力");
            };

            actions.Controls.Add(moveNegButton, 0, 0);
            actions.Controls.Add(movePosButton, 1, 0);
            actions.Controls.Add(jogNegButton, 2, 0);
            actions.Controls.Add(jogPosButton, 3, 0);
            jointControlContainer.Controls.Add(actions, 0, 2);

            // Cartesian SCARA control panel (hidden until user switches mode)
            BuildCartesianControls();
            cartControlContainer.Visible = false;
            controlPanel.Controls.Add(cartControlContainer);

            return controlPanel;
        }

        private void BuildCartesianControls()
        {
            cartControlContainer.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            cartControlContainer.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            cartControlContainer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            cartControlContainer.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

            // Row 0: Enable all 4 axes + Home + Keep R checkbox
            TableLayoutPanel top = CreateGrid(4, 1);
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 146));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 102));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
            StyleButton(enableAllAxesButton, "使能全部四轴", Success, Color.White, 0);
            StyleButton(goHomeButton, "安全回零点", CardBg, Ink, 1);
            enableAllAxesButton.Font = fontBold;
            enableAllAxesButton.Margin = new Padding(0, 0, 8, 2);
            goHomeButton.Margin = new Padding(0, 0, 8, 2);
            enableAllAxesButton.Click += async delegate { await EnableAllAxesAsync(); };
            goHomeButton.Click += async delegate { await MoveHomeZeroAsync(); };

            keepWorldRCheck.Text = "保持末端朝向 R";
            keepWorldRCheck.Dock = DockStyle.Fill;
            keepWorldRCheck.Font = fontSmall;
            keepWorldRCheck.ForeColor = Ink;
            keepWorldRCheck.Checked = false;
            keepWorldRCheck.TextAlign = ContentAlignment.MiddleRight;

            Label hint = new Label
            {
                Text = "安全顺序：上升先提Z，下降后降Z",
                Dock = DockStyle.Fill,
                Font = fontSmall,
                ForeColor = Muted,
                TextAlign = ContentAlignment.MiddleLeft
            };

            top.Controls.Add(enableAllAxesButton, 0, 0);
            top.Controls.Add(goHomeButton, 1, 0);
            top.Controls.Add(hint, 2, 0);
            top.Controls.Add(keepWorldRCheck, 3, 0);
            cartControlContainer.Controls.Add(top, 0, 0);

            // Row 1: Step mm, Step deg, Speed rev/s, Elbow mode
            TableLayoutPanel cfg = CreateGrid(4, 2);
            cfg.Padding = new Padding(0, 4, 0, 4);
            for (int i = 0; i < 4; i++) cfg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            cfg.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            cfg.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            cfg.Controls.Add(CreateCaption("XY / Z 步长 (mm)"), 0, 0);
            cfg.Controls.Add(CreateCaption("R 步长 (°)"), 1, 0);
            cfg.Controls.Add(CreateCaption("单轴速度 (圈/秒)"), 2, 0);
            cfg.Controls.Add(CreateCaption("肘部手系"), 3, 0);
            ConfigureDecimalInput(cartStepMmInput, 1.0m, 80.0m, 10.0m, 5.0m, 1);
            ConfigureDecimalInput(cartStepDegInput, 1.0m, 45.0m, 5.0m, 5.0m, 1);
            ConfigureDecimalInput(cartVelRevInput, 0.05m, 1.50m, 0.35m, 0.05m, 2);
            cartStepMmInput.Margin = new Padding(0, 2, 10, 0);
            cartStepDegInput.Margin = new Padding(0, 2, 10, 0);
            cartVelRevInput.Margin = new Padding(0, 2, 10, 0);
            elbowCombo.Dock = DockStyle.Fill;
            elbowCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            elbowCombo.Font = fontRegular;
            elbowCombo.Items.AddRange(new object[] { "自动最近", "右手系 (θ2≥0)", "左手系 (θ2≤0)" });
            elbowCombo.SelectedIndex = 0;
            elbowCombo.Margin = new Padding(0, 2, 0, 0);
            cfg.Controls.Add(cartStepMmInput, 0, 1);
            cfg.Controls.Add(cartStepDegInput, 1, 1);
            cfg.Controls.Add(cartVelRevInput, 2, 1);
            cfg.Controls.Add(elbowCombo, 3, 1);
            cartControlContainer.Controls.Add(cfg, 0, 1);

            // Row 2: 2x4 Cartesian relative jog buttons
            TableLayoutPanel jogGrid = CreateGrid(4, 2);
            jogGrid.Padding = new Padding(0, 4, 0, 6);
            for (int c = 0; c < 4; c++) jogGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            jogGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            jogGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            string[] labels = { "− X", "+ X", "− Y", "+ Y", "− Z (降)", "+ Z (升)", "− R (顺)", "+ R (逆)" };
            for (int i = 0; i < 8; i++)
            {
                int idx = i;
                Button btn = new Button();
                StyleButton(btn, labels[i], Accent, Color.White, 0);
                btn.Font = fontBold;
                btn.Margin = new Padding(0, i < 4 ? 0 : 4, (i % 4) == 3 ? 0 : 8, i < 4 ? 4 : 0);
                btn.Click += async delegate { await MoveCartesianStepButtonAsync(idx); };
                cartJogButtons[i] = btn;
                jogGrid.Controls.Add(btn, i % 4, i / 4);
            }
            cartControlContainer.Controls.Add(jogGrid, 0, 2);

            // Row 3: Absolute target X/Y/Z/R + Fill Current + Go
            TableLayoutPanel abs = CreateGrid(6, 2);
            abs.Padding = new Padding(0, 4, 0, 0);
            abs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
            abs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
            abs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
            abs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
            abs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
            abs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
            abs.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            abs.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            abs.Controls.Add(CreateCaption("目标 X (mm)"), 0, 0);
            abs.Controls.Add(CreateCaption("目标 Y (mm)"), 1, 0);
            abs.Controls.Add(CreateCaption("目标 Z (mm)"), 2, 0);
            abs.Controls.Add(CreateCaption("目标 R (°)"), 3, 0);
            ConfigureDecimalInput(targetXInput, -400.0m, 400.0m, 395.0m, 5.0m, 1);
            ConfigureDecimalInput(targetYInput, -400.0m, 400.0m, 0.0m, 5.0m, 1);
            ConfigureDecimalInput(targetZInput, 0.0m, 300.0m, 100.0m, 5.0m, 1);
            ConfigureDecimalInput(targetRInput, -180.0m, 180.0m, 0.0m, 5.0m, 1);
            targetXInput.Margin = new Padding(0, 2, 8, 0);
            targetYInput.Margin = new Padding(0, 2, 8, 0);
            targetZInput.Margin = new Padding(0, 2, 8, 0);
            targetRInput.Margin = new Padding(0, 2, 8, 0);
            StyleButton(fillCurrentTargetButton, "填入当前", CardBg, Ink, 1);
            StyleButton(moveTargetButton, "定位目标", Success, Color.White, 0);
            moveTargetButton.Font = fontBold;
            fillCurrentTargetButton.Margin = new Padding(0, 2, 8, 0);
            moveTargetButton.Margin = new Padding(0, 2, 0, 0);
            fillCurrentTargetButton.Click += delegate { FillTargetFromCurrentIfConnected(); };
            moveTargetButton.Click += async delegate { await MoveCartesianAbsoluteAsync(); };

            abs.Controls.Add(targetXInput, 0, 1);
            abs.Controls.Add(targetYInput, 1, 1);
            abs.Controls.Add(targetZInput, 2, 1);
            abs.Controls.Add(targetRInput, 3, 1);
            abs.Controls.Add(fillCurrentTargetButton, 4, 1);
            abs.Controls.Add(moveTargetButton, 5, 1);
            cartControlContainer.Controls.Add(abs, 0, 3);
        }

        private void SelectAxis(int index)
        {
            if (index < 0 || index >= 4) return;
            selectedAxis = index;
            RefreshUi();
        }

        private void ShowSettingsDialog()
        {
            if (sc4iClient.IsConnected || busy) return;
            using (Form dialog = new Form())
            {
                dialog.Text = "网络设置";
                dialog.Font = fontRegular;
                dialog.ForeColor = Ink;
                dialog.BackColor = CardBg;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ClientSize = new Size(340, 210);

                TableLayoutPanel grid = CreateGrid(2, 5);
                grid.Padding = new Padding(18, 16, 18, 14);
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                for (int r = 0; r < 4; r++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                dialog.Controls.Add(grid);

                TextBox localIpBox = new TextBox { Text = localIp, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
                NumericUpDown localPortBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = localPort, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
                TextBox cardIpBox = new TextBox { Text = cardIp, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
                NumericUpDown cardPortBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = cardPort, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };

                grid.Controls.Add(CreateDialogLabel("电脑 IP"), 0, 0);
                grid.Controls.Add(localIpBox, 1, 0);
                grid.Controls.Add(CreateDialogLabel("本地端口"), 0, 1);
                grid.Controls.Add(localPortBox, 1, 1);
                grid.Controls.Add(CreateDialogLabel("控制卡 IP"), 0, 2);
                grid.Controls.Add(cardIpBox, 1, 2);
                grid.Controls.Add(CreateDialogLabel("卡端口"), 0, 3);
                grid.Controls.Add(cardPortBox, 1, 3);

                FlowLayoutPanel buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    Margin = new Padding(0, 10, 0, 0)
                };
                Button ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 78, Height = 30 };
                Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 78, Height = 30 };
                StyleButton(ok, "确定", Accent, Color.White, 0);
                StyleButton(cancel, "取消", CardBg, Ink, 1);
                ok.Dock = DockStyle.None;
                cancel.Dock = DockStyle.None;
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                grid.Controls.Add(buttons, 0, 4);
                grid.SetColumnSpan(buttons, 2);

                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    localIp = localIpBox.Text.Trim();
                    localPort = (int)localPortBox.Value;
                    cardIp = cardIpBox.Text.Trim();
                    cardPort = (int)cardPortBox.Value;
                    SetStatus(String.Format(CultureInfo.InvariantCulture,
                        "已更新地址：{0}:{1} → {2}:{3}", localIp, localPort, cardIp, cardPort), Muted);
                }
            }
        }

        private bool TryGetLiveCounts(Sc4iStatusReply st, out int[] counts)
        {
            counts = null;
            if (st == null || !st.BusReady || st.Slaves == null || st.Slaves.Length < 4) return false;
            int[] arr = new int[4];
            for (int i = 0; i < 4; i++)
            {
                if (st.Slaves[i] == null || st.Slaves[i].ValidMask == 0) return false;
                arr[i] = st.Slaves[i].ActualPositionCounts;
            }
            counts = arr;
            return true;
        }

        private void FillTargetFromCurrentIfConnected()
        {
            int[] counts;
            if (!TryGetLiveCounts(liveStatus, out counts)) return;
            ScaraJointPose j;
            ScaraCartesianPose cart = ScaraKinematics.ForwardFromCounts(calibration, counts, out j);
            SetNumericClamped(targetXInput, (decimal)Math.Round(cart.XMm, 1));
            SetNumericClamped(targetYInput, (decimal)Math.Round(cart.YMm, 1));
            SetNumericClamped(targetZInput, (decimal)Math.Round(cart.ZMm, 1));
            SetNumericClamped(targetRInput, (decimal)Math.Round(cart.RDeg, 1));
        }

        private static void SetNumericClamped(NumericUpDown input, decimal value)
        {
            if (value < input.Minimum) value = input.Minimum;
            if (value > input.Maximum) value = input.Maximum;
            input.Value = value;
        }

        private void CaptureCurrentAsZero()
        {
            if (busy || jogActive) return;
            int[] counts;
            if (!TryGetLiveCounts(liveStatus, out counts))
            {
                SetStatus("请先连接控制卡并进 OP 后再设置零点。", Danger);
                return;
            }

            using (Form dialog = new Form())
            {
                dialog.Text = "将当前姿态设为零点";
                dialog.Font = fontRegular;
                dialog.ForeColor = Ink;
                dialog.BackColor = CardBg;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ClientSize = new Size(380, 220);

                TableLayoutPanel grid = CreateGrid(2, 4);
                grid.Padding = new Padding(18, 14, 18, 14);
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                dialog.Controls.Add(grid);

                Label desc = new Label
                {
                    Text = "确认机械臂已摆到标准零点姿态（大臂朝底板正前、小臂共线伸直、末端铁片朝正前），并输入此时末端离地高度 H0：",
                    Dock = DockStyle.Fill,
                    Font = fontSmall,
                    ForeColor = Ink
                };
                grid.Controls.Add(desc, 0, 0);
                grid.SetColumnSpan(desc, 2);

                NumericUpDown h0Box = new NumericUpDown();
                ConfigureDecimalInput(h0Box, 0.0m, 400.0m, (decimal)calibration.ZeroHeightMm, 5.0m, 1);
                grid.Controls.Add(CreateDialogLabel("当前离地高度 H0 (mm)"), 0, 1);
                grid.Controls.Add(h0Box, 1, 1);

                Label ctsInfo = new Label
                {
                    Text = String.Format(CultureInfo.InvariantCulture,
                        "当前脉冲：S0={0}, S1={1}, S2={2}, S3={3}", counts[0], counts[1], counts[2], counts[3]),
                    Dock = DockStyle.Fill,
                    Font = fontSmall,
                    ForeColor = Muted,
                    TextAlign = ContentAlignment.MiddleLeft
                };
                grid.Controls.Add(ctsInfo, 0, 2);
                grid.SetColumnSpan(ctsInfo, 2);

                FlowLayoutPanel buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    Margin = new Padding(0, 8, 0, 0)
                };
                Button ok = new Button { Text = "保存为零点", DialogResult = DialogResult.OK, Width = 104, Height = 30 };
                Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 78, Height = 30 };
                StyleButton(ok, "保存为零点", Accent, Color.White, 0);
                StyleButton(cancel, "取消", CardBg, Ink, 1);
                ok.Dock = DockStyle.None;
                cancel.Dock = DockStyle.None;
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                grid.Controls.Add(buttons, 0, 3);
                grid.SetColumnSpan(buttons, 2);

                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    calibration.CaptureZeroFromCounts(counts, (double)h0Box.Value);
                    try
                    {
                        calibration.SaveToFile(calibrationPath);
                        FillTargetFromCurrentIfConnected();
                        SetStatus(String.Format(CultureInfo.InvariantCulture,
                            "已将当前姿态保存为零点（X={0:0.1}, Y=0, Z={1:0.1}, R=0）。",
                            calibration.MaxReachMm, calibration.ZeroHeightMm), Success);
                    }
                    catch (Exception ex)
                    {
                        SetStatus("保存标定文件失败：" + ex.Message, Danger);
                    }
                    RefreshUi();
                }
            }
        }

        private void ShowCalibrationDialog()
        {
            if (busy || jogActive) return;
            using (Form dialog = new Form())
            {
                dialog.Text = "SCARA 标定参数";
                dialog.Font = fontRegular;
                dialog.ForeColor = Ink;
                dialog.BackColor = CardBg;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ClientSize = new Size(400, 360);

                TableLayoutPanel grid = CreateGrid(2, 9);
                grid.Padding = new Padding(18, 14, 18, 14);
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                for (int r = 0; r < 8; r++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                dialog.Controls.Add(grid);

                NumericUpDown l1Box = new NumericUpDown();
                NumericUpDown l2Box = new NumericUpDown();
                NumericUpDown h0Box = new NumericUpDown();
                NumericUpDown j1RatioBox = new NumericUpDown();
                NumericUpDown j2LeadBox = new NumericUpDown();
                NumericUpDown j3RatioBox = new NumericUpDown();
                NumericUpDown j4RatioBox = new NumericUpDown();
                NumericUpDown j4MinBox = new NumericUpDown();

                ConfigureDecimalInput(l1Box, 50m, 800m, (decimal)calibration.ArmLength1Mm, 1m, 2);
                ConfigureDecimalInput(l2Box, 50m, 800m, (decimal)calibration.ArmLength2Mm, 1m, 2);
                ConfigureDecimalInput(h0Box, 0m, 500m, (decimal)calibration.ZeroHeightMm, 1m, 2);
                ConfigureDecimalInput(j1RatioBox, 1m, 200m, (decimal)Math.Round(calibration.J1GearRatio, 6), 0.1m, 6);
                ConfigureDecimalInput(j2LeadBox, 1m, 50m, (decimal)Math.Round(calibration.J2LeadMmPerRev, 4), 0.5m, 3);
                ConfigureDecimalInput(j3RatioBox, 1m, 200m, (decimal)Math.Round(calibration.J3GearRatio, 6), 0.1m, 6);
                ConfigureDecimalInput(j4RatioBox, 0.5m, 100m, (decimal)Math.Round(calibration.J4GearRatio, 4), 0.2m, 4);
                ConfigureDecimalInput(j4MinBox, -180m, 0m, (decimal)calibration.J4MinDeg, 5m, 1);

                grid.Controls.Add(CreateDialogLabel("大臂长 L1 (mm)"), 0, 0); grid.Controls.Add(l1Box, 1, 0);
                grid.Controls.Add(CreateDialogLabel("小臂长 L2 (mm)"), 0, 1); grid.Controls.Add(l2Box, 1, 1);
                grid.Controls.Add(CreateDialogLabel("零点离地高度 H0 (mm)"), 0, 2); grid.Controls.Add(h0Box, 1, 2);
                grid.Controls.Add(CreateDialogLabel("J1 大臂速比"), 0, 3); grid.Controls.Add(j1RatioBox, 1, 3);
                grid.Controls.Add(CreateDialogLabel("J2 丝杆导程 (mm/圈)"), 0, 4); grid.Controls.Add(j2LeadBox, 1, 4);
                grid.Controls.Add(CreateDialogLabel("J3 小臂速比"), 0, 5); grid.Controls.Add(j3RatioBox, 1, 5);
                grid.Controls.Add(CreateDialogLabel("J4 末端速比"), 0, 6); grid.Controls.Add(j4RatioBox, 1, 6);
                grid.Controls.Add(CreateDialogLabel("J4 顺时针软限位 (°)"), 0, 7); grid.Controls.Add(j4MinBox, 1, 7);

                FlowLayoutPanel buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    Margin = new Padding(0, 10, 0, 0)
                };
                Button ok = new Button { Text = "保存", DialogResult = DialogResult.OK, Width = 78, Height = 30 };
                Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 78, Height = 30 };
                Button reset = new Button { Text = "恢复默认", Width = 88, Height = 30 };
                StyleButton(ok, "保存", Accent, Color.White, 0);
                StyleButton(cancel, "取消", CardBg, Ink, 1);
                StyleButton(reset, "恢复默认", CardBg, Muted, 1);
                ok.Dock = cancel.Dock = reset.Dock = DockStyle.None;
                reset.Click += delegate
                {
                    ScaraCalibration def = new ScaraCalibration();
                    l1Box.Value = (decimal)def.ArmLength1Mm;
                    l2Box.Value = (decimal)def.ArmLength2Mm;
                    h0Box.Value = (decimal)def.ZeroHeightMm;
                    j1RatioBox.Value = (decimal)Math.Round(def.J1GearRatio, 6);
                    j2LeadBox.Value = (decimal)Math.Round(def.J2LeadMmPerRev, 4);
                    j3RatioBox.Value = (decimal)Math.Round(def.J3GearRatio, 6);
                    j4RatioBox.Value = (decimal)Math.Round(def.J4GearRatio, 4);
                    j4MinBox.Value = (decimal)def.J4MinDeg;
                };
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(reset);
                grid.Controls.Add(buttons, 0, 8);
                grid.SetColumnSpan(buttons, 2);

                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    ScaraCalibration updated = calibration.Clone();
                    updated.ArmLength1Mm = (double)l1Box.Value;
                    updated.ArmLength2Mm = (double)l2Box.Value;
                    updated.ZeroHeightMm = (double)h0Box.Value;
                    updated.J1GearRatio = (double)j1RatioBox.Value;
                    updated.J2LeadMmPerRev = (double)j2LeadBox.Value;
                    updated.J3GearRatio = (double)j3RatioBox.Value;
                    updated.J4GearRatio = (double)j4RatioBox.Value;
                    updated.J4MinDeg = (double)j4MinBox.Value;
                    try
                    {
                        updated.SaveToFile(calibrationPath);
                        calibration = updated;
                        SetStatus("SCARA 标定参数已更新并保存。", Success);
                        RefreshUi();
                    }
                    catch (Exception ex)
                    {
                        SetStatus("标定参数无效或保存失败：" + ex.Message, Danger);
                    }
                }
            }
        }

        private async Task EnableAllAxesAsync()
        {
            if (busy || jogActive || sessionLost || !sc4iClient.IsConnected) return;
            busy = true;
            SetStatus("正在使能并授权全部四轴…", Accent);
            RefreshUi();
            CancellationTokenSource cts = new CancellationTokenSource(5000);
            CancelAndDisposeCts(ref activeOpCts);
            activeOpCts = cts;
            try
            {
                liveStatus = await sc4iClient.EnableAxesMaskAsync(0x0F, cts.Token);
                if (!IsDisposed && !Disposing)
                {
                    SetStatus("已请求使能全部四轴。", Success);
                    FillTargetFromCurrentIfConnected();
                }
            }
            catch (OperationCanceledException)
            {
                if (!formClosing && !IsDisposed && !Disposing && sc4iClient.IsConnected && ReferenceEquals(activeOpCts, cts))
                    SetStatus("四轴使能超时。", Danger);
            }
            catch (Sc4iCommandException error)
            {
                liveStatus = error.Status;
                HandleSessionErrorIfNeeded(error);
                if (!IsDisposed && !Disposing && !sessionLost) SetStatus(error.Message, Danger);
            }
            catch (Exception error)
            {
                if (!IsDisposed && !Disposing) SetStatus("四轴使能失败：" + error.Message, Danger);
            }
            finally
            {
                if (ReferenceEquals(activeOpCts, cts)) activeOpCts = null;
                cts.Dispose();
                busy = false;
                if (!IsDisposed && !Disposing) RefreshUi();
            }
        }

        private async Task MoveCartesianStepButtonAsync(int buttonIndex)
        {
            int[] counts;
            if (!TryGetLiveCounts(liveStatus, out counts)) return;
            ScaraJointPose currentJoints;
            ScaraCartesianPose cur = ScaraKinematics.ForwardFromCounts(calibration, counts, out currentJoints);

            double stepMm = (double)cartStepMmInput.Value;
            double stepDeg = (double)cartStepDegInput.Value;
            double tx = cur.XMm;
            double ty = cur.YMm;
            double tz = cur.ZMm;
            double tr = cur.RDeg;
            bool keepR = keepWorldRCheck.Checked;
            string label;

            switch (buttonIndex)
            {
                case 0: tx -= stepMm; label = String.Format(CultureInfo.InvariantCulture, "X {0:+0.#;-0.#} mm", -stepMm); break;
                case 1: tx += stepMm; label = String.Format(CultureInfo.InvariantCulture, "X {0:+0.#;-0.#} mm", stepMm); break;
                case 2: ty -= stepMm; label = String.Format(CultureInfo.InvariantCulture, "Y {0:+0.#;-0.#} mm", -stepMm); break;
                case 3: ty += stepMm; label = String.Format(CultureInfo.InvariantCulture, "Y {0:+0.#;-0.#} mm", stepMm); break;
                case 4: tz -= stepMm; label = String.Format(CultureInfo.InvariantCulture, "Z {0:+0.#;-0.#} mm", -stepMm); break;
                case 5: tz += stepMm; label = String.Format(CultureInfo.InvariantCulture, "Z {0:+0.#;-0.#} mm", stepMm); break;
                case 6: tr -= stepDeg; keepR = true; label = String.Format(CultureInfo.InvariantCulture, "R {0:+0.#;-0.#}°", -stepDeg); break;
                case 7: tr += stepDeg; keepR = true; label = String.Format(CultureInfo.InvariantCulture, "R {0:+0.#;-0.#}°", stepDeg); break;
                default: return;
            }

            await ExecuteCartesianMoveAsync(tx, ty, tz, tr, keepR, label);
        }

        private async Task MoveCartesianAbsoluteAsync()
        {
            double tx = (double)targetXInput.Value;
            double ty = (double)targetYInput.Value;
            double tz = (double)targetZInput.Value;
            double tr = (double)targetRInput.Value;
            string label = String.Format(CultureInfo.InvariantCulture,
                "目标 ({0:0.1}, {1:0.1}, {2:0.1} mm, R={3:0.1}°)", tx, ty, tz, tr);
            await ExecuteCartesianMoveAsync(tx, ty, tz, tr, keepWorldRCheck.Checked, label);
        }

        private async Task MoveHomeZeroAsync()
        {
            double tx = calibration.MaxReachMm;
            double ty = 0.0;
            double tz = calibration.ZeroHeightMm;
            double tr = 0.0;
            await ExecuteCartesianMoveAsync(tx, ty, tz, tr, true, "安全回零点");
        }

        private async Task ExecuteCartesianMoveAsync(
            double targetXMm, double targetYMm, double targetZMm, double targetRDeg, bool keepWorldR, string actionLabel)
        {
            if (busy || jogActive || sessionLost || !sc4iClient.IsConnected) return;
            int[] counts;
            if (!TryGetLiveCounts(liveStatus, out counts))
            {
                SetStatus("无法读取有效四轴位置，禁止启动笛卡尔运动。", Danger);
                return;
            }

            ScaraJointPose currentJoints = ScaraKinematics.CountsToJoints(calibration, counts);
            ScaraElbowMode pref = (ScaraElbowMode)Math.Max(0, elbowCombo.SelectedIndex);
            ScaraIkResult ik = ScaraKinematics.Inverse(
                calibration, targetXMm, targetYMm, targetZMm, targetRDeg, keepWorldR, currentJoints, pref);
            if (!ik.Success)
            {
                SetStatus(ik.Error, Danger);
                return;
            }

            bool raisingZ = ik.TargetJoints.ZMm > currentJoints.ZMm + 0.05;
            int[] order = raisingZ
                ? new int[] { calibration.J2Slave, calibration.J1Slave, calibration.J3Slave, calibration.J4Slave }
                : new int[] { calibration.J1Slave, calibration.J3Slave, calibration.J4Slave, calibration.J2Slave };

            List<int> toMove = new List<int>();
            for (int i = 0; i < order.Length; i++)
            {
                int s = order[i];
                if (Math.Abs(ik.DeltaSlaveRevs[s]) >= 0.001)
                {
                    if (!liveStatus.IsAxisEnabled(s) || !liveStatus.IsAxisAuthorized(s))
                    {
                        SetStatus("轴 " + (s + 1) + " 未使能或未授权，请先点击“使能全部四轴”。", Danger);
                        return;
                    }
                    toMove.Add(s);
                }
            }

            if (toMove.Count == 0)
            {
                SetStatus("当前已处于目标位置附近，无需移动。", Muted);
                return;
            }

            double vel = (double)cartVelRevInput.Value;
            busy = true;
            SetStatus("正在执行 " + actionLabel + "…", Accent);
            RefreshUi();
            CancellationTokenSource cts = new CancellationTokenSource(45000);
            CancelAndDisposeCts(ref activeOpCts);
            activeOpCts = cts;
            try
            {
                for (int i = 0; i < toMove.Count; i++)
                {
                    int slave = toMove[i];
                    cts.Token.ThrowIfCancellationRequested();
                    SetStatus(String.Format(CultureInfo.InvariantCulture,
                        "{0}：移动轴 {1} ({2:+0.000;-0.000} 圈)…",
                        actionLabel, slave + 1, ik.DeltaSlaveRevs[slave]), Accent);

                    liveStatus = await sc4iClient.MoveAxisToTargetRevAsync(
                        slave, ik.TargetSlaveRevs[slave], vel, DefaultAccelerationRevPerSec2, cts.Token);
                    if (IsDisposed || Disposing || !sc4iClient.IsConnected) return;
                    RefreshUi();

                    // Poll heartbeat at ~120ms until current axis settles while keeping watchdog alive.
                    DateTime stepDeadline = DateTime.UtcNow.AddSeconds(25);
                    while (true)
                    {
                        await Task.Delay(120, cts.Token);
                        liveStatus = await sc4iClient.SendHeartbeatAsync(cts.Token);
                        if (IsDisposed || Disposing || !sc4iClient.IsConnected) return;
                        RefreshUi();

                        if (liveStatus.LatchedFault != 0 || liveStatus.EstopFlag != 0 || !liveStatus.PdoOk)
                            throw new InvalidOperationException("运动中检测到急停、故障锁存或 PDO 异常，已中止序列。");
                        if (liveStatus.MotionState == 0)
                            break;
                        if (DateTime.UtcNow > stepDeadline)
                            throw new TimeoutException("等待轴 " + (slave + 1) + " 到位超时。");
                    }
                }

                if (!IsDisposed && !Disposing)
                {
                    FillTargetFromCurrentIfConnected();
                    SetStatus(actionLabel + " 已到位。", Success);
                }
            }
            catch (OperationCanceledException)
            {
                if (!formClosing && !IsDisposed && !Disposing && sc4iClient.IsConnected && ReferenceEquals(activeOpCts, cts))
                    SetStatus("笛卡尔运动已中止或超时。", Danger);
            }
            catch (Sc4iCommandException error)
            {
                liveStatus = error.Status;
                HandleSessionErrorIfNeeded(error);
                if (!IsDisposed && !Disposing && !sessionLost) SetStatus(error.Message, Danger);
            }
            catch (Exception error)
            {
                if (!IsDisposed && !Disposing) SetStatus("笛卡尔运动失败：" + error.Message, Danger);
            }
            finally
            {
                if (ReferenceEquals(activeOpCts, cts)) activeOpCts = null;
                cts.Dispose();
                busy = false;
                if (!IsDisposed && !Disposing) RefreshUi();
            }
        }

        private static void CancelAndDisposeCts(ref CancellationTokenSource field)
        {
            CancellationTokenSource old = field;
            field = null;
            if (old != null)
            {
                try { old.Cancel(); } catch { }
                try { old.Dispose(); } catch { }
            }
        }

        private void CancelAllInFlightOps()
        {
            CancelAndDisposeCts(ref pollCts);
            CancelAndDisposeCts(ref jogCts);
            CancelAndDisposeCts(ref activeOpCts);
        }

        private async Task ToggleConnectAsync()
        {
            if (busy) return;
            if (sc4iClient.IsConnected)
            {
                busy = true;
                pollTimer.Stop();
                jogTimer.Stop();
                bool shouldStop = jogActive || (liveStatus != null && liveStatus.MotionState != 0);
                jogActive = false;
                jogGeneration++;
                CancelAllInFlightOps();
                RefreshUi();
                try
                {
                    if (shouldStop)
                    {
                        try
                        {
                            using (CancellationTokenSource cts = new CancellationTokenSource(1000))
                            {
                                await sc4iClient.StopHoldAsync(cts.Token);
                            }
                        }
                        catch { }
                    }
                    sc4iClient.Disconnect();
                    liveStatus = null;
                    heartbeatFailCount = 0;
                    sessionLost = false;
                    if (!IsDisposed && !Disposing) SetStatus("已断开连接。", Muted);
                }
                finally
                {
                    busy = false;
                    if (!IsDisposed && !Disposing) RefreshUi();
                }
                return;
            }

            busy = true;
            sessionLost = false;
            heartbeatFailCount = 0;
            SetStatus("正在连接控制卡…", Accent);
            RefreshUi();
            try
            {
                CancellationTokenSource connectCts = new CancellationTokenSource(6000);
                CancelAndDisposeCts(ref activeOpCts);
                activeOpCts = connectCts;
                try
                {
                    liveStatus = await sc4iClient.ConnectAsync(localIp, localPort, cardIp, cardPort, 2000, connectCts.Token);
                }
                finally
                {
                    if (ReferenceEquals(activeOpCts, connectCts)) activeOpCts = null;
                    connectCts.Dispose();
                }
                if (IsDisposed || Disposing) return;

                if (!liveStatus.BusReady)
                {
                    SetStatus("已连接，正在初始化总线（电机不上使能）…", Accent);
                    CancellationTokenSource initCts = new CancellationTokenSource(30000);
                    CancelAndDisposeCts(ref activeOpCts);
                    activeOpCts = initCts;
                    try
                    {
                        liveStatus = await sc4iClient.InitBusToOpAsync(initCts.Token);
                    }
                    finally
                    {
                        if (ReferenceEquals(activeOpCts, initCts)) activeOpCts = null;
                        initCts.Dispose();
                    }
                    if (IsDisposed || Disposing) return;
                }

                heartbeatFailCount = 0;
                sessionLost = false;
                pollTimer.Start();
                string readyMsg = liveStatus.EnabledMask == 0
                    ? "已连接，总线就绪（电机未使能）。"
                    : "已连接，总线就绪（已有轴处于使能/持力状态）。";
                SetStatus(readyMsg, Success);
            }
            catch (Exception error)
            {
                sc4iClient.Disconnect();
                liveStatus = null;
                sessionLost = false;
                if (!formClosing && !IsDisposed && !Disposing)
                {
                    SetStatus("连接失败：" + error.Message, Danger);
                    MessageBox.Show(this, error.Message, "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally
            {
                busy = false;
                if (!formClosing && !IsDisposed && !Disposing) RefreshUi();
            }
        }

        private async Task EnableSelectedAxisAsync()
        {
            if (busy || jogActive || sessionLost || !sc4iClient.IsConnected) return;
            int axis = selectedAxis;
            busy = true;
            SetStatus("正在使能轴 " + (axis + 1) + "…", Accent);
            RefreshUi();
            CancellationTokenSource cts = new CancellationTokenSource(4000);
            CancelAndDisposeCts(ref activeOpCts);
            activeOpCts = cts;
            try
            {
                liveStatus = await sc4iClient.EnableAxisAsync(axis, cts.Token);
                if (!IsDisposed && !Disposing)
                {
                    string msg = liveStatus.IsAxisEnabled(axis)
                        ? ("轴 " + (axis + 1) + " 已使能。")
                        : ("已发送轴 " + (axis + 1) + " 使能请求…");
                    SetStatus(msg, Success);
                }
            }
            catch (OperationCanceledException)
            {
                if (!formClosing && !IsDisposed && !Disposing && sc4iClient.IsConnected && ReferenceEquals(activeOpCts, cts))
                    SetStatus("使能请求超时。", Danger);
            }
            catch (Sc4iCommandException error)
            {
                liveStatus = error.Status;
                HandleSessionErrorIfNeeded(error);
                if (!IsDisposed && !Disposing && !sessionLost) SetStatus(error.Message, Danger);
            }
            catch (Exception error)
            {
                if (!IsDisposed && !Disposing) SetStatus("使能失败：" + error.Message, Danger);
            }
            finally
            {
                if (ReferenceEquals(activeOpCts, cts)) activeOpCts = null;
                cts.Dispose();
                busy = false;
                if (!IsDisposed && !Disposing) RefreshUi();
            }
        }

        private async Task MoveSelectedAxisAsync(int direction)
        {
            if (busy || jogActive || sessionLost || !sc4iClient.IsConnected || (direction != 1 && direction != -1)) return;
            int axis = selectedAxis;
            double delta = direction * (double)stepInput.Value;
            double vel = (double)velInput.Value;
            busy = true;
            SetStatus(String.Format(CultureInfo.InvariantCulture,
                "轴 {0} 相对移动 {1:+0.00;-0.00} 圈…", axis + 1, delta), Accent);
            RefreshUi();
            CancellationTokenSource cts = new CancellationTokenSource(4000);
            CancelAndDisposeCts(ref activeOpCts);
            activeOpCts = cts;
            try
            {
                liveStatus = await sc4iClient.MoveAxisRelativeAsync(axis, delta, vel, DefaultAccelerationRevPerSec2, cts.Token);
                if (!IsDisposed && !Disposing)
                {
                    SetStatus(String.Format(CultureInfo.InvariantCulture,
                        "轴 {0} 已发送移动 {1:+0.00;-0.00} 圈。", axis + 1, delta), Ink);
                }
            }
            catch (OperationCanceledException)
            {
                if (!formClosing && !IsDisposed && !Disposing && sc4iClient.IsConnected && ReferenceEquals(activeOpCts, cts))
                    SetStatus("移动请求超时。", Danger);
            }
            catch (Sc4iCommandException error)
            {
                liveStatus = error.Status;
                HandleSessionErrorIfNeeded(error);
                if (!IsDisposed && !Disposing && !sessionLost) SetStatus(error.Message, Danger);
            }
            catch (Exception error)
            {
                if (!IsDisposed && !Disposing) SetStatus("移动失败：" + error.Message, Danger);
            }
            finally
            {
                if (ReferenceEquals(activeOpCts, cts)) activeOpCts = null;
                cts.Dispose();
                busy = false;
                if (!IsDisposed && !Disposing) RefreshUi();
            }
        }

        private async Task StartJogAsync(int direction)
        {
            if (busy || jogActive || sessionLost || !sc4iClient.IsConnected || (direction != 1 && direction != -1)) return;
            int axis = selectedAxis;
            double vel = direction * Math.Min((double)velInput.Value, Sc4iClient.MaxSafeJogVelocityRevPerSec);
            jogActive = true;
            int myGen = ++jogGeneration;
            SetStatus(String.Format(CultureInfo.InvariantCulture,
                "轴 {0} 点动中（松开即停）…", axis + 1), Accent);
            CancellationTokenSource cts = new CancellationTokenSource(2000);
            CancelAndDisposeCts(ref jogCts);
            jogCts = cts;
            try
            {
                Sc4iStatusReply reply = await sc4iClient.StartJogAsync(axis, vel, cts.Token);
                if (IsDisposed || Disposing || myGen != jogGeneration || !jogActive) return;
                liveStatus = reply;
                jogTimer.Start();
                RefreshUi();
            }
            catch (OperationCanceledException)
            {
                if (myGen == jogGeneration && jogActive)
                {
                    jogActive = false;
                    if (!IsDisposed && !Disposing)
                    {
                        SetStatus("点动启动超时。", Danger);
                        RefreshUi();
                    }
                }
            }
            catch (Sc4iCommandException error)
            {
                if (myGen == jogGeneration)
                {
                    jogActive = false;
                    liveStatus = error.Status;
                    HandleSessionErrorIfNeeded(error);
                    if (!IsDisposed && !Disposing)
                    {
                        if (!sessionLost) SetStatus(error.Message, Danger);
                        RefreshUi();
                    }
                }
            }
            catch (Exception error)
            {
                if (myGen == jogGeneration)
                {
                    jogActive = false;
                    if (!IsDisposed && !Disposing)
                    {
                        SetStatus("点动失败：" + error.Message, Danger);
                        RefreshUi();
                    }
                }
            }
            finally
            {
                if (ReferenceEquals(jogCts, cts)) jogCts = null;
                cts.Dispose();
            }
        }

        private async Task SendJogKeepaliveAsync()
        {
            if (!jogActive || jogKeepaliveInFlight || sessionLost || !sc4iClient.IsConnected) return;
            jogKeepaliveInFlight = true;
            int myGen = jogGeneration;
            CancellationTokenSource cts = new CancellationTokenSource(280);
            CancelAndDisposeCts(ref jogCts);
            jogCts = cts;
            try
            {
                Sc4iStatusReply reply = await sc4iClient.SendJogKeepaliveAsync(cts.Token);
                if (IsDisposed || Disposing || myGen != jogGeneration || !sc4iClient.IsConnected) return;
                liveStatus = reply;
                heartbeatFailCount = 0;
                if (reply.ErrorCode != 0 || reply.LatchedFault != 0 || reply.EstopFlag != 0)
                {
                    jogTimer.Stop();
                    jogActive = false;
                    jogGeneration++;
                    if (reply.ErrorCode == 0x36 || reply.ErrorCode == 0x37)
                    {
                        MarkSessionLost();
                    }
                    else if (reply.ErrorCode != 0)
                    {
                        SetStatus("点动已停止：" + Sc4iProtocol.FormatErrorCode(reply.ErrorCode), Danger);
                    }
                }
                RefreshUi();
            }
            catch (OperationCanceledException)
            {
                if (myGen == jogGeneration && jogActive && sc4iClient.IsConnected)
                {
                    jogTimer.Stop();
                    jogActive = false;
                    jogGeneration++;
                    if (!IsDisposed && !Disposing)
                    {
                        SetStatus("点动保活超时，已请求停止保持。", Danger);
                        RefreshUi();
                    }
                    await TriggerStopHoldAsync("点动保活超时，已停止保持");
                }
            }
            catch (Exception error)
            {
                if (myGen == jogGeneration && jogActive)
                {
                    jogTimer.Stop();
                    jogActive = false;
                    jogGeneration++;
                    if (!IsDisposed && !Disposing)
                    {
                        SetStatus("点动保活异常：" + error.Message, Danger);
                        RefreshUi();
                    }
                    if (sc4iClient.IsConnected)
                        await TriggerStopHoldAsync("点动通信异常，已停止保持");
                }
            }
            finally
            {
                if (ReferenceEquals(jogCts, cts)) jogCts = null;
                cts.Dispose();
                jogKeepaliveInFlight = false;
            }
        }

        private async Task StopJogAsync(string message)
        {
            if (!jogActive) return;
            jogTimer.Stop();
            jogActive = false;
            jogGeneration++;
            CancelAndDisposeCts(ref jogCts);
            await TriggerStopHoldAsync(message);
        }

        private async Task TriggerStopHoldAsync(string message)
        {
            if (!sc4iClient.IsConnected) return;
            jogTimer.Stop();
            jogActive = false;
            jogGeneration++;
            CancelAllInFlightOps();
            try
            {
                Sc4iStatusReply reply;
                using (CancellationTokenSource cts = new CancellationTokenSource(2500))
                {
                    reply = await sc4iClient.StopHoldAsync(cts.Token);
                }
                if (IsDisposed || Disposing || !sc4iClient.IsConnected) return;
                liveStatus = reply;
                if (reply.ErrorCode != 0)
                {
                    if (reply.ErrorCode == 0x36 || reply.ErrorCode == 0x37)
                    {
                        MarkSessionLost();
                    }
                    else
                    {
                        SetStatus("停止保持返回错误：" + Sc4iProtocol.FormatErrorCode(reply.ErrorCode), Danger);
                    }
                }
                else
                {
                    SetStatus(message, Accent);
                }
            }
            catch (Exception error)
            {
                if (!IsDisposed && !Disposing) SetStatus("停止失败：" + error.Message, Danger);
            }
            finally
            {
                if (!IsDisposed && !Disposing) RefreshUi();
            }
        }

        private async Task ClearFaultsAsync()
        {
            if (busy || jogActive || sessionLost || !sc4iClient.IsConnected) return;
            busy = true;
            SetStatus("正在清除故障与停机锁存…", Accent);
            RefreshUi();
            CancellationTokenSource cts = new CancellationTokenSource(5000);
            CancelAndDisposeCts(ref activeOpCts);
            activeOpCts = cts;
            try
            {
                liveStatus = await sc4iClient.ClearFaultsAndLatchAsync(cts.Token);
                if (!IsDisposed && !Disposing) SetStatus("故障已清除，可重新使能轴。", Success);
            }
            catch (OperationCanceledException)
            {
                if (!formClosing && !IsDisposed && !Disposing && sc4iClient.IsConnected && ReferenceEquals(activeOpCts, cts))
                    SetStatus("清故障超时。", Danger);
            }
            catch (Sc4iCommandException error)
            {
                liveStatus = error.Status;
                HandleSessionErrorIfNeeded(error);
                if (!IsDisposed && !Disposing && !sessionLost) SetStatus(error.Message, Danger);
            }
            catch (Exception error)
            {
                if (!IsDisposed && !Disposing) SetStatus("清故障失败：" + error.Message, Danger);
            }
            finally
            {
                if (ReferenceEquals(activeOpCts, cts)) activeOpCts = null;
                cts.Dispose();
                busy = false;
                if (!IsDisposed && !Disposing) RefreshUi();
            }
        }

        private async Task PollStatusAsync()
        {
            if (!sc4iClient.IsConnected || pollInFlight || busy || jogActive || sessionLost) return;
            pollInFlight = true;
            CancellationTokenSource cts = new CancellationTokenSource(650);
            CancelAndDisposeCts(ref pollCts);
            pollCts = cts;
            try
            {
                Sc4iStatusReply reply = await sc4iClient.SendHeartbeatAsync(cts.Token);
                if (IsDisposed || Disposing || !sc4iClient.IsConnected) return;
                liveStatus = reply;
                bool recovered = heartbeatFailCount > 0;
                heartbeatFailCount = 0;
                if (recovered) SetStatus("通信已恢复。", Success);
                RefreshUi();
            }
            catch (OperationCanceledException)
            {
                if (IsDisposed || Disposing || !sc4iClient.IsConnected || busy || jogActive) return;
                if (!ReferenceEquals(pollCts, cts)) return;
                heartbeatFailCount++;
                if (heartbeatFailCount == 1) SetStatus("通信超时，正在等待恢复…", Danger);
                RefreshUi();
            }
            catch (Sc4iCommandException error)
            {
                if (IsDisposed || Disposing || !sc4iClient.IsConnected) return;
                liveStatus = error.Status;
                if (error.ErrorCode == 0x36 || error.ErrorCode == 0x37)
                {
                    MarkSessionLost();
                }
                else
                {
                    heartbeatFailCount++;
                    if (heartbeatFailCount == 1) SetStatus(error.Message, Danger);
                }
                RefreshUi();
            }
            catch
            {
                if (IsDisposed || Disposing || !sc4iClient.IsConnected) return;
                heartbeatFailCount++;
                if (heartbeatFailCount == 1) SetStatus("通信超时，正在等待恢复…", Danger);
                RefreshUi();
            }
            finally
            {
                if (ReferenceEquals(pollCts, cts)) pollCts = null;
                cts.Dispose();
                pollInFlight = false;
            }
        }

        private void HandleSessionErrorIfNeeded(Sc4iCommandException error)
        {
            if (error != null && (error.ErrorCode == 0x36 || error.ErrorCode == 0x37))
            {
                MarkSessionLost();
            }
        }

        private void MarkSessionLost()
        {
            sessionLost = true;
            pollTimer.Stop();
            jogTimer.Stop();
            jogActive = false;
            jogGeneration++;
            if (!IsDisposed && !Disposing)
                SetStatus("会话已失效或控制卡已重启，请点击“断开”后重新连接。", Danger);
        }

        private void RefreshUi()
        {
            bool connected = sc4iClient.IsConnected;
            Sc4iStatusReply st = liveStatus;
            bool commOk = connected && !sessionLost && heartbeatFailCount == 0;
            bool busReady = connected && st != null && st.BusReady;
            bool pdoStale = busReady && (!st.PdoOk || st.PdoAgeMs == uint.MaxValue || st.PdoAgeMs >= 500u);
            bool pdoFresh = busReady && !pdoStale;
            bool hasFault = sessionLost || heartbeatFailCount > 0 || (st != null && (st.LatchedFault != 0 || st.EstopFlag != 0 || pdoStale));

            if (!connected)
            {
                connectionBadge.Text = busy ? "● 连接中…" : "● 未连接";
                connectionBadge.ForeColor = busy ? Accent : Muted;
            }
            else if (hasFault)
            {
                connectionBadge.Text = sessionLost ? "● 会话失效"
                    : pdoStale ? "● 总线中断"
                    : heartbeatFailCount > 0 ? "● 通信超时"
                    : "● 停机 / 故障";
                connectionBadge.ForeColor = Danger;
            }
            else if (st != null && st.MotionState != 0)
            {
                connectionBadge.Text = "● 运行中";
                connectionBadge.ForeColor = Accent;
            }
            else
            {
                connectionBadge.Text = busReady ? "● 已就绪" : "● 已连接";
                connectionBadge.ForeColor = busReady ? Success : Accent;
            }

            connectButton.Text = connected ? "断开" : "连接";
            connectButton.Enabled = !busy;
            connectButton.BackColor = !busy ? (connected ? Muted : Accent) : DisabledBg;
            connectButton.ForeColor = !busy ? Color.White : Muted;

            clearFaultButton.Enabled = commOk && !busy && !jogActive && busReady;
            clearFaultButton.ForeColor = clearFaultButton.Enabled ? (hasFault ? Danger : Ink) : Muted;

            estopButton.Enabled = connected;
            estopButton.BackColor = connected ? Danger : DisabledBg;
            estopButton.ForeColor = connected ? Color.White : Muted;

            settingsButton.Enabled = !connected && !busy;

            int[] liveCounts;
            bool hasLiveCounts = TryGetLiveCounts(st, out liveCounts);
            ScaraJointPose liveJoints = null;
            ScaraCartesianPose liveCart = null;
            if (hasLiveCounts)
            {
                liveCart = ScaraKinematics.ForwardFromCounts(calibration, liveCounts, out liveJoints);
                cartesianCoordsLabel.Text = String.Format(CultureInfo.InvariantCulture,
                    "X: {0:0.1} mm    Y: {1:0.1} mm    Z: {2:0.1} mm    R: {3:0.1}°",
                    liveCart.XMm, liveCart.YMm, liveCart.ZMm, liveCart.RDeg);
                cartesianBranchLabel.Text = String.Format(CultureInfo.InvariantCulture,
                    "半径 {0:0.1} mm · {1}", liveCart.RadiusMm, liveCart.DescribeBranch());
            }
            else
            {
                cartesianCoordsLabel.Text = "X: --    Y: --    Z: --    R: --";
                cartesianBranchLabel.Text = String.Format(CultureInfo.InvariantCulture,
                    "L1={0:0.#}  L2={1:0.#}  H0={2:0.#} mm",
                    calibration.ArmLength1Mm, calibration.ArmLength2Mm, calibration.ZeroHeightMm);
            }

            jointModeButton.BackColor = !cartesianMode ? Accent : CardBg;
            jointModeButton.ForeColor = !cartesianMode ? Color.White : Ink;
            jointModeButton.FlatAppearance.BorderSize = !cartesianMode ? 0 : 1;
            cartModeButton.BackColor = cartesianMode ? Accent : CardBg;
            cartModeButton.ForeColor = cartesianMode ? Color.White : Ink;
            cartModeButton.FlatAppearance.BorderSize = cartesianMode ? 0 : 1;
            jointModeButton.Enabled = !jogActive;
            cartModeButton.Enabled = !jogActive;
            setZeroButton.Enabled = hasLiveCounts && commOk && !busy && !jogActive && st.MotionState == 0;
            setZeroButton.ForeColor = setZeroButton.Enabled ? Ink : Muted;
            calDialogButton.Enabled = !busy && !jogActive;

            for (int i = 0; i < 4; i++)
            {
                bool isSelected = !cartesianMode && selectedAxis == i;
                Sc4iSlaveTelemetry sl = (st != null && st.Slaves != null && i < st.Slaves.Length) ? st.Slaves[i] : null;
                bool en = st != null && st.IsAxisEnabled(i);
                bool auth = st != null && st.IsAxisAuthorized(i);
                bool ening = st != null && st.IsAxisEnabling(i);
                bool fault = sl != null && sl.IsFault;

                string posText;
                if (hasLiveCounts && liveJoints != null)
                {
                    if (i == calibration.J1Slave) posText = liveJoints.Theta1Deg.ToString("0.1", CultureInfo.InvariantCulture) + "°";
                    else if (i == calibration.J2Slave) posText = liveJoints.ZMm.ToString("0.1", CultureInfo.InvariantCulture) + " mm";
                    else if (i == calibration.J3Slave) posText = liveJoints.Theta2Deg.ToString("0.1", CultureInfo.InvariantCulture) + "°";
                    else posText = liveJoints.Theta4Deg.ToString("0.1", CultureInfo.InvariantCulture) + "°";
                }
                else if (sl != null && (sl.ValidMask != 0 || sl.VendorId != 0))
                {
                    posText = sl.ActualRevolutions(sc4iClient.CountsPerRevolution).ToString("0.000", CultureInfo.InvariantCulture) + " 圈";
                }
                else
                {
                    posText = "--";
                }

                string revSuffix = (sl != null && (sl.ValidMask != 0 || sl.VendorId != 0))
                    ? (" · " + sl.ActualRevolutions(sc4iClient.CountsPerRevolution).ToString("0.00", CultureInfo.InvariantCulture) + "圈")
                    : "";

                string stateText = !connected ? "未连接"
                    : fault ? (sl.DriveErrorCode == 0x0E08 ? "报警 Er.E08(同步丢失)"
                        : (sl.DriveErrorCode != 0 ? "报警 0x" + sl.DriveErrorCode.ToString("X4", CultureInfo.InvariantCulture) : "驱动故障"))
                    : (en && auth) ? ("已使能" + revSuffix)
                    : en ? ("持力中" + revSuffix)
                    : ening ? "使能中…"
                    : ("未使能" + revSuffix);

                Color bg = fault ? DangerBg : (en ? SuccessBg : (isSelected ? SelectedBg : CardBg));
                Color border = isSelected ? Accent : (fault ? Danger : BorderColor);
                Color stateColor = fault ? Danger : (en ? Success : Muted);

                axisCards[i].UpdateState(posText, stateText, bg, border, isSelected ? 2 : 1, stateColor);
            }

            bool anySlaveFault = false;
            bool allAxesReady = hasLiveCounts;
            for (int i = 0; i < 4; i++)
            {
                Sc4iSlaveTelemetry s = (st != null && st.Slaves != null && i < st.Slaves.Length) ? st.Slaves[i] : null;
                if (s != null && s.IsFault) anySlaveFault = true;
                if (st == null || !st.IsAxisEnabled(i) || !st.IsAxisAuthorized(i)) allAxesReady = false;
            }
            bool canCartEnableAll = commOk && pdoFresh && !busy && !jogActive && !anySlaveFault
                && st.LatchedFault == 0 && st.EstopFlag == 0 && st.MotionState == 0;
            bool canCartMove = canCartEnableAll && hasLiveCounts;

            enableAllAxesButton.Enabled = canCartEnableAll;
            enableAllAxesButton.Text = allAxesReady ? "四轴已全部使能" : "使能全部四轴";
            enableAllAxesButton.BackColor = canCartEnableAll ? Success : DisabledBg;
            enableAllAxesButton.ForeColor = canCartEnableAll ? Color.White : Muted;
            goHomeButton.Enabled = canCartMove && allAxesReady;
            goHomeButton.ForeColor = goHomeButton.Enabled ? Ink : Muted;
            fillCurrentTargetButton.Enabled = hasLiveCounts && !busy;
            fillCurrentTargetButton.ForeColor = fillCurrentTargetButton.Enabled ? Ink : Muted;
            moveTargetButton.Enabled = canCartMove;
            moveTargetButton.BackColor = canCartMove ? Success : DisabledBg;
            moveTargetButton.ForeColor = canCartMove ? Color.White : Muted;
            for (int i = 0; i < cartJogButtons.Length; i++)
            {
                cartJogButtons[i].Enabled = canCartMove;
                cartJogButtons[i].BackColor = canCartMove ? Accent : DisabledBg;
                cartJogButtons[i].ForeColor = canCartMove ? Color.White : Muted;
            }

            Sc4iSlaveTelemetry selSlave = (st != null && st.Slaves != null && selectedAxis < st.Slaves.Length) ? st.Slaves[selectedAxis] : null;
            bool selEnabled = st != null && st.IsAxisEnabled(selectedAxis);
            bool selAuth = st != null && st.IsAxisAuthorized(selectedAxis);
            bool selEnabling = st != null && st.IsAxisEnabling(selectedAxis);
            bool selFault = selSlave != null && selSlave.IsFault;
            bool canEnable = commOk && pdoFresh && !busy && !jogActive && !selEnabling && !selFault
                && st.LatchedFault == 0 && st.EstopFlag == 0 && st.MotionState == 0;
            bool canMove = canEnable && selEnabled && selAuth;

            string selState = !connected ? "未连接"
                : sessionLost ? "会话已失效，请重新连接"
                : heartbeatFailCount > 0 ? "通信超时，等待恢复"
                : pdoStale ? "总线异常"
                : (st != null && (st.LatchedFault != 0 || st.EstopFlag != 0)) ? "停机锁存，请先清故障"
                : selFault ? "驱动报警，请先清故障"
                : selEnabling ? "使能中…"
                : (selEnabled && selAuth) ? "已使能，可运动"
                : selEnabled ? "持力中，需重新授权"
                : "未使能";

            selectedAxisLabel.Text = "轴 " + (selectedAxis + 1) + "  ·  " + selState;

            enableButton.Text = (selEnabled && !selAuth)
                ? "重新授权轴 " + (selectedAxis + 1)
                : "使能轴 " + (selectedAxis + 1);
            enableButton.Enabled = canEnable;
            enableButton.BackColor = canEnable ? Success : DisabledBg;
            enableButton.ForeColor = canEnable ? Color.White : Muted;

            moveNegButton.Enabled = canMove;
            movePosButton.Enabled = canMove;
            moveNegButton.BackColor = canMove ? Accent : DisabledBg;
            movePosButton.BackColor = canMove ? Accent : DisabledBg;
            moveNegButton.ForeColor = canMove ? Color.White : Muted;
            movePosButton.ForeColor = canMove ? Color.White : Muted;

            bool canJog = canMove || jogActive;
            jogNegButton.Enabled = canJog;
            jogPosButton.Enabled = canJog;
            jogNegButton.ForeColor = canJog ? Ink : Muted;
            jogPosButton.ForeColor = canJog ? Ink : Muted;

            stepInput.Enabled = !jogActive;
            velInput.Enabled = !jogActive;

            if (sessionLost)
            {
                SetStatus("会话已失效或控制卡已重启，请点击“断开”后重新连接。", Danger);
            }
            else if (pdoStale)
            {
                SetStatus("总线通信中断，已锁定运动，请检查网线或驱动器。", Danger);
            }
            else if (st != null && st.LatchedFault != 0)
            {
                SetStatus("故障锁存：" + Sc4iProtocol.FormatErrorCode(st.LatchedFault) + "，确认安全后点击“清故障”。", Danger);
            }
            else if (st != null && st.EstopFlag != 0)
            {
                SetStatus("急停已锁存，确认安全后点击“清故障”。", Danger);
            }
        }

        private void SetStatus(string text, Color color)
        {
            statusLabel.Text = text;
            statusLabel.ForeColor = color;
        }

        private Label CreateCaption(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                Font = fontSmall,
                ForeColor = Muted,
                TextAlign = ContentAlignment.BottomLeft,
                Margin = new Padding(0)
            };
        }

        private Label CreateDialogLabel(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                Font = fontRegular,
                ForeColor = Ink,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            };
        }

        private static TableLayoutPanel CreateGrid(int columns, int rows)
        {
            return new TableLayoutPanel
            {
                ColumnCount = columns,
                RowCount = rows,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.Transparent
            };
        }

        private static void StyleButton(Button button, string text, Color backColor, Color foreColor, int borderSize)
        {
            button.Text = text;
            button.Dock = DockStyle.Fill;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = BorderColor;
            button.FlatAppearance.BorderSize = borderSize;
            button.BackColor = backColor;
            button.ForeColor = foreColor;
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
        }

        private void ConfigureDecimalInput(NumericUpDown input, decimal min, decimal max, decimal value, decimal increment, int decimals)
        {
            input.DecimalPlaces = decimals;
            input.Increment = increment;
            input.Minimum = min;
            input.Maximum = max;
            input.Value = value;
            input.Dock = DockStyle.Fill;
            input.Font = fontInput;
        }

        internal bool RunSmokeTest(string screenshotPath, out string report)
        {
            pollTimer.Stop();
            jogTimer.Stop();
            int checks = 0;
            Action<bool, string> check = delegate(bool condition, string description)
            {
                if (!condition) throw new InvalidOperationException("界面离线检查失败：" + description);
                checks++;
            };

            check(!sc4iClient.IsConnected, "启动不自动连接实机");
            check(Text == WindowTitle, "窗体标题简洁一致");
            check(connectButton.Enabled && settingsButton.Enabled, "离线时仅允许连接与设置");
            check(!enableButton.Enabled && !moveNegButton.Enabled && !movePosButton.Enabled && !jogNegButton.Enabled && !jogPosButton.Enabled && !estopButton.Enabled && !clearFaultButton.Enabled,
                "未连接时锁定所有运动与使能按钮");
            check(axisCards.Length == 4, "显示四个轴卡片");
            check(localIp == "192.168.8.12" && localPort == 6000 && cardIp == "192.168.8.20" && cardPort == 5000, "默认通信端点正确");
            check(stepInput.Value == 0.20m && velInput.Value == 0.25m, "默认保守低速小步长");

            SelectAxis(1);
            check(selectedAxis == 1 && selectedAxisLabel.Text.StartsWith("轴 2", StringComparison.Ordinal) && enableButton.Text == "使能轴 2",
                "点击切换轴联动标题与使能按钮");
            SelectAxis(0);

            SetControlMode(true);
            check(cartesianMode && cartControlContainer.Visible && !jointControlContainer.Visible,
                "可切换到笛卡尔坐标模式");
            check(!enableAllAxesButton.Enabled && !moveTargetButton.Enabled && !goHomeButton.Enabled && !setZeroButton.Enabled,
                "离线时锁定所有笛卡尔运动与设零按钮");
            for (int i = 0; i < cartJogButtons.Length; i++)
                check(!cartJogButtons[i].Enabled, "离线时锁定笛卡尔点动按钮 " + i);
            SetControlMode(false);

            PerformLayout();
            Update();
            Rectangle controlBounds = RectangleToClient(controlPanel.RectangleToScreen(controlPanel.ClientRectangle));
            Rectangle statusBounds = RectangleToClient(statusLabel.RectangleToScreen(statusLabel.ClientRectangle));
            check(controlBounds.Bottom <= ClientSize.Height && statusBounds.Bottom <= ClientSize.Height,
                "主控区与状态行完整位于窗口视口内");
            check(!sc4iClient.IsConnected, "全过程零硬件通信");

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
                SetControlMode(true);
                PerformLayout();
                Update();
                using (Bitmap cartBmp = new Bitmap(Width, Height))
                {
                    DrawToBitmap(cartBmp, new Rectangle(0, 0, Width, Height));
                    cartBmp.Save(Path.Combine(directory ?? "", "smoke-cart-ui.png"), ImageFormat.Png);
                }
                SetControlMode(false);
            }

            report = "PASS: " + checks + " offline UI checks; 0 echo probes; no hardware operations.";
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CancelAllInFlightOps();
                if (pollTimer != null) pollTimer.Dispose();
                if (jogTimer != null) jogTimer.Dispose();
                sc4iClient.Dispose();
                fontTitle.Dispose();
                fontSection.Dispose();
                fontAxisValue.Dispose();
                fontBold.Dispose();
                fontRegular.Dispose();
                fontSmall.Dispose();
                fontInput.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class AxisCard : Panel
        {
            private readonly Label nameLabel = new Label();
            private readonly Label posLabel = new Label();
            private readonly Label stateLabel = new Label();
            private Color borderColor = BorderColor;
            private int borderWidth = 1;

            internal event EventHandler CardClicked;

            internal AxisCard(int axisNumber, Font nameFont, Font valueFont, Font stateFont)
            {
                Dock = DockStyle.Fill;
                BackColor = CardBg;
                Padding = new Padding(12, 10, 10, 10);
                Cursor = Cursors.Hand;
                DoubleBuffered = true;
                ResizeRedraw = true;

                TableLayoutPanel grid = CreateGrid(1, 3);
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
                Controls.Add(grid);

                nameLabel.Text = "轴 " + axisNumber;
                nameLabel.Dock = DockStyle.Fill;
                nameLabel.Font = nameFont;
                nameLabel.ForeColor = Muted;
                nameLabel.TextAlign = ContentAlignment.MiddleLeft;

                posLabel.Text = "--";
                posLabel.Dock = DockStyle.Fill;
                posLabel.Font = valueFont;
                posLabel.ForeColor = Ink;
                posLabel.TextAlign = ContentAlignment.MiddleLeft;
                posLabel.AutoEllipsis = true;

                stateLabel.Text = "未连接";
                stateLabel.Dock = DockStyle.Fill;
                stateLabel.Font = stateFont;
                stateLabel.ForeColor = Muted;
                stateLabel.TextAlign = ContentAlignment.MiddleLeft;
                stateLabel.AutoEllipsis = true;

                grid.Controls.Add(nameLabel, 0, 0);
                grid.Controls.Add(posLabel, 0, 1);
                grid.Controls.Add(stateLabel, 0, 2);

                Click += OnAnyClick;
                grid.Click += OnAnyClick;
                nameLabel.Click += OnAnyClick;
                posLabel.Click += OnAnyClick;
                stateLabel.Click += OnAnyClick;
            }

            internal void UpdateState(string position, string state, Color bg, Color border, int thickness, Color stateColor)
            {
                if (posLabel.Text != position) posLabel.Text = position;
                if (stateLabel.Text != state) stateLabel.Text = state;
                if (stateLabel.ForeColor != stateColor) stateLabel.ForeColor = stateColor;
                if (BackColor != bg) BackColor = bg;
                if (borderColor != border || borderWidth != thickness)
                {
                    borderColor = border;
                    borderWidth = thickness;
                    Invalidate();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using (Pen pen = new Pen(borderColor, borderWidth))
                {
                    int inset = borderWidth / 2;
                    e.Graphics.DrawRectangle(pen, inset, inset, Width - borderWidth, Height - borderWidth);
                }
            }

            private void OnAnyClick(object sender, EventArgs e)
            {
                EventHandler handler = CardClicked;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }
    }
}
