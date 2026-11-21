using System;
using System.IO;
using System.Linq;

namespace Ru7100.TvUi
{
    /// Development switches for hunting rendering problems on the TV without a rebuild. One flag per line in
    /// `<app data>/ui-dev-flags.txt` (read when the UI host starts), or set the properties from code:
    ///   skip-gradients  leave out every generated gradient, scrim ramp and corner mask image
    ///   plain-boxes     draw rounded boxes as plain rectangles instead of tinted 9-patch images
    ///   probe           draw a strip of labelled test objects along the top edge (TvUiProbe)
    ///   no-card-hole    don't play Home's pop-out card trailers (their COPY-op video hole is unverified on the RU7100)
    internal static class TvUiDevFlags
    {
        public static bool SkipGradients { get; set; }
        public static bool PlainBoxes { get; set; }
        public static bool Probe { get; set; }
        public static bool NoCardHole { get; set; }
        public static string Source { get; private set; } = "none";

        public static void Load(string dataDirectory)
        {
            // Environment switches: RU7100_UI_PROBE=1, RU7100_UI_PLAIN_BOXES=1, RU7100_UI_SKIP_GRADIENTS=1.
            if (Environment.GetEnvironmentVariable("RU7100_UI_PROBE") == "1") { Probe = true; Source = "environment"; }
            if (Environment.GetEnvironmentVariable("RU7100_UI_PLAIN_BOXES") == "1") { PlainBoxes = true; Source = "environment"; }
            if (Environment.GetEnvironmentVariable("RU7100_UI_SKIP_GRADIENTS") == "1") { SkipGradients = true; Source = "environment"; }
            try
            {
                var path = Path.Combine(dataDirectory, "ui-dev-flags.txt");
                if (!File.Exists(path))
                    return;
                var flags = File.ReadAllLines(path).Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0 && l[0] != '#').ToList();
                SkipGradients |= flags.Contains("skip-gradients");
                PlainBoxes |= flags.Contains("plain-boxes");
                Probe |= flags.Contains("probe");
                NoCardHole |= flags.Contains("no-card-hole");
                Source = path;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        public static object Describe() => new { skipGradients = SkipGradients, plainBoxes = PlainBoxes, probe = Probe, noCardHole = NoCardHole, source = Source };
    }
}
