using System;
using NewCard.Core;

namespace NewCard.Tests
{
    public static class ScaraKinematicsTests
    {
        private static int count;

        private static void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("ScaraKinematicsTests failed: " + label);
            count++;
        }

        private static void Near(double actual, double expected, double tol, string label)
        {
            if (Math.Abs(actual - expected) > tol)
                throw new Exception("ScaraKinematicsTests failed: " + label + " (expected " + expected + ", got " + actual + ")");
            count++;
        }

        public static int Run()
        {
            count = 0;
            ZeroPoseAndSignTests();
            ForwardInverseRoundTripTests();
            SafetyLimitAndWorkspaceTests();
            IniRoundTripTests();
            return count;
        }

        private static void ZeroPoseAndSignTests()
        {
            ScaraCalibration cal = new ScaraCalibration();
            ScaraJointPose j;
            ScaraCartesianPose cart = ScaraKinematics.ForwardFromCounts(cal, cal.ZeroCounts, out j);

            Near(j.Theta1Deg, 0.0, 1e-6, "Zero pose theta1 = 0");
            Near(j.ZMm, 108.0, 1e-6, "Zero pose Z = H0 = 108 mm");
            Near(j.Theta2Deg, 0.0, 1e-6, "Zero pose theta2 = 0");
            Near(j.Theta4Deg, 0.0, 1e-6, "Zero pose theta4 = 0");
            Near(cart.XMm, 395.0, 1e-6, "Zero pose X = L1 + L2 = 395 mm");
            Near(cart.YMm, 0.0, 1e-6, "Zero pose Y = 0 mm");
            Near(cart.ZMm, 108.0, 1e-6, "Zero pose cart Z = 108 mm");
            Near(cart.RDeg, 0.0, 1e-6, "Zero pose R = 0 deg");

            // Verify polarities & exact tooth ratios:
            // J1 (+cts = CW = -theta1), ratio = 216/11 -> +1 rev motor = -360/(216/11) = -18.3333 deg
            // J2 (+cts = Up = +Z), lead = 8 mm/rev -> +7.2 rev = +57.6 mm -> Z = 165.6 mm
            // J3 (+cts = CW = -theta2), ratio = 184/11 -> +4.181818 rev = -90 deg
            // J4 (+cts = CCW = +theta4), ratio = 4 -> +1.0 rev = +90 deg
            int[] shifted = new int[]
            {
                cal.ZeroCounts[0] + 262144,
                cal.ZeroCounts[1] + (int)Math.Round(7.2 * 262144.0),
                cal.ZeroCounts[2] + (int)Math.Round((184.0 / 44.0) * 262144.0),
                cal.ZeroCounts[3] + 262144
            };
            ScaraJointPose js = ScaraKinematics.CountsToJoints(cal, shifted);
            Near(js.Theta1Deg, -360.0 / (216.0 / 11.0), 1e-4, "J1 +1 rev is -18.3333 deg (CW)");
            Near(js.ZMm, 165.6, 1e-3, "J2 +7.2 rev is 165.6 mm");
            Near(js.Theta2Deg, -90.0, 1e-3, "J3 +184/44 rev is -90 deg (CW)");
            Near(js.Theta4Deg, 90.0, 1e-4, "J4 +1 rev is +90 deg (CCW)");
        }

        private static void ForwardInverseRoundTripTests()
        {
            ScaraCalibration cal = new ScaraCalibration();
            ScaraJointPose[] samples = new ScaraJointPose[]
            {
                new ScaraJointPose { Theta1Deg = 20.0, ZMm = 120.0, Theta2Deg = 45.0, Theta4Deg = -10.0 },
                new ScaraJointPose { Theta1Deg = -35.0, ZMm = 60.0, Theta2Deg = -70.0, Theta4Deg = 40.0 },
                new ScaraJointPose { Theta1Deg = 0.0, ZMm = 100.0, Theta2Deg = 90.0, Theta4Deg = 0.0 },
                new ScaraJointPose { Theta1Deg = 60.0, ZMm = 180.0, Theta2Deg = -90.0, Theta4Deg = 15.0 }
            };

            for (int i = 0; i < samples.Length; i++)
            {
                ScaraJointPose s = samples[i];
                ScaraCartesianPose fk = ScaraKinematics.Forward(cal, s);
                ScaraJointPose nearCurrent = new ScaraJointPose
                {
                    Theta1Deg = s.Theta1Deg - 3.0,
                    ZMm = s.ZMm - 5.0,
                    Theta2Deg = s.Theta2Deg + (s.Theta2Deg >= 0 ? -4.0 : 4.0),
                    Theta4Deg = s.Theta4Deg + 2.0
                };
                ScaraIkResult ik = ScaraKinematics.Inverse(
                    cal, fk.XMm, fk.YMm, fk.ZMm, fk.RDeg, true, nearCurrent, ScaraElbowMode.AutoClosest);
                Check(ik.Success, "IK succeeded for sample " + i);
                Near(ik.TargetJoints.Theta1Deg, s.Theta1Deg, 1e-5, "IK theta1 roundtrip " + i);
                Near(ik.TargetJoints.ZMm, s.ZMm, 1e-5, "IK Z roundtrip " + i);
                Near(ik.TargetJoints.Theta2Deg, s.Theta2Deg, 1e-5, "IK theta2 roundtrip " + i);
                Near(ik.TargetJoints.Theta4Deg, s.Theta4Deg, 1e-5, "IK theta4 roundtrip " + i);

                // Verify keepWorldR=false leaves J4 strictly untouched
                ScaraIkResult ikFixedWrist = ScaraKinematics.Inverse(
                    cal, fk.XMm, fk.YMm, fk.ZMm, fk.RDeg, false, nearCurrent, ScaraElbowMode.AutoClosest);
                Check(ikFixedWrist.Success, "IK keepWorldR=false succeeded " + i);
                Near(ikFixedWrist.DeltaSlaveRevs[cal.J4Slave], 0.0, 1e-9, "keepWorldR=false produces zero J4 motion " + i);
            }
        }

        private static void SafetyLimitAndWorkspaceTests()
        {
            ScaraCalibration cal = new ScaraCalibration();
            ScaraJointPose zero = new ScaraJointPose { Theta1Deg = 0, ZMm = 100, Theta2Deg = 0, Theta4Deg = 0 };

            // Beyond max reach (395 mm)
            ScaraIkResult tooFar = ScaraKinematics.Inverse(cal, 410.0, 0.0, 100.0, 0.0, false, zero, ScaraElbowMode.AutoClosest);
            Check(!tooFar.Success && tooFar.Error.Contains("超出最大伸展半径"), "Reject target beyond L1+L2");

            // Too close to column (< |250-145|+5 = 110 mm)
            ScaraIkResult tooClose = ScaraKinematics.Inverse(cal, 80.0, 0.0, 100.0, 0.0, false, zero, ScaraElbowMode.AutoClosest);
            Check(!tooClose.Success && tooClose.Error.Contains("过近"), "Reject target inside column collision zone");

            // Z below floor safety limit (20 mm)
            ScaraIkResult tooLow = ScaraKinematics.Inverse(cal, 300.0, 0.0, 10.0, 0.0, false, zero, ScaraElbowMode.AutoClosest);
            Check(!tooLow.Success && tooLow.Error.Contains("安全软限位"), "Reject Z below 20 mm");

            // J4 CW hard-stop protection (< -55 deg)
            ScaraIkResult j4TooCw = ScaraKinematics.Inverse(cal, 395.0, 0.0, 100.0, -65.0, true, zero, ScaraElbowMode.AutoClosest);
            Check(!j4TooCw.Success && j4TooCw.Error.Contains("J4"), "Reject J4 past -55 deg CW limit");
        }

        private static void IniRoundTripTests()
        {
            ScaraCalibration orig = new ScaraCalibration
            {
                ArmLength1Mm = 251.5,
                ArmLength2Mm = 144.2,
                J2LeadMmPerRev = 8.0,
                J4GearRatio = 4.2
            };
            orig.CaptureZeroFromCounts(new int[] { -100, -200, 300, -400 }, 112.5);
            string ini = orig.SerializeIni();
            ScaraCalibration loaded = ScaraCalibration.ParseIni(ini);
            Near(loaded.ArmLength1Mm, 251.5, 1e-9, "INI L1 preserved");
            Near(loaded.ArmLength2Mm, 144.2, 1e-9, "INI L2 preserved");
            Near(loaded.ZeroHeightMm, 112.5, 1e-9, "INI H0 preserved");
            Near(loaded.J4GearRatio, 4.2, 1e-9, "INI J4 ratio preserved");
            Check(loaded.ZeroCounts[0] == -100 && loaded.ZeroCounts[1] == -200 && loaded.ZeroCounts[2] == 300 && loaded.ZeroCounts[3] == -400,
                "INI ZeroCounts preserved");
        }
    }
}
