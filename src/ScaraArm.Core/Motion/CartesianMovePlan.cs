using ScaraArm.Core.Kinematics;
using ScaraArm.Core.Protocol;

namespace ScaraArm.Core.Motion
{
    public enum CartesianStepAxis
    {
        XNeg = 0,
        XPos = 1,
        YNeg = 2,
        YPos = 3,
        ZNeg = 4,
        ZPos = 5,
        RNeg = 6,
        RPos = 7
    }

    public sealed class CartesianTarget
    {
        public double XMm { get; set; }
        public double YMm { get; set; }
        public double ZMm { get; set; }
        public double RDeg { get; set; }
        public bool KeepWorldR { get; set; }
        public string Label { get; set; }
    }

    public sealed class CartesianAxisStep
    {
        public int SlaveIndex { get; internal set; }
        public double TargetRev { get; internal set; }
        public double DeltaRev { get; internal set; }
    }

    public sealed class CartesianMovePlan
    {
        public bool Success { get; internal set; }
        public string Error { get; internal set; }
        public bool RaisingZ { get; internal set; }
        public ScaraIkResult IkResult { get; internal set; }
        public CartesianAxisStep[] Steps { get; internal set; }

        public bool IsNoOp
        {
            get { return Success && (Steps == null || Steps.Length == 0); }
        }
    }

    public sealed class CartesianStepProgress
    {
        public int StepIndex { get; internal set; }
        public int TotalSteps { get; internal set; }
        public CartesianAxisStep CurrentStep { get; internal set; }
        public Sc4iStatusReply LatestStatus { get; internal set; }
        public string Message { get; internal set; }
    }
}
