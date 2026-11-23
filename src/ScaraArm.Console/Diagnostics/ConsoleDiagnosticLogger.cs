using System;
using System.Globalization;
using System.IO;
using System.Text;
using ScaraArm.Core.Protocol;
using ScaraArm.Core.Transport;

namespace ScaraArm.Console.Diagnostics
{
    /// <summary>
    /// Thread-safe daily UTF-8 diagnostic file logger for transport retries, faults, and console operations.
    /// Disk I/O errors are never propagated so logging can never disrupt hardware control.
    /// </summary>
    internal sealed class ConsoleDiagnosticLogger : IDisposable
    {
        private readonly object sync = new object();
        private readonly string logDirectory;
        private readonly bool enabled;
        private readonly Encoding utf8NoBom = new UTF8Encoding(false);
        private uint lastLoggedLatchedFault = uint.MaxValue;
        private DateTime lastLinkSummaryUtc = DateTime.MinValue;
        private bool disposed;

        public ConsoleDiagnosticLogger(string logDirectory, bool enabled)
        {
            this.logDirectory = logDirectory;
            this.enabled = enabled && !String.IsNullOrWhiteSpace(logDirectory);
        }

        public void LogInfo(string category, string message)
        {
            WriteLine("INFO ", category, message);
        }

        public void LogWarning(string category, string message)
        {
            WriteLine("WARN ", category, message);
        }

        public void LogError(string category, string message)
        {
            WriteLine("ERROR", category, message);
        }

        public void LogTransportEvent(Sc4iTransportEvent ev)
        {
            if (!enabled || ev == null) return;

            bool isKeepalive = ev.CommandCode == 0xF2
                && (ev.Subcmd == Sc4iSubcmd.Heartbeat || ev.Subcmd == Sc4iSubcmd.JogKeepalive);
            bool latchChanged = ev.Succeeded && ev.LatchedFault != lastLoggedLatchedFault;
            if (ev.Succeeded)
                lastLoggedLatchedFault = ev.LatchedFault;

            if (isKeepalive && ev.Succeeded && ev.Attempts <= 1 && ev.ErrorCode == 0 && !latchChanged)
                return;

            string level = !ev.Succeeded || ev.ErrorCode != 0 ? "ERROR"
                : (ev.Attempts > 1 || ev.LatchedFault != 0) ? "WARN "
                : "INFO ";

            string text = String.Format(
                CultureInfo.InvariantCulture,
                "{0} seq={1} attempts={2} ok={3} rtt={4:0.2}ms err=0x{5:X2} lat=0x{6:X2} pdoAge={7}ms{8}",
                ev.SubcommandName ?? "Cmd",
                ev.Seq,
                ev.Attempts,
                ev.Succeeded ? 1 : 0,
                ev.RttMs,
                ev.ErrorCode,
                ev.LatchedFault,
                ev.PdoAgeMs,
                String.IsNullOrEmpty(ev.Note) ? "" : (" | " + ev.Note));

            WriteLine(level, "UDP", text);
        }

        public void LogLinkSummaryIfDue(Sc4iLinkQualitySnapshot snap, TimeSpan interval)
        {
            if (!enabled || snap == null || snap.TotalExchanges == 0) return;

            DateTime now = DateTime.UtcNow;
            lock (sync)
            {
                if (disposed) return;
                if (lastLinkSummaryUtc != DateTime.MinValue && (now - lastLinkSummaryUtc) < interval)
                    return;
                lastLinkSummaryUtc = now;
            }

            string level = snap.IsDegraded ? "WARN " : "INFO ";
            string msg = String.Format(
                CultureInfo.InvariantCulture,
                "周期链路汇总: totalEx={0} retries={1} timeouts={2} winLoss={3:0.0}% (winRetries={4}/{5}) lastRtt={6:0.2}ms winMaxRtt={7:0.2}ms",
                snap.TotalExchanges,
                snap.TotalRetries,
                snap.TotalTimeouts,
                snap.WindowLossRatePercent,
                snap.WindowRetries,
                snap.WindowTransmissions,
                snap.LastRttMs,
                snap.WindowMaxRttMs);
            WriteLine(level, "LINK", msg);
        }

        private void WriteLine(string level, string category, string message)
        {
            if (!enabled) return;

            DateTime localNow = DateTime.Now;
            string line = String.Format(
                CultureInfo.InvariantCulture,
                "[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] [{2}] {3}{4}",
                localNow,
                level,
                category ?? "APP",
                message ?? "",
                Environment.NewLine);

            lock (sync)
            {
                if (disposed) return;
                try
                {
                    Directory.CreateDirectory(logDirectory);
                    string filePath = Path.Combine(
                        logDirectory,
                        "scara-" + localNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
                    File.AppendAllText(filePath, line, utf8NoBom);
                }
                catch
                {
                    // Never allow logging failures to affect console UI or motion safety.
                }
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                disposed = true;
            }
        }
    }
}
