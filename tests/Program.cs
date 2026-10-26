using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NewCard.Core;

namespace NewCard.Tests
{
    internal static class Program
    {
        private static int count;
        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); count++; }
        private static void Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T) { count++; return; } throw new Exception("Expected " + typeof(T).Name); }

        private static int Main()
        {
            try
            {
                int protocol = ProtocolTests.Run();
                ModelTests();
                TransportTests().GetAwaiter().GetResult();
                Console.WriteLine("PASS: protocol=" + protocol + ", model/transport=" + count + "; synthetic data and loopback only.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        private static void ModelTests()
        {
            var model = new OfflineArmModel();
            Check(model.AxisCount == 4 && !model.IsMoving, "Default four axes are idle");
            model.MoveRelative(1, 10, 4);
            model.Advance(1.5);
            Check(model.GetPosition(1) == 6 && model.GetPosition(0) == 0, "Only selected synthetic axis moves");
            Throws<InvalidOperationException>(() => model.MoveRelative(2, 1, 1));
            model.StopAll(); model.Advance(3);
            Check(model.GetPosition(1) == 6 && !model.IsMoving, "Stop must hold synthetic position");
            model.MoveRelative(1, -8, 5); model.Advance(100);
            Check(model.GetPosition(1) == -2 && !model.IsMoving, "Negative relative motion clamps to target");
            foreach (double bad in new[] { double.NaN, double.PositiveInfinity, 0, 1001.0 })
                Throws<ArgumentException>(() => model.MoveRelative(0, bad, 1));
            foreach (double bad in new[] { double.NaN, double.NegativeInfinity, 0, -1, 101.0 })
                Throws<ArgumentException>(() => model.MoveRelative(0, 1, bad));
            Throws<ArgumentOutOfRangeException>(() => model.MoveRelative(4, 1, 1));
            Throws<ArgumentOutOfRangeException>(() => model.Advance(double.NaN));
            model.MoveRelative(0, 1000, 100); model.Advance(10);
            Throws<ArgumentException>(() => model.MoveRelative(0, 1, 1));
            model.Reset();
            Check(model.GetPosition(0) == 0 && model.GetPosition(1) == 0 && !model.IsMoving, "Reset is synthetic only");
        }
        private static byte[] Reply(byte[] request)
        {
            var reply = new byte[100];
            reply[1] = 0xA5; reply[3] = 100; reply[4] = 1; reply[6] = request[6];
            reply[8] = 0x78; reply[9] = 0x56; reply[10] = 0x34; reply[11] = 0x12;
            Array.Copy(request, 8, reply, 16, 72);
            Seal(reply); return reply;
        }
        private static void Seal(byte[] packet)
        { int sum = 0; for (int i = 1; i < packet.Length; i++) sum += packet[i]; packet[0] = (byte)sum; }
        private static async Task TransportTests()
        {
            var client = new EchoClient();
            using (var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            using (var impostor = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                int port = ((IPEndPoint)server.Client.LocalEndPoint).Port;
                var probing = client.ProbeAsync("127.0.0.1", 0, "127.0.0.1", port, 2000, CancellationToken.None);
                var receiving = server.ReceiveAsync();
                Check(await Task.WhenAny(receiving, Task.Delay(3000)) == receiving, "Request arrived on loopback");
                var received = await receiving;
                Check(received.Buffer.Length == 100 && received.Buffer[1] == 0xB5 && received.Buffer[4] == 1,
                    "Only fixed echo command is sent");
                byte[] packet = Reply(received.Buffer);
                await impostor.SendAsync(packet, packet.Length, received.RemoteEndPoint);
                await Task.Delay(100);
                Check(!probing.IsCompleted, "Wrong sender must not satisfy request");
                byte[] stale = (byte[])packet.Clone(); stale[16] ^= 1; Seal(stale);
                await server.SendAsync(stale, stale.Length, received.RemoteEndPoint);
                await Task.Delay(100);
                Check(!probing.IsCompleted, "Old challenge must not satisfy request");
                await server.SendAsync(packet, packet.Length, received.RemoteEndPoint);
                EchoResult result = await probing;
                Check(result.WindowId == 0x12345678 && result.Marker == 0xA5, "Current reply accepted");
                Check(!server.Client.Poll(100000, SelectMode.SelectRead), "No auto-retry or background polling");
            }
            using (var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                int port = ((IPEndPoint)silent.Client.LocalEndPoint).Port;
                Throws<TimeoutException>(() => client.ProbeAsync("127.0.0.1", 0, "127.0.0.1", port, 150, CancellationToken.None).GetAwaiter().GetResult());
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel();
                    Throws<OperationCanceledException>(() => client.ProbeAsync("127.0.0.1", 0, "127.0.0.1", port, 150, cancellation.Token).GetAwaiter().GetResult());
                }
            }
            using (var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            using (var cancellation = new CancellationTokenSource())
            {
                int port = ((IPEndPoint)server.Client.LocalEndPoint).Port;
                var active = client.ProbeAsync("127.0.0.1", 0, "127.0.0.1", port, 3000, cancellation.Token);
                var pending = server.ReceiveAsync();
                Check(await Task.WhenAny(pending, Task.Delay(3000)) == pending, "Active request received before cancellation");
                var request = await pending;
                int fixedLocalPort = request.RemoteEndPoint.Port;
                cancellation.Cancel();
                Check(await Task.WhenAny(active, Task.Delay(1000)) == active, "Active receive cancels promptly");
                Throws<OperationCanceledException>(() => active.GetAwaiter().GetResult());
                var retry = client.ProbeAsync("127.0.0.1", fixedLocalPort, "127.0.0.1", port, 2000, CancellationToken.None);
                pending = server.ReceiveAsync();
                Check(await Task.WhenAny(pending, Task.Delay(3000)) == pending, "Port reusable immediately after cancellation");
                request = await pending;
                byte[] response = Reply(request.Buffer);
                await server.SendAsync(response, response.Length, request.RemoteEndPoint);
                Check((await retry).Marker == 0xA5, "Cancelled session does not poison next probe");
                var timedOut = client.ProbeAsync("127.0.0.1", fixedLocalPort, "127.0.0.1", port, 150, CancellationToken.None);
                pending = server.ReceiveAsync();
                Check(await Task.WhenAny(pending, Task.Delay(1000)) == pending, "Silent fixture received timeout request");
                await pending;
                Throws<TimeoutException>(() => timedOut.GetAwaiter().GetResult());
                retry = client.ProbeAsync("127.0.0.1", fixedLocalPort, "127.0.0.1", port, 2000, CancellationToken.None);
                pending = server.ReceiveAsync();
                Check(await Task.WhenAny(pending, Task.Delay(3000)) == pending, "Port reusable after timeout");
                request = await pending; response = Reply(request.Buffer);
                await server.SendAsync(response, response.Length, request.RemoteEndPoint);
                Check((await retry).Marker == 0xA5, "Timeout recovery succeeds");
            }
            Throws<ArgumentException>(() => client.ProbeAsync("0.0.0.0", 6000, "192.168.8.20", 5000, 1000, CancellationToken.None));
            Throws<ArgumentException>(() => client.ProbeAsync("127.0.0.1", 6000, "255.255.255.255", 5000, 1000, CancellationToken.None));
        }
    }
}
