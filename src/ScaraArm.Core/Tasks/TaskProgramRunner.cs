using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ScaraArm.Core.Kinematics;
using ScaraArm.Core.Motion;
using ScaraArm.Core.Protocol;
using ScaraArm.Core.Transport;

namespace ScaraArm.Core.Tasks
{
    public enum TaskRunPhase
    {
        Validating = 0,
        MovingTransit = 1,
        MovingToWaypoint = 2,
        Dwelling = 3,
        ReturningHome = 4,
        Completed = 5
    }

    public sealed class TaskValidationResult
    {
        public bool Success { get; internal set; }
        public string Error { get; internal set; }
        public int FailedWaypointIndex { get; internal set; }
        public int ActiveWaypointCount { get; internal set; }
        public int PlannedSegmentCountPerCycle { get; internal set; }
    }

    public sealed class TaskRunProgress
    {
        public TaskRunPhase Phase { get; internal set; }
        public int CycleIndex { get; internal set; }
        public int TotalCycles { get; internal set; }
        public int WaypointIndex { get; internal set; }
        public int ActiveStepNumber { get; internal set; }
        public int ActiveStepTotal { get; internal set; }
        public string WaypointName { get; internal set; }
        public string Message { get; internal set; }
        public Sc4iStatusReply LatestStatus { get; internal set; }
    }

    public sealed class TaskRunResult
    {
        public int CompletedCycles { get; internal set; }
        public int ExecutedWaypoints { get; internal set; }
        public Sc4iStatusReply FinalStatus { get; internal set; }
    }

    /// <summary>
    /// Pre-flight validator and cyclic pick-and-place task orchestrator built strictly on top of CartesianMotionPlanner.
    /// Supports safe Z-gate (archway) transit between waypoints and heartbeat-preserving dwell intervals.
    /// </summary>
    public static class TaskProgramRunner
    {
        public const double HorizontalTransitThresholdMm = 0.2;
        public const double RotationTransitThresholdDeg = 0.2;

        /// <summary>
        /// Splits a target move into a 2-segment safe archway move (raise Z + XY/R at safeZ, then lower Z)
        /// when horizontal/R motion is required and both start Z and target Z sit below SafeTransitZMm.
        /// </summary>
        public static CartesianTarget[] BuildTransitSegments(
            ScaraCartesianPose currentPose,
            CartesianTarget target,
            bool useSafeTransitZ,
            double safeTransitZMm)
        {
            if (currentPose == null) throw new ArgumentNullException("currentPose");
            if (target == null) throw new ArgumentNullException("target");

            double dx = target.XMm - currentPose.XMm;
            double dy = target.YMm - currentPose.YMm;
            double dr = target.KeepWorldR ? Math.Abs(target.RDeg - currentPose.RDeg) : 0.0;
            bool needsHorizontalOrR = (dx * dx + dy * dy) > (HorizontalTransitThresholdMm * HorizontalTransitThresholdMm)
                || dr > RotationTransitThresholdDeg;

            bool needsGate = useSafeTransitZ
                && needsHorizontalOrR
                && safeTransitZMm > currentPose.ZMm + CartesianMotionPlanner.RaiseZThresholdMm
                && safeTransitZMm > target.ZMm + CartesianMotionPlanner.RaiseZThresholdMm;

            if (!needsGate)
                return new CartesianTarget[] { target };

            string baseLabel = String.IsNullOrWhiteSpace(target.Label) ? "目标点位" : target.Label;
            CartesianTarget transit = new CartesianTarget
            {
                XMm = target.XMm,
                YMm = target.YMm,
                ZMm = safeTransitZMm,
                RDeg = target.RDeg,
                KeepWorldR = target.KeepWorldR,
                Label = baseLabel + "（提Z平移）"
            };
            CartesianTarget descend = new CartesianTarget
            {
                XMm = target.XMm,
                YMm = target.YMm,
                ZMm = target.ZMm,
                RDeg = target.RDeg,
                KeepWorldR = target.KeepWorldR,
                Label = baseLabel + "（下降到位）"
            };
            return new CartesianTarget[] { transit, descend };
        }

        /// <summary>
        /// Validates the entire teach program (parameters, live safety state, IK reachability, joint soft limits,
        /// single-step revolution limits, wrap-around transition, and optional return-home) BEFORE sending any motion command.
        /// </summary>
        public static TaskValidationResult ValidateProgram(
            ScaraCalibration calibration,
            Sc4iStatusReply currentStatus,
            TeachProgram program)
        {
            if (calibration == null) throw new ArgumentNullException("calibration");
            if (program == null) throw new ArgumentNullException("program");

            try
            {
                calibration.Validate();
                program.ValidateParameters();
            }
            catch (Exception ex)
            {
                return FailValidation(ex.Message, -1);
            }

            if (currentStatus == null)
                return FailValidation("无法获取控制卡实时状态，请先连接并进入 OP。", -1);
            if (currentStatus.LatchedFault != 0 || currentStatus.EstopFlag != 0 || !currentStatus.PdoOk)
                return FailValidation("控制卡存在急停、故障锁存或 PDO 异常，禁止启动任务。", -1);

            int[] initialCounts;
            if (!currentStatus.TryExtractLiveCounts(out initialCounts))
                return FailValidation("无法读取有效四轴位置，禁止启动任务。", -1);

            if (program.UseSafeTransitZ
                && (program.SafeTransitZMm < calibration.ZMinMm || program.SafeTransitZMm > calibration.ZMaxMm))
            {
                return FailValidation(String.Format(
                    CultureInfo.InvariantCulture,
                    "安全提 Z 高度 {0:0.1} mm 超出 Z 轴软限位 [{1:0.1}, {2:0.1}] mm。",
                    program.SafeTransitZMm, calibration.ZMinMm, calibration.ZMaxMm), -1);
            }

            List<int> activeIndices = CollectEnabledWaypointIndices(program);
            if (activeIndices.Count == 0)
                return FailValidation("点位列表中没有已启用的点位，请至少添加并启用一个点位。", -1);

            int[] simCounts = (int[])initialCounts.Clone();
            int totalSegments = 0;

            for (int i = 0; i < activeIndices.Count; i++)
            {
                int wpIdx = activeIndices[i];
                TeachWaypoint wp = program.Waypoints[wpIdx];
                int segCount;
                string segError;
                if (!SimulateMoveToTarget(
                    calibration,
                    currentStatus,
                    simCounts,
                    wp.ToCartesianTarget(),
                    wp.ElbowPreference,
                    program.UseSafeTransitZ,
                    program.SafeTransitZMm,
                    out segCount,
                    out segError))
                {
                    return FailValidation("点位 #" + (wpIdx + 1) + "（" + wp.DisplayName + "）校验失败：" + segError, wpIdx);
                }
                totalSegments += segCount;
            }

            // If cycling more than once (or continuously), also pre-check wrap-around transition from last waypoint back to first.
            if ((program.RepeatCycles == 0 || program.RepeatCycles > 1) && activeIndices.Count > 1)
            {
                int firstIdx = activeIndices[0];
                TeachWaypoint firstWp = program.Waypoints[firstIdx];
                int wrapSegs;
                string wrapError;
                if (!SimulateMoveToTarget(
                    calibration,
                    currentStatus,
                    simCounts,
                    firstWp.ToCartesianTarget(),
                    firstWp.ElbowPreference,
                    program.UseSafeTransitZ,
                    program.SafeTransitZMm,
                    out wrapSegs,
                    out wrapError))
                {
                    return FailValidation("循环衔接至点位 #" + (firstIdx + 1) + "（" + firstWp.DisplayName + "）校验失败：" + wrapError, firstIdx);
                }
            }

            if (program.ReturnHomeOnComplete && program.RepeatCycles > 0)
            {
                CartesianTarget home = CartesianMotionPlanner.ComputeHomeTarget(calibration);
                int homeSegs;
                string homeError;
                if (!SimulateMoveToTarget(
                    calibration,
                    currentStatus,
                    simCounts,
                    home,
                    ScaraElbowMode.AutoClosest,
                    program.UseSafeTransitZ,
                    program.SafeTransitZMm,
                    out homeSegs,
                    out homeError))
                {
                    return FailValidation("完成后回安全零点校验失败：" + homeError, -1);
                }
            }

            return new TaskValidationResult
            {
                Success = true,
                FailedWaypointIndex = -1,
                ActiveWaypointCount = activeIndices.Count,
                PlannedSegmentCountPerCycle = totalSegments
            };
        }

        public static Task<TaskRunResult> RunAsync(
            ISc4iMotionClient client,
            ScaraCalibration calibration,
            Sc4iStatusReply initialStatus,
            TeachProgram program,
            Action<TaskRunProgress> onProgress,
            CancellationToken cancellationToken)
        {
            return RunAsync(
                client,
                calibration,
                initialStatus,
                program,
                onProgress,
                CartesianMotionPlanner.DefaultPollIntervalMs,
                CartesianMotionPlanner.DefaultStepTimeoutMs,
                cancellationToken);
        }

        internal static async Task<TaskRunResult> RunAsync(
            ISc4iMotionClient client,
            ScaraCalibration calibration,
            Sc4iStatusReply initialStatus,
            TeachProgram program,
            Action<TaskRunProgress> onProgress,
            int pollIntervalMs,
            int stepTimeoutMs,
            CancellationToken cancellationToken)
        {
            if (client == null) throw new ArgumentNullException("client");
            if (calibration == null) throw new ArgumentNullException("calibration");
            if (program == null) throw new ArgumentNullException("program");

            ReportProgress(onProgress, TaskRunPhase.Validating, 0, program.RepeatCycles, -1, 0, 0, null, "正在预检任务点位与软限位…", initialStatus);

            TaskValidationResult validation = ValidateProgram(calibration, initialStatus, program);
            if (!validation.Success)
                throw new InvalidOperationException(validation.Error);

            List<int> activeIndices = CollectEnabledWaypointIndices(program);
            Sc4iStatusReply latest = initialStatus;
            int completedCycles = 0;
            int executedWaypoints = 0;

            for (int cycle = 1; program.RepeatCycles == 0 || cycle <= program.RepeatCycles; cycle++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (int step = 0; step < activeIndices.Count; step++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int wpIdx = activeIndices[step];
                    TeachWaypoint wp = program.Waypoints[wpIdx];

                    latest = await ExecuteWaypointMoveAsync(
                        client,
                        calibration,
                        latest,
                        wp.ToCartesianTarget(),
                        wp.ElbowPreference,
                        program,
                        cycle,
                        wpIdx,
                        step + 1,
                        activeIndices.Count,
                        wp.DisplayName,
                        onProgress,
                        pollIntervalMs,
                        stepTimeoutMs,
                        cancellationToken).ConfigureAwait(false);

                    if (wp.DwellMs > 0)
                    {
                        latest = await DwellWithHeartbeatAsync(
                            client,
                            latest,
                            wp.DwellMs,
                            cycle,
                            program.RepeatCycles,
                            wpIdx,
                            step + 1,
                            activeIndices.Count,
                            wp.DisplayName,
                            onProgress,
                            pollIntervalMs,
                            cancellationToken).ConfigureAwait(false);
                    }

                    executedWaypoints++;
                }

                completedCycles = cycle;
            }

            if (program.ReturnHomeOnComplete && program.RepeatCycles > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CartesianTarget home = CartesianMotionPlanner.ComputeHomeTarget(calibration);
                latest = await ExecuteWaypointMoveAsync(
                    client,
                    calibration,
                    latest,
                    home,
                    ScaraElbowMode.AutoClosest,
                    program,
                    completedCycles,
                    -1,
                    activeIndices.Count,
                    activeIndices.Count,
                    "安全回零点",
                    onProgress,
                    pollIntervalMs,
                    stepTimeoutMs,
                    cancellationToken,
                    TaskRunPhase.ReturningHome).ConfigureAwait(false);
            }

            string doneMessage = String.Format(
                CultureInfo.InvariantCulture,
                "任务完成：共执行 {0} 轮（{1} 个点位）。",
                completedCycles, executedWaypoints);
            ReportProgress(onProgress, TaskRunPhase.Completed, completedCycles, program.RepeatCycles, -1, activeIndices.Count, activeIndices.Count, null, doneMessage, latest);

            return new TaskRunResult
            {
                CompletedCycles = completedCycles,
                ExecutedWaypoints = executedWaypoints,
                FinalStatus = latest
            };
        }

        private static async Task<Sc4iStatusReply> ExecuteWaypointMoveAsync(
            ISc4iMotionClient client,
            ScaraCalibration calibration,
            Sc4iStatusReply currentStatus,
            CartesianTarget target,
            ScaraElbowMode elbowPreference,
            TeachProgram program,
            int cycle,
            int waypointIndex,
            int activeStepNumber,
            int activeStepTotal,
            string waypointName,
            Action<TaskRunProgress> onProgress,
            int pollIntervalMs,
            int stepTimeoutMs,
            CancellationToken cancellationToken,
            TaskRunPhase? overridePhase = null)
        {
            int[] counts;
            if (currentStatus == null || !currentStatus.TryExtractLiveCounts(out counts))
                throw new InvalidOperationException("无法读取实时四轴位置，任务已中止。");

            ScaraJointPose unusedJoints;
            ScaraCartesianPose currentPose = ScaraKinematics.ForwardFromCounts(calibration, counts, out unusedJoints);
            CartesianTarget[] segments = BuildTransitSegments(currentPose, target, program.UseSafeTransitZ, program.SafeTransitZMm);
            Sc4iStatusReply latest = currentStatus;

            for (int s = 0; s < segments.Length; s++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CartesianTarget seg = segments[s];
                TaskRunPhase phase = overridePhase.HasValue
                    ? overridePhase.Value
                    : (segments.Length > 1 && s == 0 ? TaskRunPhase.MovingTransit : TaskRunPhase.MovingToWaypoint);

                CartesianMovePlan plan = CartesianMotionPlanner.PlanMove(calibration, latest, seg, elbowPreference);
                if (!plan.Success)
                    throw new InvalidOperationException("执行“" + waypointName + "”时规划失败：" + plan.Error);

                string limitErr = CheckPlanStepRevLimits(plan);
                if (limitErr != null)
                    throw new InvalidOperationException("执行“" + waypointName + "”时超限：" + limitErr);

                if (plan.IsNoOp)
                    continue;

                string prefix = FormatCyclePrefix(cycle, program.RepeatCycles, activeStepNumber, activeStepTotal, waypointName);
                string label = prefix + " · " + (seg.Label ?? waypointName);

                Sc4iStatusReply segResult = await CartesianMotionPlanner.ExecutePlanAsync(
                    client,
                    plan,
                    program.VelocityRevPerSec,
                    program.AccelerationRevPerSec2,
                    label,
                    stepProgress =>
                    {
                        if (stepProgress == null) return;
                        if (stepProgress.LatestStatus != null) latest = stepProgress.LatestStatus;
                        ReportProgress(
                            onProgress,
                            phase,
                            cycle,
                            program.RepeatCycles,
                            waypointIndex,
                            activeStepNumber,
                            activeStepTotal,
                            waypointName,
                            stepProgress.Message,
                            latest);
                    },
                    pollIntervalMs,
                    stepTimeoutMs,
                    cancellationToken).ConfigureAwait(false);

                if (segResult != null) latest = segResult;
                if (!client.IsConnected)
                    throw new InvalidOperationException("控制卡连接已断开，任务已中止。");
            }

            return latest;
        }

        private static async Task<Sc4iStatusReply> DwellWithHeartbeatAsync(
            ISc4iMotionClient client,
            Sc4iStatusReply currentStatus,
            int dwellMs,
            int cycle,
            int totalCycles,
            int waypointIndex,
            int activeStepNumber,
            int activeStepTotal,
            string waypointName,
            Action<TaskRunProgress> onProgress,
            int pollIntervalMs,
            CancellationToken cancellationToken)
        {
            Sc4iStatusReply latest = currentStatus;
            string prefix = FormatCyclePrefix(cycle, totalCycles, activeStepNumber, activeStepTotal, waypointName);
            string dwellMsg = prefix + " · 到位停留 " + dwellMs + " ms…";

            ReportProgress(onProgress, TaskRunPhase.Dwelling, cycle, totalCycles, waypointIndex, activeStepNumber, activeStepTotal, waypointName, dwellMsg, latest);

            if (pollIntervalMs <= 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                latest = await client.SendHeartbeatAsync(cancellationToken).ConfigureAwait(false);
                VerifyMotionSafe(client, latest);
                ReportProgress(onProgress, TaskRunPhase.Dwelling, cycle, totalCycles, waypointIndex, activeStepNumber, activeStepTotal, waypointName, null, latest);
                return latest;
            }

            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long remaining = dwellMs - sw.ElapsedMilliseconds;
                if (remaining <= 0) break;

                int slice = (int)Math.Min(remaining, pollIntervalMs);
                if (slice > 0)
                    await Task.Delay(slice, cancellationToken).ConfigureAwait(false);

                latest = await client.SendHeartbeatAsync(cancellationToken).ConfigureAwait(false);
                VerifyMotionSafe(client, latest);
                ReportProgress(onProgress, TaskRunPhase.Dwelling, cycle, totalCycles, waypointIndex, activeStepNumber, activeStepTotal, waypointName, null, latest);
            }

            return latest;
        }

        private static void VerifyMotionSafe(ISc4iMotionClient client, Sc4iStatusReply status)
        {
            if (!client.IsConnected)
                throw new InvalidOperationException("控制卡连接已断开，任务已中止。");
            if (status == null || status.LatchedFault != 0 || status.EstopFlag != 0 || !status.PdoOk)
                throw new InvalidOperationException("任务停留期间检测到急停、故障锁存或 PDO 异常，已中止任务。");
        }

        private static bool SimulateMoveToTarget(
            ScaraCalibration calibration,
            Sc4iStatusReply templateStatus,
            int[] simCounts,
            CartesianTarget target,
            ScaraElbowMode elbowPreference,
            bool useSafeTransitZ,
            double safeTransitZMm,
            out int segmentCount,
            out string error)
        {
            segmentCount = 0;
            error = null;

            ScaraJointPose unused;
            ScaraCartesianPose currentPose = ScaraKinematics.ForwardFromCounts(calibration, simCounts, out unused);
            CartesianTarget[] segments = BuildTransitSegments(currentPose, target, useSafeTransitZ, safeTransitZMm);

            for (int s = 0; s < segments.Length; s++)
            {
                Sc4iStatusReply simStatus = BuildSimulatedStatus(templateStatus, simCounts);
                CartesianMovePlan plan = CartesianMotionPlanner.PlanMove(calibration, simStatus, segments[s], elbowPreference);
                if (!plan.Success)
                {
                    error = plan.Error;
                    return false;
                }

                string limitError = CheckPlanStepRevLimits(plan);
                if (limitError != null)
                {
                    error = limitError;
                    return false;
                }

                if (plan.IkResult != null && plan.IkResult.TargetSlaveRevs != null)
                {
                    for (int axis = 0; axis < 4; axis++)
                        simCounts[axis] = (int)Math.Round(plan.IkResult.TargetSlaveRevs[axis] * calibration.CountsPerRev);
                }
                segmentCount++;
            }

            return true;
        }

        private static string CheckPlanStepRevLimits(CartesianMovePlan plan)
        {
            if (plan == null || plan.Steps == null) return null;
            for (int i = 0; i < plan.Steps.Length; i++)
            {
                CartesianAxisStep step = plan.Steps[i];
                if (Math.Abs(step.DeltaRev) > Sc4iClient.MaxSafeStepRev)
                {
                    return String.Format(
                        CultureInfo.InvariantCulture,
                        "轴 {0} 单步位移 {1:0.###} 圈超过安全上限 ±{2:0.#} 圈，请在两点间增加过渡点位。",
                        step.SlaveIndex + 1,
                        step.DeltaRev,
                        Sc4iClient.MaxSafeStepRev);
                }
            }
            return null;
        }

        private static Sc4iStatusReply BuildSimulatedStatus(Sc4iStatusReply template, int[] simCounts)
        {
            Sc4iSlaveTelemetry[] slaves = new Sc4iSlaveTelemetry[4];
            for (int i = 0; i < 4; i++)
            {
                Sc4iSlaveTelemetry src = (template.Slaves != null && i < template.Slaves.Length) ? template.Slaves[i] : null;
                slaves[i] = new Sc4iSlaveTelemetry
                {
                    SlaveIndex = i,
                    ActualPositionCounts = simCounts[i],
                    CommandPositionCounts = simCounts[i],
                    StatusWord = src != null ? src.StatusWord : (ushort)0x1627,
                    ValidMask = src != null ? src.ValidMask : 1u,
                    DriveErrorCode = src != null ? src.DriveErrorCode : (ushort)0
                };
            }

            return new Sc4iStatusReply
            {
                BusReady = template.BusReady,
                ConfigLoaded = template.ConfigLoaded,
                EnabledMask = template.EnabledMask,
                AuthorizedMask = template.AuthorizedMask,
                PdoOk = template.PdoOk,
                PdoAgeMs = template.PdoAgeMs,
                EstopFlag = template.EstopFlag,
                LatchedFault = template.LatchedFault,
                Slaves = slaves
            };
        }

        private static List<int> CollectEnabledWaypointIndices(TeachProgram program)
        {
            List<int> indices = new List<int>();
            for (int i = 0; i < program.Waypoints.Count; i++)
            {
                if (program.Waypoints[i] != null && program.Waypoints[i].Enabled)
                    indices.Add(i);
            }
            return indices;
        }

        private static string FormatCyclePrefix(int cycle, int totalCycles, int activeStep, int activeTotal, string waypointName)
        {
            string cycleText = totalCycles == 0
                ? ("第 " + cycle.ToString(CultureInfo.InvariantCulture) + " 轮(连续)")
                : ("第 " + cycle.ToString(CultureInfo.InvariantCulture) + "/" + totalCycles.ToString(CultureInfo.InvariantCulture) + " 轮");
            return String.Format(
                CultureInfo.InvariantCulture,
                "{0} [{1}/{2}] {3}",
                cycleText, activeStep, activeTotal, waypointName);
        }

        private static void ReportProgress(
            Action<TaskRunProgress> onProgress,
            TaskRunPhase phase,
            int cycleIndex,
            int totalCycles,
            int waypointIndex,
            int activeStepNumber,
            int activeStepTotal,
            string waypointName,
            string message,
            Sc4iStatusReply status)
        {
            if (onProgress == null) return;
            onProgress(new TaskRunProgress
            {
                Phase = phase,
                CycleIndex = cycleIndex,
                TotalCycles = totalCycles,
                WaypointIndex = waypointIndex,
                ActiveStepNumber = activeStepNumber,
                ActiveStepTotal = activeStepTotal,
                WaypointName = waypointName,
                Message = message,
                LatestStatus = status
            });
        }

        private static TaskValidationResult FailValidation(string error, int failedWaypointIndex)
        {
            return new TaskValidationResult
            {
                Success = false,
                Error = error,
                FailedWaypointIndex = failedWaypointIndex
            };
        }
    }
}
