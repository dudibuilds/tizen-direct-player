using Newtonsoft.Json.Linq;
using Ru7100.Core.Trailers;
using Xunit;

public class ImdbTrailerTests
{
    private static JObject Payload(string edges) => JObject.Parse("{\"data\":{\"title\":{\"primaryVideos\":{\"edges\":[" + edges + "]}}}}");

    private static string Node(string id, string type, int runtime, string urls) =>
        "{\"node\":{\"id\":\"" + id + "\",\"name\":{\"value\":\"" + id + "\"},\"contentType\":{\"id\":\"amzn1.imdb.video.contenttype." + type + "\"},\"runtime\":{\"value\":" + runtime + "},\"playbackURLs\":[" + urls + "]}}";

    private static string Url(string mime, string def) => "{\"url\":\"https://imdb-video.media-imdb.com/" + mime + def + "\",\"videoMimeType\":\"" + mime + "\",\"videoDefinition\":\"" + def + "\"}";

    [Fact]
    public void PicksTrailerOfNormalLengthWithHighestMp4()
    {
        var payload = Payload(string.Join(",",
            Node("vi1", "clip", 120, Url("MP4", "DEF_1080p")),
            Node("vi2", "trailer", 31, Url("MP4", "DEF_1080p")),
            Node("vi3", "trailer", 137, Url("MP4", "DEF_480p") + "," + Url("MP4", "DEF_1080p") + "," + Url("M3U8", "DEF_AUTO") + "," + Url("MP4", "DEF_720p"))));
        var picked = ImdbTrailerResolver.Pick("tt1", payload);
        Assert.Equal("vi3", picked.VideoId);
        Assert.Equal(1080, picked.Height);
        Assert.False(picked.IsHls);
        Assert.EndsWith("MP4DEF_1080p", picked.Url);
    }

    [Fact]
    public void FallsBackToHlsAndIgnoresLowDefinitions()
    {
        var payload = Payload(Node("vi1", "trailer", 90, Url("MP4", "DEF_SD") + "," + Url("M3U8", "DEF_AUTO")));
        var picked = ImdbTrailerResolver.Pick("tt1", payload);
        Assert.True(picked.IsHls);
        Assert.EndsWith("M3U8DEF_AUTO", picked.Url);
    }

    [Fact]
    public void NoVideosMeansNoTrailer() => Assert.Null(ImdbTrailerResolver.Pick("tt1", Payload("")));
}
