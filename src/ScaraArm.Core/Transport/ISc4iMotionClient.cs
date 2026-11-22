using System.Threading;
using System.Threading.Tasks;
using ScaraArm.Core.Protocol;

namespace ScaraArm.Core.Transport
{
    /// <summary>
    /// Minimal motion-client contract consumed by higher-level planners and UI orchestration.
    /// Enables deterministic offline testing without real UDP sockets or hardware.
    /// </summary>
    public interface ISc4iMotionClient
    {
        bool IsConnected { get; }
        double CountsPerRevolution { get; }

        Task<Sc4iStatusReply> SendHeartbeatAsync(CancellationToken cancellationToken);
        Task<Sc4iStatusReply> MoveAxisToTargetRevAsync(
            int jointIndex,
            double targetRev,
            double velRevPerSec,
            double accRevPerSec2,
            CancellationToken cancellationToken);
    }
}
