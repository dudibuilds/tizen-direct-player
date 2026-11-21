using System;
using Ru7100.Core.Playback;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Ru7100.Core.Ui
{
    public enum SubtitleBackground { None, Translucent, Solid }

    /// How the app-drawn subtitle layer draws every cue (external and embedded alike).
    public sealed class SubtitleStyle
    {
        public const int MinSize = 24;
        public const int MaxSize = 72;
        public const int SizeStep = 6;
        public const int MinBottomOffset = 30;
        public const int MaxBottomOffset = 450;
        public const int BottomOffsetStep = 30;
        public const int OverlayRaise = 260;   // px the subtitles move up while the overlay is open: clear of the button row

        public static readonly IReadOnlyList<KeyValuePair<string, string>> Colors = new[]
        {
            new KeyValuePair<string, string>("White", "#FFFFFF"),
            new KeyValuePair<string, string>("Yellow", "#FFE14D"),
            new KeyValuePair<string, string>("Cyan", "#5EE7F2"),
            new KeyValuePair<string, string>("Green", "#7CF29A"),
            new KeyValuePair<string, string>("Pink", "#FF9AD5"),
            new KeyValuePair<string, string>("Grey", "#C8CCD2")
        };

        public int Size { get; set; } = 42;
        public string TextColor { get; set; } = "#FFFFFF";
        public bool Outline { get; set; } = true;
        public SubtitleBackground Background { get; set; } = SubtitleBackground.None;
        public int BackgroundOpacity { get; set; } = 60;   // percent, used by Translucent
        public int BottomOffset { get; set; } = 90;        // px above the bottom edge at 1080p
        public bool RaiseWithOverlay { get; set; } = true;
        public bool Bold { get; set; }

        public string ColorName => Colors.FirstOrDefault(c => string.Equals(c.Value, TextColor, StringComparison.OrdinalIgnoreCase)).Key ?? TextColor;

        public SubtitleStyle Clone() => (SubtitleStyle)MemberwiseClone();

        public SubtitleStyle Normalized()
        {
            var copy = Clone();
            copy.Size = Clamp(copy.Size, MinSize, MaxSize);
            copy.BackgroundOpacity = Clamp(copy.BackgroundOpacity, 10, 100);
            copy.BottomOffset = Clamp(copy.BottomOffset, MinBottomOffset, MaxBottomOffset);
            if (string.IsNullOrEmpty(copy.TextColor) || copy.TextColor[0] != '#')
                copy.TextColor = "#FFFFFF";
            return copy;
        }

        /// Background box colour as #RRGGBBAA, or null when there is no box.
        public string BoxColor
        {
            get
            {
                switch (Background)
                {
                    case SubtitleBackground.Solid: return "#000000FF";
                    case SubtitleBackground.Translucent: return "#000000" + ((int)Math.Round(BackgroundOpacity * 2.55)).ToString("X2");
                    default: return null;
                }
            }
        }

        internal static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
    }

    public sealed class TrackPreference
    {
        public string AudioLanguage { get; set; }      // ISO 639 code as reported by the track, e.g. "eng", "jpn"
        public string SubtitleLanguage { get; set; }   // ISO code, or "off"
    }

    /// Per-user player settings: subtitle style and last-used audio/subtitle language per show.
    /// One instance is shared by the overlay and the agent API; persisted through IKeyValueStore.
    public sealed class PlayerSettings
    {
        public const int MaxSubtitleOffsetMs = 30_000;   // parity F14 clamp
        public const int SubtitleOffsetStepMs = 250;
        public const string StyleKey = "ui-subtitle-style";
        public const string TracksKey = "ui-track-preferences";
        public const string MotionKey = "ui-reduced-motion";
        public const string AutoplayPreviewsKey = "ui-autoplay-previews";
        public const string PreviewSoundKey = "ui-preview-sound";
        public const string LaunchSoundKey = "ui-launch-sound";

        private readonly IKeyValueStore store;
        private readonly object gate = new object();
        private SubtitleStyle style = new SubtitleStyle();
        private Dictionary<string, TrackPreference> perShow = new Dictionary<string, TrackPreference>();
        private Task lastWrite = Task.CompletedTask;

        public PlayerSettings(IKeyValueStore store = null) => this.store = store;

        public event EventHandler Changed;

        public SubtitleStyle SubtitleStyle
        {
            get { lock (gate) return style.Clone(); }
        }

        /// Reduced motion: no animated focus, scrolling or transitions (UiAnimator jumps).
        public bool ReducedMotion { get; private set; }

        public void SetReducedMotion(bool value)
        {
            ReducedMotion = value;
            Persist(MotionKey, value ? "true" : "false");
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// Trailer previews start by themselves after a short dwell on a title (default on).
        public bool AutoplayPreviews { get; private set; } = true;
        /// Trailer previews play with sound, faded in to the preview level (default on); off starts them muted.
        public bool PreviewSound { get; private set; } = true;

        public void SetAutoplayPreviews(bool value)
        {
            AutoplayPreviews = value;
            Persist(AutoplayPreviewsKey, value ? "true" : "false");
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SetPreviewSound(bool value)
        {
            PreviewSound = value;
            Persist(PreviewSoundKey, value ? "true" : "false");
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// The launch intro's short sound on a cold start (default on).
        public bool LaunchSound { get; private set; } = true;

        public void SetLaunchSound(bool value)
        {
            LaunchSound = value;
            Persist(LaunchSoundKey, value ? "true" : "false");
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public static TrackPreference DefaultFor(bool isAnime) => isAnime
            ? new TrackPreference { AudioLanguage = Languages.AnimeAudio, SubtitleLanguage = Languages.AnimeSubtitles }
            : new TrackPreference { AudioLanguage = Languages.Normalize(Languages.Audio), SubtitleLanguage = Languages.Normalize(Languages.Subtitles) };

        public async Task LoadAsync(CancellationToken token)
        {
            if (store == null)
                return;
            // The six reads are independent: issue them together (each is a file read on a worker on the TV).
            var styleRead = store.ReadAsync(StyleKey, token);
            var tracksRead = store.ReadAsync(TracksKey, token);
            var motionRead = store.ReadAsync(MotionKey, token);
            var autoplayRead = store.ReadAsync(AutoplayPreviewsKey, token);
            var soundRead = store.ReadAsync(PreviewSoundKey, token);
            var launchRead = store.ReadAsync(LaunchSoundKey, token);
            var styleJson = await styleRead.ConfigureAwait(false);
            var tracksJson = await tracksRead.ConfigureAwait(false);
            ReducedMotion = await motionRead.ConfigureAwait(false) == "true";
            AutoplayPreviews = await autoplayRead.ConfigureAwait(false) != "false";
            PreviewSound = await soundRead.ConfigureAwait(false) != "false";
            LaunchSound = await launchRead.ConfigureAwait(false) != "false";
            lock (gate)
            {
                try
                {
                    if (!string.IsNullOrEmpty(styleJson))
                        style = (JsonConvert.DeserializeObject<SubtitleStyle>(styleJson) ?? new SubtitleStyle()).Normalized();
                }
                catch (JsonException)
                {
                    style = new SubtitleStyle();
                }
                try
                {
                    if (!string.IsNullOrEmpty(tracksJson))
                        perShow = JsonConvert.DeserializeObject<Dictionary<string, TrackPreference>>(tracksJson) ?? new Dictionary<string, TrackPreference>();
                }
                catch (JsonException)
                {
                    perShow = new Dictionary<string, TrackPreference>();
                }
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SetSubtitleStyle(SubtitleStyle value)
        {
            string json;
            lock (gate)
            {
                style = (value ?? new SubtitleStyle()).Normalized();
                json = JsonConvert.SerializeObject(style);
            }
            Persist(StyleKey, json);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void UpdateSubtitleStyle(Action<SubtitleStyle> change)
        {
            var copy = SubtitleStyle;
            change(copy);
            SetSubtitleStyle(copy);
        }

        /// Per-show choice if remembered, otherwise the global default.
        public TrackPreference GetTrackPreference(string metaId, bool isAnime)
        {
            lock (gate)
            {
                if (metaId != null && perShow.TryGetValue(metaId, out var saved))
                {
                    var fallback = DefaultFor(isAnime);
                    return new TrackPreference
                    {
                        AudioLanguage = saved.AudioLanguage ?? fallback.AudioLanguage,
                        SubtitleLanguage = saved.SubtitleLanguage ?? fallback.SubtitleLanguage
                    };
                }
            }
            return DefaultFor(isAnime);
        }

        public bool HasTrackPreference(string metaId)
        {
            lock (gate)
                return metaId != null && perShow.ContainsKey(metaId);
        }

        /// Remember a language the user picked. Pass null to leave that half unchanged; subtitles "off" is remembered.
        public void RememberTracks(string metaId, string audioLanguage, string subtitleLanguage)
        {
            if (string.IsNullOrEmpty(metaId))
                return;
            string json;
            lock (gate)
            {
                if (!perShow.TryGetValue(metaId, out var pref))
                    perShow[metaId] = pref = new TrackPreference();
                if (audioLanguage != null)
                    pref.AudioLanguage = audioLanguage;
                if (subtitleLanguage != null)
                    pref.SubtitleLanguage = subtitleLanguage;
                json = JsonConvert.SerializeObject(perShow);
            }
            Persist(TracksKey, json);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// Completes when every write queued so far has finished (tests, shutdown).
        public Task FlushAsync()
        {
            lock (gate)
                return lastWrite;
        }

        private void Persist(string key, string json)
        {
            if (store == null)
                return;
            lock (gate)
            {
                // Writes stay ordered so a slow earlier write never overwrites a newer value.
                lastWrite = lastWrite.ContinueWith(_ => store.WriteAsync(key, json, CancellationToken.None), TaskScheduler.Default).Unwrap()
                    .ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
            }
        }

        // ---- Track matching (used when a new video's tracks appear) ----

        public static bool LanguageMatches(string trackLanguage, string wanted)
        {
            if (string.IsNullOrEmpty(trackLanguage) || string.IsNullOrEmpty(wanted))
                return false;
            return Normalize(trackLanguage) == Normalize(wanted);
        }

        public static string Normalize(string language) => Languages.Normalize(language);
    }
}
