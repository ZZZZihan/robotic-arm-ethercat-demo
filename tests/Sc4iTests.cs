using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NewCard.Core;

namespace NewCard.Tests
{
    public static class Sc4iTests
    {
        private static int count;

        private static void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("Sc4iTests failed: " + label);
            count++;
        }

        private static void Throws<T>(Action action, string label) where T : Exception
        {
            try { action(); }
            catch (T) { count++; return; }
            catch (Exception ex) { throw new Exception(label + ": unexpected " + ex.GetType().Name + " (" + ex.Message + ")", ex); }
            throw new Exception(label + ": expected " + typeof(T).Name);
        }

        public static int Run()
        {
            count = 0;
            CodecTests();
            ConfigTests();
            LoopbackClientTests().GetAwaiter().GetResult();
            HardeningLoopbackTests().GetAwaiter().GetResult();
            return count;
        }

        private static void CodecTests()
        {
            byte[] helloReq = Sc4iProtocol.BuildHelloRequest(0x15);
            Check(helloReq.Length == 16, "Hello request length is 16");
            Check(helloReq[1] == 0xA5 && helloReq[2] == 0 && helloReq[3] == 16 && helloReq[4] == 0x01 && helloReq[6] == 0x15, "Hello request header");
            Check(helloReq[0] == Sc4iProtocol.CalculateChecksum(helloReq), "Hello request checksum");

            byte[] helloReply = BuildFakeHelloReply(0x15, 0x36BF0DE8485C3E0Eul, 2u);
            Sc4iHelloReply parsedHello = Sc4iProtocol.ParseHelloReply(helloReply, 0x15);
            Check(parsedHello.Magic == "SC4I0001" && parsedHello.BootId == 0x36BF0DE8485C3E0Eul && parsedHello.ProtocolVersion == 2u, "Hello reply parse");

            Throws<ProtocolException>(() => Sc4iProtocol.ParseHelloReply(helloReply, 0x16), "Hello wrong token rejected");
            byte[] badMagic = (byte[])helloReply.Clone();
            badMagic[16] = (byte)'X';
            badMagic[0] = Sc4iProtocol.CalculateChecksum(badMagic);
            Throws<ProtocolException>(() => Sc4iProtocol.ParseHelloReply(badMagic, 0x15), "Hello wrong magic rejected");

            byte[] ptp = Sc4iProtocol.BuildMoveAxisPtpPayload(1.25, 0.5, 2.0);
            Check(ptp.Length == 24, "PTP payload is 24 bytes");
            byte[] f2Req = Sc4iProtocol.BuildF2Request(Sc4iSubcmd.MoveAxisPtp, 2, 42u, 0x1122334455667788ul, 0xAABBCCDDEEFF0011ul, ptp);
            Check(f2Req.Length == 64, "PTP 0xF2 frame is 64 bytes");
            Check(f2Req[1] == 0xA5 && f2Req[2] == 0 && f2Req[3] == 64 && f2Req[4] == 0xF2, "PTP header");
            Check(f2Req[8] == 7 && f2Req[9] == 2, "PTP subcmd and joint index");
            Check(f2Req[0] == Sc4iProtocol.CalculateChecksum(f2Req), "PTP checksum");

            byte[] jog = Sc4iProtocol.BuildJogPayload(-0.3);
            byte[] f2Jog = Sc4iProtocol.BuildF2Request(Sc4iSubcmd.JogAxis, 1, 43u, 0x1122334455667788ul, 0xAABBCCDDEEFF0011ul, jog);
            Check(f2Jog.Length == 48, "Jog 0xF2 frame is 48 bytes");

            // Verify 384B telemetry parsing against real captured live OP values.
            byte[] rawOp = BuildFakeStatusReply(
                Sc4iSubcmd.Snapshot, 0, 0, 4, 100u, 0x36BF0DE8485C3E0Eul, 0x2026112100000010ul,
                0u, 0u, true, true, 0u, 0u, 0u, 255u, true, 0u, 352u, 352u, 52u,
                new int[] { -398616, -3023630, 909116, 58976 },
                new ushort[] { 0x1650, 0x1650, 0x1650, 0x1650 },
                new ushort[] { 0, 0, 0, 0 });
            Sc4iStatusReply st = Sc4iProtocol.ParseF2Reply(rawOp);
            Check(st.BusReady && st.ConfigLoaded && st.PdoOk && st.PdoCycles == 352u && st.TxPdoBytes == 52u, "Live OP header fields");
            Check(st.Slaves[0].ActualPositionCounts == -398616 && st.Slaves[0].StatusWord == 0x1650 && st.Slaves[0].ModeDisplay == 8, "Slave 0 telemetry");
            Check(Math.Abs(st.Slaves[1].ActualRevolutions(262144.0) - (-3023630.0 / 262144.0)) < 1e-9, "Slave 1 rev conversion");
            Check(!st.Slaves[0].IsFault && !st.Slaves[0].IsOperationEnabled && st.Slaves[0].ValidMask == 0x1Fu, "Slave 0 flags");
        }

        private static void ConfigTests()
        {
            Sc4iSlaveIdentity[] ids = new Sc4iSlaveIdentity[4];
            for (int i = 0; i < 4; i++)
                ids[i] = new Sc4iSlaveIdentity { VendorId = 0x00100000u, ProductCode = 0x000C0112u, Revision = 0x00010000u, SerialNumber = 0u };

            Sc4iMachineConfig cfg = Sc4iMachineConfig.CreateForScannedSlaves(ids, 6u);
            byte[] raw = cfg.Serialize();
            Check(raw.Length == 528, "MachineConfig is exactly 528 bytes");
            Check(BitConverter.ToUInt32(raw, 0x000) == 1u, "Config version = 1");
            Check(BitConverter.ToDouble(raw, 0x038) == 262144.0, "counts_per_unit[0] = 262144");
            Check(BitConverter.ToDouble(raw, 0x118) == 0.5, "factor = 0.5 strictly in (0,1)");
            Check(BitConverter.ToUInt32(raw, 0x130) == 600u && BitConverter.ToUInt32(raw, 0x158) == 600u
                && BitConverter.ToUInt32(raw, 0x180) == 630u && BitConverter.ToUInt32(raw, 0x1A8) == 630u, "2x600 and 2x630 tags");
            Check(BitConverter.ToUInt32(raw, 0x130 + 0x08) == 0x00100000u && BitConverter.ToUInt32(raw, 0x130 + 0x0C) == 0x000C0112u, "SV630N identity");
            Check(BitConverter.ToUInt32(raw, 0x130 + 0x18) == 1u && BitConverter.ToUInt32(raw, 0x130 + 0x1C) == 6u, "mode_sz=1, expected_605a=6");
            Check(BitConverter.ToUInt32(raw, 0x1D0) == 1u && BitConverter.ToUInt32(raw, 0x1E0) == 1000u, "timing fields");

            Throws<ArgumentNullException>(() => Sc4iProtocol.CalculateChecksum(null), "CalculateChecksum rejects null");

            cfg.Expected605A = 2u;
            Throws<ArgumentOutOfRangeException>(() => cfg.Serialize(), "Reject 0x605A=2");
            cfg.Expected605A = 6u;
            cfg.VelocityFactor = 1.0;
            Throws<ArgumentOutOfRangeException>(() => cfg.Serialize(), "Reject VelocityFactor=1.0");
            cfg.VelocityFactor = double.NaN;
            Throws<ArgumentOutOfRangeException>(() => cfg.Serialize(), "Reject VelocityFactor=NaN");
            cfg.VelocityFactor = 0.5;
            cfg.CountsPerUnit[0] = 0.0;
            Throws<ArgumentOutOfRangeException>(() => cfg.Serialize(), "Reject CountsPerUnit=0");
            cfg.CountsPerUnit[0] = 262144.0;
            cfg.JointMin[1] = cfg.JointMax[1];
            Throws<ArgumentOutOfRangeException>(() => cfg.Serialize(), "Reject JointMin>=JointMax");
            cfg.JointMin[1] = -8000.0;
            cfg.JointToSlave[2] = 0;
            Throws<ArgumentOutOfRangeException>(() => cfg.Serialize(), "Reject duplicate JointToSlave");
            cfg.JointToSlave[2] = 2;
            cfg.MaxFollowingErrorIdle[0] = 0;
            Throws<ArgumentOutOfRangeException>(() => cfg.Serialize(), "Reject non-positive following error");
            cfg.MaxFollowingErrorIdle[0] = 50000;
            Check(cfg.Serialize().Length == 528, "Valid config still serializes after restoring fields");
        }

        private static async Task LoopbackClientTests()
        {
            const ulong bootId = 0x123456789ABCDEF0ul;
            ulong boundSid = 0;
            bool cfgLoaded = false;
            bool busReady = false;
            byte slaveCount = 0;
            uint enabledMask = 0;
            uint authMask = 0;
            uint latchedFault = 0;
            int[] positions = { 262144, -524288, 0, 131072 };
            ushort[] statusWords = { 0x1650, 0x1650, 0x1650, 0x1650 };
            ushort[] driveErrors = { 0, 0, 0, 0 };
            double lastPtpTarget = double.NaN;
            int lastPtpAxis = -1;
            int jogKeepaliveCount = 0;
            int faultResetMaskCalls = 0;
            int faultResetLatchCalls = 0;

            using (UdpClient server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            using (CancellationTokenSource serverCts = new CancellationTokenSource())
            using (Sc4iClient client = new Sc4iClient())
            {
                int port = ((IPEndPoint)server.Client.LocalEndPoint).Port;
                Task serverLoop = Task.Run(async () =>
                {
                    Task<UdpReceiveResult> recvTask = server.ReceiveAsync();
                    while (!serverCts.IsCancellationRequested)
                    {
                        Task done = await Task.WhenAny(recvTask, Task.Delay(50, serverCts.Token)).ConfigureAwait(false);
                        if (done != recvTask) continue;
                        UdpReceiveResult rx = await recvTask.ConfigureAwait(false);
                        recvTask = server.ReceiveAsync();
                        byte[] pkt = rx.Buffer;
                        if (pkt.Length < 8 || pkt[0] != Sc4iProtocol.CalculateChecksum(pkt)) continue;
                        byte cmd = pkt[4];
                        if (cmd == 0x01 && pkt.Length == 16)
                        {
                            byte[] rep = BuildFakeHelloReply(pkt[6], bootId, 2u);
                            await server.SendAsync(rep, rep.Length, rx.RemoteEndPoint).ConfigureAwait(false);
                        }
                        else if (cmd == 0xF2 && pkt.Length >= 40)
                        {
                            Sc4iSubcmd sub = (Sc4iSubcmd)pkt[8];
                            byte arg = pkt[9];
                            uint seq = BitConverter.ToUInt32(pkt, 12);
                            ulong sid = BitConverter.ToUInt64(pkt, 24);
                            uint err = 0;

                            if (sub == Sc4iSubcmd.AcquireSession)
                            {
                                boundSid = sid;
                            }
                            else if (sub == Sc4iSubcmd.ScanSlaves)
                            {
                                slaveCount = 4;
                            }
                            else if (sub == Sc4iSubcmd.UploadConfig)
                            {
                                err = pkt.Length == 568 ? 0u : 0x28u;
                                if (err == 0) cfgLoaded = true;
                            }
                            else if (sub == Sc4iSubcmd.BusInit)
                            {
                                err = (cfgLoaded && slaveCount == 4) ? 0u : 0x29u;
                                if (err == 0) busReady = true;
                            }
                            else if (sub == Sc4iSubcmd.EnableAxes)
                            {
                                enabledMask |= arg;
                                authMask |= arg;
                                for (int i = 0; i < 4; i++)
                                    if ((arg & (1 << i)) != 0) statusWords[i] = 0x1627;
                            }
                            else if (sub == Sc4iSubcmd.MoveAxisPtp)
                            {
                                lastPtpAxis = arg;
                                lastPtpTarget = BitConverter.ToDouble(pkt, 40);
                                positions[arg] = (int)Math.Round(lastPtpTarget * 262144.0);
                            }
                            else if (sub == Sc4iSubcmd.JogKeepalive)
                            {
                                jogKeepaliveCount++;
                            }
                            else if (sub == Sc4iSubcmd.StopHold)
                            {
                                authMask = 0;
                            }
                            else if (sub == Sc4iSubcmd.FaultReset)
                            {
                                if (arg != 0)
                                {
                                    faultResetMaskCalls++;
                                    for (int i = 0; i < 4; i++)
                                        if ((arg & (1 << i)) != 0) { statusWords[i] = 0x1650; driveErrors[i] = 0; enabledMask &= ~(1u << i); }
                                }
                                else
                                {
                                    faultResetLatchCalls++;
                                    latchedFault = 0;
                                }
                            }

                            byte[] rep = BuildFakeStatusReply(
                                sub, 0, 0, slaveCount, seq, bootId, boundSid, err, latchedFault,
                                busReady, cfgLoaded, enabledMask, authMask, 0u, 255u, busReady, 0u,
                                busReady ? 100u : 0u, busReady ? 100u : 0u, busReady ? 52u : 0u,
                                positions, statusWords, driveErrors);
                            await server.SendAsync(rep, rep.Length, rx.RemoteEndPoint).ConfigureAwait(false);
                        }
                    }
                });

                Sc4iStatusReply st = await client.ConnectAsync("127.0.0.1", 0, "127.0.0.1", port, 2000, CancellationToken.None).ConfigureAwait(false);
                Check(client.IsConnected && client.BootId == bootId && !st.BusReady, "ConnectAsync succeeded read-only");

                st = await client.ScanSlavesAsync(CancellationToken.None).ConfigureAwait(false);
                Check(st.SlaveCount == 4 && !st.BusReady, "ScanSlavesAsync found 4 slaves in PRE-OP");

                st = await client.InitBusToOpAsync(CancellationToken.None).ConfigureAwait(false);
                Check(st.BusReady && st.PdoOk && st.EnabledMask == 0u, "InitBusToOpAsync entered OP with motors disabled");

                Throws<InvalidOperationException>(() =>
                    client.MoveAxisRelativeAsync(0, 0.25, 0.5, 1.0, CancellationToken.None).GetAwaiter().GetResult(),
                    "Motion blocked while axis disabled");
                Throws<ArgumentOutOfRangeException>(() =>
                    client.MoveAxisRelativeAsync(0, 25.0, 0.5, 1.0, CancellationToken.None).GetAwaiter().GetResult(),
                    "Excessive step blocked by safety cap");

                st = await client.EnableAxisAsync(0, CancellationToken.None).ConfigureAwait(false);
                Check(st.EnabledMask == 1u && st.AuthorizedMask == 1u, "EnableAxisAsync enabled only axis 0");

                st = await client.MoveAxisRelativeAsync(0, 0.25, 0.5, 1.0, CancellationToken.None).ConfigureAwait(false);
                Check(lastPtpAxis == 0 && Math.Abs(lastPtpTarget - 1.25) < 1e-6, "Relative PTP converted to absolute target");

                await client.StartJogAsync(0, -0.2, CancellationToken.None).ConfigureAwait(false);
                await client.SendJogKeepaliveAsync(CancellationToken.None).ConfigureAwait(false);
                Check(jogKeepaliveCount == 1, "Jog keepalive delivered");

                st = await client.StopHoldAsync(CancellationToken.None).ConfigureAwait(false);
                Check(st.AuthorizedMask == 0u && st.EnabledMask == 1u, "StopHold cleared auth while keeping torque");

                // Simulate fault on slave 1 + latched fault and verify 2-step reset
                statusWords[1] = 0x1658;
                driveErrors[1] = 0x2310;
                latchedFault = 0x2Du;
                st = await client.ClearFaultsAndLatchAsync(CancellationToken.None).ConfigureAwait(false);
                Check(faultResetMaskCalls == 1 && faultResetLatchCalls == 1 && st.LatchedFault == 0u, "2-step fault reset cleared drive and controller latch");

                client.Disconnect();
                Check(!client.IsConnected, "Disconnected cleanly");
                serverCts.Cancel();
                try { await serverLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }

        private static async Task HardeningLoopbackTests()
        {
            const ulong bootId = 0xCAFEBABE11223344ul;

            // 1. Strict IPv4 validation rejects shorthand / blank / broadcast addresses
            using (Sc4iClient badIpClient = new Sc4iClient())
            {
                Throws<ArgumentException>(() =>
                    badIpClient.ConnectAsync("192.168.12", 0, "192.168.8.20", 5000, 500, CancellationToken.None).GetAwaiter().GetResult(),
                    "Reject 3-octet shorthand local IP");
                Throws<ArgumentException>(() =>
                    badIpClient.ConnectAsync("127.0.0.1", 0, "192.168.20", 5000, 500, CancellationToken.None).GetAwaiter().GetResult(),
                    "Reject 3-octet shorthand card IP");
                Throws<ArgumentException>(() =>
                    badIpClient.ConnectAsync(" ", 0, "127.0.0.1", 5000, 500, CancellationToken.None).GetAwaiter().GetResult(),
                    "Reject blank local IP");
            }

            // 2. Stale reply filtering, monotonic reconnect recovery, 0x36/0x35 handling, and pre-motion PDO/ValidMask/Fault interlocks
            ulong activeSid = 0;
            uint lastAcceptedSeq = 0;
            bool busReady = false;
            bool forceSessionExpired = false;
            bool hideSnapSeq = false;
            int seqReplayCount = 0;
            uint pdoAgeMs = 0;
            uint validMaskOverride = 0x1Fu;
            int dropRepliesCount = 0;
            int[] positions = { 262144, 0, 0, 0 };
            ushort[] statusWords = { 0x1627, 0x1650, 0x1650, 0x1650 };
            ushort[] driveErrors = { 0, 0, 0, 0 };

            using (UdpClient server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            using (CancellationTokenSource serverCts = new CancellationTokenSource())
            using (Sc4iClient client = new Sc4iClient())
            {
                int port = ((IPEndPoint)server.Client.LocalEndPoint).Port;
                Task serverLoop = Task.Run(async () =>
                {
                    Task<UdpReceiveResult> recvTask = server.ReceiveAsync();
                    while (!serverCts.IsCancellationRequested)
                    {
                        Task done = await Task.WhenAny(recvTask, Task.Delay(30, serverCts.Token)).ConfigureAwait(false);
                        if (done != recvTask) continue;
                        UdpReceiveResult rx = await recvTask.ConfigureAwait(false);
                        recvTask = server.ReceiveAsync();
                        byte[] pkt = rx.Buffer;
                        if (pkt.Length < 8 || pkt[0] != Sc4iProtocol.CalculateChecksum(pkt)) continue;
                        if (dropRepliesCount > 0)
                        {
                            dropRepliesCount--;
                            continue;
                        }

                        byte cmd = pkt[4];
                        if (cmd == 0x01 && pkt.Length == 16)
                        {
                            // Inject a stale Hello reply with wrong token first; client must skip it and accept the matching one.
                            byte staleTok = (byte)((pkt[6] + 1) % 240);
                            byte[] staleHello = BuildFakeHelloReply(staleTok, bootId, 2u);
                            await server.SendAsync(staleHello, staleHello.Length, rx.RemoteEndPoint).ConfigureAwait(false);

                            byte[] goodHello = BuildFakeHelloReply(pkt[6], bootId, 2u);
                            await server.SendAsync(goodHello, goodHello.Length, rx.RemoteEndPoint).ConfigureAwait(false);
                        }
                        else if (cmd == 0xF2 && pkt.Length >= 40)
                        {
                            Sc4iSubcmd sub = (Sc4iSubcmd)pkt[8];
                            uint seq = BitConverter.ToUInt32(pkt, 12);
                            ulong sid = BitConverter.ToUInt64(pkt, 24);
                            uint err = 0;

                            if (sub == Sc4iSubcmd.Snapshot)
                            {
                                // Snapshot returns active session ID and optionally hides last accepted seq (0).
                                byte[] snap = BuildFakeStatusReplyWithValid(
                                    sub, 0, 0, 4, hideSnapSeq ? 0u : lastAcceptedSeq, bootId, activeSid, 0u, 0u,
                                    busReady, busReady, busReady ? 1u : 0u, busReady ? 1u : 0u, 0u, 255u,
                                    busReady, pdoAgeMs, 100u, 100u, 52u, positions, statusWords, driveErrors, validMaskOverride);
                                await server.SendAsync(snap, snap.Length, rx.RemoteEndPoint).ConfigureAwait(false);
                                continue;
                            }

                            if (sub == Sc4iSubcmd.AcquireSession)
                            {
                                if (activeSid == 0)
                                {
                                    activeSid = sid;
                                    lastAcceptedSeq = seq;
                                }
                                else if (sid != activeSid)
                                {
                                    err = 0x36u;
                                }
                                else if (seq <= lastAcceptedSeq)
                                {
                                    err = 0x35u;
                                    seqReplayCount++;
                                }
                                else
                                {
                                    lastAcceptedSeq = seq;
                                }
                            }
                            else if (forceSessionExpired)
                            {
                                err = 0x36u;
                            }
                            else
                            {
                                if (seq <= lastAcceptedSeq) err = 0x35u;
                                else lastAcceptedSeq = seq;
                            }

                            // Inject a stale mismatched 0xF2 reply (wrong subcmd & stale seq) before the real reply.
                            byte[] staleF2 = BuildFakeStatusReplyWithValid(
                                Sc4iSubcmd.JogKeepalive, 0, 0, 4, seq > 1 ? seq - 1 : 999u, bootId, activeSid, 0u, 0u,
                                busReady, busReady, 1u, 1u, 0u, 255u, busReady, 0u, 100u, 100u, 52u,
                                positions, statusWords, driveErrors, 0x1Fu);
                            await server.SendAsync(staleF2, staleF2.Length, rx.RemoteEndPoint).ConfigureAwait(false);

                            byte[] realF2 = BuildFakeStatusReplyWithValid(
                                sub, 0, 0, 4, (err == 0x35u && !hideSnapSeq) ? lastAcceptedSeq : seq, bootId, activeSid, err, 0u,
                                busReady, busReady, busReady ? 1u : 0u, busReady ? 1u : 0u, 0u, 255u,
                                busReady, pdoAgeMs, 100u, 100u, 52u, positions, statusWords, driveErrors, validMaskOverride);
                            await server.SendAsync(realF2, realF2.Length, rx.RemoteEndPoint).ConfigureAwait(false);
                        }
                    }
                });

                // Initial connect must ignore stale Hello and stale 0xF2 replies
                Sc4iStatusReply st = await client.ConnectAsync("127.0.0.1", 0, "127.0.0.1", port, 2000, CancellationToken.None).ConfigureAwait(false);
                Check(client.IsConnected && st.ErrorCode == 0u, "ConnectAsync skipped stale Hello and stale 0xF2 replies");

                // Disconnect and reconnect twice before bus init -> must resume activeSid with strictly increasing seq (no 0x35 lockout)
                client.Disconnect();
                st = await client.ConnectAsync("127.0.0.1", 0, "127.0.0.1", port, 2000, CancellationToken.None).ConfigureAwait(false);
                Check(client.IsConnected && client.SessionId == activeSid, "First reconnect resumed session cleanly");

                // Advance seq rapidly then reconnect immediately; hide snap.Seq and push server lastAcceptedSeq ahead so 0x35 retry actually runs
                for (int i = 0; i < 10; i++)
                    await client.SendHeartbeatAsync(CancellationToken.None).ConfigureAwait(false);
                hideSnapSeq = true;
                lastAcceptedSeq += 500u; // simulate external higher seq
                client.Disconnect();
                st = await client.ConnectAsync("127.0.0.1", 0, "127.0.0.1", port, 2000, CancellationToken.None).ConfigureAwait(false);
                Check(client.IsConnected && client.SessionId == activeSid && seqReplayCount == 1, "Second reconnect triggered 0x35 and recovered on retry");

                // Simulate fresh process restarts where server lastAcceptedSeq is far ahead (>1000) and Snapshot hides seq (matches real SC4I0001 firmware)
                lastAcceptedSeq += 25000u;
                client.Disconnect();
                using (Sc4iClient freshClient = new Sc4iClient())
                {
                    st = await freshClient.ConnectAsync("127.0.0.1", 0, "127.0.0.1", port, 2000, CancellationToken.None).ConfigureAwait(false);
                    Check(freshClient.IsConnected && freshClient.SessionId == activeSid && seqReplayCount >= 3,
                        "Fresh client recovered via multi-step 0x35 backoff when server seq was >25000 ahead");
                }
                // Re-attach original client for remaining assertions
                st = await client.ConnectAsync("127.0.0.1", 0, "127.0.0.1", port, 2000, CancellationToken.None).ConfigureAwait(false);
                Check(client.IsConnected && client.SessionId == activeSid, "Original client re-attached after fresh client restart");
                hideSnapSeq = false;

                // Pre-motion safety interlocks in MoveAxisRelativeAsync
                busReady = true;
                pdoAgeMs = 600u; // stale PDO
                Throws<InvalidOperationException>(() =>
                    client.MoveAxisRelativeAsync(0, 0.2, 0.25, 1.0, CancellationToken.None).GetAwaiter().GetResult(),
                    "Motion blocked when PdoAgeMs >= 500");

                pdoAgeMs = 0u;
                validMaskOverride = 0u; // invalid telemetry position
                Throws<InvalidOperationException>(() =>
                    client.MoveAxisRelativeAsync(0, 0.2, 0.25, 1.0, CancellationToken.None).GetAwaiter().GetResult(),
                    "Motion blocked when slave ValidMask == 0");

                validMaskOverride = 0x1Fu;
                driveErrors[0] = 0x2310;
                Throws<InvalidOperationException>(() =>
                    client.MoveAxisRelativeAsync(0, 0.2, 0.25, 1.0, CancellationToken.None).GetAwaiter().GetResult(),
                    "Motion blocked when slave has drive alarm");
                driveErrors[0] = 0;

                // InitBusToOpAsync & ScanSlavesAsync must propagate 0x36 even when BusReady == true
                forceSessionExpired = true;
                Throws<Sc4iCommandException>(() =>
                    client.InitBusToOpAsync(CancellationToken.None).GetAwaiter().GetResult(),
                    "InitBusToOpAsync rejects expired session even if BusReady");
                Throws<Sc4iCommandException>(() =>
                    client.ScanSlavesAsync(CancellationToken.None).GetAwaiter().GetResult(),
                    "ScanSlavesAsync rejects expired session even if BusReady");
                forceSessionExpired = false;

                // Fast UDP retry: dropping 2 consecutive replies must transparently recover well inside 1000 ms watchdog
                Check(Sc4iProtocol.FormatErrorCode(0x30u).Contains("心跳"), "FormatErrorCode(0x30) describes heartbeat timeout");
                Check(Sc4iProtocol.FormatErrorCode(0x2Eu).Contains("CSP"), "FormatErrorCode(0x2E) describes CSP mismatch");
                dropRepliesCount = 2;
                System.Diagnostics.Stopwatch retrySw = System.Diagnostics.Stopwatch.StartNew();
                Sc4iStatusReply recoveredHb = await client.SendHeartbeatAsync(CancellationToken.None).ConfigureAwait(false);
                retrySw.Stop();
                Check(recoveredHb.ErrorCode == 0u && dropRepliesCount == 0 && retrySw.ElapsedMilliseconds < 700,
                    "Heartbeat recovered from 2 dropped UDP packets promptly");

                dropRepliesCount = 1;
                Sc4iStatusReply recoveredJog = await client.SendJogKeepaliveAsync(CancellationToken.None).ConfigureAwait(false);
                Check(recoveredJog.ErrorCode == 0u && dropRepliesCount == 0,
                    "JogKeepalive recovered from 1 dropped UDP packet");

                // Disconnect cancels a stalled in-flight request promptly without hanging for full timeout
                dropRepliesCount = 10;
                Task<Sc4iStatusReply> stalled = client.SendHeartbeatAsync(CancellationToken.None);
                await Task.Delay(80).ConfigureAwait(false);
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                client.Disconnect();
                sw.Stop();
                Check(sw.ElapsedMilliseconds < 1000, "Disconnect preempted stalled request promptly");
                bool cancelledOrFailed = false;
                try { await stalled.ConfigureAwait(false); }
                catch (OperationCanceledException) { cancelledOrFailed = true; }
                catch (ObjectDisposedException) { cancelledOrFailed = true; }
                Check(cancelledOrFailed, "Stalled heartbeat aborted on Disconnect");

                serverCts.Cancel();
                try { await serverLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }

        private static byte[] BuildFakeStatusReplyWithValid(
            Sc4iSubcmd sub, byte motionState, byte estop, byte slaveCnt, uint seq, ulong bootId, ulong sid,
            uint err, uint latched, bool busReady, bool cfgLoaded, uint enMask, uint authMask, uint eningMask,
            uint faultSlave, bool pdoOk, uint pdoAge, uint pdoCyc, uint readyCyc, uint txPdoBytes,
            int[] actPos, ushort[] sw, ushort[] drvErr, uint validMask)
        {
            byte[] buf = BuildFakeStatusReply(
                sub, motionState, estop, slaveCnt, seq, bootId, sid, err, latched,
                busReady, cfgLoaded, enMask, authMask, eningMask, faultSlave, pdoOk, pdoAge, pdoCyc, readyCyc, txPdoBytes,
                actPos, sw, drvErr);
            for (int s = 0; s < 4; s++)
            {
                int off = 112 + s * 56;
                BitConverter.GetBytes(busReady ? validMask : 0u).CopyTo(buf, off + 48);
            }
            buf[0] = Sc4iProtocol.CalculateChecksum(buf);
            return buf;
        }

        private static byte[] BuildFakeHelloReply(byte token, ulong bootId, uint protoVer)
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

        private static byte[] BuildFakeStatusReply(
            Sc4iSubcmd sub, byte motionState, byte estop, byte slaveCnt, uint seq, ulong bootId, ulong sid,
            uint err, uint latched, bool busReady, bool cfgLoaded, uint enMask, uint authMask, uint eningMask,
            uint faultSlave, bool pdoOk, uint pdoAge, uint pdoCyc, uint readyCyc, uint txPdoBytes,
            int[] actPos, ushort[] sw, ushort[] drvErr)
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
                BitConverter.GetBytes(busReady ? 0x1Fu : 0u).CopyTo(buf, off + 48);
            }
            BitConverter.GetBytes(200000000u).CopyTo(buf, 368);
            BitConverter.GetBytes(12345u).CopyTo(buf, 376);
            buf[0] = Sc4iProtocol.CalculateChecksum(buf);
            return buf;
        }
    }
}
