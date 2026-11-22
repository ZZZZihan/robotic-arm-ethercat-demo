using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ScaraArm.Core.Kinematics;
using ScaraArm.Core.Protocol;
using ScaraArm.Core.Transport;

namespace ScaraArm.Core.Motion
{
    /// <summary>
    /// Plans and executes safe multi-axis Cartesian moves for the 4-axis column-lift SCARA arm.
    /// Safety ordering: raise Z first before horizontal motion; move horizontally first before lowering Z.
    /// </summary>
    public static class CartesianMotionPlanner
    {
        public const double MinMoveDeltaRev = 0.001;
        public const double RaiseZThresholdMm = 0.05;
        public const int DefaultPollIntervalMs = 120;
        public const int DefaultStepTimeoutMs = 25000;

        public static CartesianTarget BuildRelativeStepTarget(
            ScaraCartesianPose currentPose,
            CartesianStepAxis stepAxis,
            double stepMm,
            double stepDeg,
            bool keepWorldR)
        {
            if (currentPose == null) throw new ArgumentNullException("currentPose");

            double tx = currentPose.XMm;
            double ty = currentPose.YMm;
            double tz = currentPose.ZMm;
            double tr = currentPose.RDeg;
            bool keepR = keepWorldR;
            string label;

            switch (stepAxis)
            {
                case CartesianStepAxis.XNeg:
                    tx -= stepMm;
                    label = String.Format(CultureInfo.InvariantCulture, "X {0:+0.#;-0.#} mm", -stepMm);
                    break;
                case CartesianStepAxis.XPos:
                    tx += stepMm;
                    label = String.Format(CultureInfo.InvariantCulture, "X {0:+0.#;-0.#} mm", stepMm);
                    break;
                case CartesianStepAxis.YNeg:
                    ty -= stepMm;
                    label = String.Format(CultureInfo.InvariantCulture, "Y {0:+0.#;-0.#} mm", -stepMm);
                    break;
                case CartesianStepAxis.YPos:
                    ty += stepMm;
                    label = String.Format(CultureInfo.InvariantCulture, "Y {0:+0.#;-0.#} mm", stepMm);
                    break;
                case CartesianStepAxis.ZNeg:
                    tz -= stepMm;
                    label = String.Format(CultureInfo.InvariantCulture, "Z {0:+0.#;-0.#} mm", -stepMm);
                    break;
                case CartesianStepAxis.ZPos:
                    tz += stepMm;
                    label = String.Format(CultureInfo.InvariantCulture, "Z {0:+0.#;-0.#} mm", stepMm);
                    break;
                case CartesianStepAxis.RNeg:
                    tr -= stepDeg;
                    keepR = true;
                    label = String.Format(CultureInfo.InvariantCulture, "R {0:+0.#;-0.#}°", -stepDeg);
                    break;
                case CartesianStepAxis.RPos:
                    tr += stepDeg;
                    keepR = true;
                    label = String.Format(CultureInfo.InvariantCulture, "R {0:+0.#;-0.#}°", stepDeg);
                    break;
                default:
                    throw new ArgumentOutOfRangeException("stepAxis");
            }

            return new CartesianTarget
            {
                XMm = tx,
                YMm = ty,
                ZMm = tz,
                RDeg = tr,
                KeepWorldR = keepR,
                Label = label
            };
        }

        public static CartesianTarget ComputeHomeTarget(ScaraCalibration calibration)
        {
            if (calibration == null) throw new ArgumentNullException("calibration");
            return new CartesianTarget
            {
                XMm = calibration.MaxReachMm,
                YMm = 0.0,
                ZMm = calibration.ZeroHeightMm,
                RDeg = 0.0,
                KeepWorldR = true,
                Label = "安全回零点"
            };
        }

        public static CartesianMovePlan PlanMove(
            ScaraCalibration calibration,
            Sc4iStatusReply status,
            CartesianTarget target,
            ScaraElbowMode elbowPreference)
        {
            if (calibration == null) throw new ArgumentNullException("calibration");
            if (target == null) throw new ArgumentNullException("target");

            int[] counts;
            if (status == null || !status.TryExtractLiveCounts(out counts))
                return Fail("无法读取有效四轴位置，禁止启动笛卡尔运动。");

            ScaraJointPose currentJoints = ScaraKinematics.CountsToJoints(calibration, counts);
            ScaraIkResult ik = ScaraKinematics.Inverse(
                calibration,
                target.XMm,
                target.YMm,
                target.ZMm,
                target.RDeg,
                target.KeepWorldR,
                currentJoints,
                elbowPreference);

            if (!ik.Success)
                return Fail(ik.Error);

            bool raisingZ = ik.TargetJoints.ZMm > currentJoints.ZMm + RaiseZThresholdMm;
            int[] order = raisingZ
                ? new int[] { calibration.J2Slave, calibration.J1Slave, calibration.J3Slave, calibration.J4Slave }
                : new int[] { calibration.J1Slave, calibration.J3Slave, calibration.J4Slave, calibration.J2Slave };

            List<CartesianAxisStep> steps = new List<CartesianAxisStep>();
            for (int i = 0; i < order.Length; i++)
            {
                int slave = order[i];
                double deltaRev = ik.DeltaSlaveRevs[slave];
                if (Math.Abs(deltaRev) >= MinMoveDeltaRev)
                {
                    if (!status.IsAxisEnabled(slave) || !status.IsAxisAuthorized(slave))
                        return Fail("轴 " + (slave + 1) + " 未使能或未授权，请先点击“使能全部四轴”。");

                    steps.Add(new CartesianAxisStep
                    {
                        SlaveIndex = slave,
                        TargetRev = ik.TargetSlaveRevs[slave],
                        DeltaRev = deltaRev
                    });
                }
            }

            return new CartesianMovePlan
            {
                Success = true,
                RaisingZ = raisingZ,
                IkResult = ik,
                Steps = steps.ToArray()
            };
        }

        public static Task<Sc4iStatusReply> ExecutePlanAsync(
            ISc4iMotionClient client,
            CartesianMovePlan plan,
            double velocityRevPerSec,
            double accelerationRevPerSec2,
            string actionLabel,
            Action<CartesianStepProgress> onProgress,
            CancellationToken cancellationToken)
        {
            return ExecutePlanAsync(
                client,
                plan,
                velocityRevPerSec,
                accelerationRevPerSec2,
                actionLabel,
                onProgress,
                DefaultPollIntervalMs,
                DefaultStepTimeoutMs,
                cancellationToken);
        }

        internal static async Task<Sc4iStatusReply> ExecutePlanAsync(
            ISc4iMotionClient client,
            CartesianMovePlan plan,
            double velocityRevPerSec,
            double accelerationRevPerSec2,
            string actionLabel,
            Action<CartesianStepProgress> onProgress,
            int pollIntervalMs,
            int stepTimeoutMs,
            CancellationToken cancellationToken)
        {
            if (client == null) throw new ArgumentNullException("client");
            if (plan == null) throw new ArgumentNullException("plan");
            if (!plan.Success)
                throw new InvalidOperationException(plan.Error ?? "笛卡尔运动规划无效。");
            if (plan.Steps == null || plan.Steps.Length == 0)
                return null;

            Sc4iStatusReply latest = null;
            string label = String.IsNullOrEmpty(actionLabel) ? "笛卡尔运动" : actionLabel;

            for (int i = 0; i < plan.Steps.Length; i++)
            {
                CartesianAxisStep step = plan.Steps[i];
                cancellationToken.ThrowIfCancellationRequested();

                string stepMessage = String.Format(
                    CultureInfo.InvariantCulture,
                    "{0}：移动轴 {1} ({2:+0.000;-0.000} 圈)…",
                    label,
                    step.SlaveIndex + 1,
                    step.DeltaRev);

                ReportProgress(onProgress, i, plan.Steps.Length, step, latest, stepMessage);

                latest = await client.MoveAxisToTargetRevAsync(
                    step.SlaveIndex,
                    step.TargetRev,
                    velocityRevPerSec,
                    accelerationRevPerSec2,
                    cancellationToken).ConfigureAwait(false);

                ReportProgress(onProgress, i, plan.Steps.Length, step, latest, stepMessage);
                if (!client.IsConnected) return latest;

                DateTime stepDeadline = DateTime.UtcNow.AddMilliseconds(stepTimeoutMs);
                while (true)
                {
                    if (pollIntervalMs > 0)
                        await Task.Delay(pollIntervalMs, cancellationToken).ConfigureAwait(false);
                    else
                        cancellationToken.ThrowIfCancellationRequested();

                    latest = await client.SendHeartbeatAsync(cancellationToken).ConfigureAwait(false);
                    ReportProgress(onProgress, i, plan.Steps.Length, step, latest, null);
                    if (!client.IsConnected) return latest;

                    if (latest.LatchedFault != 0 || latest.EstopFlag != 0 || !latest.PdoOk)
                        throw new InvalidOperationException("运动中检测到急停、故障锁存或 PDO 异常，已中止序列。");
                    if (latest.MotionState == 0)
                        break;
                    if (DateTime.UtcNow > stepDeadline)
                        throw new TimeoutException("等待轴 " + (step.SlaveIndex + 1) + " 到位超时。");
                }
            }

            return latest;
        }

        private static void ReportProgress(
            Action<CartesianStepProgress> onProgress,
            int index,
            int total,
            CartesianAxisStep step,
            Sc4iStatusReply status,
            string message)
        {
            if (onProgress == null) return;
            onProgress(new CartesianStepProgress
            {
                StepIndex = index,
                TotalSteps = total,
                CurrentStep = step,
                LatestStatus = status,
                Message = message
            });
        }

        private static CartesianMovePlan Fail(string error)
        {
            return new CartesianMovePlan
            {
                Success = false,
                Error = error,
                Steps = new CartesianAxisStep[0]
            };
        }
    }
}
