using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace NewCard.Core
{
    public sealed class Sc4iCommandException : InvalidOperationException
    {
        public uint ErrorCode { get; private set; }
        public Sc4iStatusReply Status { get; private set; }

        public Sc4iCommandException(string action, Sc4iStatusReply status)
            : base(action + " 失败：" + Sc4iProtocol.FormatErrorCode(status.ErrorCode))
        {
            ErrorCode = status.ErrorCode;
            Status = status;
        }
    }

    /// <summary>
    /// Explicit, serialized UDP session client for SC4I0001 v2 firmware.
    /// All blocking UDP I/O runs on thread-pool threads so UI and E-Stop stay responsive.
    /// Never connects, initializes EtherCAT, enables servos, or commands motion automatically.
    /// </summary>
    public sealed class Sc4iClient : IDisposable
    {
        public const double MaxSafeStepRev = 20.0;
        public const double MaxSafeVelocityRevPerSec = 2.5;
        public const double MaxSafeJogVelocityRevPerSec = 1.5;
        public const double MaxSafeAccelerationRevPerSec2 = 8.0;
        public const uint MaxFreshPdoAgeMs = 500u;

        private const int SioUdpConnReset = -1744830452;

        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly object stateLock = new object();
        private readonly CancellationTokenSource disposeCts = new CancellationTokenSource();
        private Socket socket;
        private IPEndPoint remoteEndPoint;
        private CancellationTokenSource sessionCts;
        private int defaultTimeoutMs = 2000;
        private bool connected;
        private ulong bootId;
        private ulong sessionId;
        private uint nextSeq = 1;
        private uint maxUsedSeq;
        private byte helloToken;
        private Sc4iStatusReply lastStatus;
        private bool disposed;

        public bool IsConnected
        {
            get
            {
                lock (stateLock)
                {
                    return connected && !disposed;
                }
            }
        }

        public ulong BootId
        {
            get
            {
                lock (stateLock)
                {
                    return bootId;
                }
            }
        }

        public ulong SessionId
        {
            get
            {
                lock (stateLock)
                {
                    return sessionId;
                }
            }
        }

        public Sc4iStatusReply LastStatus
        {
            get
            {
                lock (stateLock)
                {
                    return lastStatus;
                }
            }
        }

        public double CountsPerRevolution { get { return Sc4iMachineConfig.DefaultCountsPerRevolution; } }

        public Task<Sc4iStatusReply> ConnectAsync(
            string localIp, int localPort, string cardIp, int cardPort, int timeoutMs, CancellationToken cancellationToken)
        {
            IPAddress local = ParseUnicast(localIp, "电脑 IP");
            IPAddress remote = ParseUnicast(cardIp, "控制卡 IP");
            if (localPort < 0 || localPort > 65535) throw new ArgumentOutOfRangeException("localPort");
            if (cardPort < 1 || cardPort > 65535) throw new ArgumentOutOfRangeException("cardPort");
            if (timeoutMs < 200 || timeoutMs > 15000) throw new ArgumentOutOfRangeException("timeoutMs");

            return Task.Run(async () =>
            {
                using (CancellationTokenSource linked = CreateConnectLinkedCts(cancellationToken))
                {
                    CancellationToken ct = linked.Token;
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        ThrowIfDisposed();
                        CloseSocketInternal();

                        Socket candidate = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        try
                        {
                            ConfigureUdpSocket(candidate);
                            candidate.Bind(new IPEndPoint(local, localPort));
                            IPEndPoint target = new IPEndPoint(remote, cardPort);

                            helloToken = (byte)((helloToken + 1) % 240);
                            if (helloToken == 0) helloToken = 1;
                            byte expectedToken = helloToken;
                            byte[] helloReq = Sc4iProtocol.BuildHelloRequest(expectedToken);
                            Sc4iHelloReply hello = ExchangeValidatedWithRetry(
                                candidate, target, helloReq, Sc4iProtocol.HelloReplyLength, timeoutMs, 250, 4, ct,
                                raw => Sc4iProtocol.ParseHelloReply(raw, expectedToken));

                            // Pure read-only snapshot first to inspect existing bus & session state.
                            Sc4iStatusReply snap = SendF2InternalWithRetry(
                                candidate, target, Sc4iSubcmd.Snapshot, 0, 0, hello.BootId, 0, null, timeoutMs, 200, 4, ct);

                            ulong newSid = GenerateSessionId();
                            uint startSeq = 1;
                            Sc4iStatusReply acquired = SendF2Internal(
                                candidate, target, Sc4iSubcmd.AcquireSession, 0, startSeq, hello.BootId, newSid, null, timeoutMs, ct);

                            if (acquired.ErrorCode == 0x36)
                            {
                                if (snap.BusReady && snap.AuthorizedMask == 0 && snap.MotionState == 0)
                                {
                                    // Firmware allows clean session takeover once prior heartbeat expires (> 1000 ms).
                                    await Task.Delay(1150, ct).ConfigureAwait(false);
                                    newSid = GenerateSessionId();
                                    startSeq = 1;
                                    acquired = SendF2Internal(
                                        candidate, target, Sc4iSubcmd.AcquireSession, 0, startSeq, hello.BootId, newSid, null, timeoutMs, ct);
                                }
                                if (acquired.ErrorCode == 0x36 && snap.SessionId != 0)
                                {
                                    // Same host/port reconnect before bus init (or while holding session): resume active session with strictly monotonic seq.
                                    newSid = snap.SessionId;
                                    startSeq = ComputeRecoverySeq(snap.Seq, maxUsedSeq);
                                    acquired = SendF2Internal(
                                        candidate, target, Sc4iSubcmd.AcquireSession, 0, startSeq, hello.BootId, newSid, null, timeoutMs, ct);
                                    uint[] replaySteps = new uint[] { 1000u, 10000u, 100000u, 1000000u, 10000000u, 100000000u };
                                    for (int attempt = 0; acquired.ErrorCode == 0x35 && attempt < replaySteps.Length; attempt++)
                                    {
                                        ulong bumped = acquired.Seq > startSeq
                                            ? (ulong)acquired.Seq + 1UL
                                            : (ulong)startSeq + replaySteps[attempt];
                                        if (bumped > uint.MaxValue - 1000u)
                                            bumped = uint.MaxValue - 1000u;
                                        if (bumped <= startSeq)
                                            break;
                                        startSeq = (uint)bumped;
                                        acquired = SendF2Internal(
                                            candidate, target, Sc4iSubcmd.AcquireSession, 0, startSeq, hello.BootId, newSid, null, timeoutMs, ct);
                                    }
                                }
                            }

                            if (acquired.ErrorCode != 0)
                                throw new Sc4iCommandException("获取控制卡会话租约", acquired);

                            CancellationTokenSource newSessionCts = new CancellationTokenSource();
                            lock (stateLock)
                            {
                                if (disposed)
                                {
                                    newSessionCts.Dispose();
                                    throw new ObjectDisposedException("Sc4iClient");
                                }
                                socket = candidate;
                                remoteEndPoint = target;
                                sessionCts = newSessionCts;
                                defaultTimeoutMs = timeoutMs;
                                bootId = hello.BootId;
                                sessionId = newSid;
                                if (startSeq > maxUsedSeq) maxUsedSeq = startSeq;
                                nextSeq = startSeq + 1u;
                                if (nextSeq == 0) nextSeq = 1u;
                                lastStatus = acquired;
                                connected = true;
                            }
                            return acquired;
                        }
                        catch
                        {
                            candidate.Dispose();
                            throw;
                        }
                    }
                    finally
                    {
                        SafeReleaseGate();
                    }
                }
            });
        }

        public void Disconnect()
        {
            CancelSessionInternal();
            bool acquired = false;
            try
            {
                lock (stateLock)
                {
                    if (disposed) return;
                }
                gate.Wait();
                acquired = true;
                CloseSocketInternal();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                if (acquired) SafeReleaseGate();
            }
        }

        public Task<Sc4iStatusReply> ReadSnapshotAsync(CancellationToken cancellationToken)
        {
            return ExecuteF2Async("读取状态快照", Sc4iSubcmd.Snapshot, 0, null, 0, false, cancellationToken);
        }

        public Task<Sc4iStatusReply> SendHeartbeatAsync(CancellationToken cancellationToken)
        {
            return ExecuteF2Async("发送心跳保活", Sc4iSubcmd.Heartbeat, 0, null, 0, true, cancellationToken);
        }

        public Task<Sc4iStatusReply> ScanSlavesAsync(CancellationToken cancellationToken)
        {
            return RunConnectedAsync(cancellationToken, ct =>
            {
                Sc4iStatusReply cur = SendNextF2("查询状态", Sc4iSubcmd.Heartbeat, 0, null, defaultTimeoutMs, ct);
                if (cur.ErrorCode != 0)
                    throw new Sc4iCommandException("查询状态", cur);
                if (cur.BusReady)
                    return Task.FromResult(cur);
                Sc4iStatusReply scanned = SendNextF2("扫描 EtherCAT 从站 (PRE-OP)", Sc4iSubcmd.ScanSlaves, 0, null, 15000, ct);
                if (scanned.ErrorCode != 0)
                    throw new Sc4iCommandException("扫描 EtherCAT 从站 (PRE-OP)", scanned);
                return Task.FromResult(scanned);
            });
        }

        public Task<Sc4iStatusReply> InitBusToOpAsync(CancellationToken cancellationToken)
        {
            return RunConnectedAsync(cancellationToken, async ct =>
            {
                Sc4iStatusReply cur = SendNextF2("查询状态", Sc4iSubcmd.Heartbeat, 0, null, defaultTimeoutMs, ct);
                if (cur.ErrorCode != 0)
                    throw new Sc4iCommandException("查询状态", cur);
                if (cur.BusReady)
                    return cur;

                if (cur.SlaveCount != 4 || !HasFourPopulatedIdentities(cur))
                {
                    cur = SendNextF2("扫描 EtherCAT 从站 (PRE-OP)", Sc4iSubcmd.ScanSlaves, 0, null, 15000, ct);
                    if (cur.ErrorCode != 0)
                        throw new Sc4iCommandException("扫描 EtherCAT 从站 (PRE-OP)", cur);
                    if (cur.SlaveCount != 4 || !HasFourPopulatedIdentities(cur))
                        throw new InvalidOperationException("扫描到的从站数量不是 4（当前为 " + cur.SlaveCount + "），不能初始化总线。");
                }

                Sc4iSlaveIdentity[] ids = new Sc4iSlaveIdentity[4];
                for (int i = 0; i < 4; i++) ids[i] = cur.Slaves[i].ToIdentity();
                Sc4iMachineConfig cfg = Sc4iMachineConfig.CreateForScannedSlaves(ids, 6u);
                byte[] cfgBytes = cfg.Serialize();

                Sc4iStatusReply uploaded = SendNextF2("下发 528B 运动卡配置", Sc4iSubcmd.UploadConfig, 0, cfgBytes, 5000, ct);
                if (uploaded.ErrorCode != 0)
                    throw new Sc4iCommandException("下发 528B 运动卡配置", uploaded);

                Sc4iStatusReply initialized = SendNextF2("初始化总线进入 OP（不上使能）", Sc4iSubcmd.BusInit, 0, null, 15000, ct);
                if (initialized.ErrorCode != 0)
                    throw new Sc4iCommandException("初始化总线进入 OP", initialized);

                // Let the 1 kHz cyclic PDO loop run ~60 cycles so live StatusWord / ActualPos / Mode populate.
                await Task.Delay(60, ct).ConfigureAwait(false);
                Sc4iStatusReply live = SendNextF2("读取 OP 实时状态", Sc4iSubcmd.Heartbeat, 0, null, defaultTimeoutMs, ct);
                if (live.ErrorCode != 0)
                    throw new Sc4iCommandException("读取 OP 实时状态", live);
                return live;
            });
        }

        public Task<Sc4iStatusReply> EnableAxisAsync(int jointIndex, CancellationToken cancellationToken)
        {
            ValidateJointIndex(jointIndex);
            byte mask = (byte)(1 << jointIndex);
            return ExecuteF2Async("使能/授权轴 " + (jointIndex + 1), Sc4iSubcmd.EnableAxes, mask, null, 3000, true, cancellationToken);
        }

        public Task<Sc4iStatusReply> EnableAxesMaskAsync(byte mask, CancellationToken cancellationToken)
        {
            if (mask == 0 || (mask & 0xF0) != 0)
                throw new ArgumentOutOfRangeException("mask", "轴掩码必须在 0x01 至 0x0F 之间。");
            return ExecuteF2Async("使能/授权轴掩码 0x" + mask.ToString("X"), Sc4iSubcmd.EnableAxes, mask, null, 4000, true, cancellationToken);
        }

        public Task<Sc4iStatusReply> MoveAxisToTargetRevAsync(
            int jointIndex, double targetRev, double velRevPerSec, double accRevPerSec2, CancellationToken cancellationToken)
        {
            ValidateJointIndex(jointIndex);
            if (Double.IsNaN(targetRev) || Double.IsInfinity(targetRev))
                throw new ArgumentOutOfRangeException("targetRev", "目标圈数必须是有限数。");
            if (Double.IsNaN(velRevPerSec) || Double.IsInfinity(velRevPerSec) || velRevPerSec < 0.005 || velRevPerSec > MaxSafeVelocityRevPerSec)
                throw new ArgumentOutOfRangeException("velRevPerSec", "速度必须在 0.005 至 " + MaxSafeVelocityRevPerSec + " rev/s 之间。");
            if (Double.IsNaN(accRevPerSec2) || Double.IsInfinity(accRevPerSec2) || accRevPerSec2 < 0.05 || accRevPerSec2 > MaxSafeAccelerationRevPerSec2)
                throw new ArgumentOutOfRangeException("accRevPerSec2", "加速度必须在 0.05 至 " + MaxSafeAccelerationRevPerSec2 + " rev/s² 之间。");

            return RunConnectedAsync(cancellationToken, ct =>
            {
                Sc4iStatusReply status = SendNextF2("运动前状态核查", Sc4iSubcmd.Heartbeat, 0, null, defaultTimeoutMs, ct);
                if (status.ErrorCode != 0)
                    throw new Sc4iCommandException("运动前状态核查", status);
                if (!status.BusReady || !status.PdoOk || status.PdoAgeMs == uint.MaxValue || status.PdoAgeMs >= MaxFreshPdoAgeMs)
                    throw new InvalidOperationException("EtherCAT 总线未处于就绪 OP 或 PDO 刷新超时，禁止启动运动。");
                if (status.LatchedFault != 0 || status.EstopFlag != 0)
                    throw new InvalidOperationException("存在锁存故障或急停标志，请先排查并清除锁存。");
                if (!status.IsAxisEnabled(jointIndex) || !status.IsAxisAuthorized(jointIndex))
                    throw new InvalidOperationException("轴 " + (jointIndex + 1) + " 未处于使能且授权状态，请先使能授权。");
                if (status.MotionState != 0)
                    throw new InvalidOperationException("当前已有运动正在执行，请等待完成或先停止。");
                if (status.Slaves == null || status.Slaves.Length <= jointIndex || status.Slaves[jointIndex] == null
                    || status.Slaves[jointIndex].ValidMask == 0)
                    throw new InvalidOperationException("轴 " + (jointIndex + 1) + " 实时遥测无效，禁止基于未确认位置启动运动。");
                if (status.Slaves[jointIndex].IsFault)
                    throw new InvalidOperationException("轴 " + (jointIndex + 1) + " 存在伺服驱动器报警，禁止启动运动。");

                double currentRev = status.Slaves[jointIndex].ActualRevolutions(CountsPerRevolution);
                double deltaRev = targetRev - currentRev;
                if (Math.Abs(deltaRev) < 0.0005)
                    return Task.FromResult(status);
                if (Math.Abs(deltaRev) > MaxSafeStepRev)
                    throw new ArgumentOutOfRangeException("targetRev", "单轴目标位移 " + deltaRev.ToString("0.###") + " 圈超过安全上限 ±" + MaxSafeStepRev + " 圈。");

                byte[] payload = Sc4iProtocol.BuildMoveAxisPtpPayload(targetRev, velRevPerSec, accRevPerSec2);
                Sc4iStatusReply reply = SendNextF2("轴 " + (jointIndex + 1) + " 绝对定位", Sc4iSubcmd.MoveAxisPtp, (byte)jointIndex, payload, 3000, ct);
                if (reply.ErrorCode != 0)
                    throw new Sc4iCommandException("轴 " + (jointIndex + 1) + " 绝对定位", reply);
                return Task.FromResult(reply);
            });
        }

        public Task<Sc4iStatusReply> MoveAxisRelativeAsync(
            int jointIndex, double deltaRev, double velRevPerSec, double accRevPerSec2, CancellationToken cancellationToken)
        {
            ValidateJointIndex(jointIndex);
            if (Double.IsNaN(deltaRev) || Double.IsInfinity(deltaRev) || deltaRev == 0.0 || Math.Abs(deltaRev) > MaxSafeStepRev)
                throw new ArgumentOutOfRangeException("deltaRev", "单次相对步长必须非零且不超过 ±" + MaxSafeStepRev + " 圈。");
            if (Double.IsNaN(velRevPerSec) || Double.IsInfinity(velRevPerSec) || velRevPerSec < 0.005 || velRevPerSec > MaxSafeVelocityRevPerSec)
                throw new ArgumentOutOfRangeException("velRevPerSec", "速度必须在 0.005 至 " + MaxSafeVelocityRevPerSec + " rev/s 之间。");
            if (Double.IsNaN(accRevPerSec2) || Double.IsInfinity(accRevPerSec2) || accRevPerSec2 < 0.05 || accRevPerSec2 > MaxSafeAccelerationRevPerSec2)
                throw new ArgumentOutOfRangeException("accRevPerSec2", "加速度必须在 0.05 至 " + MaxSafeAccelerationRevPerSec2 + " rev/s² 之间。");

            return RunConnectedAsync(cancellationToken, ct =>
            {
                Sc4iStatusReply status = SendNextF2("运动前状态核查", Sc4iSubcmd.Heartbeat, 0, null, defaultTimeoutMs, ct);
                if (status.ErrorCode != 0)
                    throw new Sc4iCommandException("运动前状态核查", status);
                if (!status.BusReady || !status.PdoOk || status.PdoAgeMs == uint.MaxValue || status.PdoAgeMs >= MaxFreshPdoAgeMs)
                    throw new InvalidOperationException("EtherCAT 总线未处于就绪 OP 或 PDO 刷新超时，禁止启动运动。");
                if (status.LatchedFault != 0 || status.EstopFlag != 0)
                    throw new InvalidOperationException("存在锁存故障或急停标志，请先排查并清除锁存。");
                if (!status.IsAxisEnabled(jointIndex) || !status.IsAxisAuthorized(jointIndex))
                    throw new InvalidOperationException("轴 " + (jointIndex + 1) + " 未处于使能且授权状态，请先点击“使能当前轴”。");
                if (status.MotionState != 0)
                    throw new InvalidOperationException("当前已有运动正在执行，请等待完成或先停止。");
                if (status.Slaves == null || status.Slaves.Length <= jointIndex || status.Slaves[jointIndex] == null
                    || status.Slaves[jointIndex].ValidMask == 0)
                    throw new InvalidOperationException("轴 " + (jointIndex + 1) + " 实时遥测无效，禁止基于未确认位置启动运动。");
                if (status.Slaves[jointIndex].IsFault)
                    throw new InvalidOperationException("轴 " + (jointIndex + 1) + " 存在伺服驱动器报警，禁止启动运动。");

                double currentRev = status.Slaves[jointIndex].ActualRevolutions(CountsPerRevolution);
                double targetRev = currentRev + deltaRev;
                byte[] payload = Sc4iProtocol.BuildMoveAxisPtpPayload(targetRev, velRevPerSec, accRevPerSec2);
                Sc4iStatusReply reply = SendNextF2("轴 " + (jointIndex + 1) + " 相对定位", Sc4iSubcmd.MoveAxisPtp, (byte)jointIndex, payload, 3000, ct);
                if (reply.ErrorCode != 0)
                    throw new Sc4iCommandException("轴 " + (jointIndex + 1) + " 相对定位", reply);
                return Task.FromResult(reply);
            });
        }

        public Task<Sc4iStatusReply> StartJogAsync(int jointIndex, double velRevPerSec, CancellationToken cancellationToken)
        {
            ValidateJointIndex(jointIndex);
            if (Double.IsNaN(velRevPerSec) || Double.IsInfinity(velRevPerSec) || velRevPerSec == 0.0 || Math.Abs(velRevPerSec) > MaxSafeJogVelocityRevPerSec)
                throw new ArgumentOutOfRangeException("velRevPerSec", "点动速度必须非零且不超过 ±" + MaxSafeJogVelocityRevPerSec + " rev/s。");
            byte[] payload = Sc4iProtocol.BuildJogPayload(velRevPerSec);
            return ExecuteF2Async("轴 " + (jointIndex + 1) + " 点动", Sc4iSubcmd.JogAxis, (byte)jointIndex, payload, 2000, true, cancellationToken);
        }

        public Task<Sc4iStatusReply> SendJogKeepaliveAsync(CancellationToken cancellationToken)
        {
            return ExecuteF2Async("点动保活", Sc4iSubcmd.JogKeepalive, 0, null, 1500, false, cancellationToken);
        }

        public Task<Sc4iStatusReply> StopHoldAsync(CancellationToken cancellationToken)
        {
            return ExecuteF2Async("停止并保持持力", Sc4iSubcmd.StopHold, 0, null, 2000, false, cancellationToken);
        }

        public Task<Sc4iStatusReply> ClearFaultsAndLatchAsync(CancellationToken cancellationToken)
        {
            return RunConnectedAsync(cancellationToken, async ct =>
            {
                Sc4iStatusReply status = SendNextF2("查询故障状态", Sc4iSubcmd.Heartbeat, 0, null, defaultTimeoutMs, ct);
                if (status.ErrorCode != 0)
                    throw new Sc4iCommandException("查询故障状态", status);
                byte faultMask = status.ComputeFaultedSlaveMask();
                if (faultMask != 0)
                {
                    Sc4iStatusReply drvReset = SendNextF2("复位驱动器报警", Sc4iSubcmd.FaultReset, faultMask, null, 3000, ct);
                    if (drvReset.ErrorCode != 0)
                        throw new Sc4iCommandException("复位驱动器报警", drvReset);
                    await Task.Delay(120, ct).ConfigureAwait(false);
                }

                Sc4iStatusReply cleared = SendNextF2("清除控制器故障/停机锁存", Sc4iSubcmd.FaultReset, 0, null, 3000, ct);
                if (cleared.ErrorCode != 0)
                    throw new Sc4iCommandException("清除控制器故障/停机锁存", cleared);
                return cleared;
            });
        }

        private Task<Sc4iStatusReply> ExecuteF2Async(
            string action, Sc4iSubcmd subcmd, byte arg, byte[] payload, int timeoutMs, bool throwOnErrorCode, CancellationToken cancellationToken)
        {
            return RunConnectedAsync(cancellationToken, ct =>
            {
                int effectiveTimeout = timeoutMs > 0 ? timeoutMs : defaultTimeoutMs;
                Sc4iStatusReply reply = SendNextF2(action, subcmd, arg, payload, effectiveTimeout, ct);
                if (throwOnErrorCode && reply.ErrorCode != 0)
                    throw new Sc4iCommandException(action, reply);
                return Task.FromResult(reply);
            });
        }

        private Task<Sc4iStatusReply> RunConnectedAsync(
            CancellationToken cancellationToken, Func<CancellationToken, Task<Sc4iStatusReply>> work)
        {
            return Task.Run(async () =>
            {
                using (CancellationTokenSource linked = CreateSessionLinkedCts(cancellationToken))
                {
                    await gate.WaitAsync(linked.Token).ConfigureAwait(false);
                    try
                    {
                        EnsureConnected();
                        return await work(linked.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        SafeReleaseGate();
                    }
                }
            });
        }

        private Sc4iStatusReply SendNextF2(string action, Sc4iSubcmd subcmd, byte arg, byte[] payload, int timeoutMs, CancellationToken ct)
        {
            int perAttemptTimeoutMs;
            int maxAttempts;
            GetRetryPolicy(subcmd, timeoutMs, out perAttemptTimeoutMs, out maxAttempts);

            Stopwatch overall = Stopwatch.StartNew();
            TimeoutException lastTimeout = null;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                int remaining = timeoutMs - (int)overall.ElapsedMilliseconds;
                if (remaining <= 0) break;
                int attemptTimeout = Math.Min(perAttemptTimeoutMs, remaining);
                if (attemptTimeout < 30 && attempt > 0) break;

                uint seq = nextSeq++;
                if (seq == 0) seq = nextSeq++;
                if (seq > maxUsedSeq) maxUsedSeq = seq;
                try
                {
                    Sc4iStatusReply reply = SendF2Internal(socket, remoteEndPoint, subcmd, arg, seq, bootId, sessionId, payload, attemptTimeout, ct);
                    lock (stateLock)
                    {
                        lastStatus = reply;
                    }
                    return reply;
                }
                catch (TimeoutException ex)
                {
                    lastTimeout = ex;
                    if (attempt + 1 >= maxAttempts || ct.IsCancellationRequested)
                        throw;
                }
            }
            throw lastTimeout ?? new TimeoutException("等待控制卡应答超时（" + timeoutMs + " ms）。");
        }

        private static void GetRetryPolicy(Sc4iSubcmd subcmd, int overallTimeoutMs, out int perAttemptTimeoutMs, out int maxAttempts)
        {
            // Only idempotent keepalive/read/stop subcommands may fast-retry on single UDP packet loss;
            // motion/enable/config commands remain single-shot to prevent duplicate execution.
            switch (subcmd)
            {
                case Sc4iSubcmd.Heartbeat:
                    perAttemptTimeoutMs = Math.Min(120, overallTimeoutMs);
                    maxAttempts = 4;
                    return;
                case Sc4iSubcmd.JogKeepalive:
                    perAttemptTimeoutMs = Math.Min(70, overallTimeoutMs);
                    maxAttempts = 3;
                    return;
                case Sc4iSubcmd.StopHold:
                    perAttemptTimeoutMs = Math.Min(150, overallTimeoutMs);
                    maxAttempts = 5;
                    return;
                case Sc4iSubcmd.Snapshot:
                    perAttemptTimeoutMs = Math.Min(150, overallTimeoutMs);
                    maxAttempts = 3;
                    return;
                default:
                    perAttemptTimeoutMs = overallTimeoutMs;
                    maxAttempts = 1;
                    return;
            }
        }

        private static Sc4iStatusReply SendF2InternalWithRetry(
            Socket udp, IPEndPoint remote, Sc4iSubcmd subcmd, byte arg, uint seq, ulong bootIdVal, ulong sessionIdVal,
            byte[] payload, int overallTimeoutMs, int perAttemptTimeoutMs, int maxAttempts, CancellationToken ct)
        {
            byte[] req = Sc4iProtocol.BuildF2Request(subcmd, arg, seq, bootIdVal, sessionIdVal, payload);
            byte expectedToken = (byte)(seq & 0x7F);
            return ExchangeValidatedWithRetry(
                udp, remote, req, Sc4iProtocol.F2ReplyLength, overallTimeoutMs, perAttemptTimeoutMs, maxAttempts, ct,
                raw =>
                {
                    Sc4iStatusReply parsed = Sc4iProtocol.ParseF2Reply(raw);
                    if (parsed.Subcmd != (byte)subcmd)
                        throw new ProtocolException("应答子命令不匹配：期望 0x" + ((byte)subcmd).ToString("X2", CultureInfo.InvariantCulture)
                            + "，实际 0x" + parsed.Subcmd.ToString("X2", CultureInfo.InvariantCulture));
                    if (seq != 0 && parsed.ErrorCode != 0x35u && parsed.Seq != seq && parsed.FrameToken != expectedToken)
                        throw new ProtocolException("应答序列号与本次请求不一致（期望 seq=" + seq + "，收到 seq=" + parsed.Seq + "）");
                    return parsed;
                });
        }

        private static T ExchangeValidatedWithRetry<T>(
            Socket udp, IPEndPoint remote, byte[] request, int expectedLength, int overallTimeoutMs, int perAttemptTimeoutMs, int maxAttempts,
            CancellationToken ct, Func<byte[], T> parser)
        {
            Stopwatch overall = Stopwatch.StartNew();
            TimeoutException lastTimeout = null;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                int remaining = overallTimeoutMs - (int)overall.ElapsedMilliseconds;
                if (remaining <= 0) break;
                int attemptTimeout = Math.Min(perAttemptTimeoutMs, remaining);
                if (attemptTimeout < 30 && attempt > 0) break;
                try
                {
                    return ExchangeValidated(udp, remote, request, expectedLength, attemptTimeout, ct, parser);
                }
                catch (TimeoutException ex)
                {
                    lastTimeout = ex;
                    if (attempt + 1 >= maxAttempts || ct.IsCancellationRequested)
                        throw;
                }
            }
            throw lastTimeout ?? new TimeoutException("等待控制卡应答超时（" + overallTimeoutMs + " ms）。");
        }

        private static Sc4iStatusReply SendF2Internal(
            Socket udp, IPEndPoint remote, Sc4iSubcmd subcmd, byte arg, uint seq, ulong bootIdVal, ulong sessionIdVal,
            byte[] payload, int timeoutMs, CancellationToken ct)
        {
            byte[] req = Sc4iProtocol.BuildF2Request(subcmd, arg, seq, bootIdVal, sessionIdVal, payload);
            byte expectedToken = (byte)(seq & 0x7F);
            return ExchangeValidated(
                udp, remote, req, Sc4iProtocol.F2ReplyLength, timeoutMs, ct,
                raw =>
                {
                    Sc4iStatusReply parsed = Sc4iProtocol.ParseF2Reply(raw);
                    if (parsed.Subcmd != (byte)subcmd)
                        throw new ProtocolException("应答子命令不匹配：期望 0x" + ((byte)subcmd).ToString("X2", CultureInfo.InvariantCulture)
                            + "，实际 0x" + parsed.Subcmd.ToString("X2", CultureInfo.InvariantCulture));
                    if (seq != 0 && parsed.ErrorCode != 0x35u && parsed.Seq != seq && parsed.FrameToken != expectedToken)
                        throw new ProtocolException("应答序列号与本次请求不一致（期望 seq=" + seq + "，收到 seq=" + parsed.Seq + "）");
                    return parsed;
                });
        }

        private static T ExchangeValidated<T>(
            Socket udp, IPEndPoint remote, byte[] request, int expectedLength, int timeoutMs, CancellationToken ct,
            Func<byte[], T> parser)
        {
            ct.ThrowIfCancellationRequested();
            // Drain any stale queued datagrams before sending the new request.
            byte[] buffer = new byte[65535];
            while (udp.Poll(0, SelectMode.SelectRead))
            {
                EndPoint stale = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    udp.ReceiveFrom(buffer, ref stale);
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.MessageSize)
                        continue;
                    break;
                }
                catch
                {
                    break;
                }
            }

            Stopwatch sw = Stopwatch.StartNew();
            udp.SendTo(request, remote);
            string lastReject = null;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                if (!udp.Poll(10000, SelectMode.SelectRead)) continue;
                EndPoint src = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try
                {
                    n = udp.ReceiveFrom(buffer, ref src);
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        lastReject = "收到 ICMP 端口不可达 (WSAECONNRESET)";
                        continue;
                    }
                    if (ex.SocketErrorCode == SocketError.MessageSize)
                    {
                        lastReject = "收到超长 UDP 报文，已忽略";
                        continue;
                    }
                    throw;
                }
                if (!remote.Equals(src))
                {
                    lastReject = "收到非目标端点的 UDP 包";
                    continue;
                }
                if (n != expectedLength)
                {
                    lastReject = "应答长度 " + n + " 与期望 " + expectedLength + " 不符";
                    continue;
                }
                byte[] copy = new byte[n];
                Array.Copy(buffer, copy, n);
                try
                {
                    T parsed = parser(copy);
                    ct.ThrowIfCancellationRequested();
                    return parsed;
                }
                catch (FormatException error)
                {
                    lastReject = error.Message;
                }
            }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException("等待控制卡应答超时（" + timeoutMs + " ms）。"
                + (lastReject == null ? "" : " 最后忽略原因：" + lastReject));
        }

        private static void ConfigureUdpSocket(Socket udp)
        {
            udp.ExclusiveAddressUse = true;
            try
            {
                // On Windows, ignore ICMP Port Unreachable errors on UDP sockets.
                udp.IOControl((IOControlCode)SioUdpConnReset, new byte[] { 0 }, null);
            }
            catch
            {
                // Unsupported on non-Windows platforms; safe to ignore.
            }
        }

        private static bool HasFourPopulatedIdentities(Sc4iStatusReply status)
        {
            if (status == null || status.Slaves == null || status.Slaves.Length != 4) return false;
            for (int i = 0; i < 4; i++)
            {
                if (status.Slaves[i] == null || status.Slaves[i].VendorId == 0 || status.Slaves[i].ProductCode == 0)
                    return false;
            }
            return true;
        }

        private static void ValidateJointIndex(int jointIndex)
        {
            if (jointIndex < 0 || jointIndex >= 4)
                throw new ArgumentOutOfRangeException("jointIndex", "轴索引必须在 0 至 3 之间。");
        }

        private static ulong GenerateSessionId()
        {
            byte[] bytes = new byte[8];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            ulong val = Sc4iProtocol.ReadU64LE(bytes, 0);
            return val == 0 ? 0x2026112100000001ul : val;
        }

        internal static uint ComputeRecoverySeq(uint snapSeq, uint priorMaxSeq)
        {
            // 100 ms ticks since 2026-01-01 outpace 5 Hz heartbeat and fit inside positive Int32 through 2032.
            long deciseconds = (long)((DateTime.UtcNow - new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds / 100.0);
            if (deciseconds < 10000L) deciseconds = 10000L;
            uint timeSeq = (uint)(deciseconds & 0x7FFFFFFFL);
            if (timeSeq < 10000u) timeSeq = 10000u;

            uint candidate = timeSeq;
            if (snapSeq < uint.MaxValue - 10000u && snapSeq + 1u > candidate)
                candidate = snapSeq + 1u;
            if (priorMaxSeq < uint.MaxValue - 10000u && priorMaxSeq + 1u > candidate)
                candidate = priorMaxSeq + 1u;
            return candidate;
        }

        private static IPAddress ParseUnicast(string text, string name)
        {
            if (String.IsNullOrWhiteSpace(text))
                throw new ArgumentException(name + " 必须是 IPv4 地址。");
            string trimmed = text.Trim();
            string[] parts = trimmed.Split('.');
            if (parts.Length != 4)
                throw new ArgumentException(name + " 必须是完整的四段 IPv4 地址。");
            for (int i = 0; i < 4; i++)
            {
                byte octet;
                if (!Byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out octet))
                    throw new ArgumentException(name + " 必须是完整的四段 IPv4 地址。");
            }
            IPAddress address;
            if (!IPAddress.TryParse(trimmed, out address) || address.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException(name + " 必须是 IPv4 地址。");
            byte[] bytes = address.GetAddressBytes();
            if (bytes[0] == 0 || bytes[0] >= 224 || bytes[3] == 255)
                throw new ArgumentException(name + " 必须是明确的单播地址。");
            return address;
        }

        private CancellationTokenSource CreateConnectLinkedCts(CancellationToken cancellationToken)
        {
            lock (stateLock)
            {
                if (disposed) throw new ObjectDisposedException("Sc4iClient");
                return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disposeCts.Token);
            }
        }

        private CancellationTokenSource CreateSessionLinkedCts(CancellationToken cancellationToken)
        {
            lock (stateLock)
            {
                if (disposed) throw new ObjectDisposedException("Sc4iClient");
                if (!connected || sessionCts == null)
                    throw new InvalidOperationException("尚未连接控制卡，请先点击“1. 连接控制卡”。");
                return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sessionCts.Token, disposeCts.Token);
            }
        }

        private void EnsureConnected()
        {
            ThrowIfDisposed();
            if (socket == null || !connected || bootId == 0 || sessionId == 0)
                throw new InvalidOperationException("尚未连接控制卡，请先点击“1. 连接控制卡”。");
        }

        private void CancelSessionInternal()
        {
            CancellationTokenSource cts;
            lock (stateLock)
            {
                cts = sessionCts;
            }
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
            }
        }

        private void CloseSocketInternal()
        {
            Socket oldSocket;
            CancellationTokenSource oldCts;
            lock (stateLock)
            {
                oldSocket = socket;
                oldCts = sessionCts;
                socket = null;
                remoteEndPoint = null;
                sessionCts = null;
                connected = false;
                bootId = 0;
                sessionId = 0;
                nextSeq = 1;
                lastStatus = null;
            }
            if (oldCts != null)
            {
                try { oldCts.Cancel(); } catch { }
                try { oldCts.Dispose(); } catch { }
            }
            if (oldSocket != null)
            {
                try { oldSocket.Dispose(); } catch { }
            }
        }

        private void ThrowIfDisposed()
        {
            lock (stateLock)
            {
                if (disposed) throw new ObjectDisposedException("Sc4iClient");
            }
        }

        private void SafeReleaseGate()
        {
            try { gate.Release(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            lock (stateLock)
            {
                if (disposed) return;
                disposed = true;
            }
            try { disposeCts.Cancel(); } catch { }
            CancelSessionInternal();
            bool acquired = false;
            try
            {
                acquired = gate.Wait(500);
            }
            catch (ObjectDisposedException) { }
            CloseSocketInternal();
            if (acquired)
            {
                SafeReleaseGate();
                try { gate.Dispose(); } catch { }
                try { disposeCts.Dispose(); } catch { }
            }
        }
    }
}
