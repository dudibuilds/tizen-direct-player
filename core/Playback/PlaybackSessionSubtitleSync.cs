using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core.Addons;

namespace Ru7100.Core.Playback
{
    /// Subtitles that fit the playing file (standing decision 2026-11-20).
    ///
    /// 1. Match: once the source opens, its OpenSubtitles hash is read from the file's first and last 64 KiB (the
    ///    torrent pins those pieces), subtitle add-ons are asked again with videoHash, videoSize and filename, and
    ///    every subtitle is scored against the file's release. A subtitle made for this release outranks the rest in
    ///    its language, and replaces an automatic choice that is not one.
    /// 2. Re-align: a subtitle made for another release is lined up with one made for this release (in any language)
    ///    by SubtitleAligner: shift and speed, used only when the match is clear.
    /// 3. Sync from the viewer's offsets: when nothing matches, two settled offset changes far enough apart reveal a
    ///    speed mismatch (a 25 fps master played at 23.976 drifts 2.5 s a minute); the speed snaps to the nearest
    ///    frame-rate ratio. Because the offset stops at ±30 s, a second point is often out of reach, so one settled
    ///    offset also counts when two independent facts agree with it: the player reports one frame rate, the
    ///    subtitle was declared for another, and that speed change explains the offset with only a small shift.
    ///    The declared rate alone is never acted on (uploaders often get it wrong).
    /// A speed fix is remembered per show and release, so the next episode of the same release starts in sync.
    /// Embedded subtitles are part of the file and never re-timed.
    public sealed partial class PlaybackSession
    {
        /// An offset counts as a sync point once the viewer has left it alone this long.
        public static readonly TimeSpan OffsetSettle = TimeSpan.FromSeconds(3);
        /// Two sync points must be this far apart in the episode, and differ by this much, to reveal a speed.
        public const long TwoPointMinSpanMs = 4 * 60 * 1000;
        public const int TwoPointMinDriftMs = 1500;
        /// One offset counts only this far into the episode (where a speed change has drifted ~20 s), and only when
        /// the shift it leaves is within max(5 s, 30 % of the drift that speed predicts there).
        public const long OnePointMinPositionMs = 8 * 60 * 1000;
        public const int OnePointMinOffsetMs = 3000;
        public const long OnePointMinShiftToleranceMs = 5000;
        /// Speed changes the viewer's offsets can reveal: the ≥ 4 % frame-rate pairs. 24 ↔ 23.976 (0.1 %) is too
        /// close to "no change" to tell from offsets set by eye; only a reference alignment finds it.
        private static readonly double[] OffsetScales = SubtitleAligner.FrameRates
            .Select(f => f.From / f.To).Where(r => Math.Abs(r - 1) > 0.03).ToArray();
        private const double SnapTolerance = 0.006;
        private int alignSequence;
        private bool raiseAfterSync;

        private readonly object syncGate = new object();
        private SubtitleTiming subtitleTiming = SubtitleTiming.Identity;
        private SubtitleText currentSubtitleText;
        private string activeFileHash;
        private readonly Dictionary<string, SubtitleText> referenceTexts = new Dictionary<string, SubtitleText>(StringComparer.Ordinal);
        private OffsetPoint pendingPoint, settledPoint;

        private struct OffsetPoint
        {
            public bool Set;
            public long PositionMs;
            public int OffsetMs;
            public DateTimeOffset At;
        }

        /// The external subtitle's timing against the file (identity when it matches or nothing better is known).
        public SubtitleTiming SubtitleSync { get { lock (syncGate) return subtitleTiming; } }

        /// The latest alignment (tests await it).
        public Task PendingAlignment { get; private set; } = Task.CompletedTask;

        /// The opened file's OpenSubtitles hash, once read.
        public string FileHash => activeFileHash;

        /// The viewer's reset (OK on Timing, or Reset): offset 0, no automatic shift or speed, and a speed fix
        /// remembered for this release is forgotten so later episodes start as they come.
        public void ResetSubtitleTiming()
        {
            var metaId = active?.MetaId;
            var releaseKey = ReleaseInfo.Parse(activeCandidate?.Filename).Key;
            Interlocked.Increment(ref alignSequence);   // an alignment still running must not re-apply its result
            lock (syncGate)
            {
                subtitleTiming = SubtitleTiming.Identity;
                pendingPoint = default;
                settledPoint = default;
            }
            pump.Timing = SubtitleTiming.Identity;
            SetSubtitleOffset(0);
            lock (syncGate) pendingPoint = default;   // the reset itself is not a sync point
            if (metaId != null)
                _ = Task.Run(async () =>
                {
                    try { await preferences.UpdateSubtitleTimingAsync(metaId, releaseKey, null, lifetime.Token, forget: true).ConfigureAwait(false); }
                    catch (Exception error) { Log("subtitles: forget timing: " + error.Message); }
                });
        }

        private void ResetSubtitleSync()
        {
            lock (syncGate)
            {
                subtitleTiming = SubtitleTiming.Identity;
                currentSubtitleText = null;
                activeFileHash = null;
                referenceTexts.Clear();
                pendingPoint = default;
                settledPoint = default;
            }
            pump.Timing = SubtitleTiming.Identity;
        }

        private async Task SubtitleWorkAsync(SessionRecord session, string preferredLanguage, Task<IReadOnlyList<SubtitleCandidate>> subtitlesTask,
            StreamCandidate candidate, IOpenedSource opened)
        {
            await DecideSubtitlesAsync(session, preferredLanguage, subtitlesTask).ConfigureAwait(false);
            await RefineSubtitlesAsync(session, candidate, opened).ConfigureAwait(false);
        }

        /// Step 1: hash, ask again with the file's details, score and re-order, and swap an automatic choice for a
        /// subtitle made for this release when one exists in the same language.
        private async Task RefineSubtitlesAsync(SessionRecord session, StreamCandidate candidate, IOpenedSource opened)
        {
            try
            {
                var fileName = candidate?.Filename;
                var size = session.SourceSizeBytes > 0 ? session.SourceSizeBytes : candidate?.SizeBytes ?? 0;
                var hash = await FileHashAsync(opened, size).ConfigureAwait(false);
                IReadOnlyList<SubtitleCandidate> more = Array.Empty<SubtitleCandidate>();
                if (services.Resolver != null && (hash != null || !string.IsNullOrEmpty(fileName)))
                {
                    var query = new StreamCandidate { Filename = fileName, SizeBytes = size > 0 ? (long?)size : null, FileHash = hash, InfoHash = candidate?.InfoHash };
                    try
                    {
                        using (var budget = TaskHelpers.Budget(clock, options.ExternalSubtitleBudget, lifetime.Token))
                            more = await services.Resolver.GetSubtitlesAsync(session.MediaType, session.VideoId, query, budget.Token)
                                .WithCancellation(budget.Token).ConfigureAwait(false) ?? Array.Empty<SubtitleCandidate>();
                    }
                    catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { Log("subtitles: file query timed out"); }
                    catch (Exception error) when (!(error is OperationCanceledException)) { Log("subtitles: file query: " + error.Message); }
                }
                SubtitleDecision switchTo = null;
                await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    if (active != session)
                        return;
                    var merged = Merge(externalSubtitles, more);
                    SubtitleMatching.Annotate(merged, fileName, hash);
                    externalSubtitles = SubtitleMatching.Order(merged, session.IsAnime);
                    activeFileHash = hash;
                    var matching = externalSubtitles.Count(s => s.Match >= SubtitleMatch.Release);
                    Log($"subtitles: {externalSubtitles.Count} for {fileName ?? "the file"} (hash {hash ?? "unknown"}), {matching} made for this release");
                    var current = session.Subtitle;
                    if (current?.Kind == SubtitleKind.External && current.External != null && current.Reason != "user")
                    {
                        var language = SubtitleSelection.NormalizeLanguage(current.External.Language);
                        var currentMatch = externalSubtitles.FirstOrDefault(s => s.Url == current.External.Url)?.Match ?? SubtitleMatch.None;
                        var best = externalSubtitles.FirstOrDefault(s => SubtitleSelection.NormalizeLanguage(s.Language) == language);
                        if (best != null && best.Url != current.External.Url && best.Match > currentMatch)
                        {
                            switchTo = new SubtitleDecision { Kind = SubtitleKind.External, External = best, Language = current.Language, Reason = "release_match" };
                            session.Subtitle = switchTo;
                            Log($"subtitles: switched to one made for this release ({best.Release})");
                        }
                    }
                    RaiseStatus();
                }
                finally { gate.Release(); }
                if (switchTo != null)
                    await LoadExternalAsync(session, switchTo, lifetime.Token).ConfigureAwait(false);
                else
                {
                    SubtitleText text;
                    lock (syncGate) text = currentSubtitleText;
                    var decision = session.Subtitle;
                    if (text != null && decision?.Kind == SubtitleKind.External)
                        PendingAlignment = AlignAsync(session, decision, text);
                }
                await PendingAlignment.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Log("subtitle refine: " + error.Message); }
        }

        private static List<SubtitleCandidate> Merge(IReadOnlyList<SubtitleCandidate> first, IReadOnlyList<SubtitleCandidate> second)
        {
            var byUrl = new Dictionary<string, SubtitleCandidate>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var item in (first ?? Array.Empty<SubtitleCandidate>()).Concat(second ?? Array.Empty<SubtitleCandidate>()))
            {
                if (item?.Url == null) continue;
                if (!byUrl.TryGetValue(item.Url, out var known))
                {
                    byUrl[item.Url] = item;
                    order.Add(item.Url);
                }
                else
                {
                    known.Release = known.Release ?? item.Release;
                    known.ReleaseGroup = known.ReleaseGroup ?? item.ReleaseGroup;
                    known.ReleaseFormat = known.ReleaseFormat ?? item.ReleaseFormat;
                    known.FpsMilli = known.FpsMilli ?? item.FpsMilli;
                    known.MovieHash = known.MovieHash ?? item.MovieHash;
                    known.HashMatch |= item.HashMatch;
                }
            }
            return order.Select(url => byUrl[url]).ToList();
        }

        private async Task<string> FileHashAsync(IOpenedSource opened, long size)
        {
            if (!(opened is IFileEnds ends) || size < OpenSubtitlesHash.ChunkBytes)
                return null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (ends.TryReadEnds(OpenSubtitlesHash.ChunkBytes, out var head, out var tail))
                        return OpenSubtitlesHash.Compute(size, head, tail);
                }
                catch (Exception error) when (!(error is OperationCanceledException)) { Log("subtitles: file hash: " + error.Message); return null; }
                await clock.Delay(TimeSpan.FromSeconds(1), lifetime.Token).ConfigureAwait(false);
            }
            Log("subtitles: file ends not available for the hash");
            return null;
        }

        /// Called when an external subtitle has been parsed and applied.
        private void OnExternalApplied(SessionRecord session, SubtitleDecision decision, SubtitleText parsed)
        {
            lock (syncGate)
            {
                currentSubtitleText = parsed;
                pendingPoint = default;
                settledPoint = default;
            }
            PendingAlignment = AlignAsync(session, decision, parsed);
        }

        /// Step 2: the timing for the chosen subtitle. A match needs none; otherwise line it up with a subtitle made
        /// for this release, else use a speed fix remembered for this release, else leave it as it is.
        private async Task AlignAsync(SessionRecord session, SubtitleDecision decision, SubtitleText parsed)
        {
            var sequence = Interlocked.Increment(ref alignSequence);
            try
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                var timing = SubtitleTiming.Identity;
                var known = externalSubtitles;
                var chosen = known.FirstOrDefault(s => s.Url == decision.External?.Url) ?? decision.External;
                var releaseKey = ReleaseInfo.Parse(activeCandidate?.Filename).Key;
                if (chosen != null && chosen.Match < SubtitleMatch.Release && parsed.Count >= SubtitleAligner.MinCues)
                {
                    var aligned = false;
                    var triedReference = false;
                    foreach (var reference in known.Where(s => s.Match >= SubtitleMatch.Release && s.Url != chosen.Url)
                                 .OrderByDescending(s => s.Match).Take(3))
                    {
                        var text = await ReferenceTextAsync(reference).ConfigureAwait(false);
                        if (text == null || text.Count < SubtitleAligner.MinCues)
                            continue;
                        triedReference = true;
                        var starts = parsed.Starts;
                        var alignTimer = System.Diagnostics.Stopwatch.StartNew();
                        var result = await Task.Run(() => SubtitleAligner.Align(starts, text.Starts)).ConfigureAwait(false);
                        Log($"subtitles: aligned against {reference.Release ?? reference.Url} in {alignTimer.ElapsedMilliseconds} ms: {result}");
                        if (!result.Accepted)
                            continue;
                        timing = result.Timing;
                        aligned = true;
                        if (timing.SpeedName != null)
                            await RememberTimingAsync(session.MetaId, releaseKey, timing).ConfigureAwait(false);
                        break;
                    }
                    // A remembered fix stands in only when there was nothing to check against: a reference that
                    // disagreed is evidence that this subtitle needs no such fix.
                    if (!aligned && !triedReference)
                    {
                        var preference = await preferences.GetAsync(session.MetaId, lifetime.Token).ConfigureAwait(false);
                        if (preference?.SubtitleTimings != null && preference.SubtitleTimings.TryGetValue(releaseKey, out var remembered) && remembered.Scale > 0)
                        {
                            timing = new SubtitleTiming { Scale = remembered.Scale, OffsetMs = remembered.OffsetMs, Source = "remembered" };
                            Log($"subtitles: speed {timing.SpeedName ?? timing.Scale.ToString("0.#####")} remembered for {releaseKey}");
                        }
                    }
                }
                await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    if (active != session || !ReferenceEquals(session.Subtitle, decision) || sequence != Volatile.Read(ref alignSequence))
                        return;   // a newer subtitle, alignment or reset came first
                    if (!timing.IsIdentity)
                        Log($"subtitles: timing {timing.Source} scale {timing.Scale:0.#####} offset {timing.OffsetMs} ms ({started.ElapsedMilliseconds} ms)");
                    lock (syncGate) subtitleTiming = timing;
                    pump.Timing = timing;
                    pump.Update(SafePlayer(() => player.PositionMs, 0L));
                    RaiseStatus();
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Log("subtitle align: " + error.Message); }
        }

        private async Task<SubtitleText> ReferenceTextAsync(SubtitleCandidate reference)
        {
            lock (syncGate)
                if (referenceTexts.TryGetValue(reference.Url, out var cached))
                    return cached;
            if (services.Subtitles == null)
                return null;
            SubtitleText parsed = null;
            try
            {
                using (var budget = TaskHelpers.Budget(clock, options.ExternalSubtitleLoadBudget, lifetime.Token))
                {
                    var text = await services.Subtitles.LoadTextAsync(reference.Url, budget.Token).WithCancellation(budget.Token).ConfigureAwait(false);
                    parsed = new SubtitleText(text);
                }
            }
            catch (Exception error) when (!lifetime.IsCancellationRequested) { Log("subtitles: reference: " + error.Message); }
            lock (syncGate)
                referenceTexts[reference.Url] = parsed;
            return parsed;
        }

        private Task RememberTimingAsync(string metaId, string releaseKey, SubtitleTiming timing) =>
            preferences.UpdateSubtitleTimingAsync(metaId, releaseKey, new RememberedTiming { Scale = timing.Scale, OffsetMs = timing.OffsetMs }, lifetime.Token);

        /// Step 3: records the viewer's offset at the current position; see SettleOffsetPoint.
        private void NoteOffsetPoint(long positionMs)
        {
            if (active?.Subtitle?.Kind != SubtitleKind.External)
                return;
            var now = clock.UtcNow;
            lock (syncGate)
            {
                if (pendingPoint.Set && now - pendingPoint.At >= OffsetSettle)
                    SettleLocked(pendingPoint);
                // An offset held at the ±30 s stop says only "more than this": it is not a sync point.
                pendingPoint = Math.Abs(subtitleOffsetMs) >= TrackRules.ClampSubtitleOffset(int.MaxValue)
                    ? default
                    : new OffsetPoint { Set = true, PositionMs = positionMs, OffsetMs = subtitleOffsetMs, At = now };
            }
            RaiseIfSynced();
        }

        private void RaiseIfSynced()
        {
            bool raise;
            lock (syncGate)
            {
                raise = raiseAfterSync;
                raiseAfterSync = false;
            }
            if (raise)
                RaiseStatus();
        }

        /// From the subtitle loop: an offset the viewer has left alone becomes a sync point.
        private void SettleOffsetPoint()
        {
            OffsetPoint point;
            lock (syncGate)
            {
                if (!pendingPoint.Set || clock.UtcNow - pendingPoint.At < OffsetSettle)
                    return;
                point = pendingPoint;
                pendingPoint = default;
                SettleLocked(point);
            }
            RaiseIfSynced();
        }

        private void SettleLocked(OffsetPoint point)
        {
            var previous = settledPoint;
            settledPoint = point;
            if (!previous.Set || Math.Abs(point.PositionMs - previous.PositionMs) < TwoPointMinSpanMs
                || Math.Abs(point.OffsetMs - previous.OffsetMs) < TwoPointMinDriftMs)
            {
                TryOnePointLocked(point);
                return;
            }
            // Where each point says the subtitle clock stands: the cue shown at p is the one at ToSubtitle(p - offset).
            var (first, second) = previous.PositionMs < point.PositionMs ? (previous, point) : (point, previous);
            var s1 = subtitleTiming.ToSubtitle(first.PositionMs - first.OffsetMs);
            var s2 = subtitleTiming.ToSubtitle(second.PositionMs - second.OffsetMs);
            if (s2 <= s1)
                return;
            var scale = (second.PositionMs - first.PositionMs) / (double)(s2 - s1);
            // The fitted scale is absolute (subtitle clock to video clock), like the candidates.
            var candidates = OffsetScales.ToList();
            if (Math.Abs(subtitleTiming.Scale - 1) > 1e-6) candidates.Add(1);   // undoing a speed fix that was wrong
            var snapped = candidates.OrderBy(c => Math.Abs(scale / c - 1)).First();
            // Every candidate is a change of 4 % or more, so a slow drift of a few seconds (an offset set by eye)
            // never lands within 0.6 % of one.
            if (Math.Abs(scale / snapped - 1) > SnapTolerance)
            {
                Log($"subtitles: two offsets imply speed {scale:0.####}, not a frame-rate ratio; left as an offset");
                return;
            }
            // Anchor on the latest point: it is where the viewer is looking now.
            var sLatest = point.PositionMs == second.PositionMs ? s2 : s1;
            var timing = new SubtitleTiming
            {
                Scale = snapped,
                OffsetMs = (long)Math.Round(point.PositionMs - snapped * sLatest),
                Source = "offset",
            };
            ApplyOffsetFixLocked(point, timing, "two offsets");
        }

        /// One settled offset, confirmed by the player's frame rate and the subtitle's declared one.
        private void TryOnePointLocked(OffsetPoint point)
        {
            if (!subtitleTiming.IsIdentity || point.PositionMs < OnePointMinPositionMs || Math.Abs(point.OffsetMs) < OnePointMinOffsetMs)
                return;
            var videoFps = FpsClass((player as IVideoFormatSource)?.VideoFps);
            var subtitleFps = FpsClass(active?.Subtitle?.External?.FpsMilli is int milli ? (int?)Math.Round(milli / 1000.0) : null);
            if (videoFps == null || subtitleFps == null || Math.Abs(videoFps.Value - subtitleFps.Value) < 0.01)
                return;
            var scale = subtitleFps.Value / videoFps.Value;
            if (Math.Abs(scale - 1) < 0.03)
                return;   // 24 ↔ 23.976: too small to confirm from one offset
            var s = point.PositionMs - point.OffsetMs;   // identity timing: the cue shown is the one at p - offset
            var shift = (long)Math.Round(point.PositionMs - scale * s);
            var predictedDrift = Math.Abs(point.PositionMs - point.PositionMs / scale);
            var tolerance = Math.Max(OnePointMinShiftToleranceMs, 0.3 * predictedDrift);
            if (Math.Sign(point.OffsetMs) != Math.Sign(scale - 1) || Math.Abs(shift) > tolerance)
            {
                Log($"subtitles: offset {point.OffsetMs} ms does not fit a {subtitleFps:0.###} → {videoFps:0.###} speed change (shift {shift} ms); left as an offset");
                return;
            }
            ApplyOffsetFixLocked(point, new SubtitleTiming { Scale = scale, OffsetMs = shift, Source = "offset" }, "the offset and the frame rates");
        }

        /// The frame-rate family a whole-number rate belongs to (Tizen reports 23 for 23.976).
        private static double? FpsClass(int? fps)
        {
            switch (fps)
            {
                case 23: case 24: return 24000 / 1001.0;
                case 25: case 50: return 25;
                case 29: case 30: case 59: case 60: return 30000 / 1001.0;
                default: return null;
            }
        }

        private void ApplyOffsetFixLocked(OffsetPoint point, SubtitleTiming timing, string evidence)
        {
            Interlocked.Increment(ref alignSequence);   // the viewer's evidence wins over an alignment still running
            subtitleTiming = timing;
            settledPoint = new OffsetPoint { Set = true, PositionMs = point.PositionMs, OffsetMs = 0, At = point.At };
            subtitleOffsetMs = 0;
            pump.OffsetMs = 0;
            pump.Timing = timing;
            Log($"subtitles: {evidence} reveal a speed change ({timing.SpeedName ?? timing.Scale.ToString("0.#####")}); fixed and remembered");
            var metaId = active?.MetaId;
            var releaseKey = ReleaseInfo.Parse(activeCandidate?.Filename).Key;
            _ = Task.Run(async () =>
            {
                try { await RememberTimingAsync(metaId, releaseKey, timing).ConfigureAwait(false); }
                catch (Exception error) { Log("subtitles: remember timing: " + error.Message); }
            });
            raiseAfterSync = true;   // raised by the caller once syncGate is released
        }
    }
}
