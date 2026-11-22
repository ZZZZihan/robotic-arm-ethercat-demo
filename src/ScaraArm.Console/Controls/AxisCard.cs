using System;
using System.Drawing;
using System.Windows.Forms;
using ScaraArm.Console.Theme;

namespace ScaraArm.Console.Controls
{
    /// <summary>
    /// Displays live position and drive state for one of the four SCARA axes.
    /// </summary>
    internal sealed class AxisCard : Panel
    {
        private readonly Label nameLabel = new Label();
        private readonly Label posLabel = new Label();
        private readonly Label stateLabel = new Label();
        private Color borderColor = UiTheme.BorderColor;
        private int borderWidth = 1;

        internal event EventHandler CardClicked;

        internal AxisCard(int axisNumber, UiTheme theme)
        {
            Dock = DockStyle.Fill;
            BackColor = UiTheme.CardBg;
            Padding = new Padding(12, 10, 10, 10);
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            ResizeRedraw = true;

            TableLayoutPanel grid = UiTheme.CreateGrid(1, 3);
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            Controls.Add(grid);

            nameLabel.Text = "轴 " + axisNumber;
            nameLabel.Dock = DockStyle.Fill;
            nameLabel.Font = theme.Bold;
            nameLabel.ForeColor = UiTheme.Muted;
            nameLabel.TextAlign = ContentAlignment.MiddleLeft;

            posLabel.Text = "--";
            posLabel.Dock = DockStyle.Fill;
            posLabel.Font = theme.AxisValue;
            posLabel.ForeColor = UiTheme.Ink;
            posLabel.TextAlign = ContentAlignment.MiddleLeft;
            posLabel.AutoEllipsis = true;

            stateLabel.Text = "未连接";
            stateLabel.Dock = DockStyle.Fill;
            stateLabel.Font = theme.Small;
            stateLabel.ForeColor = UiTheme.Muted;
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
