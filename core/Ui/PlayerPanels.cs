using System;
using Ru7100.Core.Playback;
using System.Collections.Generic;
using System.Linq;

namespace Ru7100.Core.Ui
{
    /// Overlay sub-panels opened from a button: Audio & subtitles (two columns; the first subtitle item opens the
    /// Subtitle settings side panel), Episodes (side panel) and More (side panel).
    public enum PlayerPanel { None, Tracks, Episodes, Style, More }

    public sealed class TrackChoice
    {
        public string Label { get; set; }
        public bool Selected { get; set; }
        public bool Divider { get; set; }       // drawn as a separator line above this item
        public int AudioIndex { get; set; } = -1;
        public string SubtitleId { get; set; }   // null with IsOff = the Off item
        public bool IsOff { get; set; }
        /// The first item of the subtitles column: opens Subtitle settings (timing, size, style). It used to be the last
        /// item, below every add-on language, where viewers could not find the timing control.
        public bool IsSettings { get; set; }
        public string Language { get; set; }
    }

    /// Next-episode preview, Episodes panel, two-column Audio & subtitles, and the credits countdown card.
    public sealed partial class PlayerViewModel
    {
        /// Next-episode card: the last 60 s, or from 98 %, whenever a next episode exists. With binge on it counts
        /// down to the automatic advance; with binge off it waits for OK.
        public const int CreditsWindowMs = 60_000;
        public const double CreditsRatio = 0.98;

        private string creditsDismissedFor;
        private string extrasFor;

        public PlayerPanel Panel { get; private set; }
        public MetaExtras Extras { get; private set; }
        public int TracksColumn { get; private set; }     // 0 audio, 1 subtitles
        public int AudioIndex { get; private set; }
        public int SubtitleIndex { get; private set; }
        public int EpisodesSeason { get; private set; }
        public int EpisodesIndex { get; private set; }
        public bool CreditsCardVisible { get; private set; }
        public long CreditsRemainingMs { get; private set; }

        /// The title and episode on screen. They outlive the session (end card, failure card) and follow binge advances.
        private MetaDetail CurrentMeta => Snapshot.Meta ?? lastMeta ?? request?.Meta;
        private VideoInfo CurrentVideo => Snapshot.Video ?? (lastMeta != null ? lastVideo : request?.Video);

        /// With binge on (and no stop-after) the card counts down and the engine advances by itself.
        public bool CreditsAutoplay => Snapshot.Binge && !Snapshot.StopAfterCurrent;

        /// The episode before the current one, or null.
        public VideoInfo PreviousVideo
        {
            get
            {
                var meta = CurrentMeta;
                var current = CurrentVideo;
                if (meta == null || current == null)
                    return null;
                var list = meta.Videos.Where(v => current.Season <= 0 || v.Season > 0).ToList();
                var index = list.FindIndex(v => v.Id == current.Id);
                return index > 0 ? list[index - 1] : null;
            }
        }

        /// The released episode after the current one, or null (movies, finales).
        public VideoInfo NextVideo
        {
            get
            {
                var meta = CurrentMeta;
                var current = CurrentVideo;
                if (meta == null || current == null)
                    return null;
                var list = meta.Videos.Where(v => current.Season <= 0 || v.Season > 0).ToList();
                var index = list.FindIndex(v => v.Id == current.Id);
                var next = index >= 0 && index + 1 < list.Count ? list[index + 1] : null;
                return next != null && UiFormat.IsReleased(next, Services.Clock.UtcNow) ? next : null;
            }
        }

        public EpisodeExtras ExtrasFor(VideoInfo video) =>
            video != null && Extras != null && Extras.Episodes.TryGetValue(video.Id, out var e) ? e : null;

        private void LoadExtras()
        {
            var meta = CurrentMeta;
            if (Services.Extras == null || meta?.Summary?.Id == null || extrasFor == meta.Summary.Id)
                return;
            extrasFor = meta.Summary.Id;
            Load(token => Services.Extras.GetAsync(meta.Summary.Type, meta.Summary.Id, token), e => Extras = e, _ => { });
        }

        // ---- Audio & subtitles: audio with [Original] first; subtitles Off, preferred languages, divider, rest ----

        public IReadOnlyList<TrackChoice> AudioChoices
        {
            get
            {
                var s = Snapshot;
                var tracks = s.AudioTracks.ToList();
                // The first track is the stream's original/default audio unless a label says otherwise.
                var original = tracks.FirstOrDefault(t => (t.Label ?? "").IndexOf("original", StringComparison.OrdinalIgnoreCase) >= 0) ?? tracks.FirstOrDefault();
                return tracks.OrderBy(t => t == original ? 0 : 1).Select(t => new TrackChoice
                {
                    Label = UiFormat.TrackLabel(t) + (t == original ? " [Original]" : ""),
                    Selected = t.Index == s.SelectedAudio,
                    AudioIndex = t.Index,
                    Language = t.Language
                }).ToList();
            }
        }

        public IReadOnlyList<TrackChoice> SubtitleChoices
        {
            get
            {
                var s = Snapshot;
                var pref = Settings.GetTrackPreference(CurrentMeta?.Summary?.Id, UiFormat.IsAnime(CurrentMeta));
                var preferred = new[] { pref.SubtitleLanguage }.Concat(Languages.SubtitleOrder(false)).Where(l => l != null && l != "off").Select(PlayerSettings.Normalize).Distinct().ToList();
                var list = new List<TrackChoice>
                {
                    new TrackChoice { Label = SettingsLabel(s.SubtitleOffsetMs), IsSettings = true },
                    new TrackChoice { Label = "Off", IsOff = true, Selected = s.SelectedSubtitleId == null, Divider = true }
                };
                var top = s.Subtitles.Where(o => preferred.Contains(PlayerSettings.Normalize(o.Language)))
                    .OrderBy(o => preferred.IndexOf(PlayerSettings.Normalize(o.Language))).ThenBy(o => o.External ? 1 : 0).ToList();
                var rest = s.Subtitles.Except(top).OrderBy(o => UiFormat.LanguageName(o.Language)).ToList();
                list.AddRange(top.Select(o => Choice(o, s)));
                var first = true;
                foreach (var o in rest)
                {
                    var choice = Choice(o, s);
                    choice.Divider = first;
                    first = false;
                    list.Add(choice);
                }
                return list;
            }
        }

        /// Names the timing first (the setting viewers look for) and shows the current offset when there is one.
        public static string SettingsLabel(int offsetMs) =>
            offsetMs == 0 ? "Timing, size & style  ›" : "Timing " + TimingLabel(offsetMs).Substring("Subtitles ".Length) + "  ›";

        private static TrackChoice Choice(SubtitleOption o, PlayerSnapshot s) => new TrackChoice
        {
            Label = UiFormat.LanguageName(o.Language) + (string.IsNullOrEmpty(o.Label) || o.Label == UiFormat.LanguageName(o.Language) ? "" : " · " + o.Label) + (o.External ? " (add-on)" : ""),
            Selected = o.Id == s.SelectedSubtitleId,
            SubtitleId = o.Id,
            Language = o.Language
        };

        private void OpenTracksPanel()
        {
            Panel = PlayerPanel.Tracks;
            TracksColumn = 1;
            AudioIndex = Math.Max(0, AudioChoices.ToList().FindIndex(c => c.Selected));
            var subs = SubtitleChoices.ToList();
            var selected = subs.FindIndex(c => c.Selected);
            SubtitleIndex = selected >= 0 ? selected : subs.FindIndex(c => c.IsOff);   // never lands on the settings row
        }

        // ---- Episodes panel ----

        public IReadOnlyList<int> EpisodeSeasons =>
            (CurrentMeta?.Videos ?? Array.Empty<VideoInfo>()).Select(v => v.Season).Distinct().OrderBy(x => x <= 0 ? int.MaxValue : x).ToList();

        public IReadOnlyList<VideoInfo> PanelEpisodes =>
            (CurrentMeta?.Videos ?? Array.Empty<VideoInfo>()).Where(v => v.Season == EpisodesSeason).ToList();

        private void OpenEpisodesPanel()
        {
            Panel = PlayerPanel.Episodes;
            var current = CurrentVideo;
            EpisodesSeason = current?.Season ?? EpisodeSeasons.FirstOrDefault();
            EpisodesIndex = Math.Max(0, PanelEpisodes.ToList().FindIndex(v => v.Id == current?.Id));
            LoadExtras();
        }

        private bool HandlePanelKey(KeyPress key)
        {
            if (key.Key == RemoteKey.Back)
            {
                var closed = Panel;
                if (closed == PlayerPanel.Style)
                {
                    Panel = PlayerPanel.Tracks;   // one level up: back to the list it was opened from
                    return true;
                }
                Panel = PlayerPanel.None;
                FocusedRow = closed == PlayerPanel.Episodes ? OverlayRow.Episodes : closed == PlayerPanel.More ? OverlayRow.More : OverlayRow.AudioSubtitles;
                return true;
            }
            switch (Panel)
            {
                case PlayerPanel.Tracks: return TracksKey(key);
                case PlayerPanel.Style: return StyleKey(key);
                case PlayerPanel.More: return MoreKey(key);
                default: return EpisodesKey(key);
            }
        }

        private bool TracksKey(KeyPress key)
        {
            var audio = AudioChoices;
            var subs = SubtitleChoices;
            switch (key.Key)
            {
                case RemoteKey.Left: TracksColumn = 0; break;
                case RemoteKey.Right: TracksColumn = 1; break;
                case RemoteKey.Up:
                    if (TracksColumn == 0) AudioIndex = Math.Max(0, AudioIndex - 1);
                    else SubtitleIndex = Math.Max(0, SubtitleIndex - 1);
                    break;
                case RemoteKey.Down:
                    if (TracksColumn == 0) AudioIndex = Math.Min(audio.Count - 1, AudioIndex + 1);
                    else SubtitleIndex = Math.Min(subs.Count - 1, SubtitleIndex + 1);
                    break;
                case RemoteKey.Select:
                    var showId = CurrentMeta?.Summary?.Id;
                    if (TracksColumn == 0 && AudioIndex < audio.Count)
                    {
                        var choice = audio[AudioIndex];
                        Services.Player.SelectAudio(choice.AudioIndex);
                        Settings.RememberTracks(showId, choice.Language, null);
                    }
                    else if (TracksColumn == 1 && SubtitleIndex < subs.Count)
                    {
                        var choice = subs[SubtitleIndex];
                        if (choice.IsSettings)
                        {
                            Panel = PlayerPanel.Style;
                            FocusedStyleRow = StyleRow.Timing;
                            break;
                        }
                        Services.Player.SelectSubtitle(choice.IsOff ? null : choice.SubtitleId);
                        Settings.RememberTracks(showId, null, choice.IsOff ? "off" : choice.Language);
                    }
                    break;
            }
            return true;
        }

        private bool EpisodesKey(KeyPress key)
        {
            var seasons = EpisodeSeasons.ToList();
            var episodes = PanelEpisodes;
            switch (key.Key)
            {
                case RemoteKey.Left:
                case RemoteKey.Right:
                    var at = seasons.IndexOf(EpisodesSeason) + (key.Key == RemoteKey.Left ? -1 : 1);
                    if (at >= 0 && at < seasons.Count)
                    {
                        EpisodesSeason = seasons[at];
                        EpisodesIndex = 0;
                    }
                    break;
                case RemoteKey.Up: EpisodesIndex = Math.Max(0, EpisodesIndex - 1); break;
                case RemoteKey.Down: EpisodesIndex = Math.Min(Math.Max(0, episodes.Count - 1), EpisodesIndex + 1); break;
                case RemoteKey.Select:
                    if (EpisodesIndex >= episodes.Count)
                        break;
                    var video = episodes[EpisodesIndex];
                    if (video.Id == CurrentVideo?.Id)
                    {
                        CloseOverlay();
                        break;
                    }
                    if (!UiFormat.IsReleased(video, Services.Clock.UtcNow))
                    {
                        Shell.ShowToast(UiFormat.EpisodeColon(video) + " is not out yet");
                        break;
                    }
                    var request = new PlayRequest { Meta = CurrentMeta, Video = video, Mode = Snapshot.Binge ? PlaybackMode.Binge : PlaybackMode.Single };
                    CloseOverlay();
                    Shell.RunDetached(token => Services.Player.PlayAsync(request, token));
                    break;
            }
            return true;
        }

        // ---- next-episode card near the end ----

        private bool TickCredits(PlayerSnapshot s, long nowMs)
        {
            var next = NextVideo;
            var key = s.Video?.Id;
            var remaining = s.DurationMs - s.PositionMs;
            var inCredits = next != null && s.DurationMs > 0 && s.SessionActive && key != creditsDismissedFor && !Loading
                && (remaining <= CreditsWindowMs || s.PositionMs >= CreditsRatio * s.DurationMs);
            // With binge on, the card counts down to the engine's hand-over point (EpisodeHandOff: 98 % or 30 s before
            // the end), not to the end of the file: it used to read "56 s" when the next episode was already starting.
            var autoplay = CreditsAutoplay;
            var countdown = autoplay ? EpisodeHandOff.RemainingMs(s.PositionMs, s.DurationMs) : Math.Max(0, remaining);
            var changed = inCredits != CreditsCardVisible || inCredits && SecondsShown(countdown) != SecondsShown(CreditsRemainingMs);
            CreditsCardVisible = inCredits;
            CreditsRemainingMs = inCredits ? countdown : 0;
            if (inCredits)
                LoadExtras();
            // The engine itself starts the next episode (it keeps binge's quality and size cap and records a natural
            // end); its monitor runs every 5 s, so the card can rest on "0 s" for up to one monitor tick.
            return changed;
        }

        private static long SecondsShown(long ms) => (long)Math.Ceiling(ms / 1000.0);

        /// Card visible with the overlay hidden: center plays the next episode now; Back puts the card away and keeps
        /// the credits playing (no automatic hand-over for this item; binge and stop-after keep what the viewer set).
        private bool HandleCreditsKey(KeyPress key)
        {
            if (!CreditsCardVisible)
                return false;
            switch (key.Key)
            {
                case RemoteKey.Select:
                    CreditsCardVisible = false;
                    PlayNext();
                    return true;
                case RemoteKey.Back:
                    creditsDismissedFor = Snapshot.Video?.Id;
                    CreditsCardVisible = false;
                    (Services.Player as ICreditsControls)?.KeepWatchingCredits();
                    return true;
                default:
                    return false;
            }
        }
    }
}
