using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using ScaraArm.Console.Theme;
using ScaraArm.Core.Kinematics;
using ScaraArm.Core.Motion;

namespace ScaraArm.Console.Controls
{
    internal sealed class CartesianViewState
    {
        public bool CanEnableAll { get; set; }
        public bool AllAxesReady { get; set; }
        public bool CanMove { get; set; }
        public bool CanFillCurrent { get; set; }
    }

    /// <summary>
    /// Cartesian SCARA control panel: enable all, safe home, relative ±X/±Y/±Z/±R steps, and absolute (X, Y, Z, R) move.
    /// </summary>
    internal sealed class CartesianControlPanel : Panel
    {
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

        internal event EventHandler EnableAllRequested;
        internal event EventHandler HomeRequested;
        internal event EventHandler FillCurrentRequested;
        internal event Action<CartesianStepAxis> RelativeStepRequested;
        internal event Action<CartesianTarget> AbsoluteMoveRequested;

        internal bool KeepWorldR { get { return keepWorldRCheck.Checked; } }
        internal double StepMm { get { return (double)cartStepMmInput.Value; } }
        internal double StepDeg { get { return (double)cartStepDegInput.Value; } }
        internal double VelocityRevPerSec { get { return (double)cartVelRevInput.Value; } }
        internal ScaraElbowMode ElbowPreference { get { return (ScaraElbowMode)Math.Max(0, elbowCombo.SelectedIndex); } }

        internal bool EnableAllButtonEnabled { get { return enableAllAxesButton.Enabled; } }
        internal bool HomeButtonEnabled { get { return goHomeButton.Enabled; } }
        internal bool MoveTargetButtonEnabled { get { return moveTargetButton.Enabled; } }
        internal int JogButtonCount { get { return cartJogButtons.Length; } }
        internal bool IsJogButtonEnabled(int index) { return cartJogButtons[index].Enabled; }

        internal CartesianControlPanel(UiTheme theme)
        {
            Dock = DockStyle.Fill;
            BackColor = Color.Transparent;

            TableLayoutPanel root = UiTheme.CreateGrid(1, 4);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            Controls.Add(root);

            // Row 0: Enable all 4 axes + Home + Keep R checkbox
            TableLayoutPanel top = UiTheme.CreateGrid(4, 1);
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 146));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 102));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
            UiTheme.StyleButton(enableAllAxesButton, "使能全部四轴", UiTheme.Success, Color.White, 0);
            UiTheme.StyleButton(goHomeButton, "安全回零点", UiTheme.CardBg, UiTheme.Ink, 1);
            enableAllAxesButton.Font = theme.Bold;
            enableAllAxesButton.Margin = new Padding(0, 0, 8, 2);
            goHomeButton.Margin = new Padding(0, 0, 8, 2);
            enableAllAxesButton.Click += delegate
            {
                EventHandler handler = EnableAllRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            };
            goHomeButton.Click += delegate
            {
                EventHandler handler = HomeRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            };

            keepWorldRCheck.Text = "保持末端朝向 R";
            keepWorldRCheck.Dock = DockStyle.Fill;
            keepWorldRCheck.Font = theme.Small;
            keepWorldRCheck.ForeColor = UiTheme.Ink;
            keepWorldRCheck.Checked = false;
            keepWorldRCheck.TextAlign = ContentAlignment.MiddleRight;

            Label hint = new Label
            {
                Text = "安全顺序：上升先提Z，下降后降Z",
                Dock = DockStyle.Fill,
                Font = theme.Small,
                ForeColor = UiTheme.Muted,
                TextAlign = ContentAlignment.MiddleLeft
            };

            top.Controls.Add(enableAllAxesButton, 0, 0);
            top.Controls.Add(goHomeButton, 1, 0);
            top.Controls.Add(hint, 2, 0);
            top.Controls.Add(keepWorldRCheck, 3, 0);
            root.Controls.Add(top, 0, 0);

            // Row 1: Step mm, Step deg, Speed rev/s, Elbow mode
            TableLayoutPanel cfg = UiTheme.CreateGrid(4, 2);
            cfg.Padding = new Padding(0, 4, 0, 4);
            for (int i = 0; i < 4; i++) cfg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            cfg.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            cfg.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            cfg.Controls.Add(theme.CreateCaption("XY / Z 步长 (mm)"), 0, 0);
            cfg.Controls.Add(theme.CreateCaption("R 步长 (°)"), 1, 0);
            cfg.Controls.Add(theme.CreateCaption("单轴速度 (圈/秒)"), 2, 0);
            cfg.Controls.Add(theme.CreateCaption("肘部手系"), 3, 0);
            theme.ConfigureDecimalInput(cartStepMmInput, 1.0m, 80.0m, 10.0m, 5.0m, 1);
            theme.ConfigureDecimalInput(cartStepDegInput, 1.0m, 45.0m, 5.0m, 5.0m, 1);
            theme.ConfigureDecimalInput(cartVelRevInput, 0.05m, 1.50m, 0.35m, 0.05m, 2);
            cartStepMmInput.Margin = new Padding(0, 2, 10, 0);
            cartStepDegInput.Margin = new Padding(0, 2, 10, 0);
            cartVelRevInput.Margin = new Padding(0, 2, 10, 0);
            elbowCombo.Dock = DockStyle.Fill;
            elbowCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            elbowCombo.Font = theme.Regular;
            elbowCombo.Items.AddRange(new object[] { "自动最近", "右手系 (θ2≥0)", "左手系 (θ2≤0)" });
            elbowCombo.SelectedIndex = 0;
            elbowCombo.Margin = new Padding(0, 2, 0, 0);
            cfg.Controls.Add(cartStepMmInput, 0, 1);
            cfg.Controls.Add(cartStepDegInput, 1, 1);
            cfg.Controls.Add(cartVelRevInput, 2, 1);
            cfg.Controls.Add(elbowCombo, 3, 1);
            root.Controls.Add(cfg, 0, 1);

            // Row 2: 2x4 Cartesian relative jog buttons
            TableLayoutPanel jogGrid = UiTheme.CreateGrid(4, 2);
            jogGrid.Padding = new Padding(0, 4, 0, 6);
            for (int c = 0; c < 4; c++) jogGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            jogGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            jogGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            string[] labels = { "− X", "+ X", "− Y", "+ Y", "− Z (降)", "+ Z (升)", "− R (顺)", "+ R (逆)" };
            for (int i = 0; i < 8; i++)
            {
                CartesianStepAxis axis = (CartesianStepAxis)i;
                Button btn = new Button();
                UiTheme.StyleButton(btn, labels[i], UiTheme.Accent, Color.White, 0);
                btn.Font = theme.Bold;
                btn.Margin = new Padding(0, i < 4 ? 0 : 4, (i % 4) == 3 ? 0 : 8, i < 4 ? 4 : 0);
                btn.Click += delegate
                {
                    Action<CartesianStepAxis> handler = RelativeStepRequested;
                    if (handler != null) handler(axis);
                };
                cartJogButtons[i] = btn;
                jogGrid.Controls.Add(btn, i % 4, i / 4);
            }
            root.Controls.Add(jogGrid, 0, 2);

            // Row 3: Absolute target X/Y/Z/R + Fill Current + Go
            TableLayoutPanel abs = UiTheme.CreateGrid(6, 2);
            abs.Padding = new Padding(0, 4, 0, 0);
            abs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
            abs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
            abs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
            abs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
            abs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
            abs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 17));
            abs.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            abs.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            abs.Controls.Add(theme.CreateCaption("目标 X (mm)"), 0, 0);
            abs.Controls.Add(theme.CreateCaption("目标 Y (mm)"), 1, 0);
            abs.Controls.Add(theme.CreateCaption("目标 Z (mm)"), 2, 0);
            abs.Controls.Add(theme.CreateCaption("目标 R (°)"), 3, 0);
            theme.ConfigureDecimalInput(targetXInput, -400.0m, 400.0m, 395.0m, 5.0m, 1);
            theme.ConfigureDecimalInput(targetYInput, -400.0m, 400.0m, 0.0m, 5.0m, 1);
            theme.ConfigureDecimalInput(targetZInput, 0.0m, 300.0m, 100.0m, 5.0m, 1);
            theme.ConfigureDecimalInput(targetRInput, -180.0m, 180.0m, 0.0m, 5.0m, 1);
            targetXInput.Margin = new Padding(0, 2, 8, 0);
            targetYInput.Margin = new Padding(0, 2, 8, 0);
            targetZInput.Margin = new Padding(0, 2, 8, 0);
            targetRInput.Margin = new Padding(0, 2, 8, 0);
            UiTheme.StyleButton(fillCurrentTargetButton, "填入当前", UiTheme.CardBg, UiTheme.Ink, 1);
            UiTheme.StyleButton(moveTargetButton, "定位目标", UiTheme.Success, Color.White, 0);
            moveTargetButton.Font = theme.Bold;
            fillCurrentTargetButton.Margin = new Padding(0, 2, 8, 0);
            moveTargetButton.Margin = new Padding(0, 2, 0, 0);
            fillCurrentTargetButton.Click += delegate
            {
                EventHandler handler = FillCurrentRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            };
            moveTargetButton.Click += delegate
            {
                Action<CartesianTarget> handler = AbsoluteMoveRequested;
                if (handler != null) handler(BuildAbsoluteTarget());
            };

            abs.Controls.Add(targetXInput, 0, 1);
            abs.Controls.Add(targetYInput, 1, 1);
            abs.Controls.Add(targetZInput, 2, 1);
            abs.Controls.Add(targetRInput, 3, 1);
            abs.Controls.Add(fillCurrentTargetButton, 4, 1);
            abs.Controls.Add(moveTargetButton, 5, 1);
            root.Controls.Add(abs, 0, 3);
        }

        internal void FillTarget(ScaraCartesianPose pose)
        {
            if (pose == null) return;
            UiTheme.SetNumericClamped(targetXInput, (decimal)Math.Round(pose.XMm, 1));
            UiTheme.SetNumericClamped(targetYInput, (decimal)Math.Round(pose.YMm, 1));
            UiTheme.SetNumericClamped(targetZInput, (decimal)Math.Round(pose.ZMm, 1));
            UiTheme.SetNumericClamped(targetRInput, (decimal)Math.Round(pose.RDeg, 1));
        }

        internal void UpdateView(CartesianViewState state)
        {
            enableAllAxesButton.Enabled = state.CanEnableAll;
            enableAllAxesButton.Text = state.AllAxesReady ? "四轴已全部使能" : "使能全部四轴";
            enableAllAxesButton.BackColor = state.CanEnableAll ? UiTheme.Success : UiTheme.DisabledBg;
            enableAllAxesButton.ForeColor = state.CanEnableAll ? Color.White : UiTheme.Muted;

            goHomeButton.Enabled = state.CanMove && state.AllAxesReady;
            goHomeButton.ForeColor = goHomeButton.Enabled ? UiTheme.Ink : UiTheme.Muted;

            fillCurrentTargetButton.Enabled = state.CanFillCurrent;
            fillCurrentTargetButton.ForeColor = fillCurrentTargetButton.Enabled ? UiTheme.Ink : UiTheme.Muted;

            moveTargetButton.Enabled = state.CanMove;
            moveTargetButton.BackColor = state.CanMove ? UiTheme.Success : UiTheme.DisabledBg;
            moveTargetButton.ForeColor = state.CanMove ? Color.White : UiTheme.Muted;

            for (int i = 0; i < cartJogButtons.Length; i++)
            {
                cartJogButtons[i].Enabled = state.CanMove;
                cartJogButtons[i].BackColor = state.CanMove ? UiTheme.Accent : UiTheme.DisabledBg;
                cartJogButtons[i].ForeColor = state.CanMove ? Color.White : UiTheme.Muted;
            }
        }

        private CartesianTarget BuildAbsoluteTarget()
        {
            double tx = (double)targetXInput.Value;
            double ty = (double)targetYInput.Value;
            double tz = (double)targetZInput.Value;
            double tr = (double)targetRInput.Value;
            return new CartesianTarget
            {
                XMm = tx,
                YMm = ty,
                ZMm = tz,
                RDeg = tr,
                KeepWorldR = keepWorldRCheck.Checked,
                Label = String.Format(
                    CultureInfo.InvariantCulture,
                    "目标 ({0:0.1}, {1:0.1}, {2:0.1} mm, R={3:0.1}°)",
                    tx, ty, tz, tr)
            };
        }
    }
}
