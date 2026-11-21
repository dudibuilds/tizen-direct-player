using System;
using System.Collections.Generic;

namespace Ru7100.Core.Addons
{
    /// A failed add-on or Cinemeta request (HTTP status, timeout, bad JSON or missing payload).
    public sealed class AddonRequestException : Exception
    {
        public AddonRequestException(string message, int? statusCode = null, Exception inner = null)
            : base(message, inner)
        {
            StatusCode = statusCode;
        }

        public int? StatusCode { get; }
    }

    /// A catalog declared in an add-on manifest (`catalogs[]`).
    public sealed class CatalogDescriptor
    {
        public string AddonId { get; set; }
        public string Type { get; set; }          // movie | series | (other types are kept but not browsable)
        public string Id { get; set; }
        public string Name { get; set; }
        public IReadOnlyList<string> ExtraSupported { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> ExtraRequired { get; set; } = Array.Empty<string>();
        public IReadOnlyList<string> Genres { get; set; } = Array.Empty<string>();

        public MediaType? MediaType => AddonTypes.Parse(Type);
        public bool SupportsSearch => Contains(ExtraSupported, "search");
        public bool SupportsSkip => Contains(ExtraSupported, "skip");

        /// Usable as a Home row: movie/series and no extra (genre, search, ids) is required.
        public bool IsBrowsable => MediaType.HasValue && ExtraRequired.Count == 0;

        private static bool Contains(IReadOnlyList<string> values, string value)
        {
            foreach (var item in values)
                if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }

    /// A Home row: one browsable catalog of one add-on.
    public sealed class HomeCatalog
    {
        public AddonDescriptor Addon { get; set; }
        public CatalogDescriptor Catalog { get; set; }
        public string Title => Catalog.Name + " " + (Catalog.MediaType == Core.MediaType.Series ? "Series" : "Movies");
    }

    /// One raw entry of an add-on `/stream/...` response, before ranking.
    public sealed class AddonStreamItem
    {
        public string AddonId { get; set; }
        public string AddonName { get; set; }
        public string Name { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string InfoHash { get; set; }
        public int? FileIdx { get; set; }          // only set when the JSON value is an integer
        public string Url { get; set; }
        public string ExternalUrl { get; set; }
        public IReadOnlyList<string> Sources { get; set; } = Array.Empty<string>();
        public string Filename { get; set; }
        public long? VideoSize { get; set; }
        public bool HasProxyHeaders { get; set; }

        /// F2: infoHash.lower + ":" + (fileIdx ?? "auto"), else url, else null (dropped).
        public string Identity
        {
            get
            {
                if (!string.IsNullOrEmpty(InfoHash))
                    return InfoHash.ToLowerInvariant() + ":" + (FileIdx.HasValue ? FileIdx.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "auto");
                return string.IsNullOrEmpty(Url) ? null : Url;
            }
        }

        /// F3: text searched by every rule = name + title + description + filename.
        public string SearchText => (Name ?? "") + " " + (Title ?? "") + " " + (Description ?? "") + " " + (Filename ?? "");
    }

    /// What the target device can decode. Used for hard filters (TV re-tune of F5/F15).
    public sealed class DeviceCaps
    {
        public ISet<string> SupportedVideoCodecs { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264", "hevc" };
        public bool RejectAv1 { get; set; } = true;
        public bool RejectDolbyVisionWithoutHdr10 { get; set; } = true;
        public bool RejectDts { get; set; } = true;
        public bool RejectTrueHd { get; set; } = true;

        /// Samsung RU7100 (Tizen 5.0): H.264/HEVC incl. HDR10, AAC/AC3/EAC3. No AV1, DV-only, DTS, TrueHD.
        public static DeviceCaps Ru7100 => new DeviceCaps();

        /// No device hard filters (codec labels still parsed).
        public static DeviceCaps Permissive => new DeviceCaps
        {
            SupportedVideoCodecs = null,
            RejectAv1 = false,
            RejectDolbyVisionWithoutHdr10 = false,
            RejectDts = false,
            RejectTrueHd = false,
        };
    }

    public sealed class RankOptions
    {
        public bool Episodic { get; set; } = true;
        public bool Anime { get; set; }
        /// English-language title: "multi audio" (English plus a dub) ranks like a release that names no language.
        public bool EnglishOriginal { get; set; }
        public string Quality { get; set; } = "auto";    // auto | 4k | 1080p | 720p
        public double? MaxSizeGb { get; set; }
        public DeviceCaps Device { get; set; } = DeviceCaps.Ru7100;
        public ISet<string> ExcludeIdentities { get; set; }
    }

    /// Result of parsing and scoring one stream. RejectReason != null means a hard filter dropped it.
    public sealed class StreamAnalysis
    {
        public AddonStreamItem Stream { get; set; }
        public string RejectReason { get; set; }
        public double Score { get; set; }
        public List<string> Reasons { get; } = new List<string>();
        public string Quality { get; set; }
        public int Seeders { get; set; }
        public bool SeedersKnown { get; set; }
        public long? SizeBytes { get; set; }
        public string VideoCodec { get; set; }
        public string AudioCodec { get; set; }
        public bool JapaneseAudio { get; set; }
        public bool AnimeOriginalAudio { get; set; }
        public int AnimeLanguageTier { get; set; }
        public int GeneralLanguageTier { get; set; }
        public int HealthBand { get; set; } = 1;
        /// The torrent is a season pack (ranked below a comparable single-episode torrent).
        public bool SeasonPack { get; set; }

        public int LanguageTier(bool anime) => anime ? AnimeLanguageTier : GeneralLanguageTier;
    }

    public sealed class SearchMatch
    {
        public MetaSummary Meta { get; set; }
        public double MatchScore { get; set; }
    }

    /// B2/B4 result. Either Meta is set, or Ambiguous with up to 5 candidates.
    public sealed class ResolveResult
    {
        public MetaDetail Meta { get; set; }
        public bool Ambiguous { get; set; }
        public IReadOnlyList<SearchMatch> Candidates { get; set; } = Array.Empty<SearchMatch>();
        public int? Season { get; set; }
        public int? Episode { get; set; }
    }

    public sealed class EpisodeQuery
    {
        public string Query { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
    }

    public static class AddonTypes
    {
        public static string ToStremio(MediaType type) => type == MediaType.Series ? "series" : "movie";

        public static MediaType? Parse(string type)
        {
            if (string.Equals(type, "series", StringComparison.OrdinalIgnoreCase)) return MediaType.Series;
            if (string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase)) return MediaType.Movie;
            return null;
        }
    }
}
