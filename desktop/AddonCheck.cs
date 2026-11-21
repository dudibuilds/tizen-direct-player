using System.Net;
using System.Text;
using Ru7100Probe;

internal static class AddonCheck
{
    public static async Task RunAsync()
    {
        var manifest = new Uri("https://fixture.example/config/manifest.json");
        using var client = new StremioAddonClient(new Fixture());
        var streams = await client.GetStreamsAsync(manifest, "series", "tt1234567:2:10", CancellationToken.None);
        if (streams.Length != 2 || !streams[0].IsDirect || streams[1].FileIndex != 2
            || streams[1].Magnet().AnnounceUrls.Count != 1)
            throw new InvalidOperationException("An add-on stream was misclassified.");
        var tracks = await client.GetSubtitlesAsync(manifest, "series", "tt1234567:2:10", streams[0], CancellationToken.None);
        var subtitle = await client.LoadSubtitlesAsync(tracks.Single(), CancellationToken.None);
        if (subtitle.At(1000) != "First line\nSecond line" || subtitle.At(2500) != "")
            throw new InvalidOperationException("The external subtitle timeline was read incorrectly.");
        Console.WriteLine("Stremio add-on protocol and timed subtitles verified.");
    }

    private sealed class Fixture : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var route = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            var response = route switch
            {
                "/config/stream/series/tt1234567:2:10.json" => """
                    {"streams":[
                      {"url":"https://fixture.example/film.mp4","behaviorHints":{"filename":"episode 10.mp4","videoSize":10000}},
                      {"infoHash":"0123456789abcdef0123456789abcdef01234567","fileIdx":2,"sources":["tracker:udp://fixture.example:6969/announce"]},
                      {"url":"https://fixture.example/locked.mp4","behaviorHints":{"proxyHeaders":{"request":{"Authorization":"fixture"}}}},
                      {"externalUrl":"https://fixture.example/browser"}
                    ]}
                    """,
                "/config/subtitles/series/tt1234567:2:10/videoSize=10000&filename=episode 10.mp4.json" =>
                    """{"subtitles":[{"id":"fixture","url":"https://fixture.example/en.vtt","lang":"en"}]}""",
                "/en.vtt" => "WEBVTT\n\n00:01.000 --> 00:02.500 align:center\nFirst line\nSecond line\n",
                _ => throw new InvalidOperationException("Unexpected fixture route: " + route),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8)
            });
        }
    }
}
