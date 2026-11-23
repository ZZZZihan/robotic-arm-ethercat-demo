using System;
using ScaraArm.Core.Protocol;

namespace ScaraArm.Core.Transport
{
    /// <summary>
    /// Diagnostic telemetry for a single UDP request/reply exchange (including any retries).
    /// </summary>
    public sealed class Sc4iTransportEvent
    {
        public DateTime TimestampUtc { get; internal set; }
        public byte CommandCode { get; internal set; }
        public Sc4iSubcmd Subcmd { get; internal set; }
        public string SubcommandName { get; internal set; }
        public uint Seq { get; internal set; }
        public int Attempts { get; internal set; }
        public bool Succeeded { get; internal set; }
        public double RttMs { get; internal set; }
        public uint ErrorCode { get; internal set; }
        public uint LatchedFault { get; internal set; }
        public uint PdoAgeMs { get; internal set; }
        public string Note { get; internal set; }
    }

    /// <summary>
    /// Rolling-window and session-cumulative UDP link quality snapshot.
    /// </summary>
    public sealed class Sc4iLinkQualitySnapshot
    {
        public long TotalExchanges { get; internal set; }
        public long TotalTransmissions { get; internal set; }
        public long TotalRetries { get; internal set; }
        public long TotalTimeouts { get; internal set; }
        public int WindowExchanges { get; internal set; }
        public int WindowTransmissions { get; internal set; }
        public int WindowRetries { get; internal set; }
        public double WindowLossRatePercent { get; internal set; }
        public double LastRttMs { get; internal set; }
        public double WindowMaxRttMs { get; internal set; }

        public bool IsDegraded
        {
            get { return WindowExchanges >= 10 && WindowLossRatePercent >= 5.0; }
        }

        public bool IsSevere
        {
            get { return WindowExchanges >= 10 && WindowLossRatePercent >= 15.0; }
        }
    }

    /// <summary>
    /// Thread-safe 100-exchange circular buffer tracking packet retransmissions and RTT.
    /// </summary>
    internal sealed class Sc4iLinkQualityTracker
    {
        public const int DefaultWindowSize = 100;

        private struct Entry
        {
            public int Transmissions;
            public int Retries;
            public int LostPackets;
            public double RttMs;
        }

        private readonly object sync = new object();
        private readonly Entry[] window = new Entry[DefaultWindowSize];
        private int writeIndex;
        private int count;
        private long totalExchanges;
        private long totalTransmissions;
        private long totalRetries;
        private long totalTimeouts;
        private double lastRttMs;

        public void Reset()
        {
            lock (sync)
            {
                Array.Clear(window, 0, window.Length);
                writeIndex = 0;
                count = 0;
                totalExchanges = 0;
                totalTransmissions = 0;
                totalRetries = 0;
                totalTimeouts = 0;
                lastRttMs = 0.0;
            }
        }

        public void RecordExchange(int attempts, bool succeeded, double rttMs)
        {
            int effectiveAttempts = attempts > 0 ? attempts : 1;
            int retries = effectiveAttempts - 1;
            int lost = succeeded ? retries : effectiveAttempts;
            double safeRtt = (Double.IsNaN(rttMs) || Double.IsInfinity(rttMs) || rttMs < 0.0) ? 0.0 : rttMs;

            lock (sync)
            {
                totalExchanges++;
                totalTransmissions += effectiveAttempts;
                totalRetries += retries;
                if (!succeeded) totalTimeouts++;
                if (succeeded) lastRttMs = safeRtt;

                window[writeIndex] = new Entry
                {
                    Transmissions = effectiveAttempts,
                    Retries = retries,
                    LostPackets = lost,
                    RttMs = succeeded ? safeRtt : 0.0
                };
                writeIndex = (writeIndex + 1) % window.Length;
                if (count < window.Length) count++;
            }
        }

        public Sc4iLinkQualitySnapshot Snapshot()
        {
            lock (sync)
            {
                int winTx = 0;
                int winRetries = 0;
                int winLost = 0;
                double maxRtt = 0.0;

                for (int i = 0; i < count; i++)
                {
                    Entry e = window[i];
                    winTx += e.Transmissions;
                    winRetries += e.Retries;
                    winLost += e.LostPackets;
                    if (e.RttMs > maxRtt) maxRtt = e.RttMs;
                }

                double lossPct = winTx > 0 ? (winLost * 100.0) / winTx : 0.0;
                return new Sc4iLinkQualitySnapshot
                {
                    TotalExchanges = totalExchanges,
                    TotalTransmissions = totalTransmissions,
                    TotalRetries = totalRetries,
                    TotalTimeouts = totalTimeouts,
                    WindowExchanges = count,
                    WindowTransmissions = winTx,
                    WindowRetries = winRetries,
                    WindowLossRatePercent = lossPct,
                    LastRttMs = lastRttMs,
                    WindowMaxRttMs = maxRtt
                };
            }
        }
    }
}
