using System;
using System.Collections.Generic;
using System.Linq;

namespace Ru7100.Core.Ui
{
    public enum DetailsFocus { Actions, Seasons, Episodes }

    public enum DetailsAction { Primary, Next, Episodes, Binge, Trailer, ChooseSource, Library, Notifications, MarkWatched, Rate, More }

    public enum SignatureState { None, Checking, Found, Unavailable }

    public sealed class DetailsButton
    {
        public DetailsAction Action { get; set; }
        public string Label { get; set; }
    }

    /// Per-episode row: column 0 plays, column 1 toggles watched, column 2 adds to the queue.
    public sealed class EpisodeRow
    {
        public VideoInfo Video { get; set; }
        public bool Watched { get; set; }
        public bool Released { get; set; }
        public double Progress { get; set; }   // 0..1, current video only
        public bool IsCurrent { get; set; }
        /// The episode the primary button plays ("Up next" / "Resume" in the list).
        public bool IsPrimary { get; set; }
        /// "Resume · 21m left" or "Up next" on the primary episode; null elsewhere.
        public string Marker { get; set; }
    }

    public sealed class DetailsViewModel : Screen
    {
        public const int EpisodeColumns = 3;
        public const int TrailerDelayMs = 2500;

        private readonly MediaType type;
        private readonly string id;
        private VideoInfo primaryVideo;
        private long primaryResumeMs;

        public DetailsViewModel(MediaType type, string id, MetaSummary preview = null)
        {
            this.type = type;
            this.id = id;
            Preview = preview;
        }

        public override ScreenKind Kind => ScreenKind.Details;
        public override string Title => Meta?.Summary?.Name ?? Preview?.Name ?? id;
        public MediaType Type => type;
        public string Id => id;
        public MetaSummary Preview { get; }
        /// Opened by the direct play key on a grid title: play the primary episode as soon as the page has loaded.
        public bool PlayWhenLoaded { get; set; }
        public MetaDetail Meta { get; private set; }
        public LibraryEntry Entry { get; private set; }
        public bool Loaded => Meta != null;
        public bool InLibrary => Entry != null && !Entry.Removed && !Entry.Temp;
        public DetailsFocus Focus { get; private set; }
        public int ActionIndex { get; private set; }
        public int SeasonIndex { get; private set; }
        public int EpisodeIndex { get; private set; }
        public int EpisodeColumn { get; private set; }
        public IReadOnlyList<DetailsButton> Actions { get; private set; } = Array.Empty<DetailsButton>();
        public IReadOnlyList<int> Seasons { get; private set; } = Array.Empty<int>();
        public IReadOnlyList<EpisodeRow> Episodes { get; private set; } = Array.Empty<EpisodeRow>();
        public VideoInfo PrimaryVideo => primaryVideo;
        /// 0..1 of the episode or movie the primary button resumes (drawn inside the button), or 0.
        public double PrimaryProgress
        {
            get
            {
                var state = Entry?.State;
                return primaryResumeMs > 0 && state != null && state.DurationMs > 0 ? Math.Min(1.0, state.TimeOffsetMs / (double)state.DurationMs) : 0;
            }
        }
        /// The advanced actions behind the More button, in order.
        public IReadOnlyList<DetailsButton> MoreActions { get; private set; } = Array.Empty<DetailsButton>();
        public MetaExtras Extras { get; private set; }
        /// YouTube id of the trailer playing in the header, or null.
        public string TrailerId { get; private set; }
        public bool TrailerMuted { get; private set; } = true;
        private bool trailerStopped;
        private int trailerEndSeen;
        private int trailerStartSeen;
        private long trailerStartedMs = -1;
        private bool compactShown;

        /// Netflix trailer view: TrailerCompactDelayMs after the trailer's first frames, the facts fade out and the
        /// logo shrinks toward the bottom-left so the video shows; any end or navigation restores the full billboard.
        public bool TrailerCompact => TrailerId != null && trailerStartedMs >= 0 && NowMs - trailerStartedMs >= UiLayout.TrailerCompactDelayMs;

        private void TickTrailerCompact(long nowMs)
        {
            if (TrailerId != null && trailerStartedMs < 0 && Services.TrailerStartCount != trailerStartSeen && Services.StartedTrailerId == TrailerId)
                trailerStartedMs = nowMs;
            if (TrailerCompact != compactShown)
            {
                compactShown = TrailerCompact;
                Changed();
            }
        }
        private long openedMs;

        public EpisodeExtras ExtrasFor(VideoInfo video) =>
            video != null && Extras != null && Extras.Episodes.TryGetValue(video.Id, out var e) ? e : null;

        public EpisodeRow FocusedEpisode => Episodes.Count == 0 ? null : Episodes[Math.Min(EpisodeIndex, Episodes.Count - 1)];
        public long PrimaryResumeMs => primaryResumeMs;

        protected internal override void Tick(long nowMs)
        {
            TickTrailerCompact(nowMs);
            if (TrailerId != null && Services.TrailerEndCount != trailerEndSeen && Services.EndedTrailerId == TrailerId)
            {
                TrailerId = null;   // played once: the page comes back; the Trailer button plays it again
                trailerStopped = true;
                Changed();
            }
            if (Shell?.Dialog != null)
            {
                // Never start a trailer (or the trailer view) behind a dialog or menu; the dwell restarts after it.
                openedMs = Math.Max(openedMs, nowMs - TrailerDelayMs + 1000);
            }
            if (TrailerId == null && !trailerStopped && Services.TrailerPlayback && Services.AutoplayPreviews && Services.RecentlyInteracted(nowMs) && Extras?.TrailerId != null && nowMs - openedMs >= TrailerDelayMs)
            {
                TrailerId = Extras.TrailerId;
                trailerFullScreen = false;   // the preview plays behind the page
                TrailerMuted = !Services.PreviewSound;
                trailerEndSeen = Services.TrailerEndCount;
                trailerStartSeen = Services.TrailerStartCount;
                trailerStartedMs = -1;
                Changed();
            }
        }

        protected internal override void OnPushed()
        {
            openedMs = NowMs;
            if (Services.Extras != null)
                Load(token => Services.Extras.GetAsync(type, id, token), extras =>
                {
                    Extras = extras;
                    Rebuild(false);
                }, _ => { });
            Load(token => Services.Catalog.GetMetaAsync(type, id, token), meta =>
            {
                Meta = meta;
                Error = null;
                Rebuild(selectPrimarySeason: true);
                if (PlayWhenLoaded && Shell.Current == this && Shell.Dialog == null)
                {
                    PlayWhenLoaded = false;
                    if (type == MediaType.Movie || primaryVideo != null)
                        PlayPrimary(PlaybackMode.Single);
                }
            }, error => Error = "Could not load details: " + error.Message);
            ReloadEntry(true);
        }

        protected internal override void OnResumed()
        {
            TrailerId = null;   // returning from playback: don't restart the trailer
            trailerStopped = true;
            seasonBrowsed = false;   // like Netflix, the page moves on to the episode you are on now
            ReloadEntry(true);
        }

        private void ReloadEntry(bool selectSeason)
        {
            if (Services.Library == null || Services.Account != null && !Services.Account.IsSignedIn)
                return;
            Load(token => Services.Library.GetAsync(id, token), entry =>
            {
                Entry = entry;
                Rebuild(selectSeason);
            }, _ => { });
        }

        private DateTimeOffset Now => Services.Clock.UtcNow;

        private void Rebuild(bool selectPrimarySeason)
        {
            if (Meta == null)
                return;
            ComputePrimary();
            var actions = new List<DetailsButton>();
            var more = new List<DetailsButton>();
            var state = Entry?.State;
            // Adaptive primary action: Resume S2:E3 · 21m left / Play S1:E1 / Play next: S2:E4 (new).
            if (type == MediaType.Series)
            {
                if (primaryVideo != null)
                {
                    var resume = primaryResumeMs > 0;
                    string label;
                    if (resume)
                        label = UiFormat.Facts("Resume " + UiFormat.EpisodeColon(primaryVideo), Remaining(primaryVideo.Id)).Replace("  ·  ", " · ");
                    else if (Entry == null || IsFirst(primaryVideo))
                        label = "Play " + UiFormat.EpisodeColon(primaryVideo);
                    else
                        label = "Play next: " + UiFormat.EpisodeColon(primaryVideo) + (IsNewSinceLastWatch(primaryVideo) ? " (new)" : "");
                    actions.Add(new DetailsButton { Action = DetailsAction.Primary, Label = label });
                    var next = resume ? NextReleasedAfter(primaryVideo) : null;
                    if (next != null)
                        more.Add(new DetailsButton { Action = DetailsAction.Next, Label = "Play next: " + UiFormat.EpisodeColon(next) });
                }
            }
            else
            {
                actions.Add(new DetailsButton
                {
                    Action = DetailsAction.Primary,
                    Label = primaryResumeMs > 0 ? "Resume · " + (Remaining(id) ?? UiFormat.Clock(primaryResumeMs)) : "Play"
                });
            }
            // Four everyday actions in the row: Play, Trailer, My library, Rate. The episode list is one Down away
            // (no button for it), and the advanced ones (sources, binge, next, mark watched) sit behind More.
            if (Extras?.TrailerId != null && Services.TrailerPlayback)
                actions.Add(new DetailsButton { Action = DetailsAction.Trailer, Label = "Trailer" });
            if (Services.Library != null)
                actions.Add(new DetailsButton { Action = DetailsAction.Library, Label = InLibrary ? "In my library" : "+ My library" });
            if (Services.Feedback != null)
                actions.Add(new DetailsButton { Action = DetailsAction.Rate, Label = RatedLabel ?? "Rate" });
            if (Services.SourceCatalog != null && (type == MediaType.Movie || primaryVideo != null))
                more.Add(new DetailsButton { Action = DetailsAction.ChooseSource, Label = "Choose a source" });
            if (type == MediaType.Series && primaryVideo != null)
                more.Add(new DetailsButton { Action = DetailsAction.Binge, Label = "Play and keep going (binge)" });
            if (type == MediaType.Movie && Services.Library != null)
            {
                var watched = Entry != null && Services.Library.IsWatched(Entry, Meta, id);
                more.Add(new DetailsButton { Action = DetailsAction.MarkWatched, Label = watched ? "Mark unwatched" : "Mark watched" });
            }
            MoreActions = more;
            if (more.Count > 0)
                actions.Add(new DetailsButton { Action = DetailsAction.More, Label = "More" });
            Actions = actions;
            StartSignature();
            ActionIndex = Math.Min(ActionIndex, Math.Max(0, actions.Count - 1));

            if (type == MediaType.Series)
            {
                var seasons = Meta.Videos.Select(v => v.Season).Distinct().ToList();
                Seasons = seasons.Where(s => s > 0).OrderBy(s => s).Concat(seasons.Where(s => s <= 0)).ToList();
                if (selectPrimarySeason && primaryVideo != null)
                    SeasonIndex = Math.Max(0, Seasons.ToList().IndexOf(primaryVideo.Season));
                SeasonIndex = Math.Min(SeasonIndex, Math.Max(0, Seasons.Count - 1));
                BuildEpisodes(selectPrimarySeason);
            }
        }

        private void BuildEpisodes(bool focusPrimary)
        {
            if (Seasons.Count == 0)
            {
                Episodes = Array.Empty<EpisodeRow>();
                return;
            }
            var season = Seasons[SeasonIndex];
            var state = Entry?.State;
            Episodes = Meta.Videos.Where(v => v.Season == season).Select(v => new EpisodeRow
            {
                Video = v,
                Released = UiFormat.IsReleased(v, Now),
                Watched = Entry != null && Services.Library != null && Services.Library.IsWatched(Entry, Meta, v.Id),
                IsCurrent = state != null && state.VideoId == v.Id,
                IsPrimary = primaryVideo != null && primaryVideo.Id == v.Id,
                Marker = primaryVideo == null || primaryVideo.Id != v.Id ? null
                    : primaryResumeMs > 0 ? UiFormat.Facts("Resume", Remaining(v.Id)).Replace("  ·  ", " · ") : Entry != null ? "Up next" : null,
                Progress = state != null && state.VideoId == v.Id && state.DurationMs > 0 && state.TimeOffsetMs > 1000
                    ? Math.Min(1.0, state.TimeOffsetMs / (double)state.DurationMs) : 0
            }).ToList();
            if (focusPrimary && primaryVideo != null)
                EpisodeIndex = Math.Max(0, Episodes.ToList().FindIndex(e => e.Video.Id == primaryVideo.Id));
            EpisodeIndex = Math.Min(EpisodeIndex, Math.Max(0, Episodes.Count - 1));
        }

        /// Parity H1/H2/H3: resume in 1 s .. 90 %, otherwise the next library video, otherwise the first episode.
        private void ComputePrimary()
        {
            primaryVideo = null;
            primaryResumeMs = 0;
            var state = Entry?.State;
            if (type == MediaType.Movie)
            {
                if (state != null && InResumeWindow(state))
                    primaryResumeMs = state.TimeOffsetMs;
                return;
            }
            var released = Meta.Videos.Where(v => UiFormat.IsReleased(v, Now)).ToList();
            var current = state == null ? null : Meta.Videos.FirstOrDefault(v => v.Id == state.VideoId);
            if (current != null && InResumeWindow(state))
            {
                primaryVideo = current;
                primaryResumeMs = state.TimeOffsetMs;
                return;
            }
            if (current != null && state.DurationMs > 0 && state.TimeOffsetMs >= 0.9 * state.DurationMs)
            {
                primaryVideo = NextReleasedAfter(current);
                if (primaryVideo != null)
                    return;
            }
            if (Entry != null && Services.Library != null)
                primaryVideo = Services.Library.NextVideo(Entry, Meta);
            if (primaryVideo == null)
                primaryVideo = released.FirstOrDefault(v => v.Season > 0) ?? released.FirstOrDefault();
        }

        private bool IsFirst(VideoInfo video) =>
            Meta.Videos.Where(v => v.Season > 0).Select(v => v.Id).FirstOrDefault() == video.Id;

        private bool IsNewSinceLastWatch(VideoInfo video)
        {
            var baseline = Entry?.State?.LastWatched;
            return baseline != null && video.Released != null && video.Released > baseline;
        }

        /// "21m left" for the current library video, when its duration is known.
        private string Remaining(string videoId)
        {
            var state = Entry?.State;
            return state != null && state.VideoId == videoId && state.DurationMs > 0 ? UiFormat.Remaining(state.DurationMs - state.TimeOffsetMs) : null;
        }

        // ---- signature source line ----

        public SignatureState Signature { get; private set; }
        public SourceEntry SignatureEntry { get; private set; }
        public string RatedLabel { get; private set; }
        private string signatureFor;

        /// Best playable source for the primary video, computed in the background when the page opens.
        private void StartSignature()
        {
            var signatures = Services.Signatures;
            var target = type == MediaType.Series ? primaryVideo?.Id : id;
            if (signatures == null || target == null || target == signatureFor)
                return;
            signatureFor = target;
            var video = type == MediaType.Series ? primaryVideo : null;
            if (signatures.TryGet(target, out var known))
            {
                SignatureEntry = known;
                Signature = known != null ? SignatureState.Found : SignatureState.Unavailable;
                return;
            }
            Signature = SignatureState.Checking;
            var meta = Meta;
            Load(token => signatures.GetAsync(meta, video, token), entry =>
            {
                if (signatureFor != target) return;
                SignatureEntry = entry;
                Signature = entry != null ? SignatureState.Found : SignatureState.Unavailable;
            }, _ => Signature = SignatureState.Unavailable);
        }

        private static bool InResumeWindow(LibraryState state) =>
            state.TimeOffsetMs > 1000 && (state.DurationMs <= 0 || state.TimeOffsetMs < 0.9 * state.DurationMs);

        private VideoInfo NextReleasedAfter(VideoInfo current)
        {
            var list = Meta.Videos.Where(v => current.Season <= 0 || v.Season > 0).ToList();
            var index = list.FindIndex(v => v.Id == current.Id);
            var next = index >= 0 && index + 1 < list.Count ? list[index + 1] : null;
            return next != null && UiFormat.IsReleased(next, Now) ? next : null;
        }

        /// Trailer view: the trailer plays full screen and the page steps aside (logo and one line only). Only the
        /// Trailer button opens it. Play/Pause switches its sound; any other key stops it and brings the page back.
        /// The automatic preview is not this: it plays in the header behind the page, whose actions and synopsis
        /// stay where they are, and the first key both stops it and does its job.
        public bool Immersive => TrailerId != null && trailerFullScreen;
        private bool trailerFullScreen;

        public override bool HandleKey(KeyPress key)
        {
            // Play/Pause always plays the title (user, 2026-11-19), also over a sounding preview or the full-screen
            // trailer; preview sound is the "Preview sound" setting, not a key.
            if (Loaded && (key.Key == RemoteKey.PlayPause || key.Key == RemoteKey.Play))
            {
                TrailerId = null;
                trailerStopped = true;
                PlayPrimary(PlaybackMode.Single);
                return true;
            }
            if (Immersive)
            {
                TrailerId = null;
                trailerStopped = true;   // the Trailer button plays it again
                return true;
            }
            if (TrailerId != null)
            {
                TrailerId = null;        // the preview fades on any key, and the key still acts
                trailerStopped = true;
            }
            openedMs = key.AtMs;   // the trailer waits for the page to rest
            if (key.Key == RemoteKey.Back)
                return false;
            if (!Loaded)
                return true;
            if (key.Key == RemoteKey.Play || key.Key == RemoteKey.PlayPause)
            {
                PlayPrimary(PlaybackMode.Single);
                return true;
            }
            switch (Focus)
            {
                case DetailsFocus.Actions:
                    switch (key.Key)
                    {
                        case RemoteKey.Left: ActionIndex = Math.Max(0, ActionIndex - 1); break;
                        case RemoteKey.Right: ActionIndex = Math.Min(Actions.Count - 1, ActionIndex + 1); break;
                        case RemoteKey.Down:
                            if (Seasons.Count > 1) EnterEpisodes(DetailsFocus.Seasons);
                            else if (Episodes.Count > 0) EnterEpisodes(DetailsFocus.Episodes);
                            break;
                        case RemoteKey.Select:
                            if (Actions.Count > 0)
                                Activate(Actions[ActionIndex].Action);
                            break;
                    }
                    return true;
                case DetailsFocus.Seasons:
                    switch (key.Key)
                    {
                        case RemoteKey.Left:
                        case RemoteKey.Right:
                            var season = Math.Max(0, Math.Min(Seasons.Count - 1, SeasonIndex + (key.Key == RemoteKey.Left ? -1 : 1)));
                            if (season != SeasonIndex)
                            {
                                seasonBrowsed = true;
                                SeasonIndex = season;
                                EpisodeIndex = 0;
                                BuildEpisodes(false);
                            }
                            break;
                        case RemoteKey.Up: Focus = DetailsFocus.Actions; break;
                        case RemoteKey.Down:
                        case RemoteKey.Select:
                            if (Episodes.Count > 0) Focus = DetailsFocus.Episodes;
                            break;
                    }
                    return true;
                default:
                    switch (key.Key)
                    {
                        case RemoteKey.Up:
                            if (EpisodeIndex > 0) EpisodeIndex--;
                            else Focus = Seasons.Count > 1 ? DetailsFocus.Seasons : DetailsFocus.Actions;
                            break;
                        case RemoteKey.Down: EpisodeIndex = Math.Min(Episodes.Count - 1, EpisodeIndex + 1); break;
                        case RemoteKey.Left: EpisodeColumn = Math.Max(0, EpisodeColumn - 1); break;
                        case RemoteKey.Right: EpisodeColumn = Math.Min(EpisodeColumnCount - 1, EpisodeColumn + 1); break;
                        case RemoteKey.Select: ActivateEpisode(Episodes[EpisodeIndex], EpisodeColumn); break;
                    }
                    return true;
            }
        }

        /// The viewer picked a season by hand during this visit; until then, entering the list lands on the primary episode.
        private bool seasonBrowsed;

        /// Netflix: the episode list opens on the season and row of the episode you are on (resume or up next).
        private void EnterEpisodes(DetailsFocus focus)
        {
            if (!seasonBrowsed && primaryVideo != null && Seasons.Count > 0)
            {
                var season = Seasons.ToList().IndexOf(primaryVideo.Season);
                if (season >= 0 && season != SeasonIndex)
                    SeasonIndex = season;
                BuildEpisodes(true);
            }
            Focus = focus;
        }

        public int EpisodeColumnCount => Services.Queue != null ? EpisodeColumns : EpisodeColumns - 1;

        private void Activate(DetailsAction action)
        {
            switch (action)
            {
                case DetailsAction.Primary: PlayPrimary(PlaybackMode.Single); break;
                case DetailsAction.Binge: PlayPrimary(PlaybackMode.Binge); break;
                case DetailsAction.Next:
                    var next = NextReleasedAfter(primaryVideo);
                    if (next != null)
                        Play(next, 0, PlaybackMode.Single);
                    break;
                case DetailsAction.Trailer:
                    TrailerId = Extras.TrailerId;   // straight into the trailer view, with sound
                    trailerFullScreen = true;
                    TrailerMuted = false;
                    trailerEndSeen = Services.TrailerEndCount;
                trailerStartSeen = Services.TrailerStartCount;
                trailerStartedMs = -1;
                    break;
                case DetailsAction.ChooseSource:
                    Shell.Push(new SourcePickerViewModel(Meta, primaryVideo, primaryResumeMs, PlaybackMode.Single, switching: false));
                    break;
                case DetailsAction.Episodes:
                    if (Episodes.Count > 0)
                        EnterEpisodes(DetailsFocus.Episodes);
                    break;
                case DetailsAction.More:
                    var options = MoreActions.Select(m => new DialogButton(m.Label, () => Activate(m.Action))).ToList();
                    options.Add(new DialogButton("Cancel", null));
                    Shell.ShowDialog(new ConfirmDialog("More", Title, options.ToArray()));
                    break;
                case DetailsAction.Library:
                    if (!InLibrary)
                    {
                        Run(token => Services.Library.AddAsync(Meta, token), () => { Shell.ShowToast("Added to my library"); ReloadEntry(false); });
                        break;
                    }
                    var buttons = new List<DialogButton>
                    {
                        new DialogButton("Remove from library", () => Run(token => Services.Library.RemoveAsync(id, token), () => { Shell.ShowToast("Removed from library"); ReloadEntry(false); }))
                    };
                    if (type == MediaType.Series)
                    {
                        var turnOn = Entry.State.NoNotif;
                        buttons.Add(new DialogButton(turnOn ? "Turn alerts on" : "Turn alerts off",
                            () => Run(token => Services.Library.SetNotificationsAsync(id, turnOn, token), () => ReloadEntry(false))));
                    }
                    buttons.Add(new DialogButton("Cancel", null));
                    Shell.ShowDialog(new ConfirmDialog(Title + " is in your library",
                        type == MediaType.Series ? "New-episode alerts are " + (Entry.State.NoNotif ? "off." : "on.") : null, buttons.ToArray()));
                    break;
                case DetailsAction.Rate:
                    RateDialog.Show(Shell, Services.Feedback, Meta.Summary, label =>
                    {
                        RatedLabel = label;
                        Rebuild(false);
                    });
                    break;
                case DetailsAction.Notifications:
                    var enable = Entry.State.NoNotif;
                    Run(token => Services.Library.SetNotificationsAsync(id, enable, token), () => ReloadEntry(false));
                    break;
                case DetailsAction.MarkWatched:
                    var watched = Entry != null && Services.Library.IsWatched(Entry, Meta, id);
                    Run(token => Services.Library.MarkWatchedAsync(Meta, id, !watched, token), () => ReloadEntry(false));
                    break;
            }
        }

        private void ActivateEpisode(EpisodeRow row, int column)
        {
            var video = row.Video;
            switch (column)
            {
                case 0:
                    if (!row.Released)
                    {
                        Shell.ShowToast(UiFormat.Episode(video) + " is not out yet");
                        return;
                    }
                    var state = Entry?.State;
                    var resume = state != null && state.VideoId == video.Id && InResumeWindow(state) ? state.TimeOffsetMs : 0;
                    Play(video, resume, PlaybackMode.Single);
                    break;
                case 1:
                    Run(token => Services.Library.MarkWatchedAsync(Meta, video.Id, !row.Watched, token), () => ReloadEntry(false));
                    break;
                case 2:
                    Run(token => Services.Queue.AddAsync(Meta, video, token), () => Shell.ShowToast("Added " + UiFormat.Episode(video) + " to the queue"));
                    break;
            }
        }

        public void PlayPrimary(PlaybackMode mode)
        {
            if (type == MediaType.Series && primaryVideo == null)
            {
                Shell.ShowToast("No released episodes");
                return;
            }
            Play(primaryVideo, primaryResumeMs, mode);
        }

        private void Play(VideoInfo video, long resumeMs, PlaybackMode mode)
        {
            TrailerId = null;
            Shell.StartPlayback(new PlayRequest { Meta = Meta, Video = video, ResumeMs = resumeMs, Mode = mode });
        }
    }

}
