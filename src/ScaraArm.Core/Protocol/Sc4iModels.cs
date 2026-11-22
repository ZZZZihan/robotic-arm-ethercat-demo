using System.Globalization;

namespace ScaraArm.Core.Protocol
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

        public bool IsPdoFresh(uint maxFreshAgeMs)
        {
            return BusReady && PdoOk && PdoAgeMs != uint.MaxValue && PdoAgeMs < maxFreshAgeMs;
        }

        public bool TryExtractLiveCounts(out int[] counts)
        {
            counts = null;
            if (!BusReady || Slaves == null || Slaves.Length < 4) return false;
            int[] arr = new int[4];
            for (int i = 0; i < 4; i++)
            {
                if (Slaves[i] == null || Slaves[i].ValidMask == 0) return false;
                arr[i] = Slaves[i].ActualPositionCounts;
            }
            counts = arr;
            return true;
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
}
