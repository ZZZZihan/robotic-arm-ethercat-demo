using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NewCard.Core
{
    public enum ScaraElbowMode
    {
        AutoClosest = 0,
        ElbowRight = 1,
        ElbowLeft = 2
    }

    public sealed class ScaraJointPose
    {
        public double Theta1Deg { get; set; }
        public double ZMm { get; set; }
        public double Theta2Deg { get; set; }
        public double Theta4Deg { get; set; }

        public ScaraJointPose Clone()
        {
            return new ScaraJointPose
            {
                Theta1Deg = Theta1Deg,
                ZMm = ZMm,
                Theta2Deg = Theta2Deg,
                Theta4Deg = Theta4Deg
            };
        }
    }

    public sealed class ScaraCartesianPose
    {
        public double XMm { get; internal set; }
        public double YMm { get; internal set; }
        public double ZMm { get; internal set; }
        public double RDeg { get; internal set; }
        public double RadiusMm { get; internal set; }
        public ScaraElbowMode ElbowBranch { get; internal set; }

        public string DescribeBranch()
        {
            if (ElbowBranch == ScaraElbowMode.ElbowRight) return "右手系 (θ2>0)";
            if (ElbowBranch == ScaraElbowMode.ElbowLeft) return "左手系 (θ2<0)";
            return "伸直边界 (θ2≈0)";
        }
    }

    public sealed class ScaraIkResult
    {
        public bool Success { get; internal set; }
        public string Error { get; internal set; }
        public ScaraJointPose TargetJoints { get; internal set; }
        public double[] TargetSlaveRevs { get; internal set; }
        public double[] DeltaSlaveRevs { get; internal set; }
        public ScaraElbowMode ChosenBranch { get; internal set; }
    }

    /// <summary>
    /// Physical calibration parameters for the 4-axis column-lift SCARA arm
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

            // Final confirmed Zero Pose counts (2026-11-22 16:20, H0 = 108.0 mm).
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
            sb.AppendLine("# SCARA 4-axis calibration (NewCardDemo / SC4I0001 v2)");
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

    public static class ScaraKinematics
    {
        private const double DegToRad = Math.PI / 180.0;
        private const double RadToDeg = 180.0 / Math.PI;
        private const double ReachToleranceMm = 0.05;

        public static ScaraJointPose CountsToJoints(ScaraCalibration cal, int[] slaveCounts)
        {
            if (cal == null) throw new ArgumentNullException("cal");
            if (slaveCounts == null || slaveCounts.Length != 4)
                throw new ArgumentException("必须提供 4 个从站的编码器计数。", "slaveCounts");
            cal.Validate();

            double theta1 = cal.J1Sign * (slaveCounts[cal.J1Slave] - (double)cal.ZeroCounts[cal.J1Slave]) / cal.J1CountsPerDeg;
            double zMm = cal.ZeroHeightMm + cal.J2Sign * (slaveCounts[cal.J2Slave] - (double)cal.ZeroCounts[cal.J2Slave]) / cal.J2CountsPerMm;
            double theta2 = cal.J3Sign * (slaveCounts[cal.J3Slave] - (double)cal.ZeroCounts[cal.J3Slave]) / cal.J3CountsPerDeg;
            double theta4 = cal.J4Sign * (slaveCounts[cal.J4Slave] - (double)cal.ZeroCounts[cal.J4Slave]) / cal.J4CountsPerDeg;

            return new ScaraJointPose
            {
                Theta1Deg = theta1,
                ZMm = zMm,
                Theta2Deg = theta2,
                Theta4Deg = theta4
            };
        }

        public static double[] JointsToSlaveRevs(ScaraCalibration cal, ScaraJointPose joints)
        {
            if (cal == null) throw new ArgumentNullException("cal");
            if (joints == null) throw new ArgumentNullException("joints");
            cal.Validate();

            double[] revs = new double[4];
            revs[cal.J1Slave] = (cal.ZeroCounts[cal.J1Slave] + cal.J1Sign * joints.Theta1Deg * cal.J1CountsPerDeg) / cal.CountsPerRev;
            revs[cal.J2Slave] = (cal.ZeroCounts[cal.J2Slave] + cal.J2Sign * (joints.ZMm - cal.ZeroHeightMm) * cal.J2CountsPerMm) / cal.CountsPerRev;
            revs[cal.J3Slave] = (cal.ZeroCounts[cal.J3Slave] + cal.J3Sign * joints.Theta2Deg * cal.J3CountsPerDeg) / cal.CountsPerRev;
            revs[cal.J4Slave] = (cal.ZeroCounts[cal.J4Slave] + cal.J4Sign * joints.Theta4Deg * cal.J4CountsPerDeg) / cal.CountsPerRev;
            return revs;
        }

        public static int[] JointsToSlaveCounts(ScaraCalibration cal, ScaraJointPose joints)
        {
            double[] revs = JointsToSlaveRevs(cal, joints);
            int[] counts = new int[4];
            for (int i = 0; i < 4; i++)
                counts[i] = (int)Math.Round(revs[i] * cal.CountsPerRev);
            return counts;
        }

        public static ScaraCartesianPose Forward(ScaraCalibration cal, ScaraJointPose joints)
        {
            if (cal == null) throw new ArgumentNullException("cal");
            if (joints == null) throw new ArgumentNullException("joints");
            cal.Validate();

            double t1 = joints.Theta1Deg * DegToRad;
            double t12 = (joints.Theta1Deg + joints.Theta2Deg) * DegToRad;
            double x = cal.ArmLength1Mm * Math.Cos(t1) + cal.ArmLength2Mm * Math.Cos(t12);
            double y = cal.ArmLength1Mm * Math.Sin(t1) + cal.ArmLength2Mm * Math.Sin(t12);
            double r = Math.Sqrt(x * x + y * y);
            ScaraElbowMode branch = Math.Abs(joints.Theta2Deg) < 0.5
                ? ScaraElbowMode.AutoClosest
                : (joints.Theta2Deg > 0.0 ? ScaraElbowMode.ElbowRight : ScaraElbowMode.ElbowLeft);

            return new ScaraCartesianPose
            {
                XMm = x,
                YMm = y,
                ZMm = joints.ZMm,
                RDeg = joints.Theta1Deg + joints.Theta2Deg + joints.Theta4Deg,
                RadiusMm = r,
                ElbowBranch = branch
            };
        }

        public static ScaraCartesianPose ForwardFromCounts(ScaraCalibration cal, int[] slaveCounts, out ScaraJointPose joints)
        {
            joints = CountsToJoints(cal, slaveCounts);
            return Forward(cal, joints);
        }

        public static ScaraIkResult Inverse(
            ScaraCalibration cal,
            double targetXMm,
            double targetYMm,
            double targetZMm,
            double targetRDeg,
            bool keepWorldR,
            ScaraJointPose currentJoints,
            ScaraElbowMode elbowPreference)
        {
            if (cal == null) throw new ArgumentNullException("cal");
            if (currentJoints == null) throw new ArgumentNullException("currentJoints");
            cal.Validate();

            if (!IsFinite(targetXMm) || !IsFinite(targetYMm) || !IsFinite(targetZMm) || !IsFinite(targetRDeg))
                return Fail("目标坐标包含无效数值。");

            if (targetZMm < cal.ZMinMm - 1e-6 || targetZMm > cal.ZMaxMm + 1e-6)
            {
                return Fail(String.Format(CultureInfo.InvariantCulture,
                    "目标高度 Z={0:0.1} mm 超出安全软限位 [{1:0.1}, {2:0.1}] mm。",
                    targetZMm, cal.ZMinMm, cal.ZMaxMm));
            }

            double r2 = targetXMm * targetXMm + targetYMm * targetYMm;
            double r = Math.Sqrt(r2);
            if (r > cal.MaxReachMm + ReachToleranceMm)
            {
                return Fail(String.Format(CultureInfo.InvariantCulture,
                    "目标半径 r={0:0.1} mm 超出最大伸展半径 {1:0.1} mm（L1+L2）。",
                    r, cal.MaxReachMm));
            }
            if (r < cal.MinReachMm - 1e-6)
            {
                return Fail(String.Format(CultureInfo.InvariantCulture,
                    "目标半径 r={0:0.1} mm 过近（最小安全半径 {1:0.1} mm），避免撞击立柱。",
                    r, cal.MinReachMm));
            }

            double l1 = cal.ArmLength1Mm;
            double l2 = cal.ArmLength2Mm;
            double cosT2 = (r2 - l1 * l1 - l2 * l2) / (2.0 * l1 * l2);
            if (cosT2 > 1.0) cosT2 = 1.0;
            if (cosT2 < -1.0) cosT2 = -1.0;
            double absT2Rad = Math.Acos(cosT2);

            List<ScaraIkResult> valid = new List<ScaraIkResult>();
            List<string> rejections = new List<string>();
            double[] currentRevs = JointsToSlaveRevs(cal, currentJoints);

            double[] candidateT2Rad = absT2Rad < 1e-7
                ? new double[] { 0.0 }
                : new double[] { absT2Rad, -absT2Rad };

            for (int i = 0; i < candidateT2Rad.Length; i++)
            {
                double t2Rad = candidateT2Rad[i];
                double t2Deg = t2Rad * RadToDeg;
                ScaraElbowMode branch = Math.Abs(t2Deg) < 0.5
                    ? ScaraElbowMode.AutoClosest
                    : (t2Deg > 0.0 ? ScaraElbowMode.ElbowRight : ScaraElbowMode.ElbowLeft);

                if (elbowPreference == ScaraElbowMode.ElbowRight && t2Deg < -0.5) continue;
                if (elbowPreference == ScaraElbowMode.ElbowLeft && t2Deg > 0.5) continue;

                double k1 = l1 + l2 * Math.Cos(t2Rad);
                double k2 = l2 * Math.Sin(t2Rad);
                double rawT1Deg = (Math.Atan2(targetYMm, targetXMm) - Math.Atan2(k2, k1)) * RadToDeg;
                double t1Deg = NormalizeAngleNear(rawT1Deg, currentJoints.Theta1Deg, cal.J1MinDeg, cal.J1MaxDeg);

                double t4Deg = keepWorldR
                    ? NormalizeAngleNear(targetRDeg - t1Deg - t2Deg, currentJoints.Theta4Deg, cal.J4MinDeg, cal.J4MaxDeg)
                    : currentJoints.Theta4Deg;

                string limitErr = CheckSoftLimits(cal, t1Deg, targetZMm, t2Deg, t4Deg);
                if (limitErr != null)
                {
                    rejections.Add((t2Deg >= 0 ? "右手系" : "左手系") + "：" + limitErr);
                    continue;
                }

                ScaraJointPose tj = new ScaraJointPose
                {
                    Theta1Deg = t1Deg,
                    ZMm = targetZMm,
                    Theta2Deg = t2Deg,
                    Theta4Deg = t4Deg
                };
                double[] targetRevs = JointsToSlaveRevs(cal, tj);
                double[] deltaRevs = new double[4];
                for (int s = 0; s < 4; s++) deltaRevs[s] = targetRevs[s] - currentRevs[s];

                valid.Add(new ScaraIkResult
                {
                    Success = true,
                    TargetJoints = tj,
                    TargetSlaveRevs = targetRevs,
                    DeltaSlaveRevs = deltaRevs,
                    ChosenBranch = branch
                });
            }

            if (valid.Count == 0)
            {
                string detail = rejections.Count > 0 ? String.Join("；", rejections.ToArray()) : "所选手系无可行解。";
                return Fail("逆运动学无安全解：" + detail);
            }

            if (valid.Count == 1) return valid[0];

            ScaraIkResult best = valid[0];
            double bestCost = ComputeJointCost(best.TargetJoints, currentJoints);
            for (int i = 1; i < valid.Count; i++)
            {
                double cost = ComputeJointCost(valid[i].TargetJoints, currentJoints);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = valid[i];
                }
            }
            return best;
        }

        private static double ComputeJointCost(ScaraJointPose target, ScaraJointPose current)
        {
            double d1 = Math.Abs(target.Theta1Deg - current.Theta1Deg);
            double d2 = Math.Abs(target.Theta2Deg - current.Theta2Deg);
            double d4 = Math.Abs(target.Theta4Deg - current.Theta4Deg);
            double flipPenalty = (current.Theta2Deg > 1.0 && target.Theta2Deg < -1.0)
                || (current.Theta2Deg < -1.0 && target.Theta2Deg > 1.0)
                ? 45.0
                : 0.0;
            return d1 + d2 + 0.3 * d4 + flipPenalty;
        }

        private static string CheckSoftLimits(ScaraCalibration cal, double t1, double z, double t2, double t4)
        {
            if (t1 < cal.J1MinDeg - 1e-6 || t1 > cal.J1MaxDeg + 1e-6)
                return String.Format(CultureInfo.InvariantCulture, "J1={0:0.1}° 超出 [{1:0.0}°, {2:0.0}°]", t1, cal.J1MinDeg, cal.J1MaxDeg);
            if (z < cal.ZMinMm - 1e-6 || z > cal.ZMaxMm + 1e-6)
                return String.Format(CultureInfo.InvariantCulture, "Z={0:0.1}mm 超出 [{1:0.0}, {2:0.0}]mm", z, cal.ZMinMm, cal.ZMaxMm);
            if (t2 < cal.J3MinDeg - 1e-6 || t2 > cal.J3MaxDeg + 1e-6)
                return String.Format(CultureInfo.InvariantCulture, "J3={0:0.1}° 超出 [{1:0.0}°, {2:0.0}°]", t2, cal.J3MinDeg, cal.J3MaxDeg);
            if (t4 < cal.J4MinDeg - 1e-6 || t4 > cal.J4MaxDeg + 1e-6)
                return String.Format(CultureInfo.InvariantCulture, "J4={0:0.1}° 超出 [{1:0.0}°, {2:0.0}°]", t4, cal.J4MinDeg, cal.J4MaxDeg);
            return null;
        }

        private static double NormalizeAngleNear(double angleDeg, double referenceDeg, double minDeg, double maxDeg)
        {
            double best = NormalizeMinus180To180(angleDeg);
            double bestDist = Math.Abs(best - referenceDeg);
            bool bestInRange = best >= minDeg - 1e-6 && best <= maxDeg + 1e-6;

            for (int k = -2; k <= 2; k++)
            {
                double cand = best + k * 360.0;
                bool inRange = cand >= minDeg - 1e-6 && cand <= maxDeg + 1e-6;
                double dist = Math.Abs(cand - referenceDeg);
                if ((inRange && !bestInRange) || (inRange == bestInRange && dist < bestDist))
                {
                    best = cand;
                    bestDist = dist;
                    bestInRange = inRange;
                }
            }
            return best;
        }

        private static double NormalizeMinus180To180(double deg)
        {
            double a = deg % 360.0;
            if (a > 180.0) a -= 360.0;
            if (a < -180.0) a += 360.0;
            return a;
        }

        private static bool IsFinite(double v)
        {
            return !Double.IsNaN(v) && !Double.IsInfinity(v);
        }

        private static ScaraIkResult Fail(string error)
        {
            return new ScaraIkResult { Success = false, Error = error };
        }
    }
}
