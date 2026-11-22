using System;

namespace ScaraArm.Core.Protocol
{
    /// <summary>A malformed or unrelated motion-card response.</summary>
    public sealed class ProtocolException : FormatException
    {
        public ProtocolException(string message) : base(message) { }
    }
}
