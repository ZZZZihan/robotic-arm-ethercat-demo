using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using ScaraArm.Console.Controls;
using ScaraArm.Console.Dialogs;
using ScaraArm.Console.Theme;
using ScaraArm.Core.Kinematics;
using ScaraArm.Core.Motion;
using ScaraArm.Core.Protocol;
using ScaraArm.Core.Transport;

namespace ScaraArm.Console
{
    /// <summary>
    /// Top-level Windows console window: layout composition, dialogs, and telemetry view binding.
    /// Async hardware operations live in MainForm.Operations.cs.
    /// </summary>
    internal sealed partial class MainForm : Form
    {
        internal const string WindowTitle = "四轴机械臂控制";
        private const double DefaultAccelerationRevPerSec2 = 1.0;

        private readonly UiTheme theme = new UiTheme();
        private readonly Icon appIcon;
        private readonly Sc4iClient sc4iClient = new Sc4iClient();
        private readonly System.Windows.Forms.Timer pollTimer;
        private readonly System.Windows.Forms.Timer jogTimer;

        private NetworkEndpointConfig endpoint = NetworkEndpointConfig.CreateDefault();
        private readonly string calibrationPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scara-calibration.ini");
        private ScaraCalibration calibration = new ScaraCalibration();

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
        private readonly JointControlPanel jointPanel;
        private readonly CartesianControlPanel cartesianPanel;
        private readonly Label statusLabel = new Label();

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

        // Diagnostic inspection hooks for UiSmokeTest
        internal bool IsHardwareConnected { get { return sc4iClient.IsConnected; } }
        internal bool ConnectButtonEnabled { get { return connectButton.Enabled; } }
        internal bool SettingsButtonEnabled { get { return settingsButton.Enabled; } }
        internal bool ClearFaultButtonEnabled { get { return clearFaultButton.Enabled; } }
        internal bool EstopButtonEnabled { get { return estopButton.Enabled; } }
        internal bool SetZeroButtonEnabled { get { return setZeroButton.Enabled; } }
        internal int AxisCardCount { get { return axisCards.Length; } }
        internal int SelectedAxisIndex { get { return selectedAxis; } }
        internal bool IsCartesianMode { get { return cartesianMode; } }
        internal NetworkEndpointConfig Endpoint { get { return endpoint; } }
        internal JointControlPanel JointPanel { get { return jointPanel; } }
        internal CartesianControlPanel CartesianPanel { get { return cartesianPanel; } }

        internal MainForm()
        {
            Text = WindowTitle;
            appIcon = LoadAppIcon();
            if (appIcon != null) Icon = appIcon;
            Font = theme.Regular;
            ForeColor = UiTheme.Ink;
            BackColor = UiTheme.Bg;
            ClientSize = new Size(760, 620);
            MinimumSize = new Size(720, 580);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            DoubleBuffered = true;

            try { calibration = ScaraCalibration.LoadOrDefault(calibrationPath); }
            catch { calibration = new ScaraCalibration(); }

            jointPanel = new JointControlPanel(theme);
            cartesianPanel = new CartesianControlPanel(theme);

            BuildLayout();
            WireChildEvents();

            pollTimer = new System.Windows.Forms.Timer { Interval = 150 };
            pollTimer.Tick += async delegate { await PollStatusAsync(); };

            jogTimer = new System.Windows.Forms.Timer { Interval = 80 };
            jogTimer.Tick += async delegate { await SendJogKeepaliveAsync(); };

            Deactivate += async delegate
            {
                if (jogActive) await StopJogAsync("窗口失焦，已停止点动");
            };

            FormClosing += OnMainFormClosing;

            SetStatus("未连接，点击右上角“连接”开始。", UiTheme.Muted);
            RefreshUi();
        }

        internal void StopTimersForDiagnostics()
        {
            pollTimer.Stop();
            jogTimer.Stop();
        }

        internal Rectangle GetControlPanelClientBounds()
        {
            return RectangleToClient(controlPanel.RectangleToScreen(controlPanel.ClientRectangle));
        }

        internal Rectangle GetStatusLabelClientBounds()
        {
            return RectangleToClient(statusLabel.RectangleToScreen(statusLabel.ClientRectangle));
        }

        internal void SelectAxis(int index)
        {
            if (index < 0 || index >= 4) return;
            selectedAxis = index;
            RefreshUi();
        }

        internal void SetControlMode(bool useCartesian)
        {
            if (jogActive) return;
            cartesianMode = useCartesian;
            jointPanel.Visible = !cartesianMode;
            cartesianPanel.Visible = cartesianMode;
            if (cartesianMode) FillTargetFromCurrentIfConnected();
            RefreshUi();
        }

        private void BuildLayout()
        {
            TableLayoutPanel root = UiTheme.CreateGrid(1, 6);
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
            statusLabel.Font = theme.Small;
            statusLabel.ForeColor = UiTheme.Muted;
            statusLabel.TextAlign = ContentAlignment.BottomLeft;
            statusLabel.AutoEllipsis = true;
            root.Controls.Add(statusLabel, 0, 5);
        }

        private Control BuildTopBar()
        {
            TableLayoutPanel bar = UiTheme.CreateGrid(6, 1);
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
                Font = theme.Title,
                ForeColor = UiTheme.Ink,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 4, 8, 0)
            };
            bar.Controls.Add(title, 0, 0);

            connectionBadge.Dock = DockStyle.Fill;
            connectionBadge.Font = theme.Bold;
            connectionBadge.TextAlign = ContentAlignment.MiddleLeft;
            connectionBadge.Margin = new Padding(2, 6, 8, 0);
            bar.Controls.Add(connectionBadge, 1, 0);

            UiTheme.StyleButton(connectButton, "连接", UiTheme.Accent, Color.White, 0);
            UiTheme.StyleButton(clearFaultButton, "清故障", UiTheme.CardBg, UiTheme.Ink, 1);
            UiTheme.StyleButton(estopButton, "急停", UiTheme.Danger, Color.White, 0);
            UiTheme.StyleButton(settingsButton, "设置", UiTheme.CardBg, UiTheme.Muted, 1);

            connectButton.Font = theme.Bold;
            estopButton.Font = theme.Bold;
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
            TableLayoutPanel row = UiTheme.CreateGrid(4, 1);
            row.Padding = new Padding(0, 12, 0, 12);
            for (int i = 0; i < 4; i++)
            {
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
                int captured = i;
                AxisCard card = new AxisCard(i + 1, theme);
                card.Margin = new Padding(i == 0 ? 0 : 5, 0, i == 3 ? 0 : 5, 0);
                card.CardClicked += delegate
                {
                    if (!jogActive) SelectAxis(captured);
                };
                axisCards[i] = card;
                row.Controls.Add(card, i, 0);
            }
            return row;
        }

        private Control BuildCartesianBanner()
        {
            cartesianBanner.Dock = DockStyle.Fill;
            cartesianBanner.BackColor = UiTheme.CardBg;
            cartesianBanner.Margin = new Padding(0, 0, 0, 8);
            cartesianBanner.Padding = new Padding(14, 0, 14, 0);
            cartesianBanner.Resize += delegate { cartesianBanner.Invalidate(); };
            cartesianBanner.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(UiTheme.BorderColor, 1))
                    e.Graphics.DrawRectangle(pen, 0, 0, cartesianBanner.Width - 1, cartesianBanner.Height - 1);
            };

            TableLayoutPanel row = UiTheme.CreateGrid(2, 1);
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            cartesianCoordsLabel.Dock = DockStyle.Fill;
            cartesianCoordsLabel.Font = theme.Bold;
            cartesianCoordsLabel.ForeColor = UiTheme.Ink;
            cartesianCoordsLabel.TextAlign = ContentAlignment.MiddleLeft;
            cartesianCoordsLabel.Text = "X: --   Y: --   Z: --   R: --";

            cartesianBranchLabel.Dock = DockStyle.Fill;
            cartesianBranchLabel.Font = theme.Small;
            cartesianBranchLabel.ForeColor = UiTheme.Muted;
            cartesianBranchLabel.TextAlign = ContentAlignment.MiddleRight;
            cartesianBranchLabel.Text = "L1=250 L2=145 mm";

            row.Controls.Add(cartesianCoordsLabel, 0, 0);
            row.Controls.Add(cartesianBranchLabel, 1, 0);
            cartesianBanner.Controls.Add(row);
            return cartesianBanner;
        }

        private Control BuildModeBar()
        {
            TableLayoutPanel bar = UiTheme.CreateGrid(5, 1);
            bar.Padding = new Padding(0, 0, 0, 8);
            bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 174));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));

            UiTheme.StyleButton(jointModeButton, "关节单轴", UiTheme.Accent, Color.White, 0);
            UiTheme.StyleButton(cartModeButton, "笛卡尔坐标 (SCARA)", UiTheme.CardBg, UiTheme.Ink, 1);
            UiTheme.StyleButton(setZeroButton, "设为零点", UiTheme.CardBg, UiTheme.Ink, 1);
            UiTheme.StyleButton(calDialogButton, "标定参数", UiTheme.CardBg, UiTheme.Muted, 1);
            jointModeButton.Font = theme.Bold;
            cartModeButton.Font = theme.Bold;
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

        private Control BuildControlCard()
        {
            controlPanel.Dock = DockStyle.Fill;
            controlPanel.BackColor = UiTheme.CardBg;
            controlPanel.Padding = new Padding(16, 12, 16, 14);
            controlPanel.Resize += delegate { controlPanel.Invalidate(); };
            controlPanel.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(UiTheme.BorderColor, 1))
                    e.Graphics.DrawRectangle(pen, 0, 0, controlPanel.Width - 1, controlPanel.Height - 1);
            };

            cartesianPanel.Visible = false;
            controlPanel.Controls.Add(jointPanel);
            controlPanel.Controls.Add(cartesianPanel);
            return controlPanel;
        }

        private void WireChildEvents()
        {
            jointPanel.EnableRequested += async delegate { await EnableSelectedAxisAsync(); };
            jointPanel.RelativeMoveRequested += async dir => await MoveSelectedAxisAsync(dir);
            jointPanel.JogStartRequested += async dir => await StartJogAsync(dir);
            jointPanel.JogStopRequested += async msg => await StopJogAsync(msg);

            cartesianPanel.EnableAllRequested += async delegate { await EnableAllAxesAsync(); };
            cartesianPanel.HomeRequested += async delegate { await ExecuteCartesianMoveAsync(CartesianMotionPlanner.ComputeHomeTarget(calibration)); };
            cartesianPanel.FillCurrentRequested += delegate { FillTargetFromCurrentIfConnected(); };
            cartesianPanel.RelativeStepRequested += async axis => await MoveCartesianStepAsync(axis);
            cartesianPanel.AbsoluteMoveRequested += async target => await ExecuteCartesianMoveAsync(target);
        }

        private void ShowSettingsDialog()
        {
            if (sc4iClient.IsConnected || busy) return;
            NetworkEndpointConfig updated;
            if (NetworkSettingsDialog.TryEdit(this, theme, endpoint, out updated))
            {
                endpoint = updated;
                SetStatus(String.Format(
                    CultureInfo.InvariantCulture,
                    "已更新地址：{0}:{1} → {2}:{3}",
                    endpoint.LocalIp, endpoint.LocalPort, endpoint.CardIp, endpoint.CardPort), UiTheme.Muted);
            }
        }

        private void CaptureCurrentAsZero()
        {
            if (busy || jogActive) return;
            int[] counts;
            if (liveStatus == null || !liveStatus.TryExtractLiveCounts(out counts))
            {
                SetStatus("请先连接控制卡并进 OP 后再设置零点。", UiTheme.Danger);
                return;
            }

            double heightMm;
            if (!SetZeroDialog.TryConfirmHeight(this, theme, counts, calibration.ZeroHeightMm, out heightMm))
                return;

            calibration.CaptureZeroFromCounts(counts, heightMm);
            try
            {
                calibration.SaveToFile(calibrationPath);
                FillTargetFromCurrentIfConnected();
                SetStatus(String.Format(
                    CultureInfo.InvariantCulture,
                    "已将当前姿态保存为零点（X={0:0.1}, Y=0, Z={1:0.1}, R=0）。",
                    calibration.MaxReachMm, calibration.ZeroHeightMm), UiTheme.Success);
            }
            catch (Exception ex)
            {
                SetStatus("保存标定文件失败：" + ex.Message, UiTheme.Danger);
            }
            RefreshUi();
        }

        private void ShowCalibrationDialog()
        {
            if (busy || jogActive) return;
            ScaraCalibration updated;
            if (!CalibrationDialog.TryEdit(this, theme, calibration, out updated))
                return;

            try
            {
                updated.SaveToFile(calibrationPath);
                calibration = updated;
                SetStatus("SCARA 标定参数已更新并保存。", UiTheme.Success);
                RefreshUi();
            }
            catch (Exception ex)
            {
                SetStatus("标定参数无效或保存失败：" + ex.Message, UiTheme.Danger);
            }
        }

        private void FillTargetFromCurrentIfConnected()
        {
            int[] counts;
            if (liveStatus == null || !liveStatus.TryExtractLiveCounts(out counts)) return;
            ScaraJointPose joints;
            ScaraCartesianPose cart = ScaraKinematics.ForwardFromCounts(calibration, counts, out joints);
            cartesianPanel.FillTarget(cart);
        }

        private void RefreshUi()
        {
            bool connected = sc4iClient.IsConnected;
            Sc4iStatusReply st = liveStatus;
            bool commOk = connected && !sessionLost && heartbeatFailCount == 0;
            bool busReady = connected && st != null && st.BusReady;
            bool pdoFresh = connected && st != null && st.IsPdoFresh(Sc4iClient.MaxFreshPdoAgeMs);
            bool pdoStale = busReady && !pdoFresh;
            bool hasFault = sessionLost || heartbeatFailCount > 0 || (st != null && (st.LatchedFault != 0 || st.EstopFlag != 0 || pdoStale));

            UpdateTopBar(connected, busReady, commOk, pdoStale, hasFault, st);

            int[] liveCounts = null;
            bool hasLiveCounts = st != null && st.TryExtractLiveCounts(out liveCounts);
            ScaraJointPose liveJoints = null;
            UpdateCartesianBanner(hasLiveCounts, liveCounts, out liveJoints);
            UpdateModeBar(hasLiveCounts, commOk, st);
            UpdateAxisCards(connected, hasLiveCounts, liveJoints, st);
            UpdateCartesianPanel(commOk, pdoFresh, hasLiveCounts, st);
            UpdateJointPanel(connected, commOk, pdoFresh, pdoStale, st);

            if (sessionLost)
                SetStatus("会话已失效或控制卡已重启，请点击“断开”后重新连接。", UiTheme.Danger);
            else if (pdoStale)
                SetStatus("总线通信中断，已锁定运动，请检查网线或驱动器。", UiTheme.Danger);
            else if (st != null && st.LatchedFault != 0)
                SetStatus("故障锁存：" + Sc4iProtocol.FormatErrorCode(st.LatchedFault) + "，确认安全后点击“清故障”。", UiTheme.Danger);
            else if (st != null && st.EstopFlag != 0)
                SetStatus("急停已锁存，确认安全后点击“清故障”。", UiTheme.Danger);
        }

        private void UpdateTopBar(bool connected, bool busReady, bool commOk, bool pdoStale, bool hasFault, Sc4iStatusReply st)
        {
            if (!connected)
            {
                connectionBadge.Text = busy ? "● 连接中…" : "● 未连接";
                connectionBadge.ForeColor = busy ? UiTheme.Accent : UiTheme.Muted;
            }
            else if (hasFault)
            {
                connectionBadge.Text = sessionLost ? "● 会话失效"
                    : pdoStale ? "● 总线中断"
                    : heartbeatFailCount > 0 ? "● 通信超时"
                    : "● 停机 / 故障";
                connectionBadge.ForeColor = UiTheme.Danger;
            }
            else if (st != null && st.MotionState != 0)
            {
                connectionBadge.Text = "● 运行中";
                connectionBadge.ForeColor = UiTheme.Accent;
            }
            else
            {
                connectionBadge.Text = busReady ? "● 已就绪" : "● 已连接";
                connectionBadge.ForeColor = busReady ? UiTheme.Success : UiTheme.Accent;
            }

            connectButton.Text = connected ? "断开" : "连接";
            connectButton.Enabled = !busy;
            connectButton.BackColor = !busy ? (connected ? UiTheme.Muted : UiTheme.Accent) : UiTheme.DisabledBg;
            connectButton.ForeColor = !busy ? Color.White : UiTheme.Muted;

            clearFaultButton.Enabled = commOk && !busy && !jogActive && busReady;
            clearFaultButton.ForeColor = clearFaultButton.Enabled ? (hasFault ? UiTheme.Danger : UiTheme.Ink) : UiTheme.Muted;

            estopButton.Enabled = connected;
            estopButton.BackColor = connected ? UiTheme.Danger : UiTheme.DisabledBg;
            estopButton.ForeColor = connected ? Color.White : UiTheme.Muted;

            settingsButton.Enabled = !connected && !busy;
        }

        private void UpdateCartesianBanner(bool hasLiveCounts, int[] liveCounts, out ScaraJointPose liveJoints)
        {
            liveJoints = null;
            if (hasLiveCounts)
            {
                ScaraCartesianPose liveCart = ScaraKinematics.ForwardFromCounts(calibration, liveCounts, out liveJoints);
                cartesianCoordsLabel.Text = String.Format(
                    CultureInfo.InvariantCulture,
                    "X: {0:0.1} mm    Y: {1:0.1} mm    Z: {2:0.1} mm    R: {3:0.1}°",
                    liveCart.XMm, liveCart.YMm, liveCart.ZMm, liveCart.RDeg);
                cartesianBranchLabel.Text = String.Format(
                    CultureInfo.InvariantCulture,
                    "半径 {0:0.1} mm · {1}",
                    liveCart.RadiusMm, liveCart.DescribeBranch());
            }
            else
            {
                cartesianCoordsLabel.Text = "X: --    Y: --    Z: --    R: --";
                cartesianBranchLabel.Text = String.Format(
                    CultureInfo.InvariantCulture,
                    "L1={0:0.#}  L2={1:0.#}  H0={2:0.#} mm",
                    calibration.ArmLength1Mm, calibration.ArmLength2Mm, calibration.ZeroHeightMm);
            }
        }

        private void UpdateModeBar(bool hasLiveCounts, bool commOk, Sc4iStatusReply st)
        {
            jointModeButton.BackColor = !cartesianMode ? UiTheme.Accent : UiTheme.CardBg;
            jointModeButton.ForeColor = !cartesianMode ? Color.White : UiTheme.Ink;
            jointModeButton.FlatAppearance.BorderSize = !cartesianMode ? 0 : 1;
            cartModeButton.BackColor = cartesianMode ? UiTheme.Accent : UiTheme.CardBg;
            cartModeButton.ForeColor = cartesianMode ? Color.White : UiTheme.Ink;
            cartModeButton.FlatAppearance.BorderSize = cartesianMode ? 0 : 1;
            jointModeButton.Enabled = !jogActive;
            cartModeButton.Enabled = !jogActive;
            setZeroButton.Enabled = hasLiveCounts && commOk && !busy && !jogActive && st != null && st.MotionState == 0;
            setZeroButton.ForeColor = setZeroButton.Enabled ? UiTheme.Ink : UiTheme.Muted;
            calDialogButton.Enabled = !busy && !jogActive;
        }

        private void UpdateAxisCards(bool connected, bool hasLiveCounts, ScaraJointPose liveJoints, Sc4iStatusReply st)
        {
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

                Color bg = fault ? UiTheme.DangerBg : (en ? UiTheme.SuccessBg : (isSelected ? UiTheme.SelectedBg : UiTheme.CardBg));
                Color border = isSelected ? UiTheme.Accent : (fault ? UiTheme.Danger : UiTheme.BorderColor);
                Color stateColor = fault ? UiTheme.Danger : (en ? UiTheme.Success : UiTheme.Muted);

                axisCards[i].UpdateState(posText, stateText, bg, border, isSelected ? 2 : 1, stateColor);
            }
        }

        private void UpdateCartesianPanel(bool commOk, bool pdoFresh, bool hasLiveCounts, Sc4iStatusReply st)
        {
            bool anySlaveFault = false;
            bool allAxesReady = hasLiveCounts;
            for (int i = 0; i < 4; i++)
            {
                Sc4iSlaveTelemetry s = (st != null && st.Slaves != null && i < st.Slaves.Length) ? st.Slaves[i] : null;
                if (s != null && s.IsFault) anySlaveFault = true;
                if (st == null || !st.IsAxisEnabled(i) || !st.IsAxisAuthorized(i)) allAxesReady = false;
            }
            bool canEnableAll = commOk && pdoFresh && !busy && !jogActive && !anySlaveFault
                && st != null && st.LatchedFault == 0 && st.EstopFlag == 0 && st.MotionState == 0;

            cartesianPanel.UpdateView(new CartesianViewState
            {
                CanEnableAll = canEnableAll,
                AllAxesReady = allAxesReady,
                CanMove = canEnableAll && hasLiveCounts,
                CanFillCurrent = hasLiveCounts && !busy
            });
        }

        private void UpdateJointPanel(bool connected, bool commOk, bool pdoFresh, bool pdoStale, Sc4iStatusReply st)
        {
            Sc4iSlaveTelemetry selSlave = (st != null && st.Slaves != null && selectedAxis < st.Slaves.Length) ? st.Slaves[selectedAxis] : null;
            bool selEnabled = st != null && st.IsAxisEnabled(selectedAxis);
            bool selAuth = st != null && st.IsAxisAuthorized(selectedAxis);
            bool selEnabling = st != null && st.IsAxisEnabling(selectedAxis);
            bool selFault = selSlave != null && selSlave.IsFault;
            bool canEnable = commOk && pdoFresh && !busy && !jogActive && !selEnabling && !selFault
                && st != null && st.LatchedFault == 0 && st.EstopFlag == 0 && st.MotionState == 0;

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

            jointPanel.UpdateView(new JointViewState
            {
                SelectedAxisIndex = selectedAxis,
                AxisStateText = selState,
                IsEnabled = selEnabled,
                IsAuthorized = selAuth,
                CanEnable = canEnable,
                CanMove = canEnable && selEnabled && selAuth,
                JogActive = jogActive
            });
        }

        private void SetStatus(string text, Color color)
        {
            statusLabel.Text = text;
            statusLabel.ForeColor = color;
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                using (Stream stream = typeof(MainForm).Assembly.GetManifestResourceStream("ScaraArm.Console.app.ico"))
                {
                    if (stream != null) return new Icon(stream);
                }
            }
            catch { }

            try
            {
                return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                return null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CancelAllInFlightOps();
                if (pollTimer != null) pollTimer.Dispose();
                if (jogTimer != null) jogTimer.Dispose();
                sc4iClient.Dispose();
                if (appIcon != null) appIcon.Dispose();
                theme.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
