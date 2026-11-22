using System;
using System.Globalization;
using System.IO;
using System.Text;
using ScaraArm.Core.Protocol;

namespace ScaraArm.Core.Kinematics
{
    /// <summary>
    /// Physical calibration parameters and INI persistence for the 4-axis column-lift SCARA arm
    /// (SC4I0001 v2 + 4x Inovance SV630N, 262144 counts/rev).
    /// </summary>
    public sealed class ScaraCalibration
    {
        public const double DefaultCountsPerRev = Sc4iMachineConfig.DefaultCountsPerRevolution;
        public const double DefaultL1Mm = 250.0;
        public const double DefaultL2Mm = 145.0;
        public const double DefaultZeroHeightMm = 108.0;

        // Exact tooth ratios provided for J1 (80/20 * 108/22) and J3 (80/20 * 92/22).
        public const double DefaultJ1GearRatio = 216.0 / 11.0;
        public const double DefaultJ2LeadMmPerRev = 8.0;
        public const double DefaultJ3GearRatio = 184.0 / 11.0;
        public const double DefaultJ4GearRatio = 4.0;

        public double CountsPerRev { get; set; }
        public double ArmLength1Mm { get; set; }
        public double ArmLength2Mm { get; set; }
        public double ZeroHeightMm { get; set; }

        public double J1GearRatio { get; set; }
        public double J2LeadMmPerRev { get; set; }
        public double J3GearRatio { get; set; }
        public double J4GearRatio { get; set; }

        // +1: encoder counts increase in positive right-handed direction (+CCW or +Up)
        // -1: encoder counts increase in negative right-handed direction (CW or Down)
        public int J1Sign { get; set; }
        public int J2Sign { get; set; }
        public int J3Sign { get; set; }
        public int J4Sign { get; set; }

        public int J1Slave { get; set; }
        public int J2Slave { get; set; }
        public int J3Slave { get; set; }
        public int J4Slave { get; set; }

        // Indexed by EtherCAT Slave 0..3.
        public int[] ZeroCounts { get; private set; }

        // Conservative software limits in physical units.
        public double J1MinDeg { get; set; }
        public double J1MaxDeg { get; set; }
        public double ZMinMm { get; set; }
        public double ZMaxMm { get; set; }
        public double J3MinDeg { get; set; }
        public double J3MaxDeg { get; set; }
        public double J4MinDeg { get; set; }
        public double J4MaxDeg { get; set; }

        public ScaraCalibration()
        {
            CountsPerRev = DefaultCountsPerRev;
            ArmLength1Mm = DefaultL1Mm;
            ArmLength2Mm = DefaultL2Mm;
            ZeroHeightMm = DefaultZeroHeightMm;

            J1GearRatio = DefaultJ1GearRatio;
            J2LeadMmPerRev = DefaultJ2LeadMmPerRev;
            J3GearRatio = DefaultJ3GearRatio;
            J4GearRatio = DefaultJ4GearRatio;

            J1Sign = -1; // Slave 0 (+cts = CW from top)
            J2Sign = 1;  // Slave 1 (+cts = Up)
            J3Sign = -1; // Slave 2 (+cts = CW from top)
            J4Sign = 1;  // Slave 3 (+cts = CCW from top)

            J1Slave = 0;
            J2Slave = 1;
            J3Slave = 2;
            J4Slave = 3;

            // Confirmed Zero Pose counts (2026-11-22, H0 = 108.0 mm).
            ZeroCounts = new int[] { -296968, -3112648, 928286, -117722 };

            J1MinDeg = -120.0;
            J1MaxDeg = 120.0;
            ZMinMm = 20.0;
            ZMaxMm = 210.0;
            J3MinDeg = -135.0;
            J3MaxDeg = 135.0;
            J4MinDeg = -55.0; // CW hard stop observed near -67.5 deg
            J4MaxDeg = 120.0;
        }

        public double J1CountsPerDeg { get { return CountsPerRev * J1GearRatio / 360.0; } }
        public double J2CountsPerMm { get { return CountsPerRev / J2LeadMmPerRev; } }
        public double J3CountsPerDeg { get { return CountsPerRev * J3GearRatio / 360.0; } }
        public double J4CountsPerDeg { get { return CountsPerRev * J4GearRatio / 360.0; } }

        public double MinReachMm { get { return Math.Abs(ArmLength1Mm - ArmLength2Mm) + 5.0; } }
        public double MaxReachMm { get { return ArmLength1Mm + ArmLength2Mm; } }

        public ScaraCalibration Clone()
        {
            ScaraCalibration copy = new ScaraCalibration
            {
                CountsPerRev = CountsPerRev,
                ArmLength1Mm = ArmLength1Mm,
                ArmLength2Mm = ArmLength2Mm,
                ZeroHeightMm = ZeroHeightMm,
                J1GearRatio = J1GearRatio,
                J2LeadMmPerRev = J2LeadMmPerRev,
                J3GearRatio = J3GearRatio,
                J4GearRatio = J4GearRatio,
                J1Sign = J1Sign,
                J2Sign = J2Sign,
                J3Sign = J3Sign,
                J4Sign = J4Sign,
                J1Slave = J1Slave,
                J2Slave = J2Slave,
                J3Slave = J3Slave,
                J4Slave = J4Slave,
                J1MinDeg = J1MinDeg,
                J1MaxDeg = J1MaxDeg,
                ZMinMm = ZMinMm,
                ZMaxMm = ZMaxMm,
                J3MinDeg = J3MinDeg,
                J3MaxDeg = J3MaxDeg,
                J4MinDeg = J4MinDeg,
                J4MaxDeg = J4MaxDeg
            };
            Array.Copy(ZeroCounts, copy.ZeroCounts, 4);
            return copy;
        }

        public void CaptureZeroFromCounts(int[] slaveCounts, double currentHeightMm)
        {
            if (slaveCounts == null || slaveCounts.Length != 4)
                throw new ArgumentException("必须提供 4 个从站的编码器计数。", "slaveCounts");
            if (Double.IsNaN(currentHeightMm) || Double.IsInfinity(currentHeightMm) || currentHeightMm < 0.0 || currentHeightMm > 1000.0)
                throw new ArgumentOutOfRangeException("currentHeightMm", "零点离地高度 H0 必须在 0 至 1000 mm 之间。");
            for (int i = 0; i < 4; i++) ZeroCounts[i] = slaveCounts[i];
            ZeroHeightMm = currentHeightMm;
        }

        public void Validate()
        {
            if (!IsPositiveFinite(CountsPerRev)) throw new InvalidOperationException("CountsPerRev 必须为正有限数。");
            if (!IsPositiveFinite(ArmLength1Mm) || !IsPositiveFinite(ArmLength2Mm))
                throw new InvalidOperationException("大臂 L1 与小臂 L2 必须为正有限数。");
            if (Double.IsNaN(ZeroHeightMm) || Double.IsInfinity(ZeroHeightMm))
                throw new InvalidOperationException("ZeroHeightMm 必须为有限数。");
            if (!IsPositiveFinite(J1GearRatio) || !IsPositiveFinite(J2LeadMmPerRev)
                || !IsPositiveFinite(J3GearRatio) || !IsPositiveFinite(J4GearRatio))
                throw new InvalidOperationException("减速比与丝杆导程必须为正有限数。");
            if ((J1Sign != 1 && J1Sign != -1) || (J2Sign != 1 && J2Sign != -1)
                || (J3Sign != 1 && J3Sign != -1) || (J4Sign != 1 && J4Sign != -1))
                throw new InvalidOperationException("关节极性符号必须为 +1 或 -1。");
            int[] slaves = { J1Slave, J2Slave, J3Slave, J4Slave };
            bool[] used = new bool[4];
            for (int i = 0; i < 4; i++)
            {
                if (slaves[i] < 0 || slaves[i] > 3 || used[slaves[i]])
                    throw new InvalidOperationException("J1..J4 的从站编号必须互不重复且在 0..3 范围内。");
                used[slaves[i]] = true;
            }
            if (ZeroCounts == null || ZeroCounts.Length != 4)
                throw new InvalidOperationException("ZeroCounts 长度必须为 4。");
            if (J1MinDeg >= J1MaxDeg || ZMinMm >= ZMaxMm || J3MinDeg >= J3MaxDeg || J4MinDeg >= J4MaxDeg)
                throw new InvalidOperationException("每个轴的软限位下限必须小于上限。");
        }

        public string SerializeIni()
        {
            Validate();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# SCARA 4-axis calibration (ScaraArm.Console / SC4I0001 v2)");
            sb.AppendLine("CountsPerRev=" + CountsPerRev.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("ArmLength1Mm=" + ArmLength1Mm.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("ArmLength2Mm=" + ArmLength2Mm.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("ZeroHeightMm=" + ZeroHeightMm.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("J1GearRatio=" + J1GearRatio.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("J2LeadMmPerRev=" + J2LeadMmPerRev.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("J3GearRatio=" + J3GearRatio.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("J4GearRatio=" + J4GearRatio.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("J1Sign=" + J1Sign.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("J2Sign=" + J2Sign.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("J3Sign=" + J3Sign.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("J4Sign=" + J4Sign.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("J1Slave=" + J1Slave.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("J2Slave=" + J2Slave.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("J3Slave=" + J3Slave.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("J4Slave=" + J4Slave.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("ZeroCounts0=" + ZeroCounts[0].ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("ZeroCounts1=" + ZeroCounts[1].ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("ZeroCounts2=" + ZeroCounts[2].ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("ZeroCounts3=" + ZeroCounts[3].ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("J1MinDeg=" + J1MinDeg.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("J1MaxDeg=" + J1MaxDeg.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("ZMinMm=" + ZMinMm.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("ZMaxMm=" + ZMaxMm.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("J3MinDeg=" + J3MinDeg.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("J3MaxDeg=" + J3MaxDeg.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("J4MinDeg=" + J4MinDeg.ToString("R", CultureInfo.InvariantCulture));
            sb.AppendLine("J4MaxDeg=" + J4MaxDeg.ToString("R", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static ScaraCalibration ParseIni(string content)
        {
            ScaraCalibration cal = new ScaraCalibration();
            if (String.IsNullOrWhiteSpace(content)) return cal;
            string[] lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                double d;
                int iv;
                switch (key)
                {
                    case "CountsPerRev": if (TryDouble(val, out d)) cal.CountsPerRev = d; break;
                    case "ArmLength1Mm": if (TryDouble(val, out d)) cal.ArmLength1Mm = d; break;
                    case "ArmLength2Mm": if (TryDouble(val, out d)) cal.ArmLength2Mm = d; break;
                    case "ZeroHeightMm": if (TryDouble(val, out d)) cal.ZeroHeightMm = d; break;
                    case "J1GearRatio": if (TryDouble(val, out d)) cal.J1GearRatio = d; break;
                    case "J2LeadMmPerRev": if (TryDouble(val, out d)) cal.J2LeadMmPerRev = d; break;
                    case "J3GearRatio": if (TryDouble(val, out d)) cal.J3GearRatio = d; break;
                    case "J4GearRatio": if (TryDouble(val, out d)) cal.J4GearRatio = d; break;
                    case "J1Sign": if (TryInt(val, out iv)) cal.J1Sign = iv; break;
                    case "J2Sign": if (TryInt(val, out iv)) cal.J2Sign = iv; break;
                    case "J3Sign": if (TryInt(val, out iv)) cal.J3Sign = iv; break;
                    case "J4Sign": if (TryInt(val, out iv)) cal.J4Sign = iv; break;
                    case "J1Slave": if (TryInt(val, out iv)) cal.J1Slave = iv; break;
                    case "J2Slave": if (TryInt(val, out iv)) cal.J2Slave = iv; break;
                    case "J3Slave": if (TryInt(val, out iv)) cal.J3Slave = iv; break;
                    case "J4Slave": if (TryInt(val, out iv)) cal.J4Slave = iv; break;
                    case "ZeroCounts0": if (TryInt(val, out iv)) cal.ZeroCounts[0] = iv; break;
                    case "ZeroCounts1": if (TryInt(val, out iv)) cal.ZeroCounts[1] = iv; break;
                    case "ZeroCounts2": if (TryInt(val, out iv)) cal.ZeroCounts[2] = iv; break;
                    case "ZeroCounts3": if (TryInt(val, out iv)) cal.ZeroCounts[3] = iv; break;
                    case "J1MinDeg": if (TryDouble(val, out d)) cal.J1MinDeg = d; break;
                    case "J1MaxDeg": if (TryDouble(val, out d)) cal.J1MaxDeg = d; break;
                    case "ZMinMm": if (TryDouble(val, out d)) cal.ZMinMm = d; break;
                    case "ZMaxMm": if (TryDouble(val, out d)) cal.ZMaxMm = d; break;
                    case "J3MinDeg": if (TryDouble(val, out d)) cal.J3MinDeg = d; break;
                    case "J3MaxDeg": if (TryDouble(val, out d)) cal.J3MaxDeg = d; break;
                    case "J4MinDeg": if (TryDouble(val, out d)) cal.J4MinDeg = d; break;
                    case "J4MaxDeg": if (TryDouble(val, out d)) cal.J4MaxDeg = d; break;
                }
            }
            cal.Validate();
            return cal;
        }

        public void SaveToFile(string path)
        {
            string text = SerializeIni();
            string dir = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text, Encoding.UTF8);
        }

        public static ScaraCalibration LoadOrDefault(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new ScaraCalibration();
            return ParseIni(File.ReadAllText(path, Encoding.UTF8));
        }

        private static bool TryDouble(string s, out double v)
        {
            return Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        private static bool TryInt(string s, out int v)
        {
            return Int32.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        }

        private static bool IsPositiveFinite(double v)
        {
            return !Double.IsNaN(v) && !Double.IsInfinity(v) && v > 0.0;
        }
    }
}
