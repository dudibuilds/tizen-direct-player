using System;
using System.Collections.Generic;
using System.Linq;

namespace Ru7100.Core.Ui
{
    /// Controls of the overlay. Transport is the bottom row (seek bar and play/pause); the others are one row of
    /// large buttons above it, left to right. Up/Down move between the two rows, Left/Right along the buttons.
    public enum OverlayRow { Transport, Episodes, NextEpisode, AudioSubtitles, Sources, More }

    /// Rows of the "Subtitle settings" panel (the first subtitle item in Audio & subtitles), top-to-bottom; it opens on Timing.
    public enum StyleRow { Size, Timing, Color, Outline, Background, Opacity, Position, RaiseWithOverlay, Bold, Reset }

    /// Rows of the "More" panel.
    public enum MoreRow { Restart, Binge, StopAfter }

    /// A full-screen card with buttons that replaces the picture: a start that failed, or an episode that ended.
    public enum PlayerCard { None, Failed, Ended }

    public enum CardAction { Retry, ChooseSource, NextEpisode, Episodes, Back }

    public sealed class CardButton
    {
        public CardAction Action { get; set; }
        public string Label { get; set; }
    }

    /// Playback screen. The overlay is hidden by default. Center opens it (focus on the transport bar) and,
    /// while open, activates the focused control. Media keys act directly and never open the overlay.
    /// Before the first picture the start card shows: Back cancels at once and OK opens the source picker.
    /// A failed start stays on the card with its reason and buttons; nothing throws the viewer back to Home.
    public sealed partial class PlayerViewModel : Screen
    {
        /// The overlay hides itself only while focus rests on the transport row and no panel is open.
        public const int AutoHideMs = 8000;
        /// Reopening the overlay within this window puts focus back where it was.
        public const int RestoreFocusMs = 30000;
        public const int SeekCommitDelayMs = 700;
        public const int SeekIndicatorMs = 1500;
        public const int SubtitlePreferenceWindowMs = 15000;
        /// Back with the overlay hidden shows "Press Back again to exit"; a second Back within this window exits.
        public const int ExitConfirmMs = 3000;
        /// The start card names its keys once the wait is noticeable.
        public const int StartHintDelayMs = 5000;
        /// The play glyph flashes this long when playback resumes.
        public const int ResumeFlashMs = 900;
        /// Previous restarts the episode when it is further in than this; earlier it goes to the previous episode.
        public const int RestartThresholdMs = 10000;
        public const string PreviewText = "Subtitle preview · Vista previa de subtítulos";

        private readonly PlayRequest request;
        private long lastInteractionMs;
        private long overlayClosedMs = long.MinValue / 2;
        private OverlayRow lastOverlayRow;
        private OverlayRow lastAction = OverlayRow.AudioSubtitles;
        private long? pendingSeekMs;
        /// A committed seek target shown until the player reports a position near it (or SeekLandMs passes): the seek
        /// bar used to snap back to the old position while the native seek was still running.
        private long? committedSeekMs;
        private long committedSeekUntilMs;
        public const int SeekLandMs = 8000;
        public const int SeekLandToleranceMs = 3000;
        private long lastSeekKeyMs;
        private long seekIndicatorUntilMs;
        private bool sawActive;
        private bool startReturned;
        private string preferenceVideoKey;
        private bool audioPreferenceApplied;
        private bool subtitlePreferenceApplied;
        private long preferenceStartedMs;
        private long lastPositionSeen = -1;
        private PlayerState lastStateSeen;
        private string lastCueSeen, lastStatusSeen, lastErrorSeen;
        private int lastStepSeen;
        private long startedMs;
        private long lastStartSecond = -1;
        private long exitArmedUntilMs;
        private long resumeFlashUntilMs;
        private MetaDetail lastMeta;
        private VideoInfo lastVideo;
        private bool lastBinge;

        public PlayerViewModel(PlayRequest request) => this.request = request;

        public override ScreenKind Kind => ScreenKind.Player;
        public override string Title => Snapshot.Title ?? CurrentMeta?.Summary?.Name ?? "Player";
        public PlayRequest Request => request;
        /// The player's state, read once per change instead of once per accessor: a layout pass read it a dozen times or
        /// more, and each read asks the engine for its full status (native state, position, tracks, subtitle menu). A new
        /// read happens on every Tick, every key and every state change (Shell.Version).
        public PlayerSnapshot Snapshot
        {
            get
            {
                var version = Shell?.Version ?? 0;
                if (cachedSnapshot == null || cachedVersion != version || cachedEpoch != snapshotEpoch)
                {
                    cachedSnapshot = Services.Player.Snapshot ?? new PlayerSnapshot();
                    cachedVersion = version;
                    cachedEpoch = snapshotEpoch;
                }
                return cachedSnapshot;
            }
        }

        private PlayerSnapshot cachedSnapshot;
        private int cachedVersion = -1;
        private int snapshotEpoch, cachedEpoch = -1;
        public PlayerSettings Settings => Services.Player.Settings;
        public bool OverlayVisible { get; private set; }
        public bool StyleMenuOpen => Panel == PlayerPanel.Style;
        public OverlayRow FocusedRow { get; private set; }
        public StyleRow FocusedStyleRow { get; private set; }
        public MoreRow FocusedMoreRow { get; private set; }
        public long? PendingSeekMs => pendingSeekMs;
        /// Small transport strip shown while seeking with the overlay hidden.
        public bool SeekIndicatorVisible => !OverlayVisible && (pendingSeekMs != null || NowMs < seekIndicatorUntilMs);
        public long DisplayPositionMs => pendingSeekMs ?? committedSeekMs ?? Snapshot.PositionMs;
        public bool IsSeries => CurrentVideo != null;

        // ---- start card, failure card, end card ----

        public PlayerCard Card { get; private set; }
        public IReadOnlyList<CardButton> CardButtons { get; private set; } = Array.Empty<CardButton>();
        public int CardIndex { get; private set; }
        /// Plain-words reason on the failure card, and the engine's own text under it (may be null).
        public string CardMessage { get; private set; }
        public string CardDetail { get; private set; }

        /// Before the first picture: the start card replaces the overlay and every control that cannot work yet.
        public bool Loading
        {
            get
            {
                if (Card != PlayerCard.None)
                    return false;
                var s = Snapshot;
                return s.StartStep > 0 || !s.SessionActive || s.State == PlayerState.Idle || (s.State == PlayerState.Preparing && s.PositionMs <= 0);
            }
        }

        public long StartElapsedMs => Math.Max(0, NowMs - startedMs);
        public bool StartHintVisible => StartElapsedMs >= StartHintDelayMs;
        public bool CanChooseSource => Services.SourceCatalog != null && CurrentMeta != null;

        // ---- pause feedback and the two-step exit ----

        /// Paused with the overlay hidden: the transport strip stays up with the title and time left, so a frozen
        /// picture never looks like a stall.
        public bool PauseStripVisible => !OverlayVisible && !Loading && Card == PlayerCard.None && Snapshot.State == PlayerState.Paused;
        public bool ResumeFlashVisible => NowMs < resumeFlashUntilMs;
        public bool ExitHintVisible => OverlayVisible && NowMs < exitArmedUntilMs;

        public IReadOnlyList<OverlayRow> Rows
        {
            get
            {
                var rows = new List<OverlayRow> { OverlayRow.Transport };
                if (IsSeries && CurrentMeta != null)
                    rows.Add(OverlayRow.Episodes);
                if (NextVideo != null)
                    rows.Add(OverlayRow.NextEpisode);
                rows.Add(OverlayRow.AudioSubtitles);
                if (CanChooseSource)
                    rows.Add(OverlayRow.Sources);
                rows.Add(OverlayRow.More);
                return rows;
            }
        }

        /// The button row above the seek bar.
        public IReadOnlyList<OverlayRow> Actions => Rows.Where(r => r != OverlayRow.Transport).ToList();

        public static IReadOnlyList<StyleRow> StyleRows { get; } = (StyleRow[])Enum.GetValues(typeof(StyleRow));

        public IReadOnlyList<MoreRow> MoreRows =>
            IsSeries ? new[] { MoreRow.Restart, MoreRow.Binge, MoreRow.StopAfter } : new[] { MoreRow.Restart };

        /// The text the subtitle layer draws now: the live cue, or a sample line while the settings panel is open.
        public string SubtitleText
        {
            get
            {
                var text = Snapshot.SubtitleText;
                if (string.IsNullOrEmpty(text) && StyleMenuOpen)
                    return PreviewText;
                return text;
            }
        }

        /// Pixels above the bottom edge for the subtitle block, including the raise while the overlay is open.
        public int SubtitleBottom
        {
            get
            {
                var style = Settings.SubtitleStyle;
                var raised = (OverlayVisible || PauseStripVisible) && style.RaiseWithOverlay;
                return style.BottomOffset + (raised ? SubtitleStyle.OverlayRaise : 0);
            }
        }

        /// Second line of an overlay button (what it would change or show), or "".
        public string RowValue(OverlayRow row)
        {
            var s = Snapshot;
            switch (row)
            {
                case OverlayRow.Transport: return s.State == PlayerState.Paused ? "Paused" : "Playing";
                case OverlayRow.Sources: return string.IsNullOrEmpty(s.SourceLabel) ? "" : s.SourceLabel;
                case OverlayRow.NextEpisode: return NextVideo == null ? "" : UiFormat.EpisodeColon(NextVideo) + " · " + NextVideo.Title;
                case OverlayRow.Episodes: return CurrentVideo == null ? "" : "Now: " + UiFormat.EpisodeColon(CurrentVideo);
                case OverlayRow.AudioSubtitles:
                    var audio = s.AudioTracks.Count == 0 ? null : UiFormat.TrackLabel(s.AudioTracks.FirstOrDefault(t => t.Index == s.SelectedAudio));
                    var subs = UiFormat.SubtitleLabel(s.Subtitles.FirstOrDefault(o => o.Id == s.SelectedSubtitleId));
                    return audio == null ? "Subtitles: " + subs : audio + " / " + subs;
                default: return "";
            }
        }

        public static string RowLabel(OverlayRow row)
        {
            switch (row)
            {
                case OverlayRow.Transport: return "Play / Pause";
                case OverlayRow.NextEpisode: return "Next episode";
                case OverlayRow.AudioSubtitles: return "Audio & subtitles";
                case OverlayRow.Sources: return "Source";
                default: return row.ToString();
            }
        }

        public static string MoreLabel(MoreRow row)
        {
            switch (row)
            {
                case MoreRow.Restart: return "Start from beginning";
                case MoreRow.StopAfter: return "Stop after this episode";
                default: return "Binge";
            }
        }

        public string MoreValue(MoreRow row)
        {
            switch (row)
            {
                case MoreRow.Binge: return Snapshot.Binge ? "On" : "Off";
                case MoreRow.StopAfter: return Snapshot.StopAfterCurrent ? "On" : "Off";
                default: return "";
            }
        }

        public static string StyleLabel(StyleRow row)
        {
            switch (row)
            {
                case StyleRow.Size: return "Size";
                case StyleRow.Timing: return "Timing";
                case StyleRow.Color: return "Text colour";
                case StyleRow.Outline: return "Outline & shadow";
                case StyleRow.Background: return "Background box";
                case StyleRow.Opacity: return "Box opacity";
                case StyleRow.Position: return "Position";
                case StyleRow.RaiseWithOverlay: return "Raise above overlay";
                case StyleRow.Bold: return "Font weight";
                case StyleRow.Reset: return "Reset to default";
                default: return row.ToString();
            }
        }

        /// Named steps instead of raw pixels: the agent API may still store any size or offset, shown by its nearest name.
        public static readonly IReadOnlyList<KeyValuePair<string, int>> SizePresets = new[]
        {
            new KeyValuePair<string, int>("Small", 30), new KeyValuePair<string, int>("Medium", 42),
            new KeyValuePair<string, int>("Large", 54), new KeyValuePair<string, int>("Extra large", 66)
        };

        public static readonly IReadOnlyList<KeyValuePair<string, int>> PositionPresets = new[]
        {
            new KeyValuePair<string, int>("Low", 30), new KeyValuePair<string, int>("Standard", 90), new KeyValuePair<string, int>("High", 210)
        };

        internal static int NearestPreset(IReadOnlyList<KeyValuePair<string, int>> presets, int value)
        {
            var best = 0;
            for (var i = 1; i < presets.Count; i++)
                if (Math.Abs(presets[i].Value - value) < Math.Abs(presets[best].Value - value))
                    best = i;
            return best;
        }

        /// "In sync", "Subtitles 0.5 s later", "Subtitles 0.25 s earlier".
        public static string TimingLabel(int offsetMs)
        {
            if (offsetMs == 0)
                return "In sync";
            var seconds = (Math.Abs(offsetMs) / 1000.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            return "Subtitles " + seconds + " s " + (offsetMs > 0 ? "later" : "earlier");
        }

        public string StyleValue(StyleRow row)
        {
            var st = Settings.SubtitleStyle;
            switch (row)
            {
                case StyleRow.Size: return SizePresets[NearestPreset(SizePresets, st.Size)].Key;
                case StyleRow.Timing:
                    return Snapshot.SubtitleOffsetMs == 0 && Snapshot.SubtitleSync != null ? Snapshot.SubtitleSync : TimingLabel(Snapshot.SubtitleOffsetMs);
                case StyleRow.Color: return st.ColorName;
                case StyleRow.Outline: return st.Outline ? "On" : "Off";
                case StyleRow.Background: return st.Background.ToString();
                case StyleRow.Opacity: return st.Background == SubtitleBackground.Translucent ? st.BackgroundOpacity + "%" : "—";
                case StyleRow.Position: return PositionPresets[NearestPreset(PositionPresets, st.BottomOffset)].Key;
                case StyleRow.RaiseWithOverlay: return st.RaiseWithOverlay ? "On" : "Off";
                case StyleRow.Bold: return st.Bold ? "Bold" : "Regular";
                default: return "";
            }
        }

        /// What OK does on the focused settings row, when it is not obvious from the arrows.
        public static string StyleHint(StyleRow row)
        {
            switch (row)
            {
                case StyleRow.Timing: return "Left: earlier  ·  Right: later  ·  OK: reset (also any automatic sync)";
                case StyleRow.Reset: return "OK: reset every subtitle setting";
                default: return "Left/Right: change  ·  Back: done";
            }
        }

        protected internal override void OnPushed()
        {
            lastInteractionMs = NowMs;
            startedMs = NowMs;
            if (request != null)
                Start(request);
        }

        private void Start(PlayRequest play)
        {
            Card = PlayerCard.None;
            sawActive = false;
            startedMs = NowMs;
            CloseOverlay();
            startReturned = false;
            Run(token => Services.Player.PlayAsync(play, token), () => startReturned = true, error => ShowFailure(error.Message));
        }

        /// The start (or the stream) failed: stay here and say why, with a way forward under the thumb.
        private void ShowFailure(string raw)
        {
            CloseOverlay();
            Card = PlayerCard.Failed;
            CardMessage = FriendlyError(raw, out var detail);
            CardDetail = detail;
            var buttons = new List<CardButton> { new CardButton { Action = CardAction.Retry, Label = "Try again" } };
            if (CanChooseSource)
                buttons.Add(new CardButton { Action = CardAction.ChooseSource, Label = "Choose another source" });
            buttons.Add(new CardButton { Action = CardAction.Back, Label = "Back" });
            CardButtons = buttons;
            CardIndex = 0;
            Changed();
        }

        private void ShowEnded()
        {
            CloseOverlay();
            Card = PlayerCard.Ended;
            CardMessage = null;
            CardDetail = null;
            var buttons = new List<CardButton>();
            var next = NextVideo;
            if (next != null)
                buttons.Add(new CardButton { Action = CardAction.NextEpisode, Label = "Next episode · " + UiFormat.EpisodeColon(next) });
            buttons.Add(new CardButton { Action = CardAction.Episodes, Label = "Episodes" });
            buttons.Add(new CardButton { Action = CardAction.Back, Label = "Back" });
            CardButtons = buttons;
            CardIndex = 0;
            Changed();
        }

        /// The engine's failure text in plain words; `detail` keeps the first engine reason for the small line.
        internal static string FriendlyError(string raw, out string detail)
        {
            raw = (raw ?? "").Trim();
            detail = null;
            if (raw.Length == 0)
                return "Playback could not start.";
            if (raw.StartsWith("All", StringComparison.Ordinal) && raw.IndexOf("candidate streams failed", StringComparison.Ordinal) > 0)
            {
                var at = raw.IndexOf(':');
                detail = at > 0 ? raw.Substring(at + 1).Split(';')[0].Trim() : null;
                return "None of the sources that were tried could be played.";
            }
            if (raw.StartsWith("No viable", StringComparison.Ordinal))
                return "No playable source was found for this title.";
            if (raw.StartsWith("Stream startup deadline exceeded", StringComparison.Ordinal))
            {
                var at = raw.IndexOf(':');
                detail = at > 0 ? raw.Substring(at + 1).Split(';')[0].Trim() : null;
                return "The sources took too long to answer.";
            }
            if (raw.StartsWith("You are caught up", StringComparison.Ordinal) || raw.EndsWith(".", StringComparison.Ordinal) && raw.Length < 90)
                return raw;
            detail = raw;
            return "Playback could not start.";
        }

        private PlayRequest RetryRequest()
        {
            var meta = CurrentMeta;
            if (meta == null)
                return request;
            var video = CurrentVideo;
            var sameAsRequest = request != null && request.Video?.Id == video?.Id;
            return new PlayRequest
            {
                Meta = meta, Video = video, Mode = lastBinge ? PlaybackMode.Binge : request?.Mode ?? PlaybackMode.Single,
                ResumeMs = sameAsRequest ? Math.Max(request.ResumeMs, lastPositionSeen > 0 ? lastPositionSeen : 0) : Math.Max(0, lastPositionSeen),
                Quality = request?.Quality
            };
        }

        private bool HandleCardKey(KeyPress key)
        {
            switch (key.Key)
            {
                case RemoteKey.Left:
                case RemoteKey.Up:
                    CardIndex = Math.Max(0, CardIndex - 1);
                    return true;
                case RemoteKey.Right:
                case RemoteKey.Down:
                    CardIndex = Math.Min(CardButtons.Count - 1, CardIndex + 1);
                    return true;
                case RemoteKey.Back:
                case RemoteKey.Stop:
                    Shell.Pop(this);
                    return true;
                case RemoteKey.Play:
                case RemoteKey.PlayPause:
                case RemoteKey.Select:
                    if (CardButtons.Count > 0)
                        ActivateCard(key.Key == RemoteKey.Select ? CardButtons[CardIndex].Action : CardButtons[0].Action);
                    return true;
                default:
                    return true;
            }
        }

        private void ActivateCard(CardAction action)
        {
            switch (action)
            {
                case CardAction.Retry:
                    var retry = RetryRequest();
                    if (retry != null)
                        Start(retry);
                    break;
                case CardAction.ChooseSource:
                    ChooseSource();
                    break;
                case CardAction.NextEpisode:
                    var next = NextVideo;
                    if (next != null)
                        Start(new PlayRequest { Meta = CurrentMeta, Video = next, Mode = lastBinge ? PlaybackMode.Binge : PlaybackMode.Single });
                    break;
                case CardAction.Episodes:
                    var meta = CurrentMeta;
                    var below = Shell.Stack.Count >= 2 ? Shell.Stack[Shell.Stack.Count - 2] as DetailsViewModel : null;
                    if (meta?.Summary == null || below != null && below.Id == meta.Summary.Id)
                        Shell.Pop(this);
                    else
                        Shell.Replace(new DetailsViewModel(meta.Summary.Type, meta.Summary.Id, meta.Summary));
                    break;
                default:
                    Shell.Pop(this);
                    break;
            }
        }

        /// Start card or failure card: the picker takes this screen's place, and the pick starts a fresh player.
        private void ChooseSource()
        {
            var meta = CurrentMeta;
            if (meta == null || Services.SourceCatalog == null)
                return;
            var retry = RetryRequest();
            Services.Player.Stop();
            Shell.Replace(new SourcePickerViewModel(meta, CurrentVideo, retry?.ResumeMs ?? 0, retry?.Mode ?? PlaybackMode.Single, switching: false));
        }

        /// Nothing plays yet: Back (or Stop) cancels at once with no question, OK opens the source picker, and every
        /// other key waits, so no control that cannot work is ever offered.
        private bool HandleLoadingKey(KeyPress key)
        {
            switch (key.Key)
            {
                case RemoteKey.Back:
                case RemoteKey.Stop:
                    Exit();
                    return true;
                case RemoteKey.Select:
                    ChooseSource();
                    return true;
                default:
                    return true;
            }
        }

        private void Exit()
        {
            Services.Player.Stop();
            Shell.Pop(this);
        }

        public override bool HandleKey(KeyPress key)
        {
            snapshotEpoch++;
            if (Card != PlayerCard.None)
                return HandleCardKey(key);
            if (Loading)
                return HandleLoadingKey(key);
            var controls = Services.Player;
            var armed = key.AtMs < exitArmedUntilMs;
            if (key.Key != RemoteKey.Back)
                exitArmedUntilMs = 0;

            // Dedicated media keys: labeled action, overlay untouched.
            switch (key.Key)
            {
                case RemoteKey.Play: controls.Play(); return true;
                case RemoteKey.Pause: controls.Pause(); return true;
                case RemoteKey.PlayPause: controls.TogglePause(); return true;
                case RemoteKey.Stop:
                    Exit();
                    return true;
                case RemoteKey.Rewind: Seek(-1, key); return true;
                case RemoteKey.FastForward: Seek(1, key); return true;
                case RemoteKey.Next:
                    PlayNext();
                    return true;
                case RemoteKey.Previous:
                    Previous();
                    return true;
            }

            if (!OverlayVisible && HandleCreditsKey(key))
                return true;
            if (!OverlayVisible)
            {
                switch (key.Key)
                {
                    case RemoteKey.Select:
                        if (pendingSeekMs != null)
                        {
                            CommitSeek();
                            return true;
                        }
                        OpenOverlay();
                        return true;
                    case RemoteKey.Up:
                    case RemoteKey.Down:
                    case RemoteKey.Menu:
                    case RemoteKey.Info:
                        OpenOverlay();
                        return true;
                    case RemoteKey.Left: Seek(-1, key); return true;
                    case RemoteKey.Right: Seek(1, key); return true;
                    case RemoteKey.Back:
                        // First Back shows where you are and how to leave; the second one leaves. Progress is saved.
                        OpenOverlay();
                        FocusedRow = OverlayRow.Transport;   // not a remembered button: the overlay must hide by itself again
                        exitArmedUntilMs = key.AtMs + ExitConfirmMs;
                        return true;
                    default:
                        return true;
                }
            }

            lastInteractionMs = key.AtMs;
            if (key.Key == RemoteKey.Back && armed && Panel == PlayerPanel.None)
            {
                Exit();
                return true;
            }
            if (Panel != PlayerPanel.None)
                return HandlePanelKey(key);
            return HandleOverlayKey(key);
        }

        private void PlayNext()
        {
            if (NextVideo == null)
            {
                if (IsSeries)
                    Shell.ShowToast("This is the latest episode.");
                return;
            }
            Run(token => Services.Player.PlayNextAsync(token), null, error => Shell.ShowToast("Couldn't start the next episode."));
        }

        /// Previous: back to the start of this episode, or (within its first seconds) the episode before it.
        private void Previous()
        {
            var previous = PreviousVideo;
            if (Snapshot.PositionMs > RestartThresholdMs || previous == null)
            {
                Restart();
                return;
            }
            var play = new PlayRequest { Meta = CurrentMeta, Video = previous, Mode = Snapshot.Binge ? PlaybackMode.Binge : PlaybackMode.Single };
            Shell.RunDetached(token => Services.Player.PlayAsync(play, token));
        }

        private void Restart()
        {
            pendingSeekMs = null;
            HoldSeekTarget(0);
            seekIndicatorUntilMs = NowMs + SeekIndicatorMs;
            Services.Player.SeekTo(0);
        }

        /// Auto-hide never closes an open panel, and never while focus is off the transport row.
        public bool CanAutoHide => Panel == PlayerPanel.None && FocusedRow == OverlayRow.Transport;

        private void OpenOverlay()
        {
            OverlayVisible = true;
            Panel = PlayerPanel.None;
            FocusedRow = NowMs - overlayClosedMs <= RestoreFocusMs && Rows.Contains(lastOverlayRow) ? lastOverlayRow : OverlayRow.Transport;
            LoadExtras();
            lastInteractionMs = NowMs;
        }

        public void CloseOverlay()
        {
            if (OverlayVisible)
            {
                overlayClosedMs = NowMs;
                lastOverlayRow = FocusedRow;
            }
            OverlayVisible = false;
            Panel = PlayerPanel.None;
            exitArmedUntilMs = 0;
        }

        private bool HandleOverlayKey(KeyPress key)
        {
            var actions = Actions;
            var index = actions.ToList().IndexOf(FocusedRow);
            switch (key.Key)
            {
                case RemoteKey.Back:
                    CloseOverlay();
                    return true;
                case RemoteKey.Up:
                    if (FocusedRow == OverlayRow.Transport && actions.Count > 0)
                        FocusedRow = actions.Contains(lastAction) ? lastAction : actions[0];
                    return true;
                case RemoteKey.Down:
                    if (FocusedRow != OverlayRow.Transport)
                    {
                        lastAction = FocusedRow;
                        FocusedRow = OverlayRow.Transport;
                    }
                    return true;
                case RemoteKey.Left:
                case RemoteKey.Right:
                    var direction = key.Key == RemoteKey.Left ? -1 : 1;
                    if (FocusedRow == OverlayRow.Transport || index < 0)
                        Seek(direction, key);
                    else
                    {
                        FocusedRow = actions[Math.Max(0, Math.Min(actions.Count - 1, index + direction))];
                        lastAction = FocusedRow;
                    }
                    return true;
                case RemoteKey.Select:
                    Activate(FocusedRow);
                    return true;
                default:
                    return true;
            }
        }

        private void Activate(OverlayRow row)
        {
            switch (row)
            {
                case OverlayRow.Transport:
                    if (pendingSeekMs != null)
                        CommitSeek();
                    else
                        Services.Player.TogglePause();
                    break;
                case OverlayRow.NextEpisode:
                    PlayNext();
                    break;
                case OverlayRow.Episodes:
                    OpenEpisodesPanel();
                    break;
                case OverlayRow.AudioSubtitles:
                    OpenTracksPanel();
                    break;
                case OverlayRow.More:
                    Panel = PlayerPanel.More;
                    FocusedMoreRow = MoreRows[0];
                    break;
                case OverlayRow.Sources:
                    var s = Snapshot;
                    var meta = CurrentMeta;
                    if (meta != null)
                        Shell.Push(new SourcePickerViewModel(meta, CurrentVideo, s.PositionMs, PlaybackMode.Single, switching: true));
                    break;
            }
        }

        private bool MoreKey(KeyPress key)
        {
            var rows = MoreRows.ToList();
            var index = Math.Max(0, rows.IndexOf(FocusedMoreRow));
            switch (key.Key)
            {
                case RemoteKey.Up: FocusedMoreRow = rows[Math.Max(0, index - 1)]; break;
                case RemoteKey.Down: FocusedMoreRow = rows[Math.Min(rows.Count - 1, index + 1)]; break;
                case RemoteKey.Left:
                case RemoteKey.Right:
                case RemoteKey.Select:
                    switch (FocusedMoreRow)
                    {
                        case MoreRow.Restart:
                            if (key.Key == RemoteKey.Select)
                            {
                                Restart();
                                CloseOverlay();
                            }
                            break;
                        case MoreRow.Binge: Services.Player.SetBinge(!Snapshot.Binge); break;
                        case MoreRow.StopAfter: Services.Player.SetStopAfterCurrent(!Snapshot.StopAfterCurrent); break;
                    }
                    break;
            }
            return true;
        }

        private bool StyleKey(KeyPress key)
        {
            var index = (int)FocusedStyleRow;
            switch (key.Key)
            {
                case RemoteKey.Up:
                    FocusedStyleRow = (StyleRow)Math.Max(0, index - 1);
                    return true;
                case RemoteKey.Down:
                    FocusedStyleRow = (StyleRow)Math.Min(StyleRows.Count - 1, index + 1);
                    return true;
                case RemoteKey.Left:
                    AdjustStyle(FocusedStyleRow, -1, key);
                    return true;
                case RemoteKey.Right:
                    AdjustStyle(FocusedStyleRow, 1, key);
                    return true;
                case RemoteKey.Select:
                    if (FocusedStyleRow == StyleRow.Timing)
                        Services.Player.ResetSubtitleTiming();
                    else
                        AdjustStyle(FocusedStyleRow, 1, key, wrap: true);
                    return true;
                default:
                    return true;
            }
        }

        private void AdjustStyle(StyleRow row, int direction, KeyPress key, bool wrap = false)
        {
            if (row == StyleRow.Timing)
            {
                var step = key.RepeatCount >= 4 ? 1000 : PlayerSettings.SubtitleOffsetStepMs;
                var offset = SubtitleStyle.Clamp(Snapshot.SubtitleOffsetMs + direction * step, -PlayerSettings.MaxSubtitleOffsetMs, PlayerSettings.MaxSubtitleOffsetMs);
                Services.Player.SetSubtitleOffset(offset);
                return;
            }
            int Step(IReadOnlyList<KeyValuePair<string, int>> presets, int value)
            {
                var at = NearestPreset(presets, value) + direction;
                return presets[wrap ? Wrap(at, presets.Count) : Math.Max(0, Math.Min(presets.Count - 1, at))].Value;
            }
            Settings.UpdateSubtitleStyle(st =>
            {
                switch (row)
                {
                    case StyleRow.Size:
                        st.Size = Step(SizePresets, st.Size);
                        break;
                    case StyleRow.Color:
                        var colors = SubtitleStyle.Colors;
                        var at = colors.Select((c, i) => new { c, i }).FirstOrDefault(x => string.Equals(x.c.Value, st.TextColor, StringComparison.OrdinalIgnoreCase))?.i ?? 0;
                        st.TextColor = colors[Wrap(at + direction, colors.Count)].Value;
                        break;
                    case StyleRow.Outline:
                        st.Outline = !st.Outline;
                        break;
                    case StyleRow.Background:
                        st.Background = (SubtitleBackground)Wrap((int)st.Background + direction, 3);
                        break;
                    case StyleRow.Opacity:
                        st.BackgroundOpacity = SubtitleStyle.Clamp(st.BackgroundOpacity + direction * 10, 10, 100);
                        if (st.Background == SubtitleBackground.None)
                            st.Background = SubtitleBackground.Translucent;
                        break;
                    case StyleRow.Position:
                        st.BottomOffset = Step(PositionPresets, st.BottomOffset);
                        break;
                    case StyleRow.RaiseWithOverlay:
                        st.RaiseWithOverlay = !st.RaiseWithOverlay;
                        break;
                    case StyleRow.Bold:
                        st.Bold = !st.Bold;
                        break;
                    case StyleRow.Reset:
                        var fresh = new SubtitleStyle();
                        st.Size = fresh.Size;
                        st.TextColor = fresh.TextColor;
                        st.Outline = fresh.Outline;
                        st.Background = fresh.Background;
                        st.BackgroundOpacity = fresh.BackgroundOpacity;
                        st.BottomOffset = fresh.BottomOffset;
                        st.RaiseWithOverlay = fresh.RaiseWithOverlay;
                        st.Bold = fresh.Bold;
                        break;
                }
            });
            if (row == StyleRow.Reset)
                Services.Player.ResetSubtitleTiming();
        }

        /// Step grows while the key is held: 10 s, then 30 s, 60 s, 120 s.
        public static long SeekStepMs(int repeatCount) =>
            repeatCount < 3 ? 10_000 : repeatCount < 8 ? 30_000 : repeatCount < 15 ? 60_000 : 120_000;

        private void Seek(int direction, KeyPress key)
        {
            var s = Snapshot;
            var basis = pendingSeekMs ?? committedSeekMs ?? s.PositionMs;
            var target = basis + direction * SeekStepMs(key.RepeatCount);
            if (s.DurationMs > 0)
                target = Math.Min(target, Math.Max(0, s.DurationMs - 5000));
            pendingSeekMs = Math.Max(0, target);
            lastSeekKeyMs = key.AtMs;
            seekIndicatorUntilMs = key.AtMs + SeekIndicatorMs;
        }

        private void CommitSeek()
        {
            if (pendingSeekMs == null)
                return;
            var target = pendingSeekMs.Value;
            pendingSeekMs = null;
            HoldSeekTarget(target);
            seekIndicatorUntilMs = NowMs + SeekIndicatorMs;
            Services.Player.SeekTo(target);
        }

        private void HoldSeekTarget(long target)
        {
            var s = Snapshot;
            committedSeekMs = target;
            committedSeekUntilMs = NowMs + SeekLandMs;
            committedAtMs = NowMs;
            committedFromMs = s.PositionMs;
            committedVideo = s.Video?.Id;
        }

        private long committedAtMs, committedFromMs;
        private string committedVideo;

        /// The player has acted on the committed seek: it reports a position near the target, or one that jumped away
        /// from where plain playback would be (a keyframe seek can land a few seconds off the target), or another
        /// episode is playing, or the wait ran out.
        private bool SeekLanded(PlayerSnapshot s, long nowMs)
        {
            if (Math.Abs(s.PositionMs - committedSeekMs.Value) <= SeekLandToleranceMs || nowMs >= committedSeekUntilMs || s.Video?.Id != committedVideo)
                return true;
            var expected = committedFromMs + (s.State == PlayerState.Playing ? nowMs - committedAtMs : 0);
            return Math.Abs(s.PositionMs - expected) > SeekLandToleranceMs;
        }

        protected internal override void Tick(long nowMs)
        {
            snapshotEpoch++;
            if (Card != PlayerCard.None)
            {
                // Playback started from elsewhere (the queue, the agent API) while a card was up: show the picture.
                if (!Snapshot.SessionActive)
                    return;
                Card = PlayerCard.None;
                sawActive = true;
                Changed();
            }
            var changed = false;
            if (pendingSeekMs != null && nowMs - lastSeekKeyMs >= SeekCommitDelayMs)
            {
                CommitSeek();
                changed = true;
            }
            if (OverlayVisible && CanAutoHide && nowMs - lastInteractionMs >= AutoHideMs && nowMs >= exitArmedUntilMs)
            {
                CloseOverlay();
                changed = true;
            }
            if (exitArmedUntilMs > 0 && nowMs >= exitArmedUntilMs)
            {
                exitArmedUntilMs = 0;   // the hint leaves; the overlay stays until its own timeout
                lastInteractionMs = Math.Max(lastInteractionMs, nowMs - AutoHideMs + 2000);
                changed = true;
            }
            if (resumeFlashUntilMs > 0 && nowMs >= resumeFlashUntilMs)
            {
                resumeFlashUntilMs = 0;
                changed = true;
            }

            var s = Snapshot;
            if (s.SessionActive)
            {
                sawActive = true;
                lastMeta = s.Meta ?? lastMeta;
                lastVideo = s.Meta != null ? s.Video : lastVideo;
                lastBinge = s.Binge;
            }
            else if (sawActive || startReturned && !string.IsNullOrEmpty(s.Error))
            {
                // The session ended. A failure says so here; a finished episode offers the next one; anything else
                // (stopped elsewhere, a finished movie, stop-after) leaves the player.
                if (s.State != PlayerState.Completed && !string.IsNullOrEmpty(s.Error))
                    ShowFailure(s.Error);
                else if (s.State == PlayerState.Completed && IsSeries && NextVideo != null)
                    ShowEnded();
                else
                    Shell.Pop(this);
                return;
            }

            if (Loading)
            {
                var second = (nowMs - startedMs) / 1000;
                if (second != lastStartSecond)
                {
                    lastStartSecond = second;
                    changed = true;
                }
            }

            ApplyTrackPreferences(s, nowMs);
            if (TickCredits(s, nowMs))
                changed = true;

            if (committedSeekMs != null && SeekLanded(s, nowMs))
            {
                committedSeekMs = null;   // the player has landed: show its own position again
                changed = true;
            }
            if (s.PositionMs / 1000 != lastPositionSeen / 1000 || s.State != lastStateSeen)
            {
                if (lastStateSeen == PlayerState.Paused && s.State == PlayerState.Playing)
                    resumeFlashUntilMs = nowMs + ResumeFlashMs;
                lastPositionSeen = s.PositionMs;
                lastStateSeen = s.State;
                changed = true;
            }
            // What is drawn besides the clock: a subtitle cue appearing or clearing between seconds (and while paused),
            // and the start card's step and status line. These used to wait for the next whole second or state change.
            if (s.SubtitleText != lastCueSeen || s.StatusText != lastStatusSeen || s.StartStep != lastStepSeen || s.Error != lastErrorSeen)
            {
                lastCueSeen = s.SubtitleText;
                lastStatusSeen = s.StatusText;
                lastStepSeen = s.StartStep;
                lastErrorSeen = s.Error;
                changed = true;
            }
            if (changed)
                Changed();
        }

        /// Applies the per-show (or global default) audio/subtitle languages once per video, when its tracks appear.
        private void ApplyTrackPreferences(PlayerSnapshot s, long nowMs)
        {
            if (!s.SessionActive)
                return;
            var showId = s.Meta?.Summary?.Id;
            var videoKey = (s.Video?.Id ?? showId) ?? "";
            if (videoKey != preferenceVideoKey)
            {
                preferenceVideoKey = videoKey;
                audioPreferenceApplied = false;
                subtitlePreferenceApplied = false;
                preferenceStartedMs = nowMs;
            }
            var pref = Settings.GetTrackPreference(showId, UiFormat.IsAnime(s.Meta));
            var controls = Services.Player;
            if (!audioPreferenceApplied && s.AudioTracks.Count > 0)
            {
                audioPreferenceApplied = true;
                var current = s.AudioTracks.FirstOrDefault(t => t.Index == s.SelectedAudio);
                if (current == null || !PlayerSettings.LanguageMatches(current.Language, pref.AudioLanguage))
                {
                    var match = s.AudioTracks.FirstOrDefault(t => PlayerSettings.LanguageMatches(t.Language, pref.AudioLanguage));
                    if (match != null)
                        controls.SelectAudio(match.Index);
                }
            }
            if (!subtitlePreferenceApplied)
            {
                if (pref.SubtitleLanguage == "off")
                {
                    subtitlePreferenceApplied = true;
                    if (s.SelectedSubtitleId != null)
                        controls.SelectSubtitle(null);
                    return;
                }
                var selected = s.Subtitles.FirstOrDefault(o => o.Id == s.SelectedSubtitleId);
                if (selected != null && PlayerSettings.LanguageMatches(selected.Language, pref.SubtitleLanguage))
                {
                    subtitlePreferenceApplied = true;
                    return;
                }
                var match = s.Subtitles.Where(o => PlayerSettings.LanguageMatches(o.Language, pref.SubtitleLanguage))
                    .OrderBy(o => o.External ? 1 : 0).FirstOrDefault();
                if (match != null)
                {
                    controls.SelectSubtitle(match.Id);
                    subtitlePreferenceApplied = true;
                }
                else if (nowMs - preferenceStartedMs >= SubtitlePreferenceWindowMs)
                {
                    subtitlePreferenceApplied = true;   // external subtitles had their chance to arrive
                }
            }
        }

        private static int Wrap(int value, int count) => count <= 0 ? 0 : ((value % count) + count) % count;
    }
}
