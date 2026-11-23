using System;
using System.Collections.Generic;
using System.Globalization;
using ScaraArm.Core.Kinematics;
using ScaraArm.Core.Motion;
using ScaraArm.Core.Transport;

namespace ScaraArm.Core.Tasks
{
    public enum WaypointRole
    {
        Waypoint = 0,
        Pick = 1,
        Place = 2,
        Safe = 3
    }

    /// <summary>
    /// A single taught Cartesian waypoint in a multi-point SCARA pick-and-place program.
    /// </summary>
    public sealed class TeachWaypoint
    {
        public const int MaxDwellMs = 60000;

        public string Name { get; set; }
        public WaypointRole Role { get; set; }
        public double XMm { get; set; }
        public double YMm { get; set; }
        public double ZMm { get; set; }
        public double RDeg { get; set; }
        public bool KeepWorldR { get; set; }
        public ScaraElbowMode ElbowPreference { get; set; }
        public int DwellMs { get; set; }
        public bool Enabled { get; set; }

        public TeachWaypoint()
        {
            Name = "P1";
            Role = WaypointRole.Waypoint;
            XMm = ScaraCalibration.DefaultL1Mm + ScaraCalibration.DefaultL2Mm;
            YMm = 0.0;
            ZMm = ScaraCalibration.DefaultZeroHeightMm;
            RDeg = 0.0;
            KeepWorldR = true;
            ElbowPreference = ScaraElbowMode.AutoClosest;
            DwellMs = 0;
            Enabled = true;
        }

        public TeachWaypoint Clone()
        {
            return new TeachWaypoint
            {
                Name = Name,
                Role = Role,
                XMm = XMm,
                YMm = YMm,
                ZMm = ZMm,
                RDeg = RDeg,
                KeepWorldR = KeepWorldR,
                ElbowPreference = ElbowPreference,
                DwellMs = DwellMs,
                Enabled = Enabled
            };
        }

        public CartesianTarget ToCartesianTarget(string labelOverride = null)
        {
            return new CartesianTarget
            {
                XMm = XMm,
                YMm = YMm,
                ZMm = ZMm,
                RDeg = RDeg,
                KeepWorldR = KeepWorldR,
                Label = String.IsNullOrWhiteSpace(labelOverride) ? DisplayName : labelOverride
            };
        }

        public string DisplayName
        {
            get
            {
                return String.IsNullOrWhiteSpace(Name) ? "未命名点位" : SanitizeSingleLine(Name);
            }
        }

        public void Validate(int index)
        {
            string prefix = "点位 #" + (index + 1);
            if (String.IsNullOrWhiteSpace(Name))
                throw new InvalidOperationException(prefix + " 名称不能为空。");
            if (!IsFinite(XMm) || !IsFinite(YMm) || !IsFinite(ZMm) || !IsFinite(RDeg))
                throw new InvalidOperationException(prefix + "（" + DisplayName + "）的坐标必须为有限数。");
            if (DwellMs < 0 || DwellMs > MaxDwellMs)
                throw new InvalidOperationException(prefix + "（" + DisplayName + "）的停留时间必须在 0 至 " + MaxDwellMs + " ms 之间。");
            if (!Enum.IsDefined(typeof(WaypointRole), Role))
                throw new InvalidOperationException(prefix + "（" + DisplayName + "）的点位类型无效。");
            if (!Enum.IsDefined(typeof(ScaraElbowMode), ElbowPreference))
                throw new InvalidOperationException(prefix + "（" + DisplayName + "）的肘部手系无效。");
        }

        public static TeachWaypoint FromCartesianPose(
            string name,
            ScaraCartesianPose pose,
            WaypointRole role = WaypointRole.Waypoint,
            bool keepWorldR = true,
            ScaraElbowMode elbowPreference = ScaraElbowMode.AutoClosest,
            int dwellMs = 0)
        {
            if (pose == null) throw new ArgumentNullException("pose");
            return new TeachWaypoint
            {
                Name = String.IsNullOrWhiteSpace(name) ? "P1" : SanitizeSingleLine(name),
                Role = role,
                XMm = Math.Round(pose.XMm, 2),
                YMm = Math.Round(pose.YMm, 2),
                ZMm = Math.Round(pose.ZMm, 2),
                RDeg = Math.Round(pose.RDeg, 2),
                KeepWorldR = keepWorldR,
                ElbowPreference = elbowPreference,
                DwellMs = dwellMs,
                Enabled = true
            };
        }

        public static string FormatRole(WaypointRole role)
        {
            switch (role)
            {
                case WaypointRole.Pick: return "取料";
                case WaypointRole.Place: return "放料";
                case WaypointRole.Safe: return "安全";
                default: return "通用";
            }
        }

        public static string SanitizeSingleLine(string value)
        {
            if (String.IsNullOrEmpty(value)) return String.Empty;
            return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        private static bool IsFinite(double v)
        {
            return !Double.IsNaN(v) && !Double.IsInfinity(v);
        }
    }

    /// <summary>
    /// Editable and persistable multi-point teach / cyclic pick-and-place task program.
    /// RepeatCycles == 0 means continuous cycling until stopped or cancelled.
    /// </summary>
    public sealed class TeachProgram
    {
        public const double DefaultVelocityRevPerSec = 0.35;
        public const double DefaultAccelerationRevPerSec2 = 1.0;
        public const double DefaultSafeTransitZMm = 150.0;
        public const int MaxCyclesLimit = 10000;

        public string Name { get; set; }
        public double VelocityRevPerSec { get; set; }
        public double AccelerationRevPerSec2 { get; set; }
        public bool UseSafeTransitZ { get; set; }
        public double SafeTransitZMm { get; set; }
        public int RepeatCycles { get; set; }
        public bool ReturnHomeOnComplete { get; set; }
        public List<TeachWaypoint> Waypoints { get; private set; }

        public TeachProgram()
        {
            Name = "默认取放任务";
            VelocityRevPerSec = DefaultVelocityRevPerSec;
            AccelerationRevPerSec2 = DefaultAccelerationRevPerSec2;
            UseSafeTransitZ = true;
            SafeTransitZMm = DefaultSafeTransitZMm;
            RepeatCycles = 1;
            ReturnHomeOnComplete = false;
            Waypoints = new List<TeachWaypoint>();
        }

        public TeachProgram Clone()
        {
            TeachProgram copy = new TeachProgram
            {
                Name = Name,
                VelocityRevPerSec = VelocityRevPerSec,
                AccelerationRevPerSec2 = AccelerationRevPerSec2,
                UseSafeTransitZ = UseSafeTransitZ,
                SafeTransitZMm = SafeTransitZMm,
                RepeatCycles = RepeatCycles,
                ReturnHomeOnComplete = ReturnHomeOnComplete
            };
            for (int i = 0; i < Waypoints.Count; i++)
            {
                if (Waypoints[i] != null)
                    copy.Waypoints.Add(Waypoints[i].Clone());
            }
            return copy;
        }

        public int CountEnabledWaypoints()
        {
            int count = 0;
            for (int i = 0; i < Waypoints.Count; i++)
            {
                if (Waypoints[i] != null && Waypoints[i].Enabled)
                    count++;
            }
            return count;
        }

        public void ValidateParameters()
        {
            if (Double.IsNaN(VelocityRevPerSec) || Double.IsInfinity(VelocityRevPerSec)
                || VelocityRevPerSec <= 0.0 || VelocityRevPerSec > Sc4iClient.MaxSafeVelocityRevPerSec)
            {
                throw new InvalidOperationException(String.Format(
                    CultureInfo.InvariantCulture,
                    "任务速度必须大于 0 且不超过 {0:0.##} 圈/秒。",
                    Sc4iClient.MaxSafeVelocityRevPerSec));
            }

            if (Double.IsNaN(AccelerationRevPerSec2) || Double.IsInfinity(AccelerationRevPerSec2)
                || AccelerationRevPerSec2 <= 0.0 || AccelerationRevPerSec2 > Sc4iClient.MaxSafeAccelerationRevPerSec2)
            {
                throw new InvalidOperationException(String.Format(
                    CultureInfo.InvariantCulture,
                    "任务加速度必须大于 0 且不超过 {0:0.##} 圈/秒²。",
                    Sc4iClient.MaxSafeAccelerationRevPerSec2));
            }

            if (Double.IsNaN(SafeTransitZMm) || Double.IsInfinity(SafeTransitZMm))
                throw new InvalidOperationException("安全提 Z 高度必须为有限数。");

            if (RepeatCycles < 0 || RepeatCycles > MaxCyclesLimit)
                throw new InvalidOperationException("循环次数必须在 0（连续循环）至 " + MaxCyclesLimit + " 之间。");

            for (int i = 0; i < Waypoints.Count; i++)
            {
                if (Waypoints[i] == null)
                    throw new InvalidOperationException("点位列表包含空条目（索引 " + i + "）。");
                Waypoints[i].Validate(i);
            }
        }
    }
}
