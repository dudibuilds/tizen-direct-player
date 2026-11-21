// Strict argument reader. Error codes match the old MCP: missing_argument, invalid_argument, irrelevant_argument.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Api
{
    public sealed class ApiArgs
    {
        private readonly JObject values;

        public ApiArgs(JObject values)
        {
            this.values = values ?? new JObject();
        }

        public JObject Raw => values;

        public bool Has(string name)
        {
            var token = values[name];
            return token != null && token.Type != JTokenType.Null;
        }

        public void AssertAllowed(IReadOnlyCollection<string> allowed)
        {
            var unexpected = values.Properties().Select(p => p.Name).Where(n => !allowed.Contains(n)).ToList();
            if (unexpected.Count > 0)
                throw ApiException.Irrelevant("Unexpected argument" + (unexpected.Count == 1 ? "" : "s") + ": " + string.Join(", ", unexpected));
        }

        /// Rejects arguments that only make sense for another action.
        public void AssertAbsent(string action, params string[] names)
        {
            foreach (var name in names)
                if (Has(name)) throw ApiException.Irrelevant(name + " is not valid for " + action);
        }

        public string RequireString(string name)
        {
            var value = OptionalString(name);
            if (string.IsNullOrEmpty(value)) throw ApiException.Missing(name);
            return value;
        }

        public string OptionalString(string name)
        {
            if (!Has(name)) return null;
            var token = values[name];
            if (token.Type != JTokenType.String) throw ApiException.Invalid(name + " must be a string");
            var text = ((string)token).Trim();
            return text.Length == 0 ? null : text;
        }

        public string Choice(string name, string[] choices, string fallback)
        {
            var value = Has(name) ? OptionalString(name) : fallback;
            if (value == null || Array.IndexOf(choices, value) < 0)
                throw ApiException.Invalid(name + " must be one of: " + string.Join(", ", choices));
            return value;
        }

        public bool? OptionalBool(string name)
        {
            if (!Has(name)) return null;
            var token = values[name];
            if (token.Type != JTokenType.Boolean) throw ApiException.Invalid(name + " must be a boolean");
            return (bool)token;
        }

        public bool RequireBool(string name)
        {
            var value = OptionalBool(name);
            if (value == null) throw ApiException.Missing(name, name + " must be a boolean");
            return value.Value;
        }

        public double? OptionalNumber(string name, double min = double.NegativeInfinity, double max = double.PositiveInfinity, bool exclusiveMin = false)
        {
            if (!Has(name)) return null;
            var token = values[name];
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                throw ApiException.Invalid(name + " must be a number");
            var value = token.Value<double>();
            if (double.IsNaN(value) || double.IsInfinity(value) || value > max || (exclusiveMin ? value <= min : value < min))
                throw ApiException.Invalid(name + " must be a number " + RangeText(min, max, exclusiveMin));
            return value;
        }

        public int? OptionalInt(string name, int min, int max)
        {
            var value = OptionalNumber(name);
            if (value == null) return null;
            if (Math.Floor(value.Value) != value.Value) throw ApiException.Invalid(name + " must be an integer");
            if (value.Value < min || value.Value > max)
                throw ApiException.Invalid(name + " must be an integer " + RangeText(min, max, false));
            return (int)value.Value;
        }

        public int RequireInt(string name, int min, int max)
        {
            var value = OptionalInt(name, min, max);
            if (value == null) throw ApiException.Missing(name);
            return value.Value;
        }

        public JArray OptionalArray(string name)
        {
            if (!Has(name)) return null;
            var token = values[name] as JArray;
            if (token == null) throw ApiException.Invalid(name + " must be an array");
            return token;
        }

        private static string RangeText(double min, double max, bool exclusiveMin)
        {
            var low = double.IsNegativeInfinity(min) ? null : (exclusiveMin ? "greater than " : "from ") + min.ToString(CultureInfo.InvariantCulture);
            var high = double.IsPositiveInfinity(max) ? null : (low == null ? "up to " : " to ") + max.ToString(CultureInfo.InvariantCulture);
            return (low ?? "") + (high ?? "");
        }
    }
}
