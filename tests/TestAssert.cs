using System;
using System.Text;
using ScaraArm.Core.Protocol;

namespace ScaraArm.Tests
{
    internal sealed class TestRunner
    {
        private readonly string suiteName;
        public int Count { get; private set; }

        public TestRunner(string suiteName)
        {
            this.suiteName = suiteName;
        }

        public void Check(bool condition, string label)
        {
            if (!condition) throw new Exception(suiteName + " failed: " + label);
            Count++;
        }

        public void Near(double actual, double expected, double tol, string label)
        {
            if (Math.Abs(actual - expected) > tol)
                throw new Exception(suiteName + " failed: " + label + " (expected " + expected + ", got " + actual + ")");
            Count++;
        }

        public void Throws<T>(Action action, string label) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                Count++;
                return;
            }
            catch (Exception ex)
            {
                throw new Exception(suiteName + " (" + label + "): unexpected " + ex.GetType().Name + " (" + ex.Message + ")", ex);
            }
            throw new Exception(suiteName + " (" + label + "): expected " + typeof(T).Name);
        }
    }

    internal static class Sc4iTestFrames
    {
        public static byte[] BuildHelloReply(byte token, ulong bootId, uint protoVer)
        {
            byte[] buf = new byte[100];
            buf[1] = 0xA5;
            buf[2] = 0;
            buf[3] = 100;
            buf[4] = 0x01;
            buf[6] = token;
            Encoding.ASCII.GetBytes("SC4I0001").CopyTo(buf, 16);
            BitConverter.GetBytes(bootId).CopyTo(buf, 24);
            BitConverter.GetBytes(protoVer).CopyTo(buf, 32);
            buf[0] = Sc4iProtocol.CalculateChecksum(buf);
            return buf;
        }

        public static byte[] BuildStatusReply(
            Sc4iSubcmd sub, byte motionState, byte estop, byte slaveCnt, uint seq, ulong bootId, ulong sid,
            uint err, uint latched, bool busReady, bool cfgLoaded, uint enMask, uint authMask, uint eningMask,
            uint faultSlave, bool pdoOk, uint pdoAge, uint pdoCyc, uint readyCyc, uint txPdoBytes,
            int[] actPos, ushort[] sw, ushort[] drvErr, uint validMask = 0x1Fu)
        {
            byte[] buf = new byte[384];
            buf[1] = 0xA5;
            buf[2] = 0x01;
            buf[3] = 0x80;
            buf[4] = 0xF2;
            buf[6] = (byte)(seq & 0x7F);
            buf[8] = (byte)sub;
            buf[9] = motionState;
            buf[10] = estop;
            buf[11] = slaveCnt;
            BitConverter.GetBytes(seq).CopyTo(buf, 12);
            BitConverter.GetBytes(bootId).CopyTo(buf, 16);
            BitConverter.GetBytes(sid).CopyTo(buf, 24);
            BitConverter.GetBytes(err).CopyTo(buf, 32);
            BitConverter.GetBytes(latched).CopyTo(buf, 36);
            Encoding.ASCII.GetBytes("SC4I0001").CopyTo(buf, 40);
            BitConverter.GetBytes(busReady ? 1u : 0u).CopyTo(buf, 48);
            BitConverter.GetBytes(cfgLoaded ? 1u : 0u).CopyTo(buf, 52);
            BitConverter.GetBytes(enMask).CopyTo(buf, 56);
            BitConverter.GetBytes(authMask).CopyTo(buf, 60);
            BitConverter.GetBytes(eningMask).CopyTo(buf, 64);
            BitConverter.GetBytes(faultSlave).CopyTo(buf, 68);
            BitConverter.GetBytes(pdoOk ? 1u : 0u).CopyTo(buf, 72);
            BitConverter.GetBytes(pdoAge).CopyTo(buf, 76);
            BitConverter.GetBytes(pdoCyc).CopyTo(buf, 80);
            BitConverter.GetBytes(txPdoBytes).CopyTo(buf, 92);
            BitConverter.GetBytes(readyCyc).CopyTo(buf, 96);
            BitConverter.GetBytes(2u).CopyTo(buf, 100);
            for (int s = 0; s < 4; s++)
            {
                int off = 112 + s * 56;
                if (slaveCnt == 4)
                {
                    BitConverter.GetBytes(0x00100000u).CopyTo(buf, off + 0);
                    BitConverter.GetBytes(0x000C0112u).CopyTo(buf, off + 4);
                    BitConverter.GetBytes(0x00010000u).CopyTo(buf, off + 8);
                    BitConverter.GetBytes(0u).CopyTo(buf, off + 12);
                }
                BitConverter.GetBytes(actPos[s]).CopyTo(buf, off + 16);
                BitConverter.GetBytes(actPos[s]).CopyTo(buf, off + 20);
                BitConverter.GetBytes(sw[s]).CopyTo(buf, off + 24);
                BitConverter.GetBytes((ushort)(busReady ? 8 : 0)).CopyTo(buf, off + 26);
                BitConverter.GetBytes(drvErr[s]).CopyTo(buf, off + 32);
                BitConverter.GetBytes(busReady ? validMask : 0u).CopyTo(buf, off + 48);
            }
            BitConverter.GetBytes(200000000u).CopyTo(buf, 368);
            BitConverter.GetBytes(12345u).CopyTo(buf, 376);
            buf[0] = Sc4iProtocol.CalculateChecksum(buf);
            return buf;
        }
    }
}
