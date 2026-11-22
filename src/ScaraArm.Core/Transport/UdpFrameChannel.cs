using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace ScaraArm.Core.Transport
{
    /// <summary>
    /// Low-level validated UDP datagram channel with stale-packet draining,
    /// Windows ICMP port-unreachable suppression, and bounded retry support.
    /// </summary>
    internal sealed class UdpFrameChannel : IDisposable
    {
        private const int SioUdpConnReset = -1744830452;

        private readonly Socket socket;
        private readonly IPEndPoint remoteEndPoint;

        private UdpFrameChannel(Socket socket, IPEndPoint remoteEndPoint)
        {
            this.socket = socket;
            this.remoteEndPoint = remoteEndPoint;
        }

        public static UdpFrameChannel Open(string localIp, int localPort, string cardIp, int cardPort)
        {
            IPAddress local = ParseUnicast(localIp, "电脑 IP");
            IPAddress remote = ParseUnicast(cardIp, "控制卡 IP");
            if (localPort < 0 || localPort > 65535) throw new ArgumentOutOfRangeException("localPort");
            if (cardPort < 1 || cardPort > 65535) throw new ArgumentOutOfRangeException("cardPort");

            Socket candidate = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                ConfigureUdpSocket(candidate);
                candidate.Bind(new IPEndPoint(local, localPort));
                return new UdpFrameChannel(candidate, new IPEndPoint(remote, cardPort));
            }
            catch
            {
                candidate.Dispose();
                throw;
            }
        }

        public T ExchangeValidated<T>(
            byte[] request,
            int expectedLength,
            int timeoutMs,
            CancellationToken ct,
            Func<byte[], T> parser)
        {
            ct.ThrowIfCancellationRequested();
            byte[] buffer = new byte[65535];
            DrainStaleDatagrams(buffer);

            Stopwatch sw = Stopwatch.StartNew();
            socket.SendTo(request, remoteEndPoint);
            string lastReject = null;

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                if (!socket.Poll(10000, SelectMode.SelectRead)) continue;

                EndPoint src = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try
                {
                    n = socket.ReceiveFrom(buffer, ref src);
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

                if (!remoteEndPoint.Equals(src))
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

        public T ExchangeValidatedWithRetry<T>(
            byte[] request,
            int expectedLength,
            int overallTimeoutMs,
            int perAttemptTimeoutMs,
            int maxAttempts,
            CancellationToken ct,
            Func<byte[], T> parser)
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
                    return ExchangeValidated(request, expectedLength, attemptTimeout, ct, parser);
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

        public void Dispose()
        {
            try { socket.Dispose(); } catch { }
        }

        private void DrainStaleDatagrams(byte[] buffer)
        {
            while (socket.Poll(0, SelectMode.SelectRead))
            {
                EndPoint stale = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    socket.ReceiveFrom(buffer, ref stale);
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

        internal static IPAddress ParseUnicast(string text, string name)
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
    }
}
