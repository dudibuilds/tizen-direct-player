using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Ru7100Probe
{
    /// How the TV is connected and how hard it works, for dev.diagnostics: network interface type and link details the
    /// kernel exposes without extra privileges (/sys/class/net, /proc/net/wireless), the torrent engine's measured peak
    /// and sustained download rates, and the app's CPU use. Sizing the race and the bitrate headroom from measured
    /// numbers, not from an assumed link speed.
    internal sealed class NetDiagnostics
    {
        private readonly object gate = new object();
        private double peak;
        private double sustained;
        private readonly double[] window = new double[10];
        private int samples;
        private TimeSpan lastCpu;
        private DateTime lastCpuAt;
        private double cpuPercent = -1;
        private double cpuPeak;

        /// One download-rate sample per second while a stream is open.
        public void Sample(double bytesPerSecond)
        {
            lock (gate)
            {
                peak = Math.Max(peak, bytesPerSecond);
                window[samples++ % window.Length] = bytesPerSecond;
                if (samples >= window.Length)
                    sustained = Math.Max(sustained, window.Average());
                SampleCpu();
            }
        }

        /// Highest 1 s download rate seen, and the best 10 s average (what a stream can count on).
        public double PeakBytesPerSecond { get { lock (gate) return peak; } }
        public double SustainedBytesPerSecond { get { lock (gate) return sustained; } }

        // Must hold gate.
        private void SampleCpu()
        {
            try
            {
                var now = DateTime.UtcNow;
                var cpu = Process.GetCurrentProcess().TotalProcessorTime;
                if (lastCpuAt != default && now - lastCpuAt >= TimeSpan.FromSeconds(1))
                {
                    cpuPercent = (cpu - lastCpu).TotalMilliseconds / (now - lastCpuAt).TotalMilliseconds / Environment.ProcessorCount * 100;
                    cpuPeak = Math.Max(cpuPeak, cpuPercent);
                }
                if (lastCpuAt == default || now - lastCpuAt >= TimeSpan.FromSeconds(1))
                {
                    lastCpu = cpu;
                    lastCpuAt = now;
                }
            }
            catch (Exception) { }
        }

        public string Describe()
        {
            double p, s, c, cp;
            lock (gate)
            {
                SampleCpu();
                p = peak;
                s = sustained;
                c = cpuPercent;
                cp = cpuPeak;
            }
            return $"{Link()} peak={Mbit(p)} sustained10s={Mbit(s)} cpu={(c < 0 ? "?" : c.ToString("0") + "%")} cpuPeak={cp:0}% of {Environment.ProcessorCount} cores load={Load()}";
        }

        private static string Mbit(double bytesPerSecond) => (bytesPerSecond * 8 / 1e6).ToString("0.0") + "Mbit/s";

        /// The interfaces that are up: name, wifi/ethernet, link speed (ethernet) or signal (wifi).
        internal static string Link()
        {
            try
            {
                const string root = "/sys/class/net";
                if (!Directory.Exists(root))
                    return "net=unknown";
                var parts = Directory.GetDirectories(root).Select(Path.GetFileName).Where(n => n != "lo").Select(name =>
                {
                    var dir = Path.Combine(root, name);
                    var state = Read(Path.Combine(dir, "operstate"));
                    if (state != "up")
                        return null;
                    var wifi = Directory.Exists(Path.Combine(dir, "wireless")) || Directory.Exists(Path.Combine(dir, "phy80211"));
                    var detail = wifi ? Wireless(name) : Speed(Read(Path.Combine(dir, "speed")));
                    return name + ":" + (wifi ? "wifi" : "ethernet") + (detail.Length > 0 ? " " + detail : "");
                }).Where(p => p != null).ToList();
                return "net=" + (parts.Count == 0 ? "none up" : string.Join(", ", parts));
            }
            catch (Exception error) { return "net=? (" + error.Message + ")"; }
        }

        private static string Speed(string mbit) => int.TryParse(mbit, out var v) && v > 0 ? v + "Mbit/s link" : "";

        /// Link quality and signal from /proc/net/wireless (the Wi-Fi bit rate itself needs nl80211, not available here).
        private static string Wireless(string name)
        {
            var line = (Read("/proc/net/wireless") ?? "").Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(name + ":", StringComparison.Ordinal));
            if (line == null)
                return "";
            var fields = line.Substring(line.IndexOf(':') + 1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return fields.Length >= 3 ? $"quality {fields[1].TrimEnd('.')} signal {fields[2].TrimEnd('.')}dBm" : "";
        }

        private static string Load()
        {
            var text = Read("/proc/loadavg");
            return string.IsNullOrEmpty(text) ? "?" : string.Join(" ", text.Split(' ').Take(3));
        }

        private static string Read(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
            catch (Exception) { return null; }
        }
    }
}
