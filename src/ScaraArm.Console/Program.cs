using System;
using System.Windows.Forms;
using ScaraArm.Console.Diagnostics;

namespace ScaraArm.Console
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool smokeTest = false;
            string screenshotPath = null;
            for (int index = 0; index < args.Length; index++)
            {
                if (args[index] == "--ui-smoke-test") smokeTest = true;
                else if (args[index] == "--screenshot" && index + 1 < args.Length) screenshotPath = args[++index];
                else
                {
                    System.Console.Error.WriteLine("用法：ScaraArm.Console.exe [--ui-smoke-test] [--screenshot <PNG路径>]");
                    return 2;
                }
            }
            if (!smokeTest && screenshotPath != null)
            {
                System.Console.Error.WriteLine("--screenshot 需要同时使用 --ui-smoke-test。");
                return 2;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            int result = 0;
            using (MainForm form = new MainForm())
            {
                if (smokeTest)
                {
                    form.Shown += delegate
                    {
                        form.BeginInvoke(new Action(delegate
                        {
                            try
                            {
                                string report;
                                bool passed = UiSmokeTest.Run(form, screenshotPath, out report);
                                System.Console.WriteLine(report);
                                result = passed ? 0 : 1;
                            }
                            catch (Exception error)
                            {
                                System.Console.Error.WriteLine("离线界面检查失败：" + error);
                                result = 1;
                            }
                            finally { form.Close(); }
                        }));
                    };
                }
                Application.Run(form);
            }
            return result;
        }
    }
}
