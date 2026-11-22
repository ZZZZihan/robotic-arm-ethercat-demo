using System;
using ScaraArm.Core.Protocol;

namespace ScaraArm.Core.Transport
{
    public sealed class Sc4iCommandException : InvalidOperationException
    {
        public uint ErrorCode { get; private set; }
        public Sc4iStatusReply Status { get; private set; }

        public Sc4iCommandException(string action, Sc4iStatusReply status)
            : base(action + " 失败：" + Sc4iProtocol.FormatErrorCode(status.ErrorCode))
        {
            ErrorCode = status.ErrorCode;
            Status = status;
        }
    }
}
