// Library reads and edits over ICatalog + ILibrary (+ ISourceResolver for new episodes), shaped like the old
// stremio_discover / stremio_library results. Parity C4, C8, D3-D5.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Api
{
    public sealed class LibraryItemView
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public MediaType Type { get; set; }
        public string State { get; set; }           // watching | unwatched | caught_up | library
        public string CurrentVideoId { get; set; }
        public double Progress { get; set; }        // percent, one decimal
        public VideoInfo NextEpisode { get; set; }
        public bool Notifications { get; set; }
    }

    public sealed class StreamChoice
    {
        public string Addon { get; set; }
        public string Name { get; set; }
        public string Title { get; set; }
        public string Filename { get; set; }
        public string Quality { get; set; }
        public int? Seeders { get; set; }
        public long? SizeBytes { get; set; }
        public CodecInfo Codecs { get; set; }
        public double Score { get; set; }
    }

    public sealed class CodecInfo
    {
        public string Video { get; set; }
        public string Audio { get; set; }
    }

    public sealed class NewEpisode
    {
        public string Series { get; set; }
        public string SeriesId { get; set; }
        public VideoInfo Video { get; set; }
        public StreamChoice BestStream { get; set; }
    }

    public sealed class NewEpisodesResult
    {
        public int Count { get; set; }
        public IReadOnlyList<NewEpisode> Episodes { get; set; } = Array.Empty<NewEpisode>();
    }

    public sealed class AmbiguousResult
    {
        public string Status => "ambiguous";
        public bool Ambiguous => true;
        public IReadOnlyList<SearchHit> Candidates { get; set; }
    }

    public sealed class LibraryUpdateResult
    {
        public bool Ok { get; set; } = true;
        public string Action { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public bool InLibrary { get; set; }
        public bool Notifications { get; set; }
    }

    public sealed class MarkWatchedResult
    {
        public bool Ok { get; set; } = true;
        public string Name { get; set; }
        public VideoInfo Video { get; set; }
        public bool Watched { get; set; }
        public VideoInfo NextEpisode { get; set; }
    }

    public sealed class LibraryCommands
    {
        public const string AlertsKey = "stremio-alerts";
        private const int NewEpisodeLimit = 20;
        private readonly ICatalog catalog;
        private readonly ILibrary library;
        private readonly ISourceResolver sources;
        private readonly IKeyValueStore store;
        private readonly TitleResolver titles;
        private readonly Func<DateTimeOffset> clock;
        private readonly SemaphoreSlim alertsLock = new SemaphoreSlim(1, 1);

        public LibraryCommands(ICatalog catalog, ILibrary library, ISourceResolver sources, IKeyValueStore store, Func<DateTimeOffset> clock = null)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.library = library ?? throw new ArgumentNullException(nameof(library));
            this.sources = sources;
            this.store = store;
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
            titles = new TitleResolver(catalog);
        }

        public TitleResolver Titles => titles;

        public async Task<IReadOnlyList<LibraryItemView>> ListAsync(string query, string status, CancellationToken token)
        {
            var entries = (await library.GetAllAsync(token).ConfigureAwait(false) ?? Array.Empty<LibraryEntry>())
                .Where(e => e != null && !e.Removed && Matches(e.Name, query))
                .ToList();
            var views = new List<LibraryItemView>();
            foreach (var entry in entries)
            {
                var state = entry.State ?? new LibraryState();
                var view = new LibraryItemView
                {
                    Id = entry.Id,
                    Name = entry.Name,
                    Type = entry.Type,
                    State = "library",
                    CurrentVideoId = state.VideoId,
                    Progress = state.DurationMs > 0 ? Math.Round((double)state.TimeOffsetMs / state.DurationMs * 1000) / 10 : 0,
                    Notifications = !state.NoNotif,
                };
                if (entry.Type == MediaType.Series)
                {
                    try
                    {
                        var meta = await catalog.GetMetaAsync(MediaType.Series, entry.Id, token).ConfigureAwait(false);
                        var next = meta == null ? null : library.NextVideo(entry, meta);
                        view.NextEpisode = next;
                        view.State = state.TimeOffsetMs > 1000 ? "watching" : next != null ? "unwatched" : "caught_up";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { }
                }
                else if (state.TimeOffsetMs > 1000)
                {
                    view.State = "watching";
                }
                views.Add(view);
            }
            return views.Where(v => status == "all" || v.State == status).ToList();
        }

        public async Task<NewEpisodesResult> NewEpisodesAsync(string query, bool sinceLastCheck, bool acknowledge, CancellationToken token)
        {
            if (sources == null) throw new ApiException("unavailable", "Stream discovery is not configured", 503);
            if ((sinceLastCheck || acknowledge) && store == null) throw new ApiException("unavailable", "Alert storage is not configured", 503);
            var alerts = sinceLastCheck || acknowledge ? await ReadAlertsAsync(token).ConfigureAwait(false) : new JObject();
            var announced = (JObject)alerts["announced"];
            var entries = (await library.GetAllAsync(token).ConfigureAwait(false) ?? Array.Empty<LibraryEntry>())
                .Where(e => e != null && e.Type == MediaType.Series && !e.Removed && !e.Temp && !(e.State?.NoNotif ?? false) && Matches(e.Name, query))
                .ToList();
            var found = new List<NewEpisode>();
            var gate = new SemaphoreSlim(4, 4);
            var now = clock();

            async Task CheckAsync(LibraryEntry entry)
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    MetaDetail meta;
                    try { meta = await catalog.GetMetaAsync(MediaType.Series, entry.Id, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { return; }
                    if (meta == null) return;
                    var baseline = entry.State?.LastWatched ?? entry.Created;
                    var anime = IsAnime(meta);
                    foreach (var video in meta.Videos ?? Array.Empty<VideoInfo>())
                    {
                        lock (found) if (found.Count >= NewEpisodeLimit) return;
                        if (video.Released != null && video.Released > now) continue;            // not released yet
                        if (video.Released != null && video.Released <= baseline) continue;
                        if (library.IsWatched(entry, meta, video.Id)) continue;
                        if (sinceLastCheck) lock (announced) if (announced[video.Id] != null) continue;
                        IReadOnlyList<StreamCandidate> ranked;
                        try
                        {
                            ranked = await sources.GetRankedAsync(MediaType.Series, video.Id,
                                new SourceOptions { Episodic = true, IsAnime = anime }, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { continue; }
                        if (ranked == null || ranked.Count == 0) continue;
                        lock (found)
                        {
                            if (found.Count >= NewEpisodeLimit) return;
                            found.Add(new NewEpisode { Series = meta.Summary.Name, SeriesId = meta.Summary.Id, Video = video, BestStream = ToChoice(ranked[0]) });
                        }
                        if (acknowledge) lock (announced) announced[video.Id] = now.ToString("o");
                    }
                }
                finally
                {
                    gate.Release();
                }
            }

            await Task.WhenAll(entries.Select(CheckAsync)).ConfigureAwait(false);
            if (acknowledge) await WriteAlertsAsync(alerts, token).ConfigureAwait(false);
            var sorted = found.OrderByDescending(e => e.Video.Released ?? DateTimeOffset.MinValue).ToList();
            return new NewEpisodesResult { Count = sorted.Count, Episodes = sorted };
        }

        public async Task<object> UpdateAsync(string queryOrId, string action, bool notifications, CancellationToken token)
        {
            var resolved = await titles.ResolveAsync(queryOrId, token).ConfigureAwait(false);
            if (resolved.Ambiguous) return new AmbiguousResult { Candidates = resolved.Candidates };
            var meta = resolved.Meta;
            var id = meta.Summary.Id;
            if (action == "add") await library.AddAsync(meta, token).ConfigureAwait(false);
            else if (action == "remove") await library.RemoveAsync(id, token).ConfigureAwait(false);
            else await library.SetNotificationsAsync(id, notifications, token).ConfigureAwait(false);
            var entry = await library.GetAsync(id, token).ConfigureAwait(false);
            return new LibraryUpdateResult
            {
                Action = action,
                Id = id,
                Name = entry?.Name ?? meta.Summary.Name,
                InLibrary = entry != null && !entry.Removed,
                Notifications = entry?.State == null || !entry.State.NoNotif,
            };
        }

        public async Task<object> MarkWatchedAsync(string queryOrId, int? season, int? episode, bool watched, CancellationToken token)
        {
            var resolved = await titles.ResolveAsync(queryOrId, token).ConfigureAwait(false);
            if (resolved.Ambiguous) return new AmbiguousResult { Candidates = resolved.Candidates };
            var meta = resolved.Meta;
            VideoInfo video;
            if (meta.Summary.Type == MediaType.Series)
            {
                if (season == null || episode == null) throw ApiException.Missing("season", "season and episode are required for a series");
                video = (meta.Videos ?? Array.Empty<VideoInfo>()).FirstOrDefault(v => v.Season == season && v.Episode == episode);
                if (video == null) throw ApiException.NotFound("Episode not found");
            }
            else
            {
                if (season != null || episode != null) throw ApiException.Irrelevant("season and episode are not valid for a movie");
                video = new VideoInfo { Id = meta.Summary.Id, Title = meta.Summary.Name };
            }
            await library.MarkWatchedAsync(meta, video.Id, watched, token).ConfigureAwait(false);
            VideoInfo next = null;
            if (meta.Summary.Type == MediaType.Series)
            {
                var entry = await library.GetAsync(meta.Summary.Id, token).ConfigureAwait(false);
                next = entry == null ? null : library.NextVideo(entry, meta);
            }
            return new MarkWatchedResult { Name = meta.Summary.Name, Video = video, Watched = watched, NextEpisode = next };
        }

        /// Parity F11, metadata half only: genre Anime, or Japan + Animation.
        public static bool IsAnime(MetaDetail meta)
        {
            var genres = meta.Genres ?? Array.Empty<string>();
            if (genres.Any(g => string.Equals(g, "Anime", StringComparison.OrdinalIgnoreCase))) return true;
            return genres.Any(g => string.Equals(g, "Animation", StringComparison.OrdinalIgnoreCase))
                && (meta.Country ?? "").IndexOf("japan", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static StreamChoice ToChoice(StreamCandidate stream) => new StreamChoice
        {
            Addon = stream.AddonId,
            Name = stream.Name,
            Title = stream.Title,
            Filename = stream.Filename,
            Quality = stream.Quality,
            Seeders = stream.Seeders,
            SizeBytes = stream.SizeBytes,
            Codecs = new CodecInfo { Video = stream.VideoCodec, Audio = stream.AudioCodec },
            Score = Math.Round(stream.Score * 10) / 10,
        };

        private static bool Matches(string name, string query) =>
            string.IsNullOrEmpty(query) || (name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        private async Task<JObject> ReadAlertsAsync(CancellationToken token)
        {
            JObject alerts = null;
            try
            {
                var text = await store.ReadAsync(AlertsKey, token).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(text)) alerts = ApiJson.Parse(text) as JObject;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { }
            alerts = alerts ?? new JObject();
            if (!(alerts["announced"] is JObject)) alerts["announced"] = new JObject();
            return alerts;
        }

        private async Task WriteAlertsAsync(JObject alerts, CancellationToken token)
        {
            await alertsLock.WaitAsync(token).ConfigureAwait(false);
            try { await store.WriteAsync(AlertsKey, alerts.ToString(Newtonsoft.Json.Formatting.None), token).ConfigureAwait(false); }
            finally { alertsLock.Release(); }
        }
    }
}
