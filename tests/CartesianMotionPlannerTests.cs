using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ScaraArm.Core.Kinematics;
using ScaraArm.Core.Motion;
using ScaraArm.Core.Protocol;
using ScaraArm.Core.Transport;

namespace ScaraArm.Tests
{
    internal static class CartesianMotionPlannerTests
    {
        public static int Run()
        {
            TestRunner t = new TestRunner("CartesianMotionPlannerTests");
            PlanningOrderAndValidationTests(t);
            RelativeAndHomeTargetTests(t);
            ExecutionSequenceAndAbortTests(t).GetAwaiter().GetResult();
            return t.Count;
        }

        private static Sc4iStatusReply BuildReadyStatus(int[] counts, uint enMask = 0x0Fu, uint authMask = 0x0Fu, byte motionState = 0, byte estop = 0, uint latched = 0, bool pdoOk = true)
        {
            byte[] raw = Sc4iTestFrames.BuildStatusReply(
                Sc4iSubcmd.Heartbeat, motionState, estop, 4, 1u, 1ul, 1ul,
                0u, latched, true, true, enMask, authMask, 0u, 255u, pdoOk, 0u,
                100u, 100u, 52u, counts,
                new ushort[] { 0x1627, 0x1627, 0x1627, 0x1627 },
                new ushort[] { 0, 0, 0, 0 });
            return Sc4iProtocol.ParseF2Reply(raw);
        }

        private static void PlanningOrderAndValidationTests(TestRunner t)
        {
            ScaraCalibration cal = new ScaraCalibration();
            Sc4iStatusReply atZero = BuildReadyStatus(cal.ZeroCounts);

            // Raising Z + moving XY + keeping R -> Z (Slave 1) must be FIRST
            CartesianTarget raiseTarget = new CartesianTarget
            {
                XMm = 300.0,
                YMm = 80.0,
                ZMm = 140.0,
                RDeg = 0.0,
                KeepWorldR = true,
                Label = "Raise and move"
            };
            CartesianMovePlan raisePlan = CartesianMotionPlanner.PlanMove(cal, atZero, raiseTarget, ScaraElbowMode.AutoClosest);
            t.Check(raisePlan.Success && raisePlan.RaisingZ, "Raise plan succeeded and detected raising Z");
            t.Check(raisePlan.Steps.Length == 4, "Raise plan schedules all 4 axes");
            t.Check(raisePlan.Steps[0].SlaveIndex == cal.J2Slave, "Raising Z moves J2 (Z) FIRST");
            t.Check(raisePlan.Steps[1].SlaveIndex == cal.J1Slave
                && raisePlan.Steps[2].SlaveIndex == cal.J3Slave
                && raisePlan.Steps[3].SlaveIndex == cal.J4Slave,
                "After raising Z, moves J1 -> J3 -> J4");

            // Lowering Z + moving XY + keeping R -> Z (Slave 1) must be LAST
            CartesianTarget lowerTarget = new CartesianTarget
            {
                XMm = 300.0,
                YMm = 80.0,
                ZMm = 70.0,
                RDeg = 0.0,
                KeepWorldR = true,
                Label = "Lower and move"
            };
            CartesianMovePlan lowerPlan = CartesianMotionPlanner.PlanMove(cal, atZero, lowerTarget, ScaraElbowMode.AutoClosest);
            t.Check(lowerPlan.Success && !lowerPlan.RaisingZ, "Lower plan succeeded and detected lowering Z");
            t.Check(lowerPlan.Steps.Length == 4, "Lower plan schedules all 4 axes");
            t.Check(lowerPlan.Steps[0].SlaveIndex == cal.J1Slave
                && lowerPlan.Steps[1].SlaveIndex == cal.J3Slave
                && lowerPlan.Steps[2].SlaveIndex == cal.J4Slave
                && lowerPlan.Steps[3].SlaveIndex == cal.J2Slave,
                "Lowering Z moves J1 -> J3 -> J4 first and J2 (Z) LAST");

            // Disabled required axis must reject plan BEFORE any command
            Sc4iStatusReply missingJ3 = BuildReadyStatus(cal.ZeroCounts, enMask: 0x0Bu, authMask: 0x0Bu);
            CartesianMovePlan blocked = CartesianMotionPlanner.PlanMove(cal, missingJ3, raiseTarget, ScaraElbowMode.AutoClosest);
            t.Check(!blocked.Success && blocked.Error.Contains("轴 3"), "Plan rejects when required slave 2 (axis 3) is disabled");

            // Same pose produces valid no-op plan (0 steps)
            CartesianTarget home = CartesianMotionPlanner.ComputeHomeTarget(cal);
            CartesianMovePlan noop = CartesianMotionPlanner.PlanMove(cal, atZero, home, ScaraElbowMode.AutoClosest);
            t.Check(noop.Success && noop.IsNoOp, "Target equal to current pose produces a no-op plan");
        }

        private static void RelativeAndHomeTargetTests(TestRunner t)
        {
            ScaraCalibration cal = new ScaraCalibration();
            ScaraJointPose j;
            ScaraCartesianPose zeroPose = ScaraKinematics.ForwardFromCounts(cal, cal.ZeroCounts, out j);

            CartesianTarget plusX = CartesianMotionPlanner.BuildRelativeStepTarget(zeroPose, CartesianStepAxis.XPos, 10.0, 5.0, false);
            t.Near(plusX.XMm, 405.0, 1e-6, "+X adds stepMm");
            t.Check(!plusX.KeepWorldR, "+X preserves keepWorldR=false");

            CartesianTarget plusR = CartesianMotionPlanner.BuildRelativeStepTarget(zeroPose, CartesianStepAxis.RPos, 10.0, 5.0, false);
            t.Near(plusR.RDeg, 5.0, 1e-6, "+R adds stepDeg");
            t.Check(plusR.KeepWorldR, "+R forces keepWorldR=true");

            CartesianTarget home = CartesianMotionPlanner.ComputeHomeTarget(cal);
            t.Near(home.XMm, cal.MaxReachMm, 1e-6, "Home X = L1 + L2");
            t.Near(home.YMm, 0.0, 1e-6, "Home Y = 0");
            t.Near(home.ZMm, cal.ZeroHeightMm, 1e-6, "Home Z = H0");
            t.Check(home.KeepWorldR, "Home enables KeepWorldR");
        }

        private static async Task ExecutionSequenceAndAbortTests(TestRunner t)
        {
            ScaraCalibration cal = new ScaraCalibration();
            Sc4iStatusReply atZero = BuildReadyStatus(cal.ZeroCounts);

            CartesianTarget raiseTarget = new CartesianTarget
            {
                XMm = 300.0,
                YMm = 80.0,
                ZMm = 140.0,
                RDeg = 0.0,
                KeepWorldR = false,
                Label = "Test raise"
            };
            CartesianMovePlan plan = CartesianMotionPlanner.PlanMove(cal, atZero, raiseTarget, ScaraElbowMode.AutoClosest);
            t.Check(plan.Success && plan.Steps.Length == 3, "keepWorldR=false skips J4 and plans 3 axes (Z, J1, J3)");

            // 1. Happy path: verify exact execution order [J2, J1, J3] and progress callbacks
            FakeMotionClient fake = new FakeMotionClient(cal.ZeroCounts);
            int progressCount = 0;
            Sc4iStatusReply finalStatus = await CartesianMotionPlanner.ExecutePlanAsync(
                fake, plan, 0.35, 1.0, "测试抬升", p => progressCount++, 0, 5000, CancellationToken.None).ConfigureAwait(false);

            t.Check(fake.MovedAxes.Count == 3
                && fake.MovedAxes[0] == cal.J2Slave
                && fake.MovedAxes[1] == cal.J1Slave
                && fake.MovedAxes[2] == cal.J3Slave,
                "Executed axes in exact safe order: Z -> J1 -> J3");
            t.Check(finalStatus != null && progressCount >= 6, "Received progress updates and final status");

            // 2. Mid-sequence E-Stop aborts remaining axes immediately
            FakeMotionClient estopFake = new FakeMotionClient(cal.ZeroCounts) { TriggerEstopOnHeartbeatNumber = 1 };
            t.Throws<InvalidOperationException>(() =>
                CartesianMotionPlanner.ExecutePlanAsync(
                    estopFake, plan, 0.35, 1.0, "急停中断", null, 0, 5000, CancellationToken.None).GetAwaiter().GetResult(),
                "Mid-sequence E-Stop aborts execution");
            t.Check(estopFake.MovedAxes.Count == 1, "Only first axis was commanded before E-Stop halted remaining steps");
        }

        private sealed class FakeMotionClient : ISc4iMotionClient
        {
            private readonly int[] counts;
            private int heartbeatCalls;
            public readonly List<int> MovedAxes = new List<int>();
            public int TriggerEstopOnHeartbeatNumber { get; set; }

            public bool IsConnected { get { return true; } }
            public double CountsPerRevolution { get { return Sc4iMachineConfig.DefaultCountsPerRevolution; } }

            public FakeMotionClient(int[] initialCounts)
            {
                counts = (int[])initialCounts.Clone();
            }

            public Task<Sc4iStatusReply> MoveAxisToTargetRevAsync(
                int jointIndex, double targetRev, double velRevPerSec, double accRevPerSec2, CancellationToken cancellationToken)
            {
                MovedAxes.Add(jointIndex);
                counts[jointIndex] = (int)Math.Round(targetRev * CountsPerRevolution);
                return Task.FromResult(BuildReadyStatus(counts, motionState: 1));
            }

            public Task<Sc4iStatusReply> SendHeartbeatAsync(CancellationToken cancellationToken)
            {
                heartbeatCalls++;
                byte estop = (TriggerEstopOnHeartbeatNumber > 0 && heartbeatCalls == TriggerEstopOnHeartbeatNumber) ? (byte)1 : (byte)0;
                return Task.FromResult(BuildReadyStatus(counts, motionState: 0, estop: estop));
            }
        }
    }
}
