using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using ScaraArm.Console.Theme;

namespace ScaraArm.Console.Dialogs
{
    internal static class SetZeroDialog
    {
        internal static bool TryConfirmHeight(
            IWin32Window owner, UiTheme theme, int[] currentCounts, double defaultHeightMm, out double heightMm)
        {
            heightMm = defaultHeightMm;
            using (Form dialog = new Form())
            {
                dialog.Text = "将当前姿态设为零点";
                dialog.Font = theme.Regular;
                dialog.ForeColor = UiTheme.Ink;
                dialog.BackColor = UiTheme.CardBg;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ClientSize = new Size(380, 220);

                TableLayoutPanel grid = UiTheme.CreateGrid(2, 4);
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
                    Font = theme.Small,
                    ForeColor = UiTheme.Ink
                };
                grid.Controls.Add(desc, 0, 0);
                grid.SetColumnSpan(desc, 2);

                NumericUpDown h0Box = new NumericUpDown();
                theme.ConfigureDecimalInput(h0Box, 0.0m, 400.0m, (decimal)defaultHeightMm, 5.0m, 1);
                grid.Controls.Add(theme.CreateDialogLabel("当前离地高度 H0 (mm)"), 0, 1);
                grid.Controls.Add(h0Box, 1, 1);

                Label ctsInfo = new Label
                {
                    Text = String.Format(
                        CultureInfo.InvariantCulture,
                        "当前脉冲：S0={0}, S1={1}, S2={2}, S3={3}",
                        currentCounts[0], currentCounts[1], currentCounts[2], currentCounts[3]),
                    Dock = DockStyle.Fill,
                    Font = theme.Small,
                    ForeColor = UiTheme.Muted,
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
                UiTheme.StyleButton(ok, "保存为零点", UiTheme.Accent, Color.White, 0);
                UiTheme.StyleButton(cancel, "取消", UiTheme.CardBg, UiTheme.Ink, 1);
                ok.Dock = DockStyle.None;
                cancel.Dock = DockStyle.None;
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                grid.Controls.Add(buttons, 0, 3);
                grid.SetColumnSpan(buttons, 2);

                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;

                if (dialog.ShowDialog(owner) != DialogResult.OK)
                    return false;

                heightMm = (double)h0Box.Value;
                return true;
            }
        }
    }
}
