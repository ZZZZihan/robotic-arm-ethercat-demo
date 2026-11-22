using System;
using ScaraArm.Core.Protocol;

namespace ScaraArm.Tests
{
    internal static class Sc4iProtocolTests
    {
        public static int Run()
        {
            TestRunner t = new TestRunner("Sc4iProtocolTests");
            CodecTests(t);
            ConfigTests(t);
            return t.Count;
        }

        private static void CodecTests(TestRunner t)
        {
            byte[] helloReq = Sc4iProtocol.BuildHelloRequest(0x15);
            t.Check(helloReq.Length == 16, "Hello request length is 16");
            t.Check(helloReq[1] == 0xA5 && helloReq[2] == 0 && helloReq[3] == 16 && helloReq[4] == 0x01 && helloReq[6] == 0x15, "Hello request header");
            t.Check(helloReq[0] == Sc4iProtocol.CalculateChecksum(helloReq), "Hello request checksum");

            byte[] helloReply = Sc4iTestFrames.BuildHelloReply(0x15, 0x36BF0DE8485C3E0Eul, 2u);
            Sc4iHelloReply parsedHello = Sc4iProtocol.ParseHelloReply(helloReply, 0x15);
            t.Check(parsedHello.Magic == "SC4I0001" && parsedHello.BootId == 0x36BF0DE8485C3E0Eul && parsedHello.ProtocolVersion == 2u, "Hello reply parse");

            t.Throws<ProtocolException>(() => Sc4iProtocol.ParseHelloReply(helloReply, 0x16), "Hello wrong token rejected");
            byte[] badMagic = (byte[])helloReply.Clone();
            badMagic[16] = (byte)'X';
            badMagic[0] = Sc4iProtocol.CalculateChecksum(badMagic);
            t.Throws<ProtocolException>(() => Sc4iProtocol.ParseHelloReply(badMagic, 0x15), "Hello wrong magic rejected");

            byte[] ptp = Sc4iProtocol.BuildMoveAxisPtpPayload(1.25, 0.5, 2.0);
            t.Check(ptp.Length == 24, "PTP payload is 24 bytes");
            byte[] f2Req = Sc4iProtocol.BuildF2Request(Sc4iSubcmd.MoveAxisPtp, 2, 42u, 0x1122334455667788ul, 0xAABBCCDDEEFF0011ul, ptp);
            t.Check(f2Req.Length == 64, "PTP 0xF2 frame is 64 bytes");
            t.Check(f2Req[1] == 0xA5 && f2Req[2] == 0 && f2Req[3] == 64 && f2Req[4] == 0xF2, "PTP header");
            t.Check(f2Req[8] == 7 && f2Req[9] == 2, "PTP subcmd and joint index");
            t.Check(f2Req[0] == Sc4iProtocol.CalculateChecksum(f2Req), "PTP checksum");

            byte[] jog = Sc4iProtocol.BuildJogPayload(-0.3);
            byte[] f2Jog = Sc4iProtocol.BuildF2Request(Sc4iSubcmd.JogAxis, 1, 43u, 0x1122334455667788ul, 0xAABBCCDDEEFF0011ul, jog);
            t.Check(f2Jog.Length == 48, "Jog 0xF2 frame is 48 bytes");

            // Verify 384B telemetry parsing against real captured live OP values.
            byte[] rawOp = Sc4iTestFrames.BuildStatusReply(
                Sc4iSubcmd.Snapshot, 0, 0, 4, 100u, 0x36BF0DE8485C3E0Eul, 0x2026112100000010ul,
                0u, 0u, true, true, 0u, 0u, 0u, 255u, true, 0u, 352u, 352u, 52u,
                new int[] { -398616, -3023630, 909116, 58976 },
                new ushort[] { 0x1650, 0x1650, 0x1650, 0x1650 },
                new ushort[] { 0, 0, 0, 0 });
            Sc4iStatusReply st = Sc4iProtocol.ParseF2Reply(rawOp);
            t.Check(st.BusReady && st.ConfigLoaded && st.PdoOk && st.PdoCycles == 352u && st.TxPdoBytes == 52u, "Live OP header fields");
            t.Check(st.Slaves[0].ActualPositionCounts == -398616 && st.Slaves[0].StatusWord == 0x1650 && st.Slaves[0].ModeDisplay == 8, "Slave 0 telemetry");
            t.Near(st.Slaves[1].ActualRevolutions(262144.0), -3023630.0 / 262144.0, 1e-9, "Slave 1 rev conversion");
            t.Check(!st.Slaves[0].IsFault && !st.Slaves[0].IsOperationEnabled && st.Slaves[0].ValidMask == 0x1Fu, "Slave 0 flags");
            t.Check(Sc4iProtocol.FormatErrorCode(0x30u).Contains("心跳"), "FormatErrorCode(0x30) describes heartbeat timeout");
            t.Check(Sc4iProtocol.FormatErrorCode(0x2Eu).Contains("CSP"), "FormatErrorCode(0x2E) describes CSP mismatch");
        }

        private static void ConfigTests(TestRunner t)
        {
            Sc4iSlaveIdentity[] ids = new Sc4iSlaveIdentity[4];
            for (int i = 0; i < 4; i++)
                ids[i] = new Sc4iSlaveIdentity { VendorId = 0x00100000u, ProductCode = 0x000C0112u, Revision = 0x00010000u, SerialNumber = 0u };

            Sc4iMachineConfig cfg = Sc4iMachineConfig.CreateForScannedSlaves(ids, 6u);
            byte[] raw = cfg.Serialize();
            t.Check(raw.Length == 528, "MachineConfig is exactly 528 bytes");
            t.Check(BitConverter.ToUInt32(raw, 0x000) == 1u, "Config version = 1");
            t.Check(BitConverter.ToDouble(raw, 0x038) == 262144.0, "counts_per_unit[0] = 262144");
            t.Check(BitConverter.ToDouble(raw, 0x118) == 0.5, "factor = 0.5 strictly in (0,1)");
            t.Check(BitConverter.ToUInt32(raw, 0x130) == 600u && BitConverter.ToUInt32(raw, 0x158) == 600u
                && BitConverter.ToUInt32(raw, 0x180) == 630u && BitConverter.ToUInt32(raw, 0x1A8) == 630u, "2x600 and 2x630 tags");
            t.Check(BitConverter.ToUInt32(raw, 0x130 + 0x08) == 0x00100000u && BitConverter.ToUInt32(raw, 0x130 + 0x0C) == 0x000C0112u, "SV630N identity");
            t.Check(BitConverter.ToUInt32(raw, 0x130 + 0x18) == 1u && BitConverter.ToUInt32(raw, 0x130 + 0x1C) == 6u, "mode_sz=1, expected_605a=6");
            t.Check(BitConverter.ToUInt32(raw, 0x1D0) == 1u && BitConverter.ToUInt32(raw, 0x1E0) == 1000u, "timing fields");

            t.Throws<ArgumentNullException>(() => Sc4iProtocol.CalculateChecksum(null), "CalculateChecksum rejects null");

            cfg.Expected605A = 2u;
            t.Throws<ArgumentOutOfRangeException>(() => cfg.Serialize(), "Reject 0x605A=2");
            cfg.Expected605A = 6u;
            cfg.VelocityFactor = 1.0;
            t.Throws<ArgumentOutOfRangeException>(() => cfg.Serialize(), "Reject VelocityFactor=1.0");
            cfg.VelocityFactor = double.NaN;
            t.Throws<ArgumentOutOfRangeException>(() => cfg.Serialize(), "Reject VelocityFactor=NaN");
            cfg.VelocityFactor = 0.5;
            cfg.CountsPerUnit[0] = 0.0;
            t.Throws<ArgumentOutOfRangeException>(() => cfg.Serialize(), "Reject CountsPerUnit=0");
            cfg.CountsPerUnit[0] = 262144.0;
            cfg.JointMin[1] = cfg.JointMax[1];
            t.Throws<ArgumentOutOfRangeException>(() => cfg.Serialize(), "Reject JointMin>=JointMax");
            cfg.JointMin[1] = -8000.0;
            cfg.JointToSlave[2] = 0;
            t.Throws<ArgumentOutOfRangeException>(() => cfg.Serialize(), "Reject duplicate JointToSlave");
            cfg.JointToSlave[2] = 2;
            cfg.MaxFollowingErrorIdle[0] = 0;
            t.Throws<ArgumentOutOfRangeException>(() => cfg.Serialize(), "Reject non-positive following error");
            cfg.MaxFollowingErrorIdle[0] = 50000;
            t.Check(cfg.Serialize().Length == 528, "Valid config still serializes after restoring fields");
        }
    }
}
