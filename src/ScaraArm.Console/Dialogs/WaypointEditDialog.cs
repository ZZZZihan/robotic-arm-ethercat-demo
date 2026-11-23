using System;
using System.Drawing;
using System.Windows.Forms;
using ScaraArm.Console.Theme;
using ScaraArm.Core.Kinematics;
using ScaraArm.Core.Tasks;

namespace ScaraArm.Console.Dialogs
{
    /// <summary>
    /// Modal editor dialog for creating or modifying a single taught SCARA waypoint.
    /// </summary>
    internal static class WaypointEditDialog
    {
        internal static bool TryEdit(
            IWin32Window owner,
            UiTheme theme,
            string title,
            TeachWaypoint current,
            ScaraCartesianPose livePose,
            out TeachWaypoint updated)
        {
            updated = null;
            TeachWaypoint initial = current != null ? current.Clone() : new TeachWaypoint();

            using (Form dialog = new Form())
            {
                dialog.Text = title;
                dialog.Font = theme.Regular;
                dialog.ForeColor = UiTheme.Ink;
                dialog.BackColor = UiTheme.CardBg;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ClientSize = new Size(420, 420);

                TableLayoutPanel grid = UiTheme.CreateGrid(2, 11);
                grid.Padding = new Padding(18, 14, 18, 14);
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                for (int r = 0; r < 10; r++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                dialog.Controls.Add(grid);

                TextBox nameBox = new TextBox
                {
                    Dock = DockStyle.Fill,
                    Font = theme.Regular,
                    Text = initial.DisplayName,
                    Margin = new Padding(0, 3, 0, 3)
                };

                ComboBox roleCombo = new ComboBox
                {
                    Dock = DockStyle.Fill,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Font = theme.Regular,
                    Margin = new Padding(0, 3, 0, 3)
                };
                roleCombo.Items.AddRange(new object[] { "通用点位", "取料点", "放料点", "安全/待机点" });
                roleCombo.SelectedIndex = Math.Max(0, Math.Min(3, (int)initial.Role));

                NumericUpDown xBox = new NumericUpDown();
                NumericUpDown yBox = new NumericUpDown();
                NumericUpDown zBox = new NumericUpDown();
                NumericUpDown rBox = new NumericUpDown();
                theme.ConfigureDecimalInput(xBox, -450.0m, 450.0m, 395.0m, 5.0m, 2);
                theme.ConfigureDecimalInput(yBox, -450.0m, 450.0m, 0.0m, 5.0m, 2);
                theme.ConfigureDecimalInput(zBox, 0.0m, 350.0m, 108.0m, 5.0m, 2);
                theme.ConfigureDecimalInput(rBox, -180.0m, 180.0m, 0.0m, 5.0m, 2);
                UiTheme.SetNumericClamped(xBox, (decimal)Math.Round(initial.XMm, 2));
                UiTheme.SetNumericClamped(yBox, (decimal)Math.Round(initial.YMm, 2));
                UiTheme.SetNumericClamped(zBox, (decimal)Math.Round(initial.ZMm, 2));
                UiTheme.SetNumericClamped(rBox, (decimal)Math.Round(initial.RDeg, 2));

                ComboBox elbowCombo = new ComboBox
                {
                    Dock = DockStyle.Fill,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Font = theme.Regular,
                    Margin = new Padding(0, 3, 0, 3)
                };
                elbowCombo.Items.AddRange(new object[] { "自动最近", "右手系 (θ2≥0)", "左手系 (θ2≤0)" });
                elbowCombo.SelectedIndex = Math.Max(0, Math.Min(2, (int)initial.ElbowPreference));

                NumericUpDown dwellBox = new NumericUpDown();
                theme.ConfigureDecimalInput(dwellBox, 0m, TeachWaypoint.MaxDwellMs, 0m, 100m, 0);
                UiTheme.SetNumericClamped(dwellBox, initial.DwellMs);

                CheckBox keepRCheck = new CheckBox
                {
                    Text = "保持世界坐标系末端朝向 R",
                    Dock = DockStyle.Fill,
                    Font = theme.Regular,
                    ForeColor = UiTheme.Ink,
                    Checked = initial.KeepWorldR
                };

                CheckBox enabledCheck = new CheckBox
                {
                    Text = "在循环任务中启用该点位",
                    Dock = DockStyle.Fill,
                    Font = theme.Regular,
                    ForeColor = UiTheme.Ink,
                    Checked = initial.Enabled
                };

                grid.Controls.Add(theme.CreateDialogLabel("点位名称"), 0, 0); grid.Controls.Add(nameBox, 1, 0);
                grid.Controls.Add(theme.CreateDialogLabel("点位类型"), 0, 1); grid.Controls.Add(roleCombo, 1, 1);
                grid.Controls.Add(theme.CreateDialogLabel("目标 X (mm)"), 0, 2); grid.Controls.Add(xBox, 1, 2);
                grid.Controls.Add(theme.CreateDialogLabel("目标 Y (mm)"), 0, 3); grid.Controls.Add(yBox, 1, 3);
                grid.Controls.Add(theme.CreateDialogLabel("目标 Z (mm)"), 0, 4); grid.Controls.Add(zBox, 1, 4);
                grid.Controls.Add(theme.CreateDialogLabel("目标 R (°)"), 0, 5); grid.Controls.Add(rBox, 1, 5);
                grid.Controls.Add(theme.CreateDialogLabel("肘部手系"), 0, 6); grid.Controls.Add(elbowCombo, 1, 6);
                grid.Controls.Add(theme.CreateDialogLabel("到位停留 (ms)"), 0, 7); grid.Controls.Add(dwellBox, 1, 7);
                grid.Controls.Add(keepRCheck, 1, 8);
                grid.Controls.Add(enabledCheck, 1, 9);

                FlowLayoutPanel buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    Margin = new Padding(0, 10, 0, 0)
                };
                Button ok = new Button { Width = 78, Height = 30 };
                Button cancel = new Button { DialogResult = DialogResult.Cancel, Width = 78, Height = 30 };
                Button fillLive = new Button { Width = 104, Height = 30, Enabled = livePose != null };
                UiTheme.StyleButton(ok, "确定", UiTheme.Accent, Color.White, 0);
                UiTheme.StyleButton(cancel, "取消", UiTheme.CardBg, UiTheme.Ink, 1);
                UiTheme.StyleButton(fillLive, "填入当前姿态", livePose != null ? UiTheme.CardBg : UiTheme.DisabledBg, livePose != null ? UiTheme.Ink : UiTheme.Muted, 1);
                ok.Dock = cancel.Dock = fillLive.Dock = DockStyle.None;

                fillLive.Click += delegate
                {
                    if (livePose == null) return;
                    UiTheme.SetNumericClamped(xBox, (decimal)Math.Round(livePose.XMm, 2));
                    UiTheme.SetNumericClamped(yBox, (decimal)Math.Round(livePose.YMm, 2));
                    UiTheme.SetNumericClamped(zBox, (decimal)Math.Round(livePose.ZMm, 2));
                    UiTheme.SetNumericClamped(rBox, (decimal)Math.Round(livePose.RDeg, 2));
                };

                ok.Click += delegate
                {
                    string trimmedName = TeachWaypoint.SanitizeSingleLine(nameBox.Text);
                    if (String.IsNullOrWhiteSpace(trimmedName))
                    {
                        MessageBox.Show(dialog, "点位名称不能为空。", "参数提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        nameBox.Focus();
                        return;
                    }
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                };

                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(fillLive);
                grid.Controls.Add(buttons, 0, 10);
                grid.SetColumnSpan(buttons, 2);

                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;

                if (dialog.ShowDialog(owner) != DialogResult.OK)
                    return false;

                updated = new TeachWaypoint
                {
                    Name = TeachWaypoint.SanitizeSingleLine(nameBox.Text),
                    Role = (WaypointRole)Math.Max(0, roleCombo.SelectedIndex),
                    XMm = (double)xBox.Value,
                    YMm = (double)yBox.Value,
                    ZMm = (double)zBox.Value,
                    RDeg = (double)rBox.Value,
                    KeepWorldR = keepRCheck.Checked,
                    ElbowPreference = (ScaraElbowMode)Math.Max(0, elbowCombo.SelectedIndex),
                    DwellMs = (int)dwellBox.Value,
                    Enabled = enabledCheck.Checked
                };
                return true;
            }
        }
    }
}
