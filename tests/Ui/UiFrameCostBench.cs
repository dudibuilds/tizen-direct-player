using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    /// Frame cost of quick navigation on Home, measured the way the TV host works: layout build, animator step and
    /// lowering for every 16 ms frame of a held Right across a row and repeated Down. It writes its numbers to the
    /// file named by RU7100_UI_BENCH and does nothing otherwise (timings are machine dependent, so nothing is asserted
    /// on them). Keys arrive every 128 ms of simulated time, as a held arrow does.
    public class UiFrameCostBench
    {
        public sealed class Result
        {
            public List<double> FrameMs = new List<double>();
            public int Frames, NodesMax, Created, PopCreated, PopNodes;
            /// Objects the TV painter creates when it keeps departed objects parked (up to 160, backdrops excluded)
            /// and reuses them when their key comes back.
            public int CreatedWithParking;
            public bool Popped;
            /// Megapixels of pictures first handed to the painter while navigating (the TV decodes each on its main
            /// loop), by the size the artwork host serves for that URL. Deterministic.
            public double BackdropMp, CardArtMp, PosterMp, LogoMp, StillMp;
            public int Pictures;
        }

        /// Pixels of an episode still by the width in its URL (".../w500.jpg" is 500x281); 780 px when unsized.
        private static double StillPixels(string url)
        {
            var m = System.Text.RegularExpressions.Regex.Match(url, @"/w(\d+)(\.jpg|/)");
            var w = m.Success ? int.Parse(m.Groups[1].Value) : 780;
            return w * Math.Round(w * 9 / 16.0);
        }

        public static Result Run(Action<UiHarness> prepare = null)
        {
            var h = LayoutGridTests.FullHome();
            h.Shell.Start(new HomeViewModel());
            h.Shell.Services.LastInteractionMs = 0;
            h.Shell.Services.PreviewInteractionWindowMs = long.MaxValue / 4;   // as if the viewer keeps navigating
            h.Wait(600);
            prepare?.Invoke(h);
            var dir = Path.Combine(Path.GetTempPath(), "ru7100-bench-" + Guid.NewGuid().ToString("N"));
            var assets = new UiAssets(dir);
            assets.Prewarm();   // as the TV host does at launch, off the UI thread
            var animator = new UiAnimator();
            var result = new Result();
            var known = new HashSet<string>();
            var parkedOrder = new LinkedList<string>();
            var parked = new Dictionary<string, LinkedListNode<string>>();
            long now = 0;
            var watch = new Stopwatch();
            var pictures = new HashSet<string>();
            var wasPopped = false;
            var ticks = 0;
            var lastCount = 0;
            var lastBig = new HashSet<string>();
            void Frames(int count, bool measure)
            {
                for (var i = 0; i < count; i++)
                {
                    watch.Restart();
                    var target = UiLayout.Build(h.Shell);
                    var stepped = animator.Step(target, now);
                    var lowered = UiFrameLowering.Lower(stepped, assets);
                    watch.Stop();
                    var created = 0;
                    var createdParking = 0;
                    foreach (var n in lowered.Nodes)
                        if (n.Key != null && known.Add(n.Key))
                        {
                            created++;
                            if (parked.TryGetValue(n.Key, out var spot)) { parkedOrder.Remove(spot); parked.Remove(n.Key); }
                            else createdParking++;
                        }
                    if (measure)
                        foreach (var n in lowered.Nodes)
                        {
                            var url = n.ImageUrl;
                            if (n.Kind != UiNodeKind.Image || url == null || !url.StartsWith("http", StringComparison.Ordinal) || !pictures.Add(url))
                                continue;
                            result.Pictures++;
                            if (url.Contains("/background/small/")) result.CardArtMp += 480 * 270 / 1e6;
                            else if (url.Contains("/background/")) result.BackdropMp += 1920 * 1080 / 1e6;
                            else if (url.Contains("/poster/small/")) result.PosterMp += 300 * 450 / 1e6;
                            else if (url.Contains("/poster/")) result.PosterMp += 500 * 750 / 1e6;
                            else if (url.Contains("/logo/")) result.LogoMp += 800 * 310 / 1e6;
                            else result.StillMp += StillPixels(url) / 1e6;
                        }
                    var present = new HashSet<string>(lowered.Nodes.Select(n => n.Key));
                    var bigPictures = new HashSet<string>(lowered.Nodes.Where(n => n.ImageUrl != null && n.ImageUrl.Contains("/background/medium/")).Select(n => n.Key));
                    foreach (var gone in known.Where(k => !present.Contains(k)).ToList())
                    {
                        if (lastBig.Contains(gone))
                            continue;
                        parked[gone] = parkedOrder.AddLast(gone);
                        while (parkedOrder.Count > 160) { parked.Remove(parkedOrder.First.Value); parkedOrder.RemoveFirst(); }
                    }
                    lastBig = bigPictures;
                    known.IntersectWith(present);
                    var popped = ((HomeViewModel)h.Shell.Current).PoppedTile != null;
                    if (popped && !wasPopped)
                    {
                        result.Popped = true;
                        result.PopCreated = Math.Max(result.PopCreated, created);
                        result.PopNodes = Math.Max(result.PopNodes, lowered.Nodes.Count - lastCount);
                    }
                    wasPopped = popped;
                    lastCount = lowered.Nodes.Count;
                    if (measure)
                    {
                        result.FrameMs.Add(watch.Elapsed.TotalMilliseconds);
                        result.Frames++;
                        result.Created += created;
                        result.CreatedWithParking += createdParking;
                        result.NodesMax = Math.Max(result.NodesMax, lowered.Nodes.Count);
                    }
                    // Real time: 16 ms per frame, the shell ticking every 100 ms as on the TV.
                    now += 16;
                    h.Clock.Advance(16);
                    if (++ticks % 6 == 0)
                        h.Shell.Tick();
                }
            }
            try
            {
                Frames(30, false);   // warm up (JIT, assets)
                h.Press(RemoteKey.Down, RemoteKey.Down);
                Frames(30, false);
                // Held Right across a row: a key every 120 ms.
                for (var k = 0; k < 7; k++) { h.Shell.HandleKey(RemoteKey.Right); Frames(8, true); }
                // Repeated Down through the rows.
                for (var k = 0; k < 3; k++) { h.Shell.HandleKey(RemoteKey.Down); Frames(8, true); }
                // Reversal: back up and down again through the same rows.
                for (var k = 0; k < 2; k++) { h.Shell.HandleKey(RemoteKey.Up); Frames(8, true); }
                for (var k = 0; k < 2; k++) { h.Shell.HandleKey(RemoteKey.Down); Frames(8, true); }
                Frames(40, true);   // focus rests: the billboard follows (one backdrop)
                // Rest until the card pops again: the objects a pop adds are counted on the frame it appears.
                h.Shell.Services.LastInteractionMs = h.Shell.NowMs;
                Frames(160, true);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            return result;
        }

        [Fact]
        public void WritesFrameCostsWhenAsked()
        {
            var path = Environment.GetEnvironmentVariable("RU7100_UI_BENCH");
            if (string.IsNullOrEmpty(path))
                return;
            Run();   // first run pays for JIT
            var runs = Enumerable.Range(0, 5).Select(_ => Run()).ToList();
            var all = runs.SelectMany(r => r.FrameMs).OrderBy(v => v).ToList();
            var r0 = runs[0];
            File.WriteAllText(path, string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "pictures first shown while navigating {9}: backdrops {10:0.0} MP, card art {11:0.0} MP, posters {12:0.0} MP, logos {13:0.0} MP (before the TV's card-sized copies), stills {14:0.0} MP\nframes {0}  mean {1:0.000} ms  p95 {2:0.000} ms  worst {3:0.000} ms  nodes max {4}  objects created during navigation+pop {5} ({15} with the painter's parking)  objects created by the pop {6}  nodes added by the pop {7}  popped {8}\n",
                all.Count, all.Average(), all[(int)(all.Count * 0.95)], all[all.Count - 1], r0.NodesMax, r0.Created, r0.PopCreated, r0.PopNodes, r0.Popped, r0.Pictures, r0.BackdropMp, r0.CardArtMp, r0.PosterMp, r0.LogoMp, r0.StillMp, r0.CreatedWithParking));
        }
    }
}
