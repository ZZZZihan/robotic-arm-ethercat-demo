using System;
using System.Globalization;
using System.Text;

namespace NewCard.Core
{
    public enum Sc4iSubcmd : byte
    {
        Snapshot = 0,
        AcquireSession = 1,
        Heartbeat = 2,
        ScanSlaves = 3,
        UploadConfig = 4,
        BusInit = 5,
        EnableAxes = 6,
        MoveAxisPtp = 7,
        JogAxis = 8,
        JogKeepalive = 9,
        StopHold = 10,
        FaultReset = 11
    }

    public sealed class Sc4iHelloReply
    {
        public string Magic { get; internal set; }
        public ulong BootId { get; internal set; }
        public uint ProtocolVersion { get; internal set; }
        public double ElapsedMilliseconds { get; internal set; }
    }

    public sealed class Sc4iSlaveIdentity
    {
        public uint VendorId { get; set; }
        public uint ProductCode { get; set; }
        public uint Revision { get; set; }
        public uint SerialNumber { get; set; }

        public bool IsPopulated
        {
            get { return VendorId != 0 && ProductCode != 0; }
        }
    }

    public sealed class Sc4iSlaveTelemetry
    {
        public int SlaveIndex { get; internal set; }
        public uint VendorId { get; internal set; }
        public uint ProductCode { get; internal set; }
        public uint Revision { get; internal set; }
        public uint SerialNumber { get; internal set; }
        public int ActualPositionCounts { get; internal set; }
        public int CommandPositionCounts { get; internal set; }
        public ushort StatusWord { get; internal set; }
        public ushort ModeDisplay { get; internal set; }
        public uint DigitalInputs { get; internal set; }
        public ushort DriveErrorCode { get; internal set; }
        public ushort ControlWord { get; internal set; }
        public uint DriveModelTag { get; internal set; }
        public uint ModeByteSize { get; internal set; }
        public uint JointIndex { get; internal set; }
        public uint ValidMask { get; internal set; }

        public bool IsFault
        {
            get { return (StatusWord & 0x0008) != 0 || DriveErrorCode != 0; }
        }

        public bool IsOperationEnabled
        {
            get { return (StatusWord & 0x006F) == 0x0027; }
        }

        public bool IsQuickStopActive
        {
            get { return (StatusWord & 0x006F) == 0x0007; }
        }

        public double ActualRevolutions(double countsPerRev)
        {
            return countsPerRev != 0 ? ActualPositionCounts / countsPerRev : 0.0;
        }

        public double CommandRevolutions(double countsPerRev)
        {
            return countsPerRev != 0 ? CommandPositionCounts / countsPerRev : 0.0;
        }

        public Sc4iSlaveIdentity ToIdentity()
        {
            return new Sc4iSlaveIdentity
            {
                VendorId = VendorId,
                ProductCode = ProductCode,
                Revision = Revision,
                SerialNumber = SerialNumber
            };
        }

        public string DescribeCia402State()
        {
            if (ValidMask == 0 && StatusWord == 0) return "未进OP";
            if ((StatusWord & 0x0008) != 0) return "故障(Fault)";
            int low = StatusWord & 0x006F;
            if ((StatusWord & 0x004F) == 0x0040) return "未使能(SwitchOnDisabled)";
            if (low == 0x0021) return "准备上电(Ready)";
            if (low == 0x0023) return "已上电(SwitchedOn)";
            if (low == 0x0027) return "已使能(OpEnabled)";
            if (low == 0x0007) return "快停持力(QuickStop)";
            if ((StatusWord & 0x004F) == 0x0000) return "未就绪(NotReady)";
            return "状态 0x" + StatusWord.ToString("X4", CultureInfo.InvariantCulture);
        }
    }

    public sealed class Sc4iStatusReply
    {
        public byte FrameToken { get; internal set; }
        public byte Subcmd { get; internal set; }
        public byte MotionState { get; internal set; }
        public byte EstopFlag { get; internal set; }
        public byte SlaveCount { get; internal set; }
        public uint Seq { get; internal set; }
        public ulong BootId { get; internal set; }
        public ulong SessionId { get; internal set; }
        public uint ErrorCode { get; internal set; }
        public uint LatchedFault { get; internal set; }
        public string Magic { get; internal set; }
        public bool BusReady { get; internal set; }
        public bool ConfigLoaded { get; internal set; }
        public uint EnabledMask { get; internal set; }
        public uint AuthorizedMask { get; internal set; }
        public uint EnablingMask { get; internal set; }
        public uint FaultSlave { get; internal set; }
        public bool PdoOk { get; internal set; }
        public uint PdoAgeMs { get; internal set; }
        public uint PdoCycles { get; internal set; }
        public uint TrajTick { get; internal set; }
        public uint TrajLength { get; internal set; }
        public uint TxPdoBytes { get; internal set; }
        public uint ReadyCycles { get; internal set; }
        public uint ProtocolVersion { get; internal set; }
        public uint SystemCoreClockHz { get; internal set; }
        public uint UptimeMs { get; internal set; }
        public Sc4iSlaveTelemetry[] Slaves { get; internal set; }

        public bool IsAxisEnabled(int slaveIndex)
        {
            return slaveIndex >= 0 && slaveIndex < 4 && (EnabledMask & (1u << slaveIndex)) != 0;
        }

        public bool IsAxisAuthorized(int slaveIndex)
        {
            return slaveIndex >= 0 && slaveIndex < 4 && (AuthorizedMask & (1u << slaveIndex)) != 0;
        }

        public bool IsAxisEnabling(int slaveIndex)
        {
            return slaveIndex >= 0 && slaveIndex < 4 && (EnablingMask & (1u << slaveIndex)) != 0;
        }

        public byte ComputeFaultedSlaveMask()
        {
            byte mask = 0;
            if (Slaves == null) return 0;
            for (int i = 0; i < Slaves.Length && i < 4; i++)
            {
                if (Slaves[i] != null && (Slaves[i].StatusWord & 0x0008) != 0)
                    mask |= (byte)(1 << i);
            }
            return mask;
        }
    }

    public sealed class Sc4iMachineConfig
    {
        public const int ByteSize = 528;
        public const double DefaultCountsPerRevolution = 262144.0;

        public double ArmLength1Mm { get; set; }
        public double ArmLength2Mm { get; set; }
        public double[] HomeOffset { get; private set; }
        public double[] CountsPerUnit { get; private set; }
        public double[] ZeroCounts { get; private set; }
        public double[] JointMin { get; private set; }
        public double[] JointMax { get; private set; }
        public double[] JointMaxVel { get; private set; }
        public double[] JointMaxAcc { get; private set; }
        public byte[] JointToSlave { get; private set; }
        public double[] BaseXyz { get; private set; }
        public double VelocityFactor { get; set; }
        public double CartMaxVel { get; set; }
        public double CartMaxAcc { get; set; }
        public Sc4iSlaveIdentity[] Identities { get; private set; }
        public uint Expected605A { get; set; }
        public uint ModeByteSize { get; set; }
        public uint PdoFreshTimeoutMs { get; set; }
        public uint HeartbeatTimeoutMs { get; set; }
        public uint JogTimeoutMs { get; set; }
        public uint SettleCycles { get; set; }
        public int[] MaxFollowingErrorIdle { get; private set; }
        public int[] MaxFollowingErrorMoving { get; private set; }

        public Sc4iMachineConfig()
        {
            ArmLength1Mm = 300.0;
            ArmLength2Mm = 250.0;
            HomeOffset = new double[] { 0.0, 0.0, 0.0, 0.0 };
            CountsPerUnit = new double[]
            {
                DefaultCountsPerRevolution, DefaultCountsPerRevolution,
                DefaultCountsPerRevolution, DefaultCountsPerRevolution
            };
            ZeroCounts = new double[] { 0.0, 0.0, 0.0, 0.0 };
            // ±8000 rev keeps ±(8000 * 262144) = ±2,097,152,000 inside signed Int32 range.
            JointMin = new double[] { -8000.0, -8000.0, -8000.0, -8000.0 };
            JointMax = new double[] { 8000.0, 8000.0, 8000.0, 8000.0 };
            JointMaxVel = new double[] { 5.0, 5.0, 5.0, 5.0 };
            JointMaxAcc = new double[] { 10.0, 10.0, 10.0, 10.0 };
            JointToSlave = new byte[] { 0, 1, 2, 3 };
            BaseXyz = new double[] { 0.0, 0.0, 0.0 };
            VelocityFactor = 0.5;
            CartMaxVel = 100.0;
            CartMaxAcc = 200.0;
            Identities = new Sc4iSlaveIdentity[4];
            for (int i = 0; i < 4; i++) Identities[i] = new Sc4iSlaveIdentity();
            Expected605A = 6;
            ModeByteSize = 1;
            PdoFreshTimeoutMs = 50;
            HeartbeatTimeoutMs = 1000;
            JogTimeoutMs = 300;
            SettleCycles = 10;
            MaxFollowingErrorIdle = new int[] { 50000, 50000, 50000, 50000 };
            MaxFollowingErrorMoving = new int[] { 200000, 200000, 200000, 200000 };
        }

        public static Sc4iMachineConfig CreateForScannedSlaves(Sc4iSlaveIdentity[] identities, uint expected605A = 6)
        {
            if (identities == null || identities.Length != 4)
                throw new ArgumentException("必须提供 4 个 EtherCAT 从站身份信息。", "identities");
            Sc4iMachineConfig cfg = new Sc4iMachineConfig { Expected605A = expected605A };
            for (int i = 0; i < 4; i++)
            {
                if (identities[i] == null || !identities[i].IsPopulated)
                    throw new ArgumentException("从站 " + i + " 的身份信息为空，请先执行 PRE-OP 扫描。", "identities");
                cfg.Identities[i] = new Sc4iSlaveIdentity
                {
                    VendorId = identities[i].VendorId,
                    ProductCode = identities[i].ProductCode,
                    Revision = identities[i].Revision,
                    SerialNumber = identities[i].SerialNumber
                };
            }
            return cfg;
        }

        private static bool IsFinite(double v)
        {
            return !Double.IsNaN(v) && !Double.IsInfinity(v);
        }

        public byte[] Serialize()
        {
            if (Expected605A != 5 && Expected605A != 6)
                throw new ArgumentOutOfRangeException("Expected605A", "0x605A 必须为 5 或 6（快停保持持力）。");
            if (ModeByteSize != 1 && ModeByteSize != 2)
                throw new ArgumentOutOfRangeException("ModeByteSize", "0x6060/0x6061 长度必须为 1 或 2 字节。");
            if (!IsFinite(VelocityFactor) || !(VelocityFactor > 0.0 && VelocityFactor < 1.0))
                throw new ArgumentOutOfRangeException("VelocityFactor", "速度比例因子必须严格处于 (0, 1) 区间。");
            if (!IsFinite(ArmLength1Mm) || ArmLength1Mm <= 0.0 || !IsFinite(ArmLength2Mm) || ArmLength2Mm <= 0.0)
                throw new ArgumentOutOfRangeException("ArmLength1Mm", "臂长参数必须为正有限数。");
            if (!IsFinite(CartMaxVel) || CartMaxVel <= 0.0 || !IsFinite(CartMaxAcc) || CartMaxAcc <= 0.0)
                throw new ArgumentOutOfRangeException("CartMaxVel", "笛卡尔速度/加速度上限必须为正有限数。");
            for (int b = 0; b < 3; b++)
            {
                if (!IsFinite(BaseXyz[b]))
                    throw new ArgumentOutOfRangeException("BaseXyz", "基座偏移必须是有限数。");
            }
            int slaveSeenMask = 0;
            for (int i = 0; i < 4; i++)
            {
                if (!IsFinite(HomeOffset[i]) || !IsFinite(ZeroCounts[i]))
                    throw new ArgumentOutOfRangeException("HomeOffset", "零点/偏置参数必须是有限数。");
                if (!IsFinite(CountsPerUnit[i]) || CountsPerUnit[i] == 0.0)
                    throw new ArgumentOutOfRangeException("CountsPerUnit", "轴 " + i + " 脉冲当量必须是非零有限数。");
                if (!IsFinite(JointMin[i]) || !IsFinite(JointMax[i]) || JointMin[i] >= JointMax[i])
                    throw new ArgumentOutOfRangeException("JointMin", "轴 " + i + " 软限位必须满足 JointMin < JointMax。");
                if (!IsFinite(JointMaxVel[i]) || JointMaxVel[i] <= 0.0 || !IsFinite(JointMaxAcc[i]) || JointMaxAcc[i] <= 0.0)
                    throw new ArgumentOutOfRangeException("JointMaxVel", "轴 " + i + " 最大速度/加速度必须为正有限数。");
                if (JointToSlave[i] >= 4 || (slaveSeenMask & (1 << JointToSlave[i])) != 0)
                    throw new ArgumentOutOfRangeException("JointToSlave", "JointToSlave 必须是 0..3 的唯一映射。");
                slaveSeenMask |= (1 << JointToSlave[i]);
                if (MaxFollowingErrorIdle[i] <= 0 || MaxFollowingErrorMoving[i] <= 0)
                    throw new ArgumentOutOfRangeException("MaxFollowingErrorIdle", "轴 " + i + " 跟随误差阈值必须为正整数。");
            }

            byte[] buf = new byte[ByteSize];
            Sc4iProtocol.WriteU32LE(buf, 0x000, 1u);
            Sc4iProtocol.WriteU32LE(buf, 0x004, 0u);
            Sc4iProtocol.WriteF64LE(buf, 0x008, ArmLength1Mm);
            Sc4iProtocol.WriteF64LE(buf, 0x010, ArmLength2Mm);

            for (int i = 0; i < 4; i++)
            {
                Sc4iProtocol.WriteF64LE(buf, 0x018 + i * 8, HomeOffset[i]);
                Sc4iProtocol.WriteF64LE(buf, 0x038 + i * 8, CountsPerUnit[i]);
                Sc4iProtocol.WriteF64LE(buf, 0x058 + i * 8, ZeroCounts[i]);
                Sc4iProtocol.WriteF64LE(buf, 0x078 + i * 8, JointMin[i]);
                Sc4iProtocol.WriteF64LE(buf, 0x098 + i * 8, JointMax[i]);
                Sc4iProtocol.WriteF64LE(buf, 0x0B8 + i * 8, JointMaxVel[i]);
                Sc4iProtocol.WriteF64LE(buf, 0x0D8 + i * 8, JointMaxAcc[i]);
                buf[0x0F8 + i] = JointToSlave[i];
            }

            Sc4iProtocol.WriteF64LE(buf, 0x100, BaseXyz[0]);
            Sc4iProtocol.WriteF64LE(buf, 0x108, BaseXyz[1]);
            Sc4iProtocol.WriteF64LE(buf, 0x110, BaseXyz[2]);
            Sc4iProtocol.WriteF64LE(buf, 0x118, VelocityFactor);
            Sc4iProtocol.WriteF64LE(buf, 0x120, CartMaxVel);
            Sc4iProtocol.WriteF64LE(buf, 0x128, CartMaxAcc);

            // Firmware validator requires exactly two 600 tags and two 630 tags (telemetry metadata only).
            uint[] driveTags = { 600u, 600u, 630u, 630u };
            for (int s = 0; s < 4; s++)
            {
                Sc4iSlaveIdentity id = Identities[s];
                if (id == null || !id.IsPopulated)
                    throw new InvalidOperationException("从站 " + s + " 缺少 0x1018 身份信息。");
                int off = 0x130 + s * 40;
                Sc4iProtocol.WriteU32LE(buf, off + 0x00, driveTags[s]);
                Sc4iProtocol.WriteU32LE(buf, off + 0x04, 1u);
                Sc4iProtocol.WriteU32LE(buf, off + 0x08, id.VendorId);
                Sc4iProtocol.WriteU32LE(buf, off + 0x0C, id.ProductCode);
                Sc4iProtocol.WriteU32LE(buf, off + 0x10, id.Revision);
                Sc4iProtocol.WriteU32LE(buf, off + 0x14, id.SerialNumber);
                Sc4iProtocol.WriteU32LE(buf, off + 0x18, ModeByteSize);
                Sc4iProtocol.WriteU32LE(buf, off + 0x1C, Expected605A);
                Sc4iProtocol.WriteU32LE(buf, off + 0x20, 1u);
                Sc4iProtocol.WriteU32LE(buf, off + 0x24, 0u);
            }

            Sc4iProtocol.WriteU32LE(buf, 0x1D0, 1u);
            Sc4iProtocol.WriteU32LE(buf, 0x1D4, 1u);
            Sc4iProtocol.WriteU32LE(buf, 0x1D8, 1u);
            Sc4iProtocol.WriteU32LE(buf, 0x1DC, PdoFreshTimeoutMs);
            Sc4iProtocol.WriteU32LE(buf, 0x1E0, HeartbeatTimeoutMs);
            Sc4iProtocol.WriteU32LE(buf, 0x1E4, JogTimeoutMs);
            Sc4iProtocol.WriteU32LE(buf, 0x1E8, SettleCycles);
            Sc4iProtocol.WriteU32LE(buf, 0x1EC, 0u);

            for (int i = 0; i < 4; i++)
            {
                Sc4iProtocol.WriteI32LE(buf, 0x1F0 + i * 4, MaxFollowingErrorIdle[i]);
                Sc4iProtocol.WriteI32LE(buf, 0x200 + i * 4, MaxFollowingErrorMoving[i]);
            }

            return buf;
        }
    }

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
