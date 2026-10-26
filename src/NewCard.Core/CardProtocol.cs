using System;

namespace NewCard.Core
{
    /// <summary>A malformed or unrelated card response.</summary>
    public sealed class ProtocolException : FormatException
    {
        public ProtocolException(string message) : base(message) { }
    }

    public sealed class EchoReply
    {
        public byte Marker { get; private set; }
        public uint WindowId { get; private set; }

        internal EchoReply(byte marker, uint windowId)
        {
            Marker = marker;
            WindowId = windowId;
        }
    }

    /// <summary>
    /// Encodes only the non-motion, 100-byte power-up query (0x01).
    /// Byte offsets refer to the UDP payload, excluding W5500 receive metadata.
    /// </summary>
    public static class CardProtocol
    {
        public const int EchoPacketLength = 100;
        public const int ChallengeLength = 72;

        public static byte[] BuildEchoRequest(byte token, byte[] challenge)
        {
            ValidateArguments(token, challenge);
            byte[] packet = new byte[EchoPacketLength];
            packet[1] = 0xB5;
            packet[2] = 0;
            packet[3] = EchoPacketLength;
            packet[4] = 0x01;
            packet[5] = 0;
            packet[6] = token;
            Buffer.BlockCopy(challenge, 0, packet, 8, ChallengeLength);
            packet[0] = CalculateChecksum(packet);
            return packet;
        }

        public static EchoReply ParseEchoReply(byte[] packet, byte token, byte[] challenge)
        {
            ValidateArguments(token, challenge);
            if (packet == null)
                throw new ArgumentNullException("packet");
            if (packet.Length != EchoPacketLength)
                throw new ProtocolException("应答长度必须恰好为 100 字节。");
            if (packet[2] != 0 || packet[3] != EchoPacketLength)
                throw new ProtocolException("应答头中的长度与固定帧长不一致。");
            if (packet[0] != CalculateChecksum(packet))
                throw new ProtocolException("应答校验和错误。");
            if (packet[1] != 0xA5 && packet[1] != 0xB5)
                throw new ProtocolException("应答不是 App 或 Boot 的查询帧。");
            if (packet[4] != 0x01 || packet[5] != 0)
                throw new ProtocolException("应答命令与本次通信查询不一致。");
            if (packet[6] != token)
                throw new ProtocolException("应答令牌与本次请求不一致。");
            if (packet[7] != 0)
                throw new ProtocolException("运动卡返回错误应答：0x" + packet[7].ToString("X2") + "。");
            if (packet[1] == 0xB5)
                throw new ProtocolException("收到 Boot 格式回复；Boot 不回显挑战，未验证 App 通信。不要刷写或运动。");
            for (int i = 0; i < ChallengeLength; i++)
            {
                if (packet[16 + i] != challenge[i])
                    throw new ProtocolException("应答回显与本次请求的挑战数据不一致。");
            }

            uint windowId = (uint)packet[8]
                | ((uint)packet[9] << 8)
                | ((uint)packet[10] << 16)
                | ((uint)packet[11] << 24);
            return new EchoReply(packet[1], windowId);
        }

        private static void ValidateArguments(byte token, byte[] challenge)
        {
            if (token > 0xF0)
                throw new ArgumentOutOfRangeException("token", "动态令牌范围为 0x00 至 0xF0。");
            if (challenge == null)
                throw new ArgumentNullException("challenge");
            if (challenge.Length != ChallengeLength)
                throw new ArgumentException("挑战数据必须恰好为 72 字节。", "challenge");
        }

        private static byte CalculateChecksum(byte[] packet)
        {
            int sum = 0;
            for (int i = 1; i < packet.Length; i++)
                sum += packet[i];
            return (byte)(sum & 0xFF);
        }
    }
}
