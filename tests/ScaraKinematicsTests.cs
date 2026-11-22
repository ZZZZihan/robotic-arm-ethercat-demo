using System;
using ScaraArm.Core.Kinematics;

namespace ScaraArm.Tests
{
    internal static class ScaraKinematicsTests
    {
        public static int Run()
        {
            TestRunner t = new TestRunner("ScaraKinematicsTests");
            ZeroPoseAndSignTests(t);
            ForwardInverseRoundTripTests(t);
            SafetyLimitAndWorkspaceTests(t);
            IniRoundTripTests(t);
            return t.Count;
        }

        private static void ZeroPoseAndSignTests(TestRunner t)
        {
            ScaraCalibration cal = new ScaraCalibration();
            ScaraJointPose j;
            ScaraCartesianPose cart = ScaraKinematics.ForwardFromCounts(cal, cal.ZeroCounts, out j);

            t.Near(j.Theta1Deg, 0.0, 1e-6, "Zero pose theta1 = 0");
            t.Near(j.ZMm, 108.0, 1e-6, "Zero pose Z = H0 = 108 mm");
            t.Near(j.Theta2Deg, 0.0, 1e-6, "Zero pose theta2 = 0");
            t.Near(j.Theta4Deg, 0.0, 1e-6, "Zero pose theta4 = 0");
            t.Near(cart.XMm, 395.0, 1e-6, "Zero pose X = L1 + L2 = 395 mm");
            t.Near(cart.YMm, 0.0, 1e-6, "Zero pose Y = 0 mm");
            t.Near(cart.ZMm, 108.0, 1e-6, "Zero pose cart Z = 108 mm");
            t.Near(cart.RDeg, 0.0, 1e-6, "Zero pose R = 0 deg");

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
            t.Near(js.Theta1Deg, -360.0 / (216.0 / 11.0), 1e-4, "J1 +1 rev is -18.3333 deg (CW)");
            t.Near(js.ZMm, 165.6, 1e-3, "J2 +7.2 rev is 165.6 mm");
            t.Near(js.Theta2Deg, -90.0, 1e-3, "J3 +184/44 rev is -90 deg (CW)");
            t.Near(js.Theta4Deg, 90.0, 1e-4, "J4 +1 rev is +90 deg (CCW)");
        }

        private static void ForwardInverseRoundTripTests(TestRunner t)
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
                t.Check(ik.Success, "IK succeeded for sample " + i);
                t.Near(ik.TargetJoints.Theta1Deg, s.Theta1Deg, 1e-5, "IK theta1 roundtrip " + i);
                t.Near(ik.TargetJoints.ZMm, s.ZMm, 1e-5, "IK Z roundtrip " + i);
                t.Near(ik.TargetJoints.Theta2Deg, s.Theta2Deg, 1e-5, "IK theta2 roundtrip " + i);
                t.Near(ik.TargetJoints.Theta4Deg, s.Theta4Deg, 1e-5, "IK theta4 roundtrip " + i);

                ScaraIkResult ikFixedWrist = ScaraKinematics.Inverse(
                    cal, fk.XMm, fk.YMm, fk.ZMm, fk.RDeg, false, nearCurrent, ScaraElbowMode.AutoClosest);
                t.Check(ikFixedWrist.Success, "IK keepWorldR=false succeeded " + i);
                t.Near(ikFixedWrist.DeltaSlaveRevs[cal.J4Slave], 0.0, 1e-9, "keepWorldR=false produces zero J4 motion " + i);
            }
        }

        private static void SafetyLimitAndWorkspaceTests(TestRunner t)
        {
            ScaraCalibration cal = new ScaraCalibration();
            ScaraJointPose zero = new ScaraJointPose { Theta1Deg = 0, ZMm = 100, Theta2Deg = 0, Theta4Deg = 0 };

            ScaraIkResult tooFar = ScaraKinematics.Inverse(cal, 410.0, 0.0, 100.0, 0.0, false, zero, ScaraElbowMode.AutoClosest);
            t.Check(!tooFar.Success && tooFar.Error.Contains("超出最大伸展半径"), "Reject target beyond L1+L2");

            ScaraIkResult tooClose = ScaraKinematics.Inverse(cal, 80.0, 0.0, 100.0, 0.0, false, zero, ScaraElbowMode.AutoClosest);
            t.Check(!tooClose.Success && tooClose.Error.Contains("过近"), "Reject target inside column collision zone");

            ScaraIkResult tooLow = ScaraKinematics.Inverse(cal, 300.0, 0.0, 10.0, 0.0, false, zero, ScaraElbowMode.AutoClosest);
            t.Check(!tooLow.Success && tooLow.Error.Contains("安全软限位"), "Reject Z below 20 mm");

            ScaraIkResult j4TooCw = ScaraKinematics.Inverse(cal, 395.0, 0.0, 100.0, -65.0, true, zero, ScaraElbowMode.AutoClosest);
            t.Check(!j4TooCw.Success && j4TooCw.Error.Contains("J4"), "Reject J4 past -55 deg CW limit");
        }

        private static void IniRoundTripTests(TestRunner t)
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
            t.Near(loaded.ArmLength1Mm, 251.5, 1e-9, "INI L1 preserved");
            t.Near(loaded.ArmLength2Mm, 144.2, 1e-9, "INI L2 preserved");
            t.Near(loaded.ZeroHeightMm, 112.5, 1e-9, "INI H0 preserved");
            t.Near(loaded.J4GearRatio, 4.2, 1e-9, "INI J4 ratio preserved");
            t.Check(loaded.ZeroCounts[0] == -100 && loaded.ZeroCounts[1] == -200 && loaded.ZeroCounts[2] == 300 && loaded.ZeroCounts[3] == -400,
                "INI ZeroCounts preserved");
        }
    }
}
