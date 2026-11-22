using System.Drawing;
using System.Windows.Forms;
using ScaraArm.Console.Theme;

namespace ScaraArm.Console.Dialogs
{
    internal sealed class NetworkEndpointConfig
    {
        public string LocalIp { get; set; }
        public int LocalPort { get; set; }
        public string CardIp { get; set; }
        public int CardPort { get; set; }

        public static NetworkEndpointConfig CreateDefault()
        {
            return new NetworkEndpointConfig
            {
                LocalIp = "192.168.8.12",
                LocalPort = 6000,
                CardIp = "192.168.8.20",
                CardPort = 5000
            };
        }
    }

    internal static class NetworkSettingsDialog
    {
        internal static bool TryEdit(IWin32Window owner, UiTheme theme, NetworkEndpointConfig current, out NetworkEndpointConfig updated)
        {
            updated = null;
            using (Form dialog = new Form())
            {
                dialog.Text = "网络设置";
                dialog.Font = theme.Regular;
                dialog.ForeColor = UiTheme.Ink;
                dialog.BackColor = UiTheme.CardBg;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.MaximizeBox = false;
                dialog.MinimizeBox = false;
                dialog.ClientSize = new Size(340, 210);

                TableLayoutPanel grid = UiTheme.CreateGrid(2, 5);
                grid.Padding = new Padding(18, 16, 18, 14);
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                for (int r = 0; r < 4; r++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                dialog.Controls.Add(grid);

                TextBox localIpBox = new TextBox { Text = current.LocalIp, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
                NumericUpDown localPortBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = current.LocalPort, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
                TextBox cardIpBox = new TextBox { Text = current.CardIp, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };
                NumericUpDown cardPortBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = current.CardPort, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4) };

                grid.Controls.Add(theme.CreateDialogLabel("电脑 IP"), 0, 0);
                grid.Controls.Add(localIpBox, 1, 0);
                grid.Controls.Add(theme.CreateDialogLabel("本地端口"), 0, 1);
                grid.Controls.Add(localPortBox, 1, 1);
                grid.Controls.Add(theme.CreateDialogLabel("控制卡 IP"), 0, 2);
                grid.Controls.Add(cardIpBox, 1, 2);
                grid.Controls.Add(theme.CreateDialogLabel("卡端口"), 0, 3);
                grid.Controls.Add(cardPortBox, 1, 3);

                FlowLayoutPanel buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    Margin = new Padding(0, 10, 0, 0)
                };
                Button ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 78, Height = 30 };
                Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 78, Height = 30 };
                UiTheme.StyleButton(ok, "确定", UiTheme.Accent, Color.White, 0);
                UiTheme.StyleButton(cancel, "取消", UiTheme.CardBg, UiTheme.Ink, 1);
                ok.Dock = DockStyle.None;
                cancel.Dock = DockStyle.None;
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                grid.Controls.Add(buttons, 0, 4);
                grid.SetColumnSpan(buttons, 2);

                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;

                if (dialog.ShowDialog(owner) != DialogResult.OK)
                    return false;

                updated = new NetworkEndpointConfig
                {
                    LocalIp = localIpBox.Text.Trim(),
                    LocalPort = (int)localPortBox.Value,
                    CardIp = cardIpBox.Text.Trim(),
                    CardPort = (int)cardPortBox.Value
                };
                return true;
            }
        }
    }
}
