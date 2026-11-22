using System;

namespace ScaraArm.Core.Protocol
{
    /// <summary>
    /// 528-byte machine configuration uploaded to SC4I0001 v2 firmware prior to EtherCAT OP transition.
    /// </summary>
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

        public byte[] Serialize()
        {
            Validate();

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

        private void Validate()
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
        }

        private static bool IsFinite(double v)
        {
            return !Double.IsNaN(v) && !Double.IsInfinity(v);
        }
    }
}
