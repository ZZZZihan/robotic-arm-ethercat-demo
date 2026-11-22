using System;
using System.Collections.Generic;
using System.Globalization;

namespace ScaraArm.Core.Kinematics
{
    /// <summary>
    /// Forward and inverse kinematics for the 4-axis column-lift SCARA arm.
    /// </summary>
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
