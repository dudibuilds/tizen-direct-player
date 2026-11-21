using System;
using System.Linq;
using System.Text;
using Ru7100.Core.Api;
using Xunit;

namespace Ru7100.Tests.Api
{
    public class AuthTests
    {
        private static readonly byte[] Secret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        private static readonly byte[] OtherSecret = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
        private const string Nonce = "0123456789abcdef0123456789abcdef";
        private DateTimeOffset now = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

        private ApiAuthenticator Create(int maxNonces = 100) => new ApiAuthenticator(new[]
        {
            new ApiKey("agent", Secret, new[] { ApiScopes.Playback }),
            new ApiKey("other", OtherSecret, new[] { ApiScopes.Admin }),
        }, () => now, maxNonces);

        private string Time(int offsetSeconds = 0) => (now.ToUnixTimeSeconds() + offsetSeconds).ToString();

        private static byte[] Body(string text) => Encoding.UTF8.GetBytes(text);

        private ApiAuthResult Call(ApiAuthenticator auth, string body, string signedBody = null, string time = null, string nonce = Nonce,
            string keyId = "agent", byte[] secret = null, string path = "/api", string signedPath = null, string method = "POST")
        {
            time = time ?? Time();
            var signature = ApiAuthenticator.Sign(secret ?? Secret, method, signedPath ?? path, time, nonce, Body(signedBody ?? body));
            return auth.Authenticate(method, path, keyId, time, nonce, signature, Body(body));
        }

        [Fact]
        public void ValidSignatureSucceedsAndCarriesScopes()
        {
            var result = Call(Create(), "{\"command\":\"status\"}");
            Assert.True(result.Succeeded);
            Assert.Equal("agent", result.Key.Id);
            Assert.Equal(new[] { ApiScopes.Playback }, result.Key.Scopes);
        }

        [Fact]
        public void BodyHashIsHexSha256OfRawBytes()
        {
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", ApiAuthenticator.BodyHash(new byte[0]));
        }

        [Fact]
        public void SignatureMatchesDocumentedMessageFormat()
        {
            var body = Body("{}");
            var message = "POST\n/api\n1700000000\n" + Nonce + "\n" + ApiAuthenticator.BodyHash(body);
            using (var hmac = new System.Security.Cryptography.HMACSHA256(Secret))
                Assert.Equal(Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))),
                    ApiAuthenticator.Sign(Secret, "POST", "/api", "1700000000", Nonce, body));
        }

        [Fact]
        public void BadSignatureFails() =>
            Assert.Equal("bad_signature", Call(Create(), "{}", secret: OtherSecret).Failure);

        [Fact]
        public void GarbageSignatureFails()
        {
            var auth = Create();
            Assert.Equal("bad_signature", auth.Authenticate("POST", "/api", "agent", Time(), Nonce, "not base64!", Body("{}")).Failure);
            Assert.Equal("bad_signature", auth.Authenticate("POST", "/api", "agent", Time(), Nonce, Convert.ToBase64String(new byte[32]), Body("{}")).Failure);
        }

        [Fact]
        public void TamperedBodyFails() =>
            Assert.Equal("bad_signature", Call(Create(), "{\"command\":\"playback.stop\"}", signedBody: "{\"command\":\"status\"}").Failure);

        [Fact]
        public void TamperedPathFails() =>
            Assert.Equal("bad_signature", Call(Create(), "", path: "/api/status", signedPath: "/api").Failure);

        [Fact]
        public void TamperedMethodFails()
        {
            var auth = Create();
            var time = Time();
            var signature = ApiAuthenticator.Sign(Secret, "GET", "/api", time, Nonce, Body("{}"));
            Assert.Equal("bad_signature", auth.Authenticate("POST", "/api", "agent", time, Nonce, signature, Body("{}")).Failure);
        }

        [Fact]
        public void KeyIdCannotBorrowAnotherKeysScopes() =>
            Assert.Equal("bad_signature", Call(Create(), "{}", keyId: "other", secret: Secret).Failure);

        [Fact]
        public void ReplayedNonceFails()
        {
            var auth = Create();
            Assert.True(Call(auth, "{}").Succeeded);
            Assert.Equal("replay", Call(auth, "{}").Failure);
        }

        [Fact]
        public void FailedSignatureDoesNotBurnTheNonce()
        {
            var auth = Create();
            Assert.False(Call(auth, "{}", secret: OtherSecret).Succeeded);
            Assert.True(Call(auth, "{}").Succeeded);
        }

        [Fact]
        public void SameNonceUnderAnotherKeyIsIndependent()
        {
            var auth = Create();
            Assert.True(Call(auth, "{}").Succeeded);
            Assert.True(Call(auth, "{}", keyId: "other", secret: OtherSecret).Succeeded);
        }

        [Theory]
        [InlineData(-61)]
        [InlineData(61)]
        [InlineData(-3600)]
        public void ExpiredOrFutureTimeFails(int offset) =>
            Assert.Equal("stale", Call(Create(), "{}", time: Time(offset)).Failure);

        [Theory]
        [InlineData(-60)]
        [InlineData(60)]
        public void EdgeOfWindowSucceeds(int offset) =>
            Assert.True(Call(Create(), "{}", time: Time(offset)).Succeeded);

        [Fact]
        public void NonceCacheEvictsExpiredEntriesWhenFull()
        {
            var auth = Create(maxNonces: 2);
            Assert.True(Call(auth, "{}", nonce: "aaaaaaaaaaaaaaaa").Succeeded);
            Assert.True(Call(auth, "{}", nonce: "bbbbbbbbbbbbbbbb").Succeeded);
            Assert.Equal("busy", Call(auth, "{}", nonce: "cccccccccccccccc").Failure);
            now = now.AddSeconds(61);
            Assert.True(Call(auth, "{}", nonce: "cccccccccccccccc").Succeeded);
        }

        [Fact]
        public void UnknownKeyFails() => Assert.Equal("unknown_key", Call(Create(), "{}", keyId: "nobody").Failure);

        [Theory]
        [InlineData("short")]
        [InlineData("has spaces in the nonce value")]
        [InlineData(null)]
        public void MalformedNonceFails(string nonce) =>
            Assert.Equal("missing_headers", Call(Create(), "{}", nonce: nonce).Failure);

        [Theory]
        [InlineData("")]
        [InlineData("12.5")]
        [InlineData("-5")]
        [InlineData("soon")]
        public void MalformedTimeFails(string time) =>
            Assert.Equal("missing_headers", Call(Create(), "{}", time: time).Failure);

        [Fact]
        public void KeyFileParsesScopesAndRejectsWeakKeys()
        {
            var secret = Convert.ToBase64String(Secret);
            var keys = ApiKey.ParseKeyFile("{\"keys\":[{\"id\":\"a\",\"secret\":\"" + secret + "\",\"scopes\":[\"playback\",\"library\"]}]}");
            Assert.Equal(new[] { "playback", "library" }, keys.Single().Scopes);
            Assert.Throws<ArgumentException>(() => ApiKey.ParseKeyFile("{\"keys\":[{\"id\":\"a\",\"secret\":\"AAAA\",\"scopes\":[\"playback\"]}]}"));
            Assert.Throws<ArgumentException>(() => ApiKey.ParseKeyFile("{\"keys\":[{\"id\":\"a\",\"secret\":\"" + secret + "\",\"scopes\":[\"root\"]}]}"));
            Assert.Throws<ArgumentException>(() => ApiKey.ParseKeyFile("{\"keys\":[{\"id\":\"a\",\"secret\":\"" + secret + "\",\"scopes\":[]}]}"));
            Assert.Throws<FormatException>(() => ApiKey.ParseKeyFile("{\"keys\":[{\"id\":\"a\",\"secret\":\"" + secret + "\",\"scopes\":[\"admin\"]},{\"id\":\"a\",\"secret\":\"" + secret + "\",\"scopes\":[\"admin\"]}]}"));
        }

        [Fact]
        public void ScopeRules()
        {
            Assert.True(ApiScopes.Allows(new[] { "admin" }, new[] { "playback" }));
            Assert.True(ApiScopes.Allows(new[] { "library" }, new[] { "playback", "library" }));
            Assert.False(ApiScopes.Allows(new[] { "playback" }, new[] { "recommendations" }));
            Assert.False(ApiScopes.Allows(null, new[] { "playback" }));
        }
    }
}
