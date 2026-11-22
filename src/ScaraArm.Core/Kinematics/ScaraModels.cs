namespace ScaraArm.Core.Kinematics
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
}
