using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace NewCard.Core
{
    public sealed class EchoResult
    {
        public byte Marker { get; internal set; }
        public uint WindowId { get; internal set; }
        public string Message { get; internal set; }
        public double ElapsedMilliseconds { get; internal set; }
    }

    /// <summary>One explicit UDP exchange. Never polls, initializes a bus or sends motion.</summary>
    public sealed class EchoClient
    {
        public Task<EchoResult> ProbeAsync(string localIp, int localPort, string cardIp, int cardPort,
            int timeoutMs, CancellationToken cancellationToken)
        {
            IPAddress local = ParseUnicast(localIp, "电脑 IP");
            IPAddress remote = ParseUnicast(cardIp, "运动卡 IP");
            if (localPort < 0 || localPort > 65535) throw new ArgumentOutOfRangeException("localPort");
            if (cardPort < 1 || cardPort > 65535) throw new ArgumentOutOfRangeException("cardPort");
            if (timeoutMs < 100 || timeoutMs > 10000) throw new ArgumentOutOfRangeException("timeoutMs");
            return Task.Run(() => Exchange(new IPEndPoint(local, localPort), new IPEndPoint(remote, cardPort), timeoutMs, cancellationToken), cancellationToken);
        }

        private static IPAddress ParseUnicast(string text, string name)
        {
            IPAddress address;
            if (!IPAddress.TryParse(text, out address) || address.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException(name + " 必须是 IPv4 地址。");
            byte[] bytes = address.GetAddressBytes();
            if (bytes[0] == 0 || bytes[0] >= 224 || bytes[3] == 255)
                throw new ArgumentException(name + " 必须是明确的单播地址。");
            return address;
        }

        private static EchoResult Exchange(IPEndPoint local, IPEndPoint remote, int timeoutMs, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            byte[] challenge = new byte[72];
            byte[] nonce = new byte[1];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            { random.GetBytes(challenge); random.GetBytes(nonce); }
            byte requestToken = (byte)(nonce[0] % 241);
            byte[] request = CardProtocol.BuildEchoRequest(requestToken, challenge);
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.ExclusiveAddressUse = true;
                socket.Bind(local);
                token.ThrowIfCancellationRequested();
                Stopwatch elapsed = Stopwatch.StartNew();
                socket.SendTo(request, remote);
                byte[] buffer = new byte[65535];
                string rejected = null;
                while (elapsed.ElapsedMilliseconds < timeoutMs)
                {
                    token.ThrowIfCancellationRequested();
                    if (!socket.Poll(50000, SelectMode.SelectRead)) continue;
                    EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                    int count = socket.ReceiveFrom(buffer, ref source);
                    if (!remote.Equals(source)) { rejected = "收到其他地址或端口的数据，已忽略"; continue; }
                    byte[] packet = new byte[count];
                    Array.Copy(buffer, packet, count);
                    try
                    {
                        EchoReply reply = CardProtocol.ParseEchoReply(packet, requestToken, challenge);
                        token.ThrowIfCancellationRequested();
                        return new EchoResult { Marker = reply.Marker, WindowId = reply.WindowId,
                            ElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds,
                            Message = "App 通信回显校验通过；未查询轴状态，不能据此判断总线或机械臂就绪。" };
                    }
                    catch (FormatException error) { rejected = error.Message; }
                }
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("未收到本次探测的有效 App 回显。请核对两端 IP、固定回包端口及固件模式。" +
                    (rejected == null ? "" : " 最后拒绝原因：" + rejected));
            }
        }
    }
}
