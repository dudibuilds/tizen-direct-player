// Agent API basics: scopes, errors, JSON settings. C# 8 / netstandard2.0-era BCL (compiles into the tizen50 app).
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Ru7100.Core.Api
{
    /// Permission scopes carried by each API key. Admin implies every other scope.
    public static class ApiScopes
    {
        public const string Playback = "playback";             // player commands, queue, status
        public const string Library = "library";               // Stremio library reads and edits
        public const string Recommendations = "recommendations"; // recommendation updates (commands registered by the host)
        public const string Admin = "admin";                   // add-on install/remove, account link/sign-out, everything

        public static readonly IReadOnlyList<string> All = new[] { Playback, Library, Recommendations, Admin };

        public static bool IsKnown(string scope)
        {
            foreach (var known in All)
                if (known == scope) return true;
            return false;
        }

        public static bool Allows(IEnumerable<string> granted, IReadOnlyCollection<string> required)
        {
            if (granted == null) return false;
            foreach (var scope in granted)
            {
                if (scope == Admin) return true;
                foreach (var need in required)
                    if (need == scope) return true;
            }
            return false;
        }
    }

    /// Error with a stable machine code. Handlers (and the playback engine) throw this for expected failures.
    public sealed class ApiException : Exception
    {
        public ApiException(string code, string message, int statusCode = 400) : base(message)
        {
            Code = code;
            StatusCode = statusCode;
        }

        public string Code { get; }
        public int StatusCode { get; }

        public static ApiException Missing(string field, string detail = null) =>
            new ApiException("missing_argument", detail ?? field + " is required");
        public static ApiException Invalid(string message) => new ApiException("invalid_argument", message);
        public static ApiException Irrelevant(string message) => new ApiException("irrelevant_argument", message);
        public static ApiException Conflict(string code, string message) => new ApiException(code, message, 409);
        public static ApiException NotFound(string message) => new ApiException("not_found", message, 404);
    }

    public static class ApiJson
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Include,
            DateParseHandling = DateParseHandling.None,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            Converters = { new PlayerStateConverter(), new StringEnumConverter(new CamelCaseNamingStrategy()) },
        };

        /// Player states go out upper case (PLAYING, PAUSED, STOPPED) like the old MCP's control results.
        private sealed class PlayerStateConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType) =>
                objectType == typeof(PlayerState) || objectType == typeof(PlayerState?);

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                if (value == null) writer.WriteNull();
                else writer.WriteValue(value.ToString().ToUpperInvariant());
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null) return null;
                return Enum.Parse(typeof(PlayerState), Convert.ToString(reader.Value, System.Globalization.CultureInfo.InvariantCulture), true);
            }
        }

        public static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);

        /// Round-trips through text so the token holds exactly what goes on the wire (ISO date strings, not Date tokens).
        public static JToken ToToken(object value) => value == null ? JValue.CreateNull() : Parse(Serialize(value));

        public static string Serialize(object value) => JsonConvert.SerializeObject(value, Formatting.None, Settings);

        /// Parses request JSON without turning ISO strings into dates.
        public static JToken Parse(string text)
        {
            using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
            {
                var token = JToken.ReadFrom(reader);
                while (reader.Read())
                    if (reader.TokenType != JsonToken.Comment)
                        throw new JsonReaderException("Unexpected content after JSON value.");
                return token;
            }
        }
    }
}
