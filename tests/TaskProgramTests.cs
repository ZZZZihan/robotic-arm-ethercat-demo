using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ScaraArm.Core.Kinematics;
using ScaraArm.Core.Motion;
using ScaraArm.Core.Protocol;
using ScaraArm.Core.Tasks;
using ScaraArm.Core.Transport;

namespace ScaraArm.Tests
{
    internal static class TaskProgramTests
    {
        public static int Run()
        {
            TestRunner t = new TestRunner("TaskProgramTests");
            PersistenceRoundTripAndErrorTests(t);
            SafeTransitSegmentTests(t);
            ValidationTests(t);
            CyclicExecutionAndSafetyTests(t).GetAwaiter().GetResult();
            return t.Count;
        }

        private static Sc4iStatusReply BuildReadyStatus(
            int[] counts,
            uint enMask = 0x0Fu,
            uint authMask = 0x0Fu,
            byte motionState = 0,
            byte estop = 0,
            uint latched = 0,
            bool pdoOk = true)
        {
            byte[] raw = Sc4iTestFrames.BuildStatusReply(
                Sc4iSubcmd.Heartbeat, motionState, estop, 4, 1u, 1ul, 1ul,
                0u, latched, true, true, enMask, authMask, 0u, 255u, pdoOk, 0u,
                100u, 100u, 52u, counts,
                new ushort[] { 0x1627, 0x1627, 0x1627, 0x1627 },
                new ushort[] { 0, 0, 0, 0 });
            return Sc4iProtocol.ParseF2Reply(raw);
        }

        private static TeachProgram BuildSamplePickAndPlaceProgram()
        {
            TeachProgram program = new TeachProgram
            {
                Name = "双工位循环取放",
                VelocityRevPerSec = 0.40,
                AccelerationRevPerSec2 = 1.2,
                UseSafeTransitZ = true,
                SafeTransitZMm = 150.0,
                RepeatCycles = 2,
                ReturnHomeOnComplete = true
            };
            program.Waypoints.Add(new TeachWaypoint
            {
                Name = "P1 取料点",
                Role = WaypointRole.Pick,
                XMm = 300.0,
                YMm = 80.0,
                ZMm = 60.0,
                RDeg = 10.0,
                KeepWorldR = true,
                ElbowPreference = ScaraElbowMode.AutoClosest,
                DwellMs = 300,
                Enabled = true
            });
            program.Waypoints.Add(new TeachWaypoint
            {
                Name = "P_SKIP 临时跳过点",
                Role = WaypointRole.Waypoint,
                XMm = 320.0,
                YMm = 0.0,
                ZMm = 120.0,
                RDeg = 0.0,
                KeepWorldR = false,
                ElbowPreference = ScaraElbowMode.ElbowRight,
                DwellMs = 100,
                Enabled = false
            });
            program.Waypoints.Add(new TeachWaypoint
            {
                Name = "P2 放料点",
                Role = WaypointRole.Place,
                XMm = 280.0,
                YMm = -90.0,
                ZMm = 65.0,
                RDeg = -15.0,
                KeepWorldR = true,
                ElbowPreference = ScaraElbowMode.AutoClosest,
                DwellMs = 400,
                Enabled = true
            });
            return program;
        }

        private static void PersistenceRoundTripAndErrorTests(TestRunner t)
        {
            TeachProgram original = BuildSamplePickAndPlaceProgram();
            string ini = TeachProgramStore.Serialize(original);
            TeachProgram parsed = TeachProgramStore.Parse(ini);

            t.Check(parsed.Name == original.Name, "Program name preserved");
            t.Near(parsed.VelocityRevPerSec, 0.40, 1e-9, "Velocity preserved");
            t.Near(parsed.AccelerationRevPerSec2, 1.2, 1e-9, "Acceleration preserved");
            t.Check(parsed.UseSafeTransitZ == true, "UseSafeTransitZ preserved");
            t.Near(parsed.SafeTransitZMm, 150.0, 1e-9, "SafeTransitZMm preserved");
            t.Check(parsed.RepeatCycles == 2 && parsed.ReturnHomeOnComplete, "Cycles and ReturnHome preserved");
            t.Check(parsed.Waypoints.Count == 3, "All 3 waypoints parsed in order");
            t.Check(parsed.Waypoints[0].Role == WaypointRole.Pick
                && parsed.Waypoints[1].Enabled == false
                && parsed.Waypoints[1].ElbowPreference == ScaraElbowMode.ElbowRight
                && parsed.Waypoints[2].Role == WaypointRole.Place
                && parsed.Waypoints[2].DwellMs == 400,
                "Waypoint fields, roles, elbow mode, enabled flag, and dwell preserved");
            t.Near(parsed.Waypoints[2].YMm, -90.0, 1e-9, "Negative Y coordinate preserved");

            // File save / load + LoadOrDefault on missing path
            string tempDir = Path.Combine(Path.GetTempPath(), "scara-task-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                string missing = Path.Combine(tempDir, "missing.ini");
                TeachProgram def = TeachProgramStore.LoadOrDefault(missing);
                t.Check(def != null && def.Waypoints.Count == 0, "LoadOrDefault returns clean default when file missing");

                string filePath = Path.Combine(tempDir, "nested", "pick-place.ini");
                TeachProgramStore.SaveToFile(original, filePath);
                TeachProgram reloaded = TeachProgramStore.LoadFromFile(filePath);
                t.Check(reloaded.Waypoints.Count == 3 && reloaded.Waypoints[0].Name == "P1 取料点",
                    "SaveToFile creates directories and LoadFromFile restores UTF-8 Chinese names");
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }

            // Sanitize newlines in names so INI format cannot be corrupted
            TeachProgram dirty = new TeachProgram { Name = "行1\r\n行2" };
            dirty.Waypoints.Add(new TeachWaypoint { Name = "P1\nInjected=1" });
            TeachProgram sanitized = TeachProgramStore.Parse(TeachProgramStore.Serialize(dirty));
            t.Check(sanitized.Name.IndexOf('\n') < 0 && sanitized.Waypoints[0].Name.IndexOf('\n') < 0,
                "Newlines in program/waypoint names are sanitized");

            // Reject invalid format version & malformed fields
            t.Throws<FormatException>(() => TeachProgramStore.Parse("FormatVersion=99\n"), "Unsupported FormatVersion rejected");
            t.Throws<FormatException>(() => TeachProgramStore.Parse("[Waypoint.0]\nXMm=notanumber\n"), "Malformed double rejected");
            t.Throws<InvalidOperationException>(() => TeachProgramStore.Parse("VelocityRevPerSec=9.9\n"), "Excessive velocity rejected");
            t.Throws<InvalidOperationException>(() => TeachProgramStore.Parse("[Waypoint.0]\nName=P1\nDwellMs=-5\n"), "Negative dwell rejected");
        }

        private static void SafeTransitSegmentTests(TestRunner t)
        {
            ScaraCalibration cal = new ScaraCalibration();
            ScaraJointPose j;
            ScaraCartesianPose atZero = ScaraKinematics.ForwardFromCounts(cal, cal.ZeroCounts, out j); // X=395, Y=0, Z=108

            // Horizontal move with start Z (108) and target Z (60) both below safeZ (150) -> 2 segments
            CartesianTarget lowTarget = new CartesianTarget { XMm = 300, YMm = 80, ZMm = 60, RDeg = 0, KeepWorldR = true, Label = "P1" };
            CartesianTarget[] gateSegs = TaskProgramRunner.BuildTransitSegments(atZero, lowTarget, true, 150.0);
            t.Check(gateSegs.Length == 2, "Low-to-low horizontal move splits into 2 archway segments");
            t.Near(gateSegs[0].ZMm, 150.0, 1e-6, "First segment rises to safeZ and moves XY");
            t.Near(gateSegs[0].XMm, 300.0, 1e-6, "First segment carries target X");
            t.Near(gateSegs[1].ZMm, 60.0, 1e-6, "Second segment lowers to target Z");

            // Target Z already >= safeZ -> single segment
            CartesianTarget highTarget = new CartesianTarget { XMm = 300, YMm = 80, ZMm = 160, RDeg = 0, KeepWorldR = true, Label = "High" };
            CartesianTarget[] highSegs = TaskProgramRunner.BuildTransitSegments(atZero, highTarget, true, 150.0);
            t.Check(highSegs.Length == 1, "Target Z >= safeZ uses single segment");

            // Pure vertical move (same XY & R) -> single segment
            CartesianTarget verticalTarget = new CartesianTarget { XMm = atZero.XMm, YMm = atZero.YMm, ZMm = 60, RDeg = atZero.RDeg, KeepWorldR = true, Label = "Down" };
            CartesianTarget[] vertSegs = TaskProgramRunner.BuildTransitSegments(atZero, verticalTarget, true, 150.0);
            t.Check(vertSegs.Length == 1, "Pure vertical move does not insert transit pop-up");

            // Safe transit disabled -> single segment
            CartesianTarget[] directSegs = TaskProgramRunner.BuildTransitSegments(atZero, lowTarget, false, 150.0);
            t.Check(directSegs.Length == 1, "UseSafeTransitZ=false uses single segment");
        }

        private static void ValidationTests(TestRunner t)
        {
            ScaraCalibration cal = new ScaraCalibration();
            Sc4iStatusReply ready = BuildReadyStatus(cal.ZeroCounts);

            // Empty program rejected
            TaskValidationResult emptyRes = TaskProgramRunner.ValidateProgram(cal, ready, new TeachProgram());
            t.Check(!emptyRes.Success, "Empty waypoint list fails validation");

            // Safe transit Z above ZMax rejected
            TeachProgram badZ = BuildSamplePickAndPlaceProgram();
            badZ.SafeTransitZMm = 260.0;
            TaskValidationResult badZRes = TaskProgramRunner.ValidateProgram(cal, ready, badZ);
            t.Check(!badZRes.Success && badZRes.Error.IndexOf("软限位", StringComparison.Ordinal) >= 0,
                "Out-of-range safe transit Z fails validation");

            // Unreachable waypoint fails with exact index
            TeachProgram unreachable = BuildSamplePickAndPlaceProgram();
            unreachable.Waypoints[2].XMm = 600.0;
            TaskValidationResult unreachRes = TaskProgramRunner.ValidateProgram(cal, ready, unreachable);
            t.Check(!unreachRes.Success && unreachRes.FailedWaypointIndex == 2,
                "Unreachable waypoint fails validation with exact waypoint index");

            // Disabled axis fails before motion
            Sc4iStatusReply missingAxis3 = BuildReadyStatus(cal.ZeroCounts, enMask: 0x0Bu, authMask: 0x0Bu);
            TaskValidationResult axisRes = TaskProgramRunner.ValidateProgram(cal, missingAxis3, BuildSamplePickAndPlaceProgram());
            t.Check(!axisRes.Success, "Disabled axis fails pre-flight validation");

            // Latched E-Stop fails before motion
            Sc4iStatusReply estopped = BuildReadyStatus(cal.ZeroCounts, estop: 1);
            TaskValidationResult estopRes = TaskProgramRunner.ValidateProgram(cal, estopped, BuildSamplePickAndPlaceProgram());
            t.Check(!estopRes.Success, "E-Stop status fails pre-flight validation");

            // Valid pick-and-place program passes
            TaskValidationResult okRes = TaskProgramRunner.ValidateProgram(cal, ready, BuildSamplePickAndPlaceProgram());
            t.Check(okRes.Success && okRes.ActiveWaypointCount == 2 && okRes.PlannedSegmentCountPerCycle == 4,
                "Valid 2-point archway pick-and-place program passes validation with 4 segments/cycle");
        }

        private static async Task CyclicExecutionAndSafetyTests(TestRunner t)
        {
            ScaraCalibration cal = new ScaraCalibration();
            Sc4iStatusReply atZero = BuildReadyStatus(cal.ZeroCounts);
            TeachProgram program = BuildSamplePickAndPlaceProgram();

            // 1. Full 2-cycle pick-and-place run with safe Z-gate + disabled skip + return home
            FakeTaskMotionClient fake = new FakeTaskMotionClient(cal.ZeroCounts);
            List<TaskRunPhase> seenPhases = new List<TaskRunPhase>();
            List<int> visitedWaypointIndices = new List<int>();
            TaskRunResult result = await TaskProgramRunner.RunAsync(
                fake,
                cal,
                atZero,
                program,
                p =>
                {
                    if (seenPhases.Count == 0 || seenPhases[seenPhases.Count - 1] != p.Phase)
                        seenPhases.Add(p.Phase);
                    if (p.Phase == TaskRunPhase.Dwelling && p.Message != null)
                        visitedWaypointIndices.Add(p.WaypointIndex);
                },
                0,
                5000,
                CancellationToken.None).ConfigureAwait(false);

            t.Check(result.CompletedCycles == 2 && result.ExecutedWaypoints == 4,
                "2 cycles x 2 enabled waypoints = 4 executed waypoints (disabled skipped)");
            t.Check(visitedWaypointIndices.Count == 4
                && visitedWaypointIndices[0] == 0
                && visitedWaypointIndices[1] == 2
                && visitedWaypointIndices[2] == 0
                && visitedWaypointIndices[3] == 2,
                "Visited exact enabled waypoint sequence [0, 2, 0, 2]");
            t.Check(seenPhases.Contains(TaskRunPhase.MovingTransit)
                && seenPhases.Contains(TaskRunPhase.MovingToWaypoint)
                && seenPhases.Contains(TaskRunPhase.Dwelling)
                && seenPhases.Contains(TaskRunPhase.ReturningHome)
                && seenPhases.Contains(TaskRunPhase.Completed),
                "All expected task phases reported");

            // Verify first axis commanded is Z (J2Slave) rising to SafeTransitZMm (150mm) BEFORE any horizontal J1/J3 move
            t.Check(fake.MoveLog.Count > 0 && fake.MoveLog[0].Axis == cal.J2Slave,
                "First axis move raises Z to safe transit height before horizontal motion");
            double firstZMm = cal.ZeroHeightMm + cal.J2Sign * (fake.MoveLog[0].TargetRev * cal.CountsPerRev - cal.ZeroCounts[cal.J2Slave]) / cal.J2CountsPerMm;
            t.Near(firstZMm, 150.0, 0.05, "First Z lift reaches SafeTransitZMm = 150.0 mm");

            // Verify final pose returned to safe home (395, 0, 108, 0)
            ScaraJointPose finalJoints;
            ScaraCartesianPose finalPose = ScaraKinematics.ForwardFromCounts(cal, fake.CurrentCounts, out finalJoints);
            t.Near(finalPose.XMm, cal.MaxReachMm, 0.1, "Returned to home X");
            t.Near(finalPose.YMm, 0.0, 0.1, "Returned to home Y");
            t.Near(finalPose.ZMm, cal.ZeroHeightMm, 0.1, "Returned to home Z");

            // 2. Real timed dwell sends multiple heartbeats so control card never times out
            TeachProgram dwellProgram = new TeachProgram
            {
                RepeatCycles = 1,
                UseSafeTransitZ = false
            };
            dwellProgram.Waypoints.Add(new TeachWaypoint
            {
                Name = "原地停留",
                XMm = cal.MaxReachMm,
                YMm = 0.0,
                ZMm = cal.ZeroHeightMm,
                RDeg = 0.0,
                KeepWorldR = true,
                DwellMs = 65,
                Enabled = true
            });
            FakeTaskMotionClient dwellFake = new FakeTaskMotionClient(cal.ZeroCounts);
            await TaskProgramRunner.RunAsync(dwellFake, cal, atZero, dwellProgram, null, 20, 5000, CancellationToken.None).ConfigureAwait(false);
            t.Check(dwellFake.HeartbeatCalls >= 3, "Timed dwell polls SendHeartbeatAsync periodically (" + dwellFake.HeartbeatCalls + " beats)");

            // 3. Continuous mode (RepeatCycles == 0) stops cleanly on cancellation
            TeachProgram continuousProgram = BuildSamplePickAndPlaceProgram();
            continuousProgram.RepeatCycles = 0;
            continuousProgram.ReturnHomeOnComplete = false;
            FakeTaskMotionClient contFake = new FakeTaskMotionClient(cal.ZeroCounts);
            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                int dwellHits = 0;
                bool cancelled = false;
                try
                {
                    await TaskProgramRunner.RunAsync(
                        contFake,
                        cal,
                        atZero,
                        continuousProgram,
                        p =>
                        {
                            if (p.Phase == TaskRunPhase.Dwelling && p.Message != null)
                            {
                                dwellHits++;
                                if (dwellHits >= 5) cts.Cancel(); // Stop mid-3rd cycle
                            }
                        },
                        0,
                        5000,
                        cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                t.Check(cancelled && dwellHits == 5, "Continuous cycle mode runs beyond 2 cycles and halts immediately on cancellation");
            }

            // 4. E-Stop during dwell immediately aborts remaining waypoints
            TeachProgram estopDwellProg = BuildSamplePickAndPlaceProgram();
            estopDwellProg.RepeatCycles = 1;
            estopDwellProg.ReturnHomeOnComplete = false;
            FakeTaskMotionClient estopDwellFake = new FakeTaskMotionClient(cal.ZeroCounts)
            {
                TriggerEstopOnHeartbeatAfterDwell = true
            };
            bool estopAborted = false;
            int completedPointsBeforeEstop = 0;
            try
            {
                await TaskProgramRunner.RunAsync(
                    estopDwellFake,
                    cal,
                    atZero,
                    estopDwellProg,
                    p =>
                    {
                        if (p.Phase == TaskRunPhase.Dwelling && p.Message != null)
                        {
                            completedPointsBeforeEstop++;
                            estopDwellFake.ArmEstopNextHeartbeat = true;
                        }
                    },
                    0,
                    5000,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                estopAborted = true;
            }
            t.Check(estopAborted && completedPointsBeforeEstop == 1,
                "E-Stop detected during first waypoint dwell aborts immediately before moving to second waypoint");
        }

        private struct AxisMoveRecord
        {
            public int Axis;
            public double TargetRev;
        }

        private sealed class FakeTaskMotionClient : ISc4iMotionClient
        {
            private readonly int[] counts;
            public readonly List<AxisMoveRecord> MoveLog = new List<AxisMoveRecord>();
            public int HeartbeatCalls { get; private set; }
            public bool TriggerEstopOnHeartbeatAfterDwell { get; set; }
            public bool ArmEstopNextHeartbeat { get; set; }

            public bool IsConnected { get { return true; } }
            public double CountsPerRevolution { get { return Sc4iMachineConfig.DefaultCountsPerRevolution; } }
            public int[] CurrentCounts { get { return (int[])counts.Clone(); } }

            public FakeTaskMotionClient(int[] initialCounts)
            {
                counts = (int[])initialCounts.Clone();
            }

            public Task<Sc4iStatusReply> MoveAxisToTargetRevAsync(
                int jointIndex, double targetRev, double velRevPerSec, double accRevPerSec2, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MoveLog.Add(new AxisMoveRecord { Axis = jointIndex, TargetRev = targetRev });
                counts[jointIndex] = (int)Math.Round(targetRev * CountsPerRevolution);
                return Task.FromResult(BuildReadyStatus(counts, motionState: 1));
            }

            public Task<Sc4iStatusReply> SendHeartbeatAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HeartbeatCalls++;
                byte estop = (TriggerEstopOnHeartbeatAfterDwell && ArmEstopNextHeartbeat) ? (byte)1 : (byte)0;
                return Task.FromResult(BuildReadyStatus(counts, motionState: 0, estop: estop));
            }
        }
    }
}
