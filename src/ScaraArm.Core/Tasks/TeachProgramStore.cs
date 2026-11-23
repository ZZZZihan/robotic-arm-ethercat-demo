using System;
using System.Globalization;
using System.IO;
using System.Text;
using ScaraArm.Core.Kinematics;

namespace ScaraArm.Core.Tasks
{
    /// <summary>
    /// Human-readable UTF-8 INI persistence for SCARA teach waypoint lists and cyclic pick-and-place programs.
    /// Zero third-party dependencies; compatible with both net48 and net8.0.
    /// </summary>
    public static class TeachProgramStore
    {
        public const int CurrentFormatVersion = 1;

        public static string Serialize(TeachProgram program)
        {
            if (program == null) throw new ArgumentNullException("program");
            program.ValidateParameters();

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# SCARA 4-axis teach program (ScaraArm.Core.Tasks v0.2)");
            sb.AppendLine("FormatVersion=" + CurrentFormatVersion.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("Name=" + TeachWaypoint.SanitizeSingleLine(String.IsNullOrWhiteSpace(program.Name) ? "默认取放任务" : program.Name));
            sb.AppendLine("VelocityRevPerSec=" + program.VelocityRevPerSec.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("AccelerationRevPerSec2=" + program.AccelerationRevPerSec2.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("UseSafeTransitZ=" + (program.UseSafeTransitZ ? "true" : "false"));
            sb.AppendLine("SafeTransitZMm=" + program.SafeTransitZMm.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("RepeatCycles=" + program.RepeatCycles.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("ReturnHomeOnComplete=" + (program.ReturnHomeOnComplete ? "true" : "false"));

            for (int i = 0; i < program.Waypoints.Count; i++)
            {
                TeachWaypoint wp = program.Waypoints[i];
                sb.AppendLine();
                sb.AppendLine("[Waypoint." + i.ToString(CultureInfo.InvariantCulture) + "]");
                sb.AppendLine("Name=" + wp.DisplayName);
                sb.AppendLine("Role=" + wp.Role.ToString());
                sb.AppendLine("XMm=" + wp.XMm.ToString("R", CultureInfo.InvariantCulture));
                sb.AppendLine("YMm=" + wp.YMm.ToString("R", CultureInfo.InvariantCulture));
                sb.AppendLine("ZMm=" + wp.ZMm.ToString("R", CultureInfo.InvariantCulture));
                sb.AppendLine("RDeg=" + wp.RDeg.ToString("R", CultureInfo.InvariantCulture));
                sb.AppendLine("KeepWorldR=" + (wp.KeepWorldR ? "true" : "false"));
                sb.AppendLine("ElbowPreference=" + wp.ElbowPreference.ToString());
                sb.AppendLine("DwellMs=" + wp.DwellMs.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Enabled=" + (wp.Enabled ? "true" : "false"));
            }

            return sb.ToString();
        }

        public static TeachProgram Parse(string content)
        {
            TeachProgram program = new TeachProgram();
            if (String.IsNullOrWhiteSpace(content)) return program;

            TeachWaypoint current = null;
            string[] lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                string line = lines[lineIdx].Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                    continue;

                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    string section = line.Substring(1, line.Length - 2).Trim();
                    if (section.StartsWith("Waypoint.", StringComparison.OrdinalIgnoreCase))
                    {
                        current = new TeachWaypoint
                        {
                            Name = "P" + (program.Waypoints.Count + 1).ToString(CultureInfo.InvariantCulture)
                        };
                        program.Waypoints.Add(current);
                    }
                    else
                    {
                        current = null;
                    }
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();

                if (current == null)
                {
                    ParseHeaderKey(program, key, val, lineIdx + 1);
                }
                else
                {
                    ParseWaypointKey(current, key, val, lineIdx + 1);
                }
            }

            program.ValidateParameters();
            return program;
        }

        public static void SaveToFile(TeachProgram program, string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentException("保存路径不能为空。", "path");
            string text = Serialize(program);
            string dir = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text, Encoding.UTF8);
        }

        public static TeachProgram LoadFromFile(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
                throw new ArgumentException("文件路径不能为空。", "path");
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }

        public static TeachProgram LoadOrDefault(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new TeachProgram();
            return LoadFromFile(path);
        }

        private static void ParseHeaderKey(TeachProgram program, string key, string val, int lineNumber)
        {
            switch (key)
            {
                case "FormatVersion":
                    int ver = ParseIntOrThrow(val, key, lineNumber);
                    if (ver != CurrentFormatVersion)
                        throw new FormatException("不支持的示教文件版本：" + ver + "（当前仅支持版本 " + CurrentFormatVersion + "）。");
                    break;
                case "Name":
                    if (!String.IsNullOrWhiteSpace(val))
                        program.Name = TeachWaypoint.SanitizeSingleLine(val);
                    break;
                case "VelocityRevPerSec":
                    program.VelocityRevPerSec = ParseDoubleOrThrow(val, key, lineNumber);
                    break;
                case "AccelerationRevPerSec2":
                    program.AccelerationRevPerSec2 = ParseDoubleOrThrow(val, key, lineNumber);
                    break;
                case "UseSafeTransitZ":
                    program.UseSafeTransitZ = ParseBoolOrThrow(val, key, lineNumber);
                    break;
                case "SafeTransitZMm":
                    program.SafeTransitZMm = ParseDoubleOrThrow(val, key, lineNumber);
                    break;
                case "RepeatCycles":
                    program.RepeatCycles = ParseIntOrThrow(val, key, lineNumber);
                    break;
                case "ReturnHomeOnComplete":
                    program.ReturnHomeOnComplete = ParseBoolOrThrow(val, key, lineNumber);
                    break;
            }
        }

        private static void ParseWaypointKey(TeachWaypoint wp, string key, string val, int lineNumber)
        {
            switch (key)
            {
                case "Name":
                    wp.Name = TeachWaypoint.SanitizeSingleLine(val);
                    break;
                case "Role":
                    WaypointRole role;
                    if (!Enum.TryParse<WaypointRole>(val, true, out role) || !Enum.IsDefined(typeof(WaypointRole), role))
                        throw new FormatException("第 " + lineNumber + " 行 Role 值无效：" + val);
                    wp.Role = role;
                    break;
                case "XMm":
                    wp.XMm = ParseDoubleOrThrow(val, key, lineNumber);
                    break;
                case "YMm":
                    wp.YMm = ParseDoubleOrThrow(val, key, lineNumber);
                    break;
                case "ZMm":
                    wp.ZMm = ParseDoubleOrThrow(val, key, lineNumber);
                    break;
                case "RDeg":
                    wp.RDeg = ParseDoubleOrThrow(val, key, lineNumber);
                    break;
                case "KeepWorldR":
                    wp.KeepWorldR = ParseBoolOrThrow(val, key, lineNumber);
                    break;
                case "ElbowPreference":
                    ScaraElbowMode elbow;
                    if (!Enum.TryParse<ScaraElbowMode>(val, true, out elbow) || !Enum.IsDefined(typeof(ScaraElbowMode), elbow))
                        throw new FormatException("第 " + lineNumber + " 行 ElbowPreference 值无效：" + val);
                    wp.ElbowPreference = elbow;
                    break;
                case "DwellMs":
                    wp.DwellMs = ParseIntOrThrow(val, key, lineNumber);
                    break;
                case "Enabled":
                    wp.Enabled = ParseBoolOrThrow(val, key, lineNumber);
                    break;
            }
        }

        private static double ParseDoubleOrThrow(string val, string key, int lineNumber)
        {
            double d;
            if (!Double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)
                || Double.IsNaN(d) || Double.IsInfinity(d))
            {
                throw new FormatException("第 " + lineNumber + " 行 " + key + " 数值无效：" + val);
            }
            return d;
        }

        private static int ParseIntOrThrow(string val, string key, int lineNumber)
        {
            int iv;
            if (!Int32.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out iv))
                throw new FormatException("第 " + lineNumber + " 行 " + key + " 整数值无效：" + val);
            return iv;
        }

        private static bool ParseBoolOrThrow(string val, string key, int lineNumber)
        {
            if (String.Equals(val, "true", StringComparison.OrdinalIgnoreCase) || val == "1")
                return true;
            if (String.Equals(val, "false", StringComparison.OrdinalIgnoreCase) || val == "0")
                return false;
            throw new FormatException("第 " + lineNumber + " 行 " + key + " 布尔值无效：" + val);
        }
    }
}
