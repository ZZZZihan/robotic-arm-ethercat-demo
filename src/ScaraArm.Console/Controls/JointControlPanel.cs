using System;
using System.Drawing;
using System.Windows.Forms;
using ScaraArm.Console.Theme;

namespace ScaraArm.Console.Controls
{
    internal sealed class JointViewState
    {
        public int SelectedAxisIndex { get; set; }
        public string AxisStateText { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsAuthorized { get; set; }
        public bool CanEnable { get; set; }
        public bool CanMove { get; set; }
        public bool JogActive { get; set; }
    }

    /// <summary>
    /// Joint single-axis debugging panel: enable, relative step move, and hold-to-jog.
    /// </summary>
    internal sealed class JointControlPanel : Panel
    {
        private readonly Label selectedAxisLabel = new Label();
        private readonly Button enableButton = new Button();
        private readonly NumericUpDown stepInput = new NumericUpDown();
        private readonly NumericUpDown velInput = new NumericUpDown();
        private readonly Button moveNegButton = new Button();
        private readonly Button movePosButton = new Button();
        private readonly Button jogNegButton = new Button();
        private readonly Button jogPosButton = new Button();

        internal event EventHandler EnableRequested;
        internal event Action<int> RelativeMoveRequested;
        internal event Action<int> JogStartRequested;
        internal event Action<string> JogStopRequested;

        internal decimal StepRevolutions { get { return stepInput.Value; } }
        internal decimal VelocityRevPerSec { get { return velInput.Value; } }
        internal string SelectedAxisHeaderText { get { return selectedAxisLabel.Text; } }
        internal string EnableButtonText { get { return enableButton.Text; } }
        internal bool EnableButtonEnabled { get { return enableButton.Enabled; } }
        internal bool MoveButtonsEnabled { get { return moveNegButton.Enabled || movePosButton.Enabled; } }
        internal bool JogButtonsEnabled { get { return jogNegButton.Enabled || jogPosButton.Enabled; } }

        internal JointControlPanel(UiTheme theme)
        {
            Dock = DockStyle.Fill;
            BackColor = Color.Transparent;

            TableLayoutPanel root = UiTheme.CreateGrid(1, 3);
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            // Row 0: Selected axis title + Enable button
            TableLayoutPanel header = UiTheme.CreateGrid(2, 1);
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            selectedAxisLabel.Dock = DockStyle.Fill;
            selectedAxisLabel.Font = theme.Section;
            selectedAxisLabel.ForeColor = UiTheme.Ink;
            selectedAxisLabel.TextAlign = ContentAlignment.MiddleLeft;
            UiTheme.StyleButton(enableButton, "使能轴 1", UiTheme.Success, Color.White, 0);
            enableButton.Font = theme.Bold;
            enableButton.Margin = new Padding(8, 2, 0, 2);
            enableButton.Click += delegate
            {
                EventHandler handler = EnableRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            };
            header.Controls.Add(selectedAxisLabel, 0, 0);
            header.Controls.Add(enableButton, 1, 0);
            root.Controls.Add(header, 0, 0);

            // Row 1: Step & Speed inputs
            TableLayoutPanel inputs = UiTheme.CreateGrid(2, 2);
            inputs.Padding = new Padding(0, 12, 0, 6);
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            inputs.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            inputs.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            inputs.Controls.Add(theme.CreateCaption("步长 (圈)"), 0, 0);
            inputs.Controls.Add(theme.CreateCaption("速度 (圈/秒)"), 1, 0);
            theme.ConfigureDecimalInput(stepInput, 0.01m, 10.00m, 0.20m, 0.05m, 2);
            theme.ConfigureDecimalInput(velInput, 0.02m, 2.00m, 0.25m, 0.05m, 2);
            stepInput.Margin = new Padding(0, 2, 14, 2);
            velInput.Margin = new Padding(0, 2, 0, 2);
            inputs.Controls.Add(stepInput, 0, 1);
            inputs.Controls.Add(velInput, 1, 1);
            root.Controls.Add(inputs, 0, 1);

            // Row 2: Move & Jog buttons
            TableLayoutPanel actions = UiTheme.CreateGrid(4, 1);
            actions.Padding = new Padding(0, 12, 0, 2);
            for (int i = 0; i < 4; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

            UiTheme.StyleButton(moveNegButton, "− 反向", UiTheme.Accent, Color.White, 0);
            UiTheme.StyleButton(movePosButton, "+ 正向", UiTheme.Accent, Color.White, 0);
            UiTheme.StyleButton(jogNegButton, "◀ 按住点动", UiTheme.CardBg, UiTheme.Ink, 1);
            UiTheme.StyleButton(jogPosButton, "▶ 按住点动", UiTheme.CardBg, UiTheme.Ink, 1);
            moveNegButton.Font = theme.Bold;
            movePosButton.Font = theme.Bold;
            moveNegButton.Margin = new Padding(0, 0, 10, 0);
            movePosButton.Margin = new Padding(0, 0, 10, 0);
            jogNegButton.Margin = new Padding(0, 0, 10, 0);
            jogPosButton.Margin = new Padding(0);

            moveNegButton.Click += delegate { RaiseRelativeMove(-1); };
            movePosButton.Click += delegate { RaiseRelativeMove(1); };
            WireJogButton(jogNegButton, -1);
            WireJogButton(jogPosButton, 1);

            actions.Controls.Add(moveNegButton, 0, 0);
            actions.Controls.Add(movePosButton, 1, 0);
            actions.Controls.Add(jogNegButton, 2, 0);
            actions.Controls.Add(jogPosButton, 3, 0);
            root.Controls.Add(actions, 0, 2);
        }

        internal void UpdateView(JointViewState state)
        {
            int axisNumber = state.SelectedAxisIndex + 1;
            selectedAxisLabel.Text = "轴 " + axisNumber + "  ·  " + state.AxisStateText;

            enableButton.Text = (state.IsEnabled && !state.IsAuthorized)
                ? "重新授权轴 " + axisNumber
                : "使能轴 " + axisNumber;
            enableButton.Enabled = state.CanEnable;
            enableButton.BackColor = state.CanEnable ? UiTheme.Success : UiTheme.DisabledBg;
            enableButton.ForeColor = state.CanEnable ? Color.White : UiTheme.Muted;

            moveNegButton.Enabled = state.CanMove;
            movePosButton.Enabled = state.CanMove;
            moveNegButton.BackColor = state.CanMove ? UiTheme.Accent : UiTheme.DisabledBg;
            movePosButton.BackColor = state.CanMove ? UiTheme.Accent : UiTheme.DisabledBg;
            moveNegButton.ForeColor = state.CanMove ? Color.White : UiTheme.Muted;
            movePosButton.ForeColor = state.CanMove ? Color.White : UiTheme.Muted;

            bool canJog = state.CanMove || state.JogActive;
            jogNegButton.Enabled = canJog;
            jogPosButton.Enabled = canJog;
            jogNegButton.ForeColor = canJog ? UiTheme.Ink : UiTheme.Muted;
            jogPosButton.ForeColor = canJog ? UiTheme.Ink : UiTheme.Muted;

            stepInput.Enabled = !state.JogActive;
            velInput.Enabled = !state.JogActive;
        }

        private void WireJogButton(Button button, int direction)
        {
            button.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    Action<int> handler = JogStartRequested;
                    if (handler != null) handler(direction);
                }
            };
            button.MouseUp += delegate { RaiseJogStop("已停止点动并持力"); };
            button.MouseCaptureChanged += delegate { RaiseJogStop("已停止点动并持力"); };
        }

        private void RaiseRelativeMove(int direction)
        {
            Action<int> handler = RelativeMoveRequested;
            if (handler != null) handler(direction);
        }

        private void RaiseJogStop(string reason)
        {
            Action<string> handler = JogStopRequested;
            if (handler != null) handler(reason);
        }
    }
}
