using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ScaraArm.Core.Protocol;
using ScaraArm.Core.Transport;

namespace ScaraArm.Tests
{
    internal static class Sc4iClientTests
    {
        public static int Run()
        {
            TestRunner t = new TestRunner("Sc4iClientTests");
            LoopbackClientTests(t).GetAwaiter().GetResult();
            HardeningLoopbackTests(t).GetAwaiter().GetResult();
            return t.Count;
        }

        private static async Task LoopbackClientTests(TestRunner t)
        {
            const ulong bootId = 0x123456789ABCDEF0ul;
            ulong boundSid = 0;
            bool cfgLoaded = false;
            bool busReady = false;
            byte slaveCount = 0;
            uint enabledMask = 0;
            uint authMask = 0;
            uint latchedFault = 0;
            byte estopFlag = 0;
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
                            byte[] rep = Sc4iTestFrames.BuildHelloReply(pkt[6], bootId, 2u);
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

                            byte[] rep = Sc4iTestFrames.BuildStatusReply(
                                sub, 0, estopFlag, slaveCount, seq, bootId, boundSid, err, latchedFault,
                                busReady, cfgLoaded, enabledMask, authMask, 0u, 255u, busReady, 0u,
                                busReady ? 100u : 0u, busReady ? 100u : 0u, busReady ? 52u : 0u,
                                positions, statusWords, driveErrors);
                            await server.SendAsync(rep, rep.Length, rx.RemoteEndPoint).ConfigureAwait(false);
                        }
                    }
                });

                Sc4iStatusReply st = await client.ConnectAsync("127.0.0.1", 0, "127.0.0.1", port, 2000, CancellationToken.None).ConfigureAwait(false);
                t.Check(client.IsConnected && client.BootId == bootId && !st.BusReady, "ConnectAsync succeeded read-only");

                st = await client.ScanSlavesAsync(CancellationToken.None).ConfigureAwait(false);
                t.Check(st.SlaveCount == 4 && !st.BusReady, "ScanSlavesAsync found 4 slaves in PRE-OP");

                st = await client.InitBusToOpAsync(CancellationToken.None).ConfigureAwait(false);
                t.Check(st.BusReady && st.PdoOk && st.EnabledMask == 0u, "InitBusToOpAsync entered OP with motors disabled");

                t.Throws<InvalidOperationException>(() =>
                    client.MoveAxisRelativeAsync(0, 0.25, 0.5, 1.0, CancellationToken.None).GetAwaiter().GetResult(),
                    "Motion blocked while axis disabled");
                t.Throws<ArgumentOutOfRangeException>(() =>
                    client.MoveAxisRelativeAsync(0, 25.0, 0.5, 1.0, CancellationToken.None).GetAwaiter().GetResult(),
                    "Excessive step blocked by safety cap");

                st = await client.EnableAxisAsync(0, CancellationToken.None).ConfigureAwait(false);
                t.Check(st.EnabledMask == 1u && st.AuthorizedMask == 1u, "EnableAxisAsync enabled only axis 0");

                st = await client.MoveAxisRelativeAsync(0, 0.25, 0.5, 1.0, CancellationToken.None).ConfigureAwait(false);
                t.Check(lastPtpAxis == 0 && Math.Abs(lastPtpTarget - 1.25) < 1e-6, "Relative PTP converted to absolute target");

                await client.StartJogAsync(0, -0.2, CancellationToken.None).ConfigureAwait(false);
                await client.SendJogKeepaliveAsync(CancellationToken.None).ConfigureAwait(false);
                t.Check(jogKeepaliveCount == 1, "Jog keepalive delivered");

                st = await client.StopHoldAsync(CancellationToken.None).ConfigureAwait(false);
                t.Check(st.AuthorizedMask == 0u && st.EnabledMask == 1u, "StopHold cleared auth while keeping torque");

                // Simulate fault on slave 1 + latched fault and verify 2-step reset
                statusWords[1] = 0x1658;
                driveErrors[1] = 0x2310;
                latchedFault = 0x2Du;
                st = await client.ClearFaultsAndLatchAsync(CancellationToken.None).ConfigureAwait(false);
                t.Check(faultResetMaskCalls == 1 && faultResetLatchCalls == 1 && st.LatchedFault == 0u, "2-step fault reset cleared drive and controller latch");

                // Simulate pure 0x30 heartbeat hold protection -> one-step clear + authorize
                authMask = 0u;
                statusWords[0] = 0x1617;
                latchedFault = 0x30u;
                st = await client.RecoverHoldProtectionAndAuthorizeAsync(0, CancellationToken.None).ConfigureAwait(false);
                t.Check(st.LatchedFault == 0u && st.AuthorizedMask == 1u && st.EnabledMask == 1u && faultResetLatchCalls == 2,
                    "RecoverHoldProtectionAndAuthorizeAsync cleared 0x30 and re-authorized held axes");

                // Reject one-step recovery if a drive alarm or non-0x30 latch is present
                driveErrors[1] = 0x2310;
                statusWords[1] = 0x1658;
                latchedFault = 0x30u;
                t.Throws<InvalidOperationException>(() =>
                    client.RecoverHoldProtectionAndAuthorizeAsync(0x0F, CancellationToken.None).GetAwaiter().GetResult(),
                    "RecoverHoldProtectionAndAuthorizeAsync rejects when a drive alarm exists");
                driveErrors[1] = 0;
                statusWords[1] = 0x1650;
                latchedFault = 0x2Fu;
                t.Throws<InvalidOperationException>(() =>
                    client.RecoverHoldProtectionAndAuthorizeAsync(0x0F, CancellationToken.None).GetAwaiter().GetResult(),
                    "RecoverHoldProtectionAndAuthorizeAsync rejects non-0x30 latched fault");
                latchedFault = 0;
                estopFlag = 1;
                t.Throws<InvalidOperationException>(() =>
                    client.RecoverHoldProtectionAndAuthorizeAsync(0x0F, CancellationToken.None).GetAwaiter().GetResult(),
                    "RecoverHoldProtectionAndAuthorizeAsync rejects when E-Stop flag is active");
                estopFlag = 0;

                client.Disconnect();
                t.Check(!client.IsConnected, "Disconnected cleanly");
                serverCts.Cancel();
                try { await serverLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }

        private static async Task HardeningLoopbackTests(TestRunner t)
        {
            const ulong bootId = 0xCAFEBABE11223344ul;

            // 1. Strict IPv4 validation rejects shorthand / blank / broadcast addresses
            using (Sc4iClient badIpClient = new Sc4iClient())
            {
                t.Throws<ArgumentException>(() =>
                    badIpClient.ConnectAsync("192.168.12", 0, "192.168.8.20", 5000, 500, CancellationToken.None).GetAwaiter().GetResult(),
                    "Reject 3-octet shorthand local IP");
                t.Throws<ArgumentException>(() =>
                    badIpClient.ConnectAsync("127.0.0.1", 0, "192.168.20", 5000, 500, CancellationToken.None).GetAwaiter().GetResult(),
                    "Reject 3-octet shorthand card IP");
                t.Throws<ArgumentException>(() =>
                    badIpClient.ConnectAsync(" ", 0, "127.0.0.1", 5000, 500, CancellationToken.None).GetAwaiter().GetResult(),
                    "Reject blank local IP");
            }

            // 2. Stale reply filtering, monotonic reconnect recovery, 0x36/0x35 handling, and pre-motion interlocks
            ulong activeSid = 0;
            uint lastAcceptedSeq = 0;
            bool busReady = false;
            bool forceSessionExpired = false;
            bool hideSnapSeq = false;
            int seqReplayCount = 0;
            uint pdoAgeMs = 0;
            uint validMaskOverride = 0x1Fu;
            int dropRepliesCount = 0;
            int dropNextAcquireRequestCount = 0;
            int dropNextPtpRequestCount = 0;
            int dropNextPtpReplyOnlyCount = 0;
            int ptpExecutedCount = 0;
            int dropNextJogRequestCount = 0;
            int dropNextJogReplyOnlyCount = 0;
            int jogExecutedCount = 0;
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
                            byte staleTok = (byte)((pkt[6] + 1) % 240);
                            byte[] staleHello = Sc4iTestFrames.BuildHelloReply(staleTok, bootId, 2u);
                            await server.SendAsync(staleHello, staleHello.Length, rx.RemoteEndPoint).ConfigureAwait(false);

                            byte[] goodHello = Sc4iTestFrames.BuildHelloReply(pkt[6], bootId, 2u);
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
                                byte[] snap = Sc4iTestFrames.BuildStatusReply(
                                    sub, 0, 0, 4, hideSnapSeq ? 0u : lastAcceptedSeq, bootId, activeSid, 0u, 0u,
                                    busReady, busReady, busReady ? 1u : 0u, busReady ? 1u : 0u, 0u, 255u,
                                    busReady, pdoAgeMs, 100u, 100u, 52u, positions, statusWords, driveErrors, validMaskOverride);
                                await server.SendAsync(snap, snap.Length, rx.RemoteEndPoint).ConfigureAwait(false);
                                continue;
                            }

                            if (sub == Sc4iSubcmd.AcquireSession)
                            {
                                if (dropNextAcquireRequestCount > 0)
                                {
                                    dropNextAcquireRequestCount--;
                                    continue;
                                }
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
                                if (sub == Sc4iSubcmd.MoveAxisPtp && dropNextPtpRequestCount > 0)
                                {
                                    dropNextPtpRequestCount--;
                                    continue;
                                }
                                if (sub == Sc4iSubcmd.JogAxis && dropNextJogRequestCount > 0)
                                {
                                    dropNextJogRequestCount--;
                                    continue;
                                }
                                if (seq <= lastAcceptedSeq) err = 0x35u;
                                else
                                {
                                    lastAcceptedSeq = seq;
                                    if (sub == Sc4iSubcmd.MoveAxisPtp)
                                    {
                                        ptpExecutedCount++;
                                        double tgt = BitConverter.ToDouble(pkt, 40);
                                        positions[pkt[9]] = (int)Math.Round(tgt * 262144.0);
                                        if (dropNextPtpReplyOnlyCount > 0)
                                        {
                                            dropNextPtpReplyOnlyCount--;
                                            continue;
                                        }
                                    }
                                    else if (sub == Sc4iSubcmd.JogAxis)
                                    {
                                        jogExecutedCount++;
                                        if (dropNextJogReplyOnlyCount > 0)
                                        {
                                            dropNextJogReplyOnlyCount--;
                                            continue;
                                        }
                                    }
                                }
                            }

                            byte[] staleF2 = Sc4iTestFrames.BuildStatusReply(
                                Sc4iSubcmd.JogKeepalive, 0, 0, 4, seq > 1 ? seq - 1 : 999u, bootId, activeSid, 0u, 0u,
                                busReady, busReady, 1u, 1u, 0u, 255u, busReady, 0u, 100u, 100u, 52u,
                                positions, statusWords, driveErrors, 0x1Fu);
                            await server.SendAsync(staleF2, staleF2.Length, rx.RemoteEndPoint).ConfigureAwait(false);

                            byte[] realF2 = Sc4iTestFrames.BuildStatusReply(
                                sub, 0, 0, 4, (err == 0x35u && !hideSnapSeq) ? lastAcceptedSeq : seq, bootId, activeSid, err, 0u,
                                busReady, busReady, busReady ? 1u : 0u, busReady ? 1u : 0u, 0u, 255u,
                                busReady, pdoAgeMs, 100u, 100u, 52u, positions, statusWords, driveErrors, validMaskOverride);
                            await server.SendAsync(realF2, realF2.Length, rx.RemoteEndPoint).ConfigureAwait(false);
                        }
                    }
                });

                Sc4iStatusReply st = await client.ConnectAsync("127.0.0.1", 0, "127.0.0.1", port, 2000, CancellationToken.None).ConfigureAwait(false);
                t.Check(client.IsConnected && st.ErrorCode == 0u, "ConnectAsync skipped stale Hello and stale 0xF2 replies");

                client.Disconnect();
                st = await client.ConnectAsync("127.0.0.1", 0, "127.0.0.1", port, 2000, CancellationToken.None).ConfigureAwait(false);
                t.Check(client.IsConnected && client.SessionId == activeSid, "First reconnect resumed session cleanly");

                for (int i = 0; i < 10; i++)
                    await client.SendHeartbeatAsync(CancellationToken.None).ConfigureAwait(false);
                hideSnapSeq = true;
                lastAcceptedSeq += 500u;
                client.Disconnect();
                st = await client.ConnectAsync("127.0.0.1", 0, "127.0.0.1", port, 2000, CancellationToken.None).ConfigureAwait(false);
                t.Check(client.IsConnected && client.SessionId == activeSid && seqReplayCount == 1, "Second reconnect triggered 0x35 and recovered on retry");

                lastAcceptedSeq += 25000u;
                client.Disconnect();
                using (Sc4iClient freshClient = new Sc4iClient())
                {
                    st = await freshClient.ConnectAsync("127.0.0.1", 0, "127.0.0.1", port, 2000, CancellationToken.None).ConfigureAwait(false);
                    t.Check(freshClient.IsConnected && freshClient.SessionId == activeSid && seqReplayCount >= 3,
                        "Fresh client recovered via multi-step 0x35 backoff when server seq was >25000 ahead");
                }

                st = await client.ConnectAsync("127.0.0.1", 0, "127.0.0.1", port, 2000, CancellationToken.None).ConfigureAwait(false);
                t.Check(client.IsConnected && client.SessionId == activeSid, "Original client re-attached after fresh client restart");
                hideSnapSeq = false;

                busReady = true;
                pdoAgeMs = 600u;
                t.Throws<InvalidOperationException>(() =>
                    client.MoveAxisRelativeAsync(0, 0.2, 0.25, 1.0, CancellationToken.None).GetAwaiter().GetResult(),
                    "Motion blocked when PdoAgeMs >= 500");

                pdoAgeMs = 0u;
                validMaskOverride = 0u;
                t.Throws<InvalidOperationException>(() =>
                    client.MoveAxisRelativeAsync(0, 0.2, 0.25, 1.0, CancellationToken.None).GetAwaiter().GetResult(),
                    "Motion blocked when slave ValidMask == 0");

                validMaskOverride = 0x1Fu;
                driveErrors[0] = 0x2310;
                t.Throws<InvalidOperationException>(() =>
                    client.MoveAxisRelativeAsync(0, 0.2, 0.25, 1.0, CancellationToken.None).GetAwaiter().GetResult(),
                    "Motion blocked when slave has drive alarm");
                driveErrors[0] = 0;

                forceSessionExpired = true;
                t.Throws<Sc4iCommandException>(() =>
                    client.InitBusToOpAsync(CancellationToken.None).GetAwaiter().GetResult(),
                    "InitBusToOpAsync rejects expired session even if BusReady");
                t.Throws<Sc4iCommandException>(() =>
                    client.ScanSlavesAsync(CancellationToken.None).GetAwaiter().GetResult(),
                    "ScanSlavesAsync rejects expired session even if BusReady");
                forceSessionExpired = false;

                dropRepliesCount = 2;
                Stopwatch retrySw = Stopwatch.StartNew();
                Sc4iStatusReply recoveredHb = await client.SendHeartbeatAsync(CancellationToken.None).ConfigureAwait(false);
                retrySw.Stop();
                t.Check(recoveredHb.ErrorCode == 0u && dropRepliesCount == 0 && retrySw.ElapsedMilliseconds < 700,
                    "Heartbeat recovered from 2 dropped UDP packets promptly");

                // 7-packet consecutive burst loss (reproducing live 12:55:15 EMI burst)
                dropRepliesCount = 7;
                Stopwatch burstSw = Stopwatch.StartNew();
                Sc4iStatusReply burstRecovered = await client.SendHeartbeatAsync(CancellationToken.None).ConfigureAwait(false);
                burstSw.Stop();
                t.Check(burstRecovered.ErrorCode == 0u && dropRepliesCount == 0 && burstSw.ElapsedMilliseconds < 700,
                    "Heartbeat recovered from 7 consecutive dropped UDP replies within <700ms");

                dropRepliesCount = 1;
                Sc4iStatusReply recoveredJog = await client.SendJogKeepaliveAsync(CancellationToken.None).ConfigureAwait(false);
                t.Check(recoveredJog.ErrorCode == 0u && dropRepliesCount == 0,
                    "JogKeepalive recovered from 1 dropped UDP packet");

                // MoveAxisPtp dropped request -> same-seq retry succeeds once
                ptpExecutedCount = 0;
                dropNextPtpRequestCount = 1;
                Sc4iStatusReply ptpReqRecovered = await client.MoveAxisToTargetRevAsync(0, 1.5, 0.5, 1.0, CancellationToken.None).ConfigureAwait(false);
                t.Check(ptpReqRecovered.ErrorCode == 0u && ptpExecutedCount == 1 && dropNextPtpRequestCount == 0,
                    "MoveAxisPtp recovered from dropped request via same-seq retry without duplicate execution");

                // MoveAxisPtp reply lost after execution -> same-seq retry sees 0x35 and confirms via Heartbeat without re-executing
                ptpExecutedCount = 0;
                dropNextPtpReplyOnlyCount = 1;
                Sc4iStatusReply ptpRepRecovered = await client.MoveAxisToTargetRevAsync(0, 1.8, 0.5, 1.0, CancellationToken.None).ConfigureAwait(false);
                t.Check(ptpRepRecovered.ErrorCode == 0u && ptpExecutedCount == 1 && dropNextPtpReplyOnlyCount == 0
                    && Math.Abs(ptpRepRecovered.Slaves[0].ActualRevolutions(client.CountsPerRevolution) - 1.8) < 1e-4,
                    "MoveAxisPtp recovered from lost reply via 0x35 replay detection and Heartbeat confirmation");

                // StartJogAsync dropped request and dropped reply -> same-seq retry + 0x35 Heartbeat recovery
                jogExecutedCount = 0;
                dropNextJogRequestCount = 1;
                Sc4iStatusReply jogReqRecovered = await client.StartJogAsync(0, 0.25, CancellationToken.None).ConfigureAwait(false);
                t.Check(jogReqRecovered.ErrorCode == 0u && jogExecutedCount == 1 && dropNextJogRequestCount == 0,
                    "StartJogAsync recovered from dropped request via same-seq retry");

                jogExecutedCount = 0;
                dropNextJogReplyOnlyCount = 1;
                Sc4iStatusReply jogRepRecovered = await client.StartJogAsync(0, -0.25, CancellationToken.None).ConfigureAwait(false);
                t.Check(jogRepRecovered.ErrorCode == 0u && jogExecutedCount == 1 && dropNextJogReplyOnlyCount == 0,
                    "StartJogAsync recovered from lost reply via 0x35 detection without duplicate start");

                // ConnectAsync (AcquireSession) recovers from dropped request packet
                client.Disconnect();
                dropNextAcquireRequestCount = 1;
                Sc4iStatusReply reconnectedAfterDrop = await client.ConnectAsync("127.0.0.1", 0, "127.0.0.1", port, 2000, CancellationToken.None).ConfigureAwait(false);
                t.Check(client.IsConnected && reconnectedAfterDrop.ErrorCode == 0u && dropNextAcquireRequestCount == 0,
                    "ConnectAsync (AcquireSession) recovered from 1 dropped request packet");

                dropRepliesCount = 30;
                Task<Sc4iStatusReply> stalled = client.SendHeartbeatAsync(CancellationToken.None);
                await Task.Delay(80).ConfigureAwait(false);
                Stopwatch sw = Stopwatch.StartNew();
                client.Disconnect();
                sw.Stop();
                t.Check(sw.ElapsedMilliseconds < 1000, "Disconnect preempted stalled request promptly");
                bool cancelledOrFailed = false;
                try { await stalled.ConfigureAwait(false); }
                catch (OperationCanceledException) { cancelledOrFailed = true; }
                catch (ObjectDisposedException) { cancelledOrFailed = true; }
                t.Check(cancelledOrFailed, "Stalled heartbeat aborted on Disconnect");

                serverCts.Cancel();
                try { await serverLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
    }
}
