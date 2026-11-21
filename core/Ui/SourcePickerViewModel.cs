using System;
using Ru7100.Core.Playback;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Ru7100.Core.Ui
{
    /// "Choose a source". By default the list is short: the best few sources of each quality in rank order (the top
    /// pick tagged "Best match"), ending in a "Show all N sources" row; weak swarms wait behind that row. Quality
    /// chips (All / 4K / 1080p / 720p) narrow it: Left/Right on the list switch chip, and the chip bar is one Up
    /// from the first row. Unplayable streams are hidden unless toggled.
    public sealed class SourcePickerViewModel : Screen
    {
        /// Sources of one quality in the short list ("All"), and in the short list of a single quality.
        public const int PerQuality = 3;
        public const int PerQualityFocused = 6;
        /// Torrents with fewer known seeders than this wait behind "Show all".
        public const int SeederFloor = 5;
        public const string AllQualities = "All";

        private readonly MetaDetail meta;
        private readonly VideoInfo video;
        private readonly long resumeMs;
        private readonly PlaybackMode mode;
        private IReadOnlyList<SourceEntry> all = Array.Empty<SourceEntry>();

        public SourcePickerViewModel(MetaDetail meta, VideoInfo video, long resumeMs, PlaybackMode mode, bool switching)
        {
            this.meta = meta;
            this.video = video;
            this.resumeMs = resumeMs;
            this.mode = mode;
            Switching = switching;
            IsAnime = UiFormat.IsAnime(meta);
        }

        public override ScreenKind Kind => ScreenKind.SourcePicker;
        public override string Title => (meta?.Summary?.Name ?? "") + (video != null ? " · " + UiFormat.Episode(video) : "");
        public bool Switching { get; }
        public bool IsAnime { get; }
        public bool Loading { get; private set; } = true;
        public bool ShowHidden { get; private set; }
        /// The whole list instead of the best few per quality.
        public bool ShowAll { get; private set; }
        /// -1 = chip bar, otherwise index into Entries; Entries.Count = the "Show all" row.
        public int Index { get; private set; }
        /// Focus on the chip bar: the quality chips, then the unplayable toggle.
        public int ToolbarIndex { get; private set; }
        public int QualityIndex { get; private set; }
        public int HiddenCount { get { Derive(); return hiddenCount; } }
        public int PlayableCount { get { Derive(); return all.Count - hiddenCount; } }
        public StreamCandidate Best => BestEntry?.Candidate;
        public SourceEntry BestEntry { get { Derive(); return bestEntry; } }
        /// More than one add-on answered: rows name theirs.
        public bool ShowAddon { get { Derive(); return showAddon; } }

        /// "All", then the qualities on offer, best first.
        public IReadOnlyList<string> Qualities { get { Derive(); return qualities; } }

        public string Quality => Qualities[Math.Min(QualityIndex, Qualities.Count - 1)];

        private static string QualityOf(SourceEntry e) => e.Facts?.Quality ?? "SD";

        private static bool AboveFloor(SourceEntry e) =>
            e.Candidate.InfoHash == null || e.Candidate.Seeders == null || e.Candidate.Seeders >= SeederFloor;

        /// Everything the current chip and the unplayable toggle allow, in rank order.
        private IReadOnlyList<SourceEntry> Matching() { Derive(); return matching; }

        public IReadOnlyList<SourceEntry> Entries { get { Derive(); return entries; } }

        // The lists above are derived once per change of the sources, chip, toggle or Show all, not on every read: the
        // layout and each key read them many times, and each read refiltered the whole list.
        private IReadOnlyList<SourceEntry> derivedFrom;
        private bool derivedHidden, derivedShowAll;
        private int derivedQualityIndex = -1;
        private int hiddenCount;
        private SourceEntry bestEntry;
        private bool showAddon;
        private IReadOnlyList<string> qualities;
        private IReadOnlyList<SourceEntry> matching, entries;

        private void Derive()
        {
            if (ReferenceEquals(derivedFrom, all) && derivedHidden == ShowHidden && derivedShowAll == ShowAll && derivedQualityIndex == QualityIndex)
                return;
            if (!ReferenceEquals(derivedFrom, all))
            {
                hiddenCount = all.Count(e => !e.Playable);
                bestEntry = all.FirstOrDefault(e => e.Playable);
                showAddon = all.Select(e => e.AddonName).Where(n => !string.IsNullOrEmpty(n)).Distinct().Count() > 1;
                var list = new List<string> { AllQualities };
                list.AddRange(all.Where(e => e.Playable).Select(e => QualityOf(e)).Distinct().OrderBy(QualityRank));
                qualities = list;
            }
            derivedFrom = all;
            derivedHidden = ShowHidden;
            derivedShowAll = ShowAll;
            derivedQualityIndex = QualityIndex;
            var quality = qualities[Math.Min(QualityIndex, qualities.Count - 1)];
            matching = all.Where(e => (ShowHidden || e.Playable) && (quality == AllQualities || QualityOf(e) == quality)).ToList();
            entries = ShortList(matching, quality);
        }

        private IReadOnlyList<SourceEntry> ShortList(IReadOnlyList<SourceEntry> matching, string quality)
        {
            if (ShowAll)
                return matching;
            var limit = quality == AllQualities ? PerQuality : PerQualityFocused;
            var taken = new Dictionary<string, int>();
            var list = new List<SourceEntry>();
            foreach (var e in matching)
            {
                if (!e.Playable)
                {
                    list.Add(e);   // "Show unplayable" shows them all, each with its reason
                    continue;
                }
                if (!AboveFloor(e) && list.Count > 0)
                    continue;
                taken.TryGetValue(QualityOf(e), out var n);
                if (n >= limit)
                    continue;
                taken[QualityOf(e)] = n + 1;
                list.Add(e);
            }
            return list;
        }

        /// Sources the short list leaves out (0 when the whole list shows).
        public int MoreCount => ShowAll ? 0 : Matching().Count - Entries.Count;
        public bool HasShowAllRow => MoreCount > 0;
        public bool ShowAllFocused => HasShowAllRow && Index == Entries.Count;
        public string ShowAllLabel => "Show all " + Matching().Count + (Quality == AllQualities ? "" : " " + Quality) + " sources";

        /// Kept for older callers/tests: ranked candidates in display order.
        public IReadOnlyList<StreamCandidate> Candidates => Entries.Select(e => e.Candidate).ToList();

        public static int QualityRank(string quality)
        {
            switch (quality)
            {
                case "4K": return 0;
                case "1080p": return 1;
                case "720p": return 2;
                default: return 3;
            }
        }

        /// One line saying why the top pick won, as reasons rather than a repeat of the row's own facts.
        public static string WhyBest(SourceEntry entry, bool anime)
        {
            var c = entry.Candidate;
            var parts = new List<string>();
            if (anime)
            {
                var language = UiFormat.LanguageTier(c.LanguageTier, true);
                if (language != null) parts.Add(language);
            }
            else if (c.LanguageTier >= 4)
            {
                parts.Add("English audio confirmed");
                if (c.LanguageTier == 7) parts.Add(Languages.Name(Languages.Subtitles) + " subtitles");
                else if (c.LanguageTier >= 5) parts.Add("subtitles included");
            }
            else if (c.LanguageTier == 3) parts.Add(Languages.Name(Languages.Subtitles) + " subtitles");
            if (c.InfoHash == null && c.Url != null) parts.Add("direct link");
            else if (c.HealthBand >= 2) parts.Add("healthy swarm");
            parts.Add(parts.Count == 0 ? "top ranked for quality and size" : "best quality among them");
            return string.Join(" · ", parts);
        }

        /// Compact text for logs/tests: quality, size, audio, subs, seeders.
        public static IReadOnlyList<string> Badges(StreamCandidate c, bool anime)
        {
            var f = SourceFacts.Parse(c, anime);
            var badges = new List<string> { f.Quality };
            if (f.Hdr != null) badges.Add(f.Hdr);
            var size = UiFormat.Size(c.SizeBytes);
            if (size != null) badges.Add(size);
            var language = UiFormat.LanguageTier(c.LanguageTier, anime);
            if (language != null) badges.Add(language);
            if (c.Seeders != null) badges.Add(c.Seeders + " seeders");
            if (f.VideoCodec != null) badges.Add(f.VideoCodec);
            if (c.InfoHash == null && c.Url != null) badges.Add("Direct");
            return badges;
        }

        protected internal override void OnPushed()
        {
            var options = new SourceOptions { Episodic = video != null, IsAnime = IsAnime, EnglishOriginal = SourceOptions.IsEnglishOriginal(meta) };
            var catalog = Services.SourceCatalog;
            Load(token => catalog.GetAsync(meta, video, options, token), list =>
            {
                Loading = false;
                all = list ?? Array.Empty<SourceEntry>();
                Index = Entries.Count > 0 ? 0 : -1;
            }, error =>
            {
                Loading = false;
                Index = -1;
                Error = "No sources: " + error.Message;
            });
        }

        private int RowCount => Entries.Count + (HasShowAllRow ? 1 : 0);

        private void SetQuality(int index)
        {
            var count = Qualities.Count;
            index = Math.Max(0, Math.Min(count - 1, index));
            if (index == QualityIndex)
                return;
            QualityIndex = index;
            ShowAll = false;
            if (Index >= 0)
                Index = RowCount > 0 ? 0 : -1;
        }

        public override bool HandleKey(KeyPress key)
        {
            var entries = Entries;
            var chips = Qualities.Count;
            switch (key.Key)
            {
                case RemoteKey.Back: return false;
                case RemoteKey.Up:
                    Index = Math.Max(-1, Index - 1);
                    if (Index < 0) ToolbarIndex = Math.Min(QualityIndex, chips - 1);
                    return true;
                case RemoteKey.Down: Index = Math.Min(RowCount - 1, Index + 1); return true;
                case RemoteKey.Left:
                case RemoteKey.Right:
                    var step = key.Key == RemoteKey.Left ? -1 : 1;
                    if (Index < 0)
                        ToolbarIndex = Math.Max(0, Math.Min(chips, ToolbarIndex + step));   // chips, then the unplayable toggle
                    else
                        SetQuality(QualityIndex + step);   // on the list: straight to the next quality
                    return true;
                case RemoteKey.Select:
                    if (Index < 0)
                    {
                        if (ToolbarIndex < chips) SetQuality(ToolbarIndex);
                        else ShowHidden = !ShowHidden;
                        return true;
                    }
                    if (Index >= entries.Count)
                    {
                        if (HasShowAllRow) ShowAll = true;   // focus stays where the next source now is
                        return true;
                    }
                    var entry = entries[Index];
                    if (!entry.Playable)
                    {
                        Shell.ShowToast("Can't play on this TV: " + entry.RejectReason);
                        return true;
                    }
                    var candidate = entry.Candidate;
                    if (Switching)
                    {
                        var shell = Shell;
                        shell.Pop(this);
                        shell.RunDetached(token => shell.Services.Player.SwitchSourceAsync(candidate, token));
                    }
                    else
                    {
                        Shell.Replace(new PlayerViewModel(new PlayRequest { Meta = meta, Video = video, ResumeMs = resumeMs, Mode = mode, Source = candidate }));
                    }
                    return true;
            }
            return true;
        }
    }
}
