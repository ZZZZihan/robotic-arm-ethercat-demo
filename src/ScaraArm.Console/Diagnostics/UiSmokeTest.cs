using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace ScaraArm.Console.Diagnostics
{
    /// <summary>
    /// Offline UI smoke test executed via --ui-smoke-test without any hardware or UDP traffic.
    /// </summary>
    internal static class UiSmokeTest
    {
        internal static bool Run(MainForm form, string screenshotPath, out string report)
        {
            form.StopTimersForDiagnostics();
            int checks = 0;
            Action<bool, string> check = delegate(bool condition, string description)
            {
                if (!condition) throw new InvalidOperationException("界面离线检查失败：" + description);
                checks++;
            };

            check(!form.IsHardwareConnected, "启动不自动连接实机");
            check(form.Text == MainForm.WindowTitle, "窗体标题简洁一致");
            check(form.ConnectButtonEnabled && form.SettingsButtonEnabled, "离线时仅允许连接与设置");
            check(!form.JointPanel.EnableButtonEnabled
                && !form.JointPanel.MoveButtonsEnabled
                && !form.JointPanel.JogButtonsEnabled
                && !form.EstopButtonEnabled
                && !form.ClearFaultButtonEnabled,
                "未连接时锁定所有运动与使能按钮");
            check(form.AxisCardCount == 4, "显示四个轴卡片");
            check(form.Endpoint.LocalIp == "192.168.8.12"
                && form.Endpoint.LocalPort == 6000
                && form.Endpoint.CardIp == "192.168.8.20"
                && form.Endpoint.CardPort == 5000,
                "默认通信端点正确");
            check(form.JointPanel.StepRevolutions == 0.20m && form.JointPanel.VelocityRevPerSec == 0.25m,
                "默认保守低速小步长");

            form.SelectAxis(1);
            check(form.SelectedAxisIndex == 1
                && form.JointPanel.SelectedAxisHeaderText.StartsWith("轴 2", StringComparison.Ordinal)
                && form.JointPanel.EnableButtonText == "使能轴 2",
                "点击切换轴联动标题与使能按钮");
            form.SelectAxis(0);

            form.SetControlMode(true);
            check(form.IsCartesianMode && form.CartesianPanel.Visible && !form.JointPanel.Visible,
                "可切换到笛卡尔坐标模式");
            check(!form.CartesianPanel.EnableAllButtonEnabled
                && !form.CartesianPanel.MoveTargetButtonEnabled
                && !form.CartesianPanel.HomeButtonEnabled
                && !form.SetZeroButtonEnabled,
                "离线时锁定所有笛卡尔运动与设零按钮");
            for (int i = 0; i < form.CartesianPanel.JogButtonCount; i++)
                check(!form.CartesianPanel.IsJogButtonEnabled(i), "离线时锁定笛卡尔点动按钮 " + i);
            form.SetControlMode(false);

            form.PerformLayout();
            form.Update();
            Rectangle controlBounds = form.GetControlPanelClientBounds();
            Rectangle statusBounds = form.GetStatusLabelClientBounds();
            check(controlBounds.Bottom <= form.ClientSize.Height && statusBounds.Bottom <= form.ClientSize.Height,
                "主控区与状态行完整位于窗口视口内");
            check(!form.IsHardwareConnected, "全过程零硬件通信");

            if (!String.IsNullOrWhiteSpace(screenshotPath))
            {
                string fullPath = Path.GetFullPath(screenshotPath);
                string directory = Path.GetDirectoryName(fullPath);
                if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save(fullPath, ImageFormat.Png);
                }
                form.SetControlMode(true);
                form.PerformLayout();
                form.Update();
                using (Bitmap cartBmp = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(cartBmp, new Rectangle(0, 0, form.Width, form.Height));
                    cartBmp.Save(Path.Combine(directory ?? "", "smoke-cart-ui.png"), ImageFormat.Png);
                }
                form.SetControlMode(false);
            }

            report = "PASS: " + checks + " offline UI checks; no hardware operations.";
            return true;
        }
    }
}
