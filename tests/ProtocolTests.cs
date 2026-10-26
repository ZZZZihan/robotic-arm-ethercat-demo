using System;
using NewCard.Core;

namespace NewCard.Tests
{
    /// <summary>Offline protocol vectors; no card, UDP socket, or vendor program is used.</summary>
    public static class ProtocolTests
    {
        private static int count;

        public static int Run()
        {
            count = 0;
            byte[] challenge = Sequence();

            // Independently calculated vector: B5 + 64 + 01 + 2A + sum(0..71) = 0xB40.
            byte[] request = CardProtocol.BuildEchoRequest(0x2A, challenge);
            byte[] expectedRequest = Hex(
                "40 B5 00 64 01 00 2A 00 " +
                "00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F " +
                "10 11 12 13 14 15 16 17 18 19 1A 1B 1C 1D 1E 1F " +
                "20 21 22 23 24 25 26 27 28 29 2A 2B 2C 2D 2E 2F " +
                "30 31 32 33 34 35 36 37 38 39 3A 3B 3C 3D 3E 3F " +
                "40 41 42 43 44 45 46 47 " +
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00");
            BytesEqual(expectedRequest, request, "100-byte request vector");

            // App reply: LE window 0x78563412, challenge at 16..87, sum = 0xC44.
            byte[] reply = AppVector();
            EchoReply parsed = CardProtocol.ParseEchoReply(reply, 0x2A, challenge);
            Equal((byte)0xA5, parsed.Marker, "App marker");
            Equal(0x78563412u, parsed.WindowId, "little-endian window id");

            Throws<ArgumentNullException>(() => CardProtocol.BuildEchoRequest(0, null), "null challenge");
            Throws<ArgumentException>(() => CardProtocol.BuildEchoRequest(0, new byte[71]), "short challenge");
            Throws<ArgumentException>(() => CardProtocol.BuildEchoRequest(0, new byte[73]), "long challenge");
            Throws<ArgumentOutOfRangeException>(() => CardProtocol.BuildEchoRequest(0xF1, challenge), "reserved token F1");
            Throws<ArgumentOutOfRangeException>(() => CardProtocol.BuildEchoRequest(0xFF, challenge), "reserved token FF");
            Throws<ArgumentNullException>(() => CardProtocol.ParseEchoReply(null, 0x2A, challenge), "null packet");
            Throws<ArgumentNullException>(() => CardProtocol.ParseEchoReply(reply, 0x2A, null), "parse null challenge");
            Throws<ArgumentException>(() => CardProtocol.ParseEchoReply(reply, 0x2A, new byte[0]), "parse wrong challenge size");
            Throws<ArgumentOutOfRangeException>(() => CardProtocol.ParseEchoReply(reply, 0xF1, challenge), "parse reserved token");
            Throws<ProtocolException>(() => CardProtocol.ParseEchoReply(new byte[0], 0x2A, challenge), "empty reply");
            Throws<ProtocolException>(() => CardProtocol.ParseEchoReply(new byte[99], 0x2A, challenge), "truncated reply");
            Throws<ProtocolException>(() => CardProtocol.ParseEchoReply(new byte[101], 0x2A, challenge), "trailing data");
            Throws<ProtocolException>(() => CardProtocol.ParseEchoReply(new byte[16], 0x2A, challenge), "short ACK is not echo success");
            RejectMutation(reply, challenge, 0, 0, false, "checksum");
            RejectMutation(reply, challenge, 2, 1, true, "big-endian length high byte");
            RejectMutation(reply, challenge, 3, 99, true, "declared length differs");
            RejectMutation(reply, challenge, 1, 0xC5, true, "active report marker");
            RejectMutation(reply, challenge, 4, 0x10, true, "status opcode");
            RejectMutation(reply, challenge, 5, 1, true, "subcommand");
            RejectMutation(reply, challenge, 6, 0x2B, true, "stale token");
            RejectMutation(reply, challenge, 7, 0xE1, true, "checksum error ACK");
            RejectMutation(reply, challenge, 7, 0xE2, true, "invalid command ACK");
            RejectMutation(reply, challenge, 7, 1, true, "nonzero ACK");
            RejectMutation(reply, challenge, 16, 0xFF, true, "first echo byte");
            RejectMutation(reply, challenge, 87, 0, true, "last echo byte");
            byte[] wrongChallenge = Sequence();
            wrongChallenge[10] ^= 0x80;
            Throws<ProtocolException>(() => CardProtocol.ParseEchoReply(reply, 0x2A, wrongChallenge), "different request challenge");

            // Actual Boot_017 sets its 84-byte backup buffer to zero, without copying the request.
            byte[] boot = new byte[100];
            Buffer.BlockCopy(Hex("58 B5 00 64 01 00 2A 00 12 34 56 78 00 00 00 00"), 0, boot, 0, 16);
            ProtocolException bootFailure = Throws<ProtocolException>(
                () => CardProtocol.ParseEchoReply(boot, 0x2A, challenge), "real Boot response rejected");
            True(bootFailure.Message.IndexOf("Boot", StringComparison.Ordinal) >= 0, "Boot reason is visible");
            RejectMutation(reply, challenge, 1, 0xB5, true, "Boot with forged echo still rejected");

            foreach (byte token in new byte[] { 0, 0xF0 })
            {
                byte[] boundaryRequest = CardProtocol.BuildEchoRequest(token, challenge);
                Equal(token, boundaryRequest[6], "boundary request token");
                byte[] boundaryReply = AppVector();
                boundaryReply[6] = token;
                RepairChecksum(boundaryReply);
                Equal((byte)0xA5, CardProtocol.ParseEchoReply(boundaryReply, token, challenge).Marker, "boundary reply token");
            }

            byte[] highWindow = AppVector();
            for (int i = 8; i < 12; i++) highWindow[i] = 0xFF;
            RepairChecksum(highWindow);
            Equal(UInt32.MaxValue, CardProtocol.ParseEchoReply(highWindow, 0x2A, challenge).WindowId, "unsigned window id");

            byte[] requestCopy = CardProtocol.BuildEchoRequest(0x2A, challenge);
            challenge[0] = 0xFF;
            Equal((byte)0, requestCopy[8], "request owns copied challenge");
            return count;
        }

        private static byte[] AppVector()
        {
            return Hex(
                "44 A5 00 64 01 00 2A 00 12 34 56 78 00 00 00 00 " +
                "00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F " +
                "10 11 12 13 14 15 16 17 18 19 1A 1B 1C 1D 1E 1F " +
                "20 21 22 23 24 25 26 27 28 29 2A 2B 2C 2D 2E 2F " +
                "30 31 32 33 34 35 36 37 38 39 3A 3B 3C 3D 3E 3F " +
                "40 41 42 43 44 45 46 47 " +
                "00 00 00 00 00 00 00 00 00 00 00 00");
        }

        private static void RejectMutation(byte[] source, byte[] challenge, int offset, byte value, bool repair, string label)
        {
            byte[] packet = (byte[])source.Clone();
            packet[offset] = value;
            if (repair) RepairChecksum(packet);
            Throws<ProtocolException>(() => CardProtocol.ParseEchoReply(packet, 0x2A, challenge), label);
        }

        private static void RepairChecksum(byte[] packet)
        {
            int sum = 0;
            for (int i = 1; i < packet.Length; i++) sum += packet[i];
            packet[0] = (byte)(sum % 256);
        }

        private static byte[] Sequence()
        {
            byte[] bytes = new byte[72];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)i;
            return bytes;
        }

        private static byte[] Hex(string value)
        {
            string[] parts = value.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            byte[] bytes = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++) bytes[i] = Convert.ToByte(parts[i], 16);
            return bytes;
        }

        private static void BytesEqual(byte[] expected, byte[] actual, string label)
        {
            if (expected.Length != actual.Length) throw new Exception(label + ": length mismatch.");
            for (int i = 0; i < expected.Length; i++)
                if (expected[i] != actual[i]) throw new Exception(label + ": byte " + i + " differs.");
            count++;
        }

        private static void Equal<T>(T expected, T actual, string label)
        {
            if (!Object.Equals(expected, actual)) throw new Exception(label + ": expected " + expected + ", actual " + actual + ".");
            count++;
        }

        private static void True(bool condition, string label)
        {
            if (!condition) throw new Exception(label + ": assertion failed.");
            count++;
        }

        private static T Throws<T>(Action action, string label) where T : Exception
        {
            try { action(); }
            catch (T exception) { count++; return exception; }
            catch (Exception exception) { throw new Exception(label + ": unexpected " + exception.GetType().Name + ".", exception); }
            throw new Exception(label + ": expected " + typeof(T).Name + ".");
        }
    }
}
