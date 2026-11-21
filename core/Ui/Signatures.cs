using System;
using Ru7100.Core.Playback;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100.Core.Ui
{
    /// The "signature line": the best playable source of a title or episode, e.g. "1080p · 2.3 GB · EN audio · ES subs".
    /// Computed in the background through ISourceCatalog and cached per video id (shared by the title page, search
    /// cards and Browse filters).
    public sealed class SourceSignatures
    {
        public const int Limit = 128;
        private readonly ISourceCatalog catalog;
        private readonly ISourceResolver subtitles;
        private readonly Dictionary<string, Task<SourceEntry>> cache = new Dictionary<string, Task<SourceEntry>>();
        private readonly LinkedList<string> recent = new LinkedList<string>();

        /// subtitles: optional; adds the languages subtitle add-ons (OpenSubtitles) offer for the best source, since
        /// torrent names rarely mention subtitles.
        public SourceSignatures(ISourceCatalog catalog, ISourceResolver subtitles = null)
        {
            this.catalog = catalog;
            this.subtitles = subtitles;
        }

        public static string Key(MetaDetail meta, VideoInfo video) => video?.Id ?? meta?.Summary?.Id;

        /// Completed result if known: entry (or null when nothing is playable). False while unknown or running.
        public bool TryGet(string key, out SourceEntry entry)
        {
            lock (cache)
            {
                if (key != null && cache.TryGetValue(key, out var task) && task.Status == TaskStatus.RanToCompletion)
                {
                    entry = task.Result;
                    return true;
                }
            }
            entry = null;
            return false;
        }

        public Task<SourceEntry> GetAsync(MetaDetail meta, VideoInfo video, CancellationToken token)
        {
            var key = Key(meta, video);
            lock (cache)
            {
                if (cache.TryGetValue(key, out var hit) && !hit.IsFaulted && !hit.IsCanceled)
                    return hit;
                // Not tied to the caller's token: a page closing early must not poison the shared cache.
                var task = LoadAsync(meta, video);
                cache[key] = task;
                recent.AddFirst(key);
                while (recent.Count > Limit)
                {
                    cache.Remove(recent.Last.Value);
                    recent.RemoveLast();
                }
                return task;
            }
        }

        private async Task<SourceEntry> LoadAsync(MetaDetail meta, VideoInfo video)
        {
            var options = new SourceOptions { Episodic = video != null, IsAnime = UiFormat.IsAnime(meta), EnglishOriginal = SourceOptions.IsEnglishOriginal(meta) };
            var list = await catalog.GetAsync(meta, video, options, CancellationToken.None).ConfigureAwait(false);
            var best = list.FirstOrDefault(e => e.Playable);
            if (best == null || subtitles == null)
                return best;
            IReadOnlyList<SubtitleCandidate> external;
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    external = await subtitles.GetSubtitlesAsync(meta.Summary.Type, video?.Id ?? meta.Summary.Id, best.Candidate, cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return best;
            }
            var facts = best.Facts ?? SourceFacts.Parse(best.Candidate, options.IsAnime);
            var subs = facts.SubtitleLanguages.ToList();
            foreach (var code in (external ?? Array.Empty<SubtitleCandidate>()).Select(s => PlayerSettings.Normalize(s.Language)))
            {
                var label = code == "eng" || Languages.HasPreferredSubtitles && code == PlayerSettings.Normalize(Languages.Subtitles) ? Languages.Short(code) : null;
                if (label != null && !subs.Contains(label))
                    subs.Add(label);
            }
            var first = Languages.HasPreferredSubtitles ? Languages.Short(Languages.Subtitles) : "EN";
            subs = subs.OrderBy(s => s == first ? 0 : s == "EN" ? 1 : 2).ToList();
            return new SourceEntry
            {
                Candidate = best.Candidate, AddonName = best.AddonName, RejectReason = best.RejectReason, Reasons = best.Reasons,
                Facts = new SourceFacts
                {
                    Quality = facts.Quality, Hdr = facts.Hdr, VideoCodec = facts.VideoCodec, AudioCodec = facts.AudioCodec, Channels = facts.Channels,
                    ReleaseType = facts.ReleaseType, AudioLanguages = facts.AudioLanguages, SubtitleLanguages = subs
                }
            };
        }

        /// "1080p HDR10 · 2.3 GB · EN audio · ES subs".
        public static string Line(SourceEntry entry)
        {
            if (entry == null) return null;
            var f = entry.Facts ?? SourceFacts.Parse(entry.Candidate, false);
            var audio = f.AudioLanguages.Count == 0 ? null : string.Join("/", f.AudioLanguages.Take(2)) + " audio";
            var subs = f.SubtitleLanguages.Count == 0 ? null : string.Join("/", f.SubtitleLanguages.Take(2)) + " subs";
            return UiFormat.Facts(f.Quality + (f.Hdr != null ? " " + f.Hdr : ""), UiFormat.Size(entry.Candidate.SizeBytes), f.Channels, audio, subs).Replace("  ·  ", " · ");
        }
    }

    public static class SourceBadges
    {
        /// Plain words for a title's best playable source: "4K" / "HD" / "SD", "Spanish subs" (the preferred language), "Ready to play".
        /// Size, channels, codecs and swarm health stay in the source picker.
        public static IReadOnlyList<string> For(SourceEntry entry)
        {
            var list = new List<string>();
            if (entry == null)
                return list;
            var f = entry.Facts ?? SourceFacts.Parse(entry.Candidate, false);
            list.Add(f.Quality == "4K" ? "4K" : f.Quality == "1080p" || f.Quality == "720p" ? "HD" : "SD");
            if (f.Hdr != null) list.Add("HDR");
            if (Languages.HasPreferredSubtitles && f.SubtitleLanguages.Contains(Languages.Short(Languages.Subtitles))) list.Add(Languages.Name(Languages.Subtitles) + " subs");
            list.Add("Ready to play");
            return list;
        }

        public static string Line(SourceEntry entry) => string.Join(" · ", For(entry));
    }

    /// Rate dialog shared by the title page and focused cards (Home, Search). Labels avoid any brand wording.
    public static class RateDialog
    {
        /// Menu key on a focused card: title page plus the rate actions.
        public static void ShowCardActions(TvShell shell, IFeedbackSink feedback, MetaSummary title, Action openDetails)
        {
            var buttons = new List<DialogButton> { new DialogButton("Title page", openDetails) };
            buttons.AddRange(RateButtons(shell, feedback, title, null));
            buttons.Add(new DialogButton("Cancel", null));
            shell.ShowDialog(new ConfirmDialog(title.Name, null, buttons.ToArray()));
        }

        public static void Show(TvShell shell, IFeedbackSink feedback, MetaSummary title, Action<string> rated = null)
        {
            if (feedback == null || title?.Id == null)
                return;
            var buttons = RateButtons(shell, feedback, title, rated).ToList();
            buttons.Add(new DialogButton("Cancel", null));
            shell.ShowDialog(new ConfirmDialog("Rate " + title.Name, "Ratings tune your For You row.", buttons.ToArray()));
        }

        private static IEnumerable<DialogButton> RateButtons(TvShell shell, IFeedbackSink feedback, MetaSummary title, Action<string> rated)
        {
            if (feedback == null || title?.Id == null)
                yield break;
            void Send(Func<CancellationToken, Task> action, string label, string toast) =>
                shell.RunDetached(async token =>
                {
                    await action(token).ConfigureAwait(false);
                    shell.Services.Dispatcher.Post(() =>
                    {
                        rated?.Invoke(label);
                        shell.ShowToast(toast);
                    });
                });
            yield return new DialogButton("I like this", () => Send(t => feedback.ThumbsUpAsync(title.Id, t), "Rated: like", "Thanks. More like " + title.Name + " coming up."));
            yield return new DialogButton("Not for me", () => Send(t => feedback.ThumbsDownAsync(title.Id, t), "Rated: not for me", "Got it. We'll show fewer like this."));
            yield return new DialogButton("Not interested", () => Send(t => feedback.NotInterestedAsync(title.Id, t), "Not interested", "Hidden from your recommendations."));
        }
    }
}
