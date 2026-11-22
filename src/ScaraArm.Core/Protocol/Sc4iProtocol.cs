using System;
using System.Globalization;
using System.Text;

namespace ScaraArm.Core.Protocol
{
    /// <summary>
    /// Wire codec for the SC4I0001 v2 EtherCAT motion firmware running on the HC32F4A0 card.
    /// </summary>
    public static class Sc4iProtocol
    {
        public const string ExpectedMagic = "SC4I0001";
        public const int HelloRequestLength = 16;
        public const int HelloReplyLength = 100;
        public const int F2HeaderLength = 40;
        public const int F2ReplyLength = 384;
        public const byte AppMarker = 0xA5;

        public static byte[] BuildHelloRequest(byte token)
        {
            byte[] packet = new byte[HelloRequestLength];
            packet[1] = AppMarker;
            packet[2] = 0;
            packet[3] = HelloRequestLength;
            packet[4] = 0x01;
            packet[5] = 0x00;
            packet[6] = token;
            packet[7] = 0x00;
            packet[0] = CalculateChecksum(packet);
            return packet;
        }

        public static Sc4iHelloReply ParseHelloReply(byte[] packet, byte expectedToken)
        {
            ValidateFrameHeader(packet, HelloReplyLength, 0x01);
            if (packet[6] != expectedToken)
                throw new ProtocolException("Hello 应答令牌不一致。");
            string magic = Encoding.ASCII.GetString(packet, 16, 8);
            if (!String.Equals(magic, ExpectedMagic, StringComparison.Ordinal))
                throw new ProtocolException("控制卡固件标识不是 " + ExpectedMagic + "，实际为：" + magic);
            ulong bootId = ReadU64LE(packet, 24);
            uint protoVer = ReadU32LE(packet, 32);
            if (bootId == 0)
                throw new ProtocolException("控制卡返回的 boot_id 为 0。");
            return new Sc4iHelloReply
            {
                Magic = magic,
                BootId = bootId,
                ProtocolVersion = protoVer
            };
        }

        public static byte[] BuildF2Request(Sc4iSubcmd subcmd, byte arg, uint seq, ulong bootId, ulong sessionId, byte[] payload)
        {
            int payloadLen = payload == null ? 0 : payload.Length;
            int totalLen = F2HeaderLength + payloadLen;
            if (totalLen > ushort.MaxValue)
                throw new ArgumentException("帧长超出 16 位长度字段上限。", "payload");

            byte[] packet = new byte[totalLen];
            packet[1] = AppMarker;
            packet[2] = (byte)((totalLen >> 8) & 0xFF);
            packet[3] = (byte)(totalLen & 0xFF);
            packet[4] = 0xF2;
            packet[5] = 0x00;
            packet[6] = (byte)(seq & 0x7F);
            packet[7] = 0x00;
            packet[8] = (byte)subcmd;
            packet[9] = arg;
            packet[10] = 0x00;
            packet[11] = 0x00;
            WriteU32LE(packet, 12, seq);
            WriteU64LE(packet, 16, bootId);
            WriteU64LE(packet, 24, sessionId);
            WriteU64LE(packet, 32, 0ul);
            if (payloadLen > 0)
                Buffer.BlockCopy(payload, 0, packet, F2HeaderLength, payloadLen);
            packet[0] = CalculateChecksum(packet);
            return packet;
        }

        public static byte[] BuildMoveAxisPtpPayload(double targetUnits, double maxVelUnitsPerSec, double maxAccUnitsPerSec2)
        {
            if (Double.IsNaN(targetUnits) || Double.IsInfinity(targetUnits))
                throw new ArgumentException("目标位置必须是有限数。", "targetUnits");
            if (Double.IsNaN(maxVelUnitsPerSec) || Double.IsInfinity(maxVelUnitsPerSec) || maxVelUnitsPerSec <= 0)
                throw new ArgumentException("速度必须为正有限数。", "maxVelUnitsPerSec");
            if (Double.IsNaN(maxAccUnitsPerSec2) || Double.IsInfinity(maxAccUnitsPerSec2) || maxAccUnitsPerSec2 <= 0)
                throw new ArgumentException("加速度必须为正有限数。", "maxAccUnitsPerSec2");

            byte[] payload = new byte[24];
            WriteF64LE(payload, 0, targetUnits);
            WriteF64LE(payload, 8, maxVelUnitsPerSec);
            WriteF64LE(payload, 16, maxAccUnitsPerSec2);
            return payload;
        }

        public static byte[] BuildJogPayload(double velocityUnitsPerSec)
        {
            if (Double.IsNaN(velocityUnitsPerSec) || Double.IsInfinity(velocityUnitsPerSec) || velocityUnitsPerSec == 0.0)
                throw new ArgumentException("点动速度必须是非零有限数。", "velocityUnitsPerSec");
            byte[] payload = new byte[8];
            WriteF64LE(payload, 0, velocityUnitsPerSec);
            return payload;
        }

        public static Sc4iStatusReply ParseF2Reply(byte[] packet)
        {
            ValidateFrameHeader(packet, F2ReplyLength, 0xF2);
            string magic = Encoding.ASCII.GetString(packet, 40, 8);
            if (!String.Equals(magic, ExpectedMagic, StringComparison.Ordinal))
                throw new ProtocolException("0xF2 应答固件标识异常：" + magic);

            Sc4iSlaveTelemetry[] slaves = new Sc4iSlaveTelemetry[4];
            for (int s = 0; s < 4; s++)
            {
                int off = 112 + s * 56;
                slaves[s] = new Sc4iSlaveTelemetry
                {
                    SlaveIndex = s,
                    VendorId = ReadU32LE(packet, off + 0),
                    ProductCode = ReadU32LE(packet, off + 4),
                    Revision = ReadU32LE(packet, off + 8),
                    SerialNumber = ReadU32LE(packet, off + 12),
                    ActualPositionCounts = ReadI32LE(packet, off + 16),
                    CommandPositionCounts = ReadI32LE(packet, off + 20),
                    StatusWord = ReadU16LE(packet, off + 24),
                    ModeDisplay = ReadU16LE(packet, off + 26),
                    DigitalInputs = ReadU32LE(packet, off + 28),
                    DriveErrorCode = ReadU16LE(packet, off + 32),
                    ControlWord = ReadU16LE(packet, off + 34),
                    DriveModelTag = ReadU32LE(packet, off + 36),
                    ModeByteSize = ReadU32LE(packet, off + 40),
                    JointIndex = ReadU32LE(packet, off + 44),
                    ValidMask = ReadU32LE(packet, off + 48)
                };
            }

            return new Sc4iStatusReply
            {
                FrameToken = packet[6],
                Subcmd = packet[8],
                MotionState = packet[9],
                EstopFlag = packet[10],
                SlaveCount = packet[11],
                Seq = ReadU32LE(packet, 12),
                BootId = ReadU64LE(packet, 16),
                SessionId = ReadU64LE(packet, 24),
                ErrorCode = ReadU32LE(packet, 32),
                LatchedFault = ReadU32LE(packet, 36),
                Magic = magic,
                BusReady = ReadU32LE(packet, 48) != 0,
                ConfigLoaded = ReadU32LE(packet, 52) != 0,
                EnabledMask = ReadU32LE(packet, 56),
                AuthorizedMask = ReadU32LE(packet, 60),
                EnablingMask = ReadU32LE(packet, 64),
                FaultSlave = ReadU32LE(packet, 68),
                PdoOk = ReadU32LE(packet, 72) != 0,
                PdoAgeMs = ReadU32LE(packet, 76),
                PdoCycles = ReadU32LE(packet, 80),
                TrajTick = ReadU32LE(packet, 84),
                TrajLength = ReadU32LE(packet, 88),
                TxPdoBytes = ReadU32LE(packet, 92),
                ReadyCycles = ReadU32LE(packet, 96),
                ProtocolVersion = ReadU32LE(packet, 100),
                SystemCoreClockHz = ReadU32LE(packet, 368),
                UptimeMs = ReadU32LE(packet, 376),
                Slaves = slaves
            };
        }

        public static string FormatErrorCode(uint code)
        {
            switch (code)
            {
                case 0x00: return "正常 (0x00)";
                case 0x05: return "目标超出软限位 (0x05)";
                case 0x07: return "轨迹校验失败 (0x07)";
                case 0x08: return "速度或加速度超限 (0x08)";
                case 0x09: return "单步运动参数无效 (0x09)";
                case 0x28: return "参数或掩码无效 / 目标轴未处于故障态 (0x28)";
                case 0x29: return "配置校验未通过、总线已初始化或 0x605A 不匹配 (0x29)";
                case 0x2A: return "控制器忙、正在运动或存在急停/停机锁存，请先点击清故障解锁 (0x2A)";
                case 0x2B: return "EtherCAT 总线未就绪，请先执行“初始化总线 (进 OP)” (0x2B)";
                case 0x2C: return "PDO 数据未刷新或超时 (0x2C)";
                case 0x2D: return "驱动器报警、非 CSP 模式或上电时已使能 (0x2D)";
                case 0x2E: return "驱动器模式偏离 CSP (0x6061≠8) 或 SDO 写 0x6060=8 失败 (0x2E)";
                case 0x2F: return "跟随误差超限 (0x2F)";
                case 0x30: return "上位机心跳/点动保活超时，已触发安全快停保护 (0x30)";
                case 0x31: return "控制卡 1 kHz 周期节拍异常 (0x31)";
                case 0x32: return "EtherCAT PDO 映射或总线初始化失败 (0x32)";
                case 0x33: return "从站 0x1018 身份与配置不一致 (0x33)";
                case 0x34: return "该轴未使能或未授权，请先点击“使能当前轴” (0x34)";
                case 0x35: return "序列号乱序或重放校验失败 (0x35)";
                case 0x36: return "会话租约冲突或已失效 (0x36)";
                case 0x37: return "BootID 不匹配或未知子命令 (0x37)";
                case 0x3A: return "EtherCAT 同步/心跳超时或进入 OP 失败 (0x3A)";
                case 0x3B: return "刚进入 OP 尚未稳定，请稍等片刻重试 (0x3B)";
                default: return "固件错误码 0x" + code.ToString("X2", CultureInfo.InvariantCulture);
            }
        }

        private static void ValidateFrameHeader(byte[] packet, int expectedLength, byte expectedCmd)
        {
            if (packet == null)
                throw new ArgumentNullException("packet");
            if (packet.Length != expectedLength)
                throw new ProtocolException("应答长度异常：期望 " + expectedLength + " 字节，实际 " + packet.Length + " 字节。");
            if (packet[0] != CalculateChecksum(packet))
                throw new ProtocolException("应答校验和错误。");
            if (packet[1] != AppMarker)
                throw new ProtocolException("应答标记不是 App (0xA5)，实际为 0x" + packet[1].ToString("X2", CultureInfo.InvariantCulture) + "。");
            int declared = (packet[2] << 8) | packet[3];
            if (declared != expectedLength)
                throw new ProtocolException("应答声明长度 " + declared + " 与实际长度不一致。");
            if (packet[4] != expectedCmd || packet[5] != 0)
                throw new ProtocolException("应答命令号不匹配：0x" + packet[4].ToString("X2", CultureInfo.InvariantCulture) + "。");
            if (packet[7] != 0)
                throw new ProtocolException("底层应答错误码：0x" + packet[7].ToString("X2", CultureInfo.InvariantCulture) + "。");
        }

        public static byte CalculateChecksum(byte[] packet)
        {
            if (packet == null)
                throw new ArgumentNullException("packet");
            int sum = 0;
            for (int i = 1; i < packet.Length; i++)
                sum += packet[i];
            return (byte)(sum & 0xFF);
        }

        internal static ushort ReadU16LE(byte[] buf, int offset)
        {
            return (ushort)(buf[offset] | (buf[offset + 1] << 8));
        }

        internal static uint ReadU32LE(byte[] buf, int offset)
        {
            return (uint)buf[offset]
                | ((uint)buf[offset + 1] << 8)
                | ((uint)buf[offset + 2] << 16)
                | ((uint)buf[offset + 3] << 24);
        }

        internal static int ReadI32LE(byte[] buf, int offset)
        {
            return unchecked((int)ReadU32LE(buf, offset));
        }

        internal static ulong ReadU64LE(byte[] buf, int offset)
        {
            ulong lo = ReadU32LE(buf, offset);
            ulong hi = ReadU32LE(buf, offset + 4);
            return lo | (hi << 32);
        }

        internal static void WriteU32LE(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        internal static void WriteI32LE(byte[] buf, int offset, int value)
        {
            WriteU32LE(buf, offset, unchecked((uint)value));
        }

        internal static void WriteU64LE(byte[] buf, int offset, ulong value)
        {
            WriteU32LE(buf, offset, (uint)(value & 0xFFFFFFFFul));
            WriteU32LE(buf, offset + 4, (uint)(value >> 32));
        }

        internal static void WriteF64LE(byte[] buf, int offset, double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            WriteU64LE(buf, offset, unchecked((ulong)bits));
        }
    }
}
