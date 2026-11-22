using System;
using System.Drawing;
using System.Windows.Forms;
using ScaraArm.Console.Theme;
using ScaraArm.Core.Kinematics;

namespace ScaraArm.Console.Dialogs
{
    internal static class CalibrationDialog
    {
        internal static bool TryEdit(
            IWin32Window owner, UiTheme theme, ScaraCalibration current, out ScaraCalibration updated)
        {
            updated = null;
            using (Form dialog = new Form())
            {
                dialog.Text = "SCARA 标定参数";
                dialog.Font = theme.Regular;
                dialog.ForeColor = UiTheme.Ink;
                dialog.BackColor = UiTheme.CardBg;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ClientSize = new Size(400, 360);

                TableLayoutPanel grid = UiTheme.CreateGrid(2, 9);
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

                theme.ConfigureDecimalInput(l1Box, 50m, 800m, (decimal)current.ArmLength1Mm, 1m, 2);
                theme.ConfigureDecimalInput(l2Box, 50m, 800m, (decimal)current.ArmLength2Mm, 1m, 2);
                theme.ConfigureDecimalInput(h0Box, 0m, 500m, (decimal)current.ZeroHeightMm, 1m, 2);
                theme.ConfigureDecimalInput(j1RatioBox, 1m, 200m, (decimal)Math.Round(current.J1GearRatio, 6), 0.1m, 6);
                theme.ConfigureDecimalInput(j2LeadBox, 1m, 50m, (decimal)Math.Round(current.J2LeadMmPerRev, 4), 0.5m, 3);
                theme.ConfigureDecimalInput(j3RatioBox, 1m, 200m, (decimal)Math.Round(current.J3GearRatio, 6), 0.1m, 6);
                theme.ConfigureDecimalInput(j4RatioBox, 0.5m, 100m, (decimal)Math.Round(current.J4GearRatio, 4), 0.2m, 4);
                theme.ConfigureDecimalInput(j4MinBox, -180m, 0m, (decimal)current.J4MinDeg, 5m, 1);

                grid.Controls.Add(theme.CreateDialogLabel("大臂长 L1 (mm)"), 0, 0); grid.Controls.Add(l1Box, 1, 0);
                grid.Controls.Add(theme.CreateDialogLabel("小臂长 L2 (mm)"), 0, 1); grid.Controls.Add(l2Box, 1, 1);
                grid.Controls.Add(theme.CreateDialogLabel("零点离地高度 H0 (mm)"), 0, 2); grid.Controls.Add(h0Box, 1, 2);
                grid.Controls.Add(theme.CreateDialogLabel("J1 大臂速比"), 0, 3); grid.Controls.Add(j1RatioBox, 1, 3);
                grid.Controls.Add(theme.CreateDialogLabel("J2 丝杆导程 (mm/圈)"), 0, 4); grid.Controls.Add(j2LeadBox, 1, 4);
                grid.Controls.Add(theme.CreateDialogLabel("J3 小臂速比"), 0, 5); grid.Controls.Add(j3RatioBox, 1, 5);
                grid.Controls.Add(theme.CreateDialogLabel("J4 末端速比"), 0, 6); grid.Controls.Add(j4RatioBox, 1, 6);
                grid.Controls.Add(theme.CreateDialogLabel("J4 顺时针软限位 (°)"), 0, 7); grid.Controls.Add(j4MinBox, 1, 7);

                FlowLayoutPanel buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    Margin = new Padding(0, 10, 0, 0)
                };
                Button ok = new Button { Text = "保存", DialogResult = DialogResult.OK, Width = 78, Height = 30 };
                Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 78, Height = 30 };
                Button reset = new Button { Text = "恢复默认", Width = 88, Height = 30 };
                UiTheme.StyleButton(ok, "保存", UiTheme.Accent, Color.White, 0);
                UiTheme.StyleButton(cancel, "取消", UiTheme.CardBg, UiTheme.Ink, 1);
                UiTheme.StyleButton(reset, "恢复默认", UiTheme.CardBg, UiTheme.Muted, 1);
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

                if (dialog.ShowDialog(owner) != DialogResult.OK)
                    return false;

                ScaraCalibration edited = current.Clone();
                edited.ArmLength1Mm = (double)l1Box.Value;
                edited.ArmLength2Mm = (double)l2Box.Value;
                edited.ZeroHeightMm = (double)h0Box.Value;
                edited.J1GearRatio = (double)j1RatioBox.Value;
                edited.J2LeadMmPerRev = (double)j2LeadBox.Value;
                edited.J3GearRatio = (double)j3RatioBox.Value;
                edited.J4GearRatio = (double)j4RatioBox.Value;
                edited.J4MinDeg = (double)j4MinBox.Value;
                updated = edited;
                return true;
            }
        }
    }
}
