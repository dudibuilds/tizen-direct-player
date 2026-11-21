using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100.Core.Playback
{
    // ---- Companion interfaces for IPlayerBackend (the shared contract lacks these; see report). ----

    /// A backend that can select embedded subtitle tracks and surface their cues to the app subtitle layer
    /// instead of rendering them natively.
    public interface ISubtitleCapablePlayer
    {
        /// null turns embedded subtitles off.
        void SelectEmbeddedSubtitle(int? index);
        /// Applies a timing offset to embedded cues (positive = later). Clamped to ±30 s.
        void SetEmbeddedSubtitleOffset(int offsetMs);
        event EventHandler<SubtitleCue> SubtitleCue;
        int? SelectedAudio { get; }
    }

    /// A backend that can report the platform interrupting playback (the TV taking the decoder or audio for another app).
    public interface IInterruptiblePlayer
    {
        /// The reason of an interruption since the last call (and clears it), or null.
        string TakeInterruption();
    }

    /// A backend with a configurable video area (fullscreen or a region of interest).
    public interface IVideoAreaPlayer
    {
        void SetFullscreen();
        void SetVideoArea(int x, int y, int width, int height);
    }

    public interface ISubtitleLoader
    {
        Task<string> LoadTextAsync(string url, CancellationToken token);
    }

    public sealed class HttpSubtitleLoader : ISubtitleLoader, IDisposable
    {
        public const int MaxBytes = 4 * 1024 * 1024;
        private readonly HttpClient http;
        private readonly TimeSpan total;

        /// total: the whole download, headers and body (HttpClient.Timeout alone stops at the headers here).
        public HttpSubtitleLoader(HttpMessageHandler handler = null, TimeSpan? total = null)
        {
            http = handler == null ? new HttpClient() : new HttpClient(handler);
            this.total = total ?? TimeSpan.FromSeconds(20);
            http.Timeout = this.total;
        }

        public async Task<string> LoadTextAsync(string url, CancellationToken token)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidDataException("Subtitles must use HTTP.");
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(total);
                try
                {
                    using (var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                    // Older runtimes ignore tokens on an in-flight body read: disposing the response ends it.
                    using (deadline.Token.Register(() => { try { response.Dispose(); } catch (Exception) { } }))
                    {
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength > MaxBytes)
                            throw new InvalidDataException("The subtitle file is too large.");
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var body = new MemoryStream())
                        {
                            var chunk = new byte[16 * 1024];
                            int read;
                            while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, deadline.Token).ConfigureAwait(false)) > 0)
                            {
                                if (body.Length + read > MaxBytes)
                                    throw new InvalidDataException("The subtitle file is too large.");
                                body.Write(chunk, 0, read);
                            }
                            deadline.Token.ThrowIfCancellationRequested();
                            return DecodeText(body.ToArray());
                        }
                    }
                }
                catch (Exception error) when (deadline.IsCancellationRequested && !token.IsCancellationRequested && !(error is InvalidDataException))
                {
                    throw new TimeoutException($"The subtitle download did not finish within {total.TotalSeconds:0} s.");
                }
            }
        }

        /// UTF-8 when valid, otherwise Latin-1.
        public static string DecodeText(byte[] body)
        {
            try { return new UTF8Encoding(false, true).GetString(body).TrimStart('﻿'); }
            catch (DecoderFallbackException) { }
            return Encoding.GetEncoding("ISO-8859-1").GetString(body);
        }

        public void Dispose() => http.Dispose();
    }

    /// SRT / WebVTT cue list for external subtitles.
    public sealed class SubtitleText
    {
        private static readonly Regex Timing = new Regex(
            @"^(?<start>(?:\d+:)?\d{1,2}:\d{2}[,.]\d{3})\s+-->\s+(?<end>(?:\d+:)?\d{1,2}:\d{2}[,.]\d{3})(?:\s|$)");
        private static readonly Regex Tags = new Regex(@"<[^>]+>|\{\\[^}]*\}");
        private readonly SubtitleCue[] cues;

        public SubtitleText(string content)
        {
            var normalized = (content ?? "").TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n');
            var parsed = new List<SubtitleCue>();
            foreach (var entry in Regex.Split(normalized, "\n[ \t]*\n"))
            {
                var lines = entry.Split('\n');
                var index = Array.FindIndex(lines, line => Timing.IsMatch(line.Trim()));
                if (index < 0 || index == lines.Length - 1)
                    continue;
                var match = Timing.Match(lines[index].Trim());
                if (!TryTimestamp(match.Groups["start"].Value, out var start) || !TryTimestamp(match.Groups["end"].Value, out var end) || start >= end)
                    continue;
                var text = Tags.Replace(string.Join("\n", lines.Skip(index + 1)), "").Trim();
                if (text.Length > 0)
                    parsed.Add(new SubtitleCue { StartMs = start, DurationMs = end - start, Text = text });
            }
            cues = parsed.OrderBy(cue => cue.StartMs).ToArray();
        }

        public int Count => cues.Length;

        /// Cue start times in order (for alignment).
        public IReadOnlyList<long> Starts => cues.Select(cue => cue.StartMs).ToArray();

        /// The cue visible at a media position, or null.
        public SubtitleCue At(long positionMs)
        {
            int low = 0, high = cues.Length - 1, found = -1;
            while (low <= high)
            {
                var mid = (low + high) / 2;
                if (cues[mid].StartMs <= positionMs) { found = mid; low = mid + 1; }
                else high = mid - 1;
            }
            for (var i = found; i >= 0 && i > found - 4; i--)
                if (positionMs < cues[i].StartMs + cues[i].DurationMs)
                    return cues[i];
            return null;
        }

        private static bool TryTimestamp(string value, out long milliseconds)
        {
            milliseconds = 0;
            var parts = value.Replace(',', '.').Split(':');
            if (parts.Length < 2 || parts.Length > 3)
                return false;
            if (!double.TryParse(parts[parts.Length - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                return false;
            if (!int.TryParse(parts[parts.Length - 2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
                return false;
            var hours = 0;
            if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours))
                return false;
            milliseconds = (long)Math.Round(((hours * 60 + minutes) * 60 + seconds) * 1000);
            return true;
        }
    }

    /// Position-driven pump for external cues. The offset is applied here (app-side), so both directions work.
    /// Embedded cues arrive from the player at presentation time; their offset is applied by the backend
    /// (Tizen Player.SetSubtitleOffset), since an app-side offset could only delay them.
    public sealed class SubtitleCuePump
    {
        private Func<long, SubtitleCue> lookup;
        private SubtitleCue current;

        public event EventHandler<SubtitleCue> CueChanged;

        public int OffsetMs { get; set; }

        /// The subtitle's clock against the file's (a release shift or speed fix); the viewer's OffsetMs shifts on top.
        public SubtitleTiming Timing { get; set; } = SubtitleTiming.Identity;

        public void SetSource(Func<long, SubtitleCue> source)
        {
            lookup = source;
            Emit(null);
        }

        public void SetSource(SubtitleText text) => SetSource(text == null ? (Func<long, SubtitleCue>)null : text.At);

        public void Update(long positionMs)
        {
            var source = lookup;
            if (source == null)
                return;
            Emit(source(Timing.ToSubtitle(positionMs - OffsetMs)));
        }

        private void Emit(SubtitleCue cue)
        {
            if (ReferenceEquals(cue, current))
                return;
            current = cue;
            CueChanged?.Invoke(this, cue ?? SubtitleCue.Clear);
        }
    }
}
