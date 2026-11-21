// Title search ranking, query-or-id resolution with ambiguity (parity B1, B2) and episode parsing (B4).
// Lives here because ICatalog has no resolve method; the playback engine may reuse it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100.Core.Api
{
    public sealed class SearchHit
    {
        public string Id { get; set; }
        public MediaType Type { get; set; }
        public string Name { get; set; }
        public string ReleaseInfo { get; set; }
        public string Poster { get; set; }
        public string PosterShape { get; set; } = "poster";
        public double MatchScore { get; set; }
    }

    public sealed class ResolvedTitle
    {
        public MetaDetail Meta { get; set; }
        public IReadOnlyList<SearchHit> Candidates { get; set; }
        public bool Ambiguous => Meta == null;
    }

    public sealed class TitleResolver
    {
        private static readonly Regex ImdbId = new Regex("^tt\\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex EpisodePattern = new Regex(
            "(?:season\\s*(\\d+)\\s*(?:episode|ep)\\s*(\\d+)|s(\\d{1,2})\\s*e(\\d{1,3}))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private readonly ICatalog catalog;

        public TitleResolver(ICatalog catalog)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public static bool IsImdbId(string value) => value != null && ImdbId.IsMatch(value);

        public static (string Query, int? Season, int? Episode) ParseEpisode(string query)
        {
            var text = query ?? "";
            var match = EpisodePattern.Match(text);
            if (!match.Success) return (text.Trim(), null, null);
            var season = int.Parse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value);
            var episode = int.Parse(match.Groups[2].Success ? match.Groups[2].Value : match.Groups[4].Value);
            var rest = Regex.Replace(text.Remove(match.Index, match.Length).Insert(match.Index, " "), "\\s+", " ").Trim();
            return (rest, season, episode);
        }

        public static double TitleSimilarity(string query, string title)
        {
            var a = NormalizeTitle(query);
            var b = NormalizeTitle(title);
            if (a == b) return 1;
            if (a.IndexOf(b, StringComparison.Ordinal) >= 0 || b.IndexOf(a, StringComparison.Ordinal) >= 0) return 0.9;
            var aa = new HashSet<string>(a.Split(' '));
            var bb = new HashSet<string>(b.Split(' '));
            var common = aa.Count(word => bb.Contains(word));
            return (double)common / Math.Max(Math.Max(aa.Count, bb.Count), 1);
        }

        private static string NormalizeTitle(string value)
        {
            var builder = new StringBuilder();
            var space = false;
            foreach (var raw in (value ?? "").ToLowerInvariant())
            {
                if ((raw >= 'a' && raw <= 'z') || (raw >= '0' && raw <= '9'))
                {
                    builder.Append(raw);
                    space = false;
                }
                else if (!space)
                {
                    builder.Append(' ');
                    space = true;
                }
            }
            return builder.ToString().Trim();
        }

        public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, MediaType? type, int limit, CancellationToken token)
        {
            var results = await catalog.SearchAsync(query, type, 20, token).ConfigureAwait(false) ?? Array.Empty<MetaSummary>();
            return results
                .Where(meta => meta != null)
                .Select(meta => new SearchHit
                {
                    Id = meta.Id,
                    Type = meta.Type,
                    Name = meta.Name,
                    ReleaseInfo = meta.ReleaseInfo,
                    Poster = meta.Poster,
                    MatchScore = Math.Round(TitleSimilarity(query, meta.Name), 4),
                })
                .OrderByDescending(hit => hit.MatchScore)
                .ThenByDescending(hit => hit.ReleaseInfo ?? "", StringComparer.Ordinal)
                .Take(Math.Max(1, Math.Min(20, limit)))
                .ToList();
        }

        public async Task<ResolvedTitle> ResolveAsync(string queryOrId, CancellationToken token)
        {
            if (IsImdbId(queryOrId))
            {
                foreach (var type in new[] { MediaType.Series, MediaType.Movie })
                {
                    var meta = await TryGetMetaAsync(type, queryOrId, token).ConfigureAwait(false);
                    if (meta != null) return new ResolvedTitle { Meta = meta };
                }
            }

            var results = await SearchAsync(queryOrId, null, 5, token).ConfigureAwait(false);
            if (results.Count == 0) throw ApiException.NotFound("No Stremio result found for: " + queryOrId);
            if (results.Count > 1 && results[0].MatchScore < 0.72 && results[0].MatchScore - results[1].MatchScore < 0.15)
                return new ResolvedTitle { Candidates = results.Take(5).ToList() };

            var resolved = await TryGetMetaAsync(results[0].Type, results[0].Id, token).ConfigureAwait(false);
            if (resolved == null) throw ApiException.NotFound("No Stremio metadata for: " + results[0].Id);
            return new ResolvedTitle { Meta = resolved };
        }

        private async Task<MetaDetail> TryGetMetaAsync(MediaType type, string id, CancellationToken token)
        {
            try
            {
                var meta = await catalog.GetMetaAsync(type, id, token).ConfigureAwait(false);
                return meta?.Summary == null ? null : meta;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return null; }
        }
    }
}
