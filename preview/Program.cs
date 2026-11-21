using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Ru7100.Core.Ui;

namespace Ru7100.Preview
{
    /// Single "UI thread" for the preview: keys, ticks and async continuations all run under one lock,
    /// like the EFL main loop on the TV.
    internal sealed class UiLoop : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> posted = new ConcurrentQueue<Action>();
        public readonly object Gate = new object();

        public void Post(Action action) => posted.Enqueue(action);

        public void Drain()
        {
            while (posted.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine("UI action failed: " + error);
                }
            }
        }
    }

    internal static class Program
    {
        /// Same cadence as the TV host: 30 fps frames, shell ticks every 100 ms.
        private const int FrameMs = 33;
        private const int ShellTickMs = 100;

        private static TvShell shell;
        private static FeedbackInbox feedback;
        private static readonly Ru7100.Core.Trailers.ImdbTrailerResolver imdbTrailers = new Ru7100.Core.Trailers.ImdbTrailerResolver();
        /// Changes on every server start; the page reloads itself when it sees a new one (dotnet watch restarts).
        private static readonly string BootId = Guid.NewGuid().ToString("N").Substring(0, 8);
        private static IVideoFollower video;
        private static Ru7100.Core.App.AppHost app;
        private static Composition.Built built;
        private static readonly System.Net.Http.HttpClient mediaClient = new System.Net.Http.HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        private static UiLoop loop;
        private static bool? liveArg;
        private static bool live;
        private static string dataDirectory;
        private static readonly UiAnimator animator = new UiAnimator();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static UiFrame target;
        private static UiFrame frame = new UiFrame();
        private static int builtVersion = -1;
        private static int frameVersion;

        public static int Main(string[] args)
        {
            var port = 5173;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--live") liveArg = true;
                else if (args[i] == "--fake") liveArg = false;
                else if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
            }
            dataDirectory = Path.Combine(Path.GetTempPath(), "ru7100-ui-preview");
            // Each preview serves torrent streams on its own loopback port: several previews on one Mac (yours, a
            // reviewer's) used to fight over the TV's fixed port, and every source of the loser failed to start.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RU7100_MEDIA_PORT")))
                Environment.SetEnvironmentVariable("RU7100_MEDIA_PORT", (40000 + port % 20000).ToString(System.Globalization.CultureInfo.InvariantCulture));
            loop = new UiLoop();
            Reset();
            AppDomain.CurrentDomain.ProcessExit += (s, e) => built?.Cleanup();
            Console.CancelKeyPress += (s, e) => built?.Cleanup();

            var listener = new HttpListener();
            var prefix = "http://127.0.0.1:" + port + "/";
            feedback = new FeedbackInbox(FindProjectRoot(), port);
            listener.Prefixes.Add(prefix);
            listener.Start();
            Console.WriteLine("RU7100 UI preview at " + prefix + (live ? " (live add-on data)" : " (fake data)"));

            var ticker = new Thread(() =>
            {
                long lastTick = 0;
                while (true)
                {
                    lock (loop.Gate)
                    {
                        loop.Drain();
                        var now = clock.ElapsedMilliseconds;
                        if (now - lastTick >= ShellTickMs)
                        {
                            lastTick = now;
                            shell.Tick();
                        }
                        StepFrame();
                    }
                    Thread.Sleep(FrameMs);
                }
            }) { IsBackground = true, Name = "ui-tick" };
            ticker.Start();

            while (true)
            {
                var context = listener.GetContext();
                Task.Run(() => Serve(context));
            }
        }

        /// Rebuilds the layout when the shell changed and advances the animator (call under the gate).
        private static void StepFrame()
        {
            var changed = shell.Version != builtVersion;
            if (!changed && !animator.IsAnimating)
                return;
            if (changed)
            {
                builtVersion = shell.Version;
                animator.ReducedMotion = shell.Services.Player?.Settings?.ReducedMotion ?? false;
                target = UiLayout.Build(shell);
            }
            frame = animator.Step(target, clock.ElapsedMilliseconds);
            frameVersion++;
        }

        private static void Reset()
        {
            lock (loop.Gate)
            {
                built?.Cleanup();
                built = Composition.Build(liveArg, loop, dataDirectory);
                video = built.Video;
                app = built.App;
                live = built.Live;
                built.Services.ResourceUrl = relative => "/res/" + relative;   // packaged images, served from probe/shared/res
                shell = new TvShell(built.Services);
                app?.AttachShell(shell);
                shell.Start(new HomeViewModel().WithLaunchIntro());
                builtVersion = -1;
            }
        }

        private static async Task Serve(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            try
            {
                var path = request.Url.AbsolutePath;
                if (request.HttpMethod == "GET" && path == "/")
                    Write(response, "text/html; charset=utf-8", PreviewPage.Html);
                else if (request.HttpMethod == "GET" && path.StartsWith("/res/", StringComparison.Ordinal))
                    ServeResource(response, path.Substring(5));
                else if (request.HttpMethod == "GET" && path == "/state")
                {
                    int.TryParse(request.QueryString["since"], out var since);
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while (request.QueryString["since"] != null && FrameVersion() == since && DateTime.UtcNow < deadline)
                        await Task.Delay(15).ConfigureAwait(false);
                    Write(response, "application/json", State());
                }
                else if (request.HttpMethod == "POST" && path == "/video-report")
                {
                    string body;
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                        body = await reader.ReadToEndAsync().ConfigureAwait(false);
                    var report = Newtonsoft.Json.Linq.JObject.Parse(body);
                    built?.Browser?.Report((int?)report["open"] ?? -1, (double?)report["position"] ?? 0, (double?)report["duration"] ?? 0,
                        (bool?)report["ended"] ?? false, (string)report["error"]);
                    Write(response, "text/plain", "ok");
                }
                else if (request.HttpMethod == "GET" && path == "/media")
                    await ProxyMedia(request, response).ConfigureAwait(false);
                else if (request.HttpMethod == "GET" && path == "/text")
                    Write(response, "text/plain; charset=utf-8", Text());
                else if (request.HttpMethod == "GET" && path == "/snapshot.html")
                {
                    // Static render of the settled layout (no animation), e.g. for headless screenshots.
                    string html;
                    lock (loop.Gate)
                    {
                        loop.Drain();
                        html = UiHtml.RenderStage(UiLayout.Build(shell));
                    }
                    var pin = request.QueryString["pin"];
                    var marker = "";
                    if (!string.IsNullOrEmpty(pin) && pin.Split(',').Length == 2 && int.TryParse(pin.Split(',')[0], out var px) && int.TryParse(pin.Split(',')[1], out var py))
                        marker = $"<div style=\"position:absolute;left:{px - 34}px;top:{py - 34}px;width:68px;height:68px;border:6px solid #ff2d2d;border-radius:50%;box-shadow:0 0 0 4px rgba(0,0,0,.6);z-index:99999\"></div>"
                            + $"<div style=\"position:absolute;left:{px + 30}px;top:{py - 70}px;background:#ff2d2d;color:#fff;font:700 34px Arial;padding:2px 12px;border-radius:20px;z-index:99999\">{System.Net.WebUtility.HtmlEncode(request.QueryString["n"] ?? "")}</div>";
                    Write(response, "text/html; charset=utf-8", "<!doctype html><meta charset=utf-8><body style=\"margin:0;background:#000\"><div style=\"position:relative;width:1920px;height:1080px;overflow:hidden\">" + html + marker + "</div></body>");
                }
                else if (request.HttpMethod == "GET" && path == "/trailer")
                {
                    // Same source as the TV: IMDb's full-length MP4 first; null tells the page to fall back to YouTube.
                    Ru7100.Core.Trailers.ImdbTrailer trailer = null;
                    try { trailer = await imdbTrailers.ResolveAsync(request.QueryString["title"], CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception error) { Console.WriteLine("trailer lookup: " + error.Message); }
                    Write(response, "application/json", JsonConvert.SerializeObject(trailer == null ? null : new { url = trailer.Url, hls = trailer.IsHls, height = trailer.Height, seconds = trailer.RuntimeSeconds }));
                }
                else if (request.HttpMethod == "GET" && path == "/feedback")
                    Write(response, "application/json", JsonConvert.SerializeObject(feedback.List()));
                else if (request.HttpMethod == "POST" && path.StartsWith("/feedback", StringComparison.Ordinal))
                {
                    string body;
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                        body = await reader.ReadToEndAsync().ConfigureAwait(false);
                    var json = string.IsNullOrWhiteSpace(body) ? new Newtonsoft.Json.Linq.JObject() : Newtonsoft.Json.Linq.JObject.Parse(body);
                    object result;
                    if (path == "/feedback/add")
                    {
                        var note = feedback.Add((int?)json["x"] ?? 0, (int?)json["y"] ?? 0, (string)json["key"], (string)json["text"], (string)json["comment"], Text());
                        result = new { id = note.Id };
                    }
                    else if (path == "/feedback/delete")
                        result = new { ok = feedback.Remove((int?)json["id"] ?? -1) };
                    else if (path == "/feedback/update")
                    {
                        feedback.Update((int?)json["id"] ?? -1, (string)json["comment"]);
                        result = new { ok = true };
                    }
                    else if (path == "/feedback/send")
                    {
                        var folder = feedback.Send();
                        if (folder != null)
                            Console.WriteLine("UI feedback batch: " + folder);
                        result = new { folder };
                    }
                    else
                    {
                        response.StatusCode = 404;
                        result = new { error = "unknown" };
                    }
                    Write(response, "application/json", JsonConvert.SerializeObject(result));
                }
                else if (request.HttpMethod == "GET" && path == "/keymap")
                    Write(response, "application/json", JsonConvert.SerializeObject(PreviewPage.KeyMap));
                else if (request.HttpMethod == "POST" && path == "/trailer-started")
                {
                    string id;
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                        id = (await reader.ReadToEndAsync().ConfigureAwait(false)).Trim();
                    lock (loop.Gate)
                    {
                        loop.Drain();
                        shell.Services.ReportTrailerStarted(id);
                        StepFrame();
                    }
                    Write(response, "text/plain", "ok");
                }
                else if (request.HttpMethod == "POST" && path == "/key")
                {
                    string body;
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                        body = (await reader.ReadToEndAsync().ConfigureAwait(false)).Trim();
                    var name = request.QueryString["key"] ?? body;
                    var key = Enum.TryParse<RemoteKey>(name, true, out var parsed) && Enum.IsDefined(typeof(RemoteKey), parsed) ? parsed : RemoteKeys.FromTizen(name);
                    if (key == RemoteKey.None)
                    {
                        response.StatusCode = 400;
                        Write(response, "text/plain", "Unknown key " + name);
                        return;
                    }
                    bool handled;
                    lock (loop.Gate)
                    {
                        loop.Drain();
                        handled = shell.HandleKey(key);
                        StepFrame();
                    }
                    if (!handled && key == RemoteKey.Back)
                        Console.WriteLine("Back on the root screen: the TV app would exit here.");
                    Write(response, "text/plain; charset=utf-8", Text());
                }
                else if (request.HttpMethod == "POST" && path == "/reset")
                {
                    Reset();
                    Write(response, "text/plain; charset=utf-8", Text());
                }
                else
                {
                    response.StatusCode = 404;
                    Write(response, "text/plain", "Not found");
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                try
                {
                    response.StatusCode = 500;
                    Write(response, "text/plain", error.Message);
                }
                catch (Exception)
                {
                }
            }
        }

        /// Same-origin proxy for the torrent engine's loopback stream (range requests pass through).
        private static async Task ProxyMedia(HttpListenerRequest request, HttpListenerResponse response)
        {
            var target = request.QueryString["u"];
            if (target == null || !(target.StartsWith("http://127.0.0.1", StringComparison.Ordinal) || target.StartsWith("http://localhost", StringComparison.Ordinal)))
            {
                response.StatusCode = 400;
                Write(response, "text/plain", "Only loopback media can be proxied");
                return;
            }
            var forward = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, target);
            var range = request.Headers["Range"];
            if (!string.IsNullOrEmpty(range))
                forward.Headers.TryAddWithoutValidation("Range", range);
            try
            {
                using (var upstream = await mediaClient.SendAsync(forward, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    response.StatusCode = (int)upstream.StatusCode;
                    response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "video/mp4";
                    response.Headers["Accept-Ranges"] = "bytes";
                    if (upstream.Content.Headers.ContentRange != null)
                        response.Headers["Content-Range"] = upstream.Content.Headers.ContentRange.ToString();
                    if (upstream.Content.Headers.ContentLength.HasValue)
                        response.ContentLength64 = upstream.Content.Headers.ContentLength.Value;
                    using (var stream = await upstream.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        await stream.CopyToAsync(response.OutputStream).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // The browser cancels range requests while seeking; nothing to report.
            }
            finally
            {
                try { response.OutputStream.Close(); } catch (Exception) { }
            }
        }

        private static int FrameVersion()
        {
            lock (loop.Gate)
                return frameVersion;
        }

        private static string Text()
        {
            lock (loop.Gate)
            {
                loop.Drain();
                return UiText.RenderText(shell);
            }
        }

        /// The repository root (where AGENTS.md is), so feedback lands in local-data/ whatever the working directory.
        private static string FindProjectRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
                dir = dir.Parent;
            return dir?.FullName ?? Directory.GetCurrentDirectory();
        }

        private static string State()
        {
            lock (loop.Gate)
            {
                loop.Drain();
                var screen = shell.Current?.Kind.ToString();
                return JsonConvert.SerializeObject(new
                {
                    version = frameVersion,
                    boot = BootId,
                    screen,
                    live,
                    background = frame.Background,
                    nodes = frame.Nodes.Select(n => new
                    {
                        k = n.Key, t = (int)n.Kind, x = n.X, y = n.Y, w = n.W, h = n.H, o = n.Opacity,
                        f = n.Fill, s = n.Text, fs = n.FontSize, c = n.Color, b = n.Bold, a = (int)n.Align,
                        wr = n.Wrap, ol = n.Outline, sk = n.Stroke, img = PreviewImage(n), mk = UiGlyphs.IsGlyph(n.ImageUrl, out _), sh = PreviewShadow(n), ct = n.Contain, ab = n.AnchorBottom, cx = n.CropX == 0.5 ? (double?)null : n.CropX, cy = n.CropY == 0.5 ? (double?)null : n.CropY, v = n.VideoId, ti = n.TitleId, m = n.Muted,
                        r = n.EffectiveRadius, hz = !n.Vertical
                    }),
                    text = UiText.RenderText(shell),
                    video = screen == "Player" ? video.VideoState : null
                }, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore });
            }
        }

        /// Glyphs reach the page as an SVG mask (same shapes as the TV's PNG); shadows carry only their CSS.
        private static string PreviewImage(UiNode n) =>
            UiGlyphs.IsGlyph(n.ImageUrl, out var glyph) ? UiGlyphs.SvgDataUrl(glyph) : UiGlyphs.IsShadow(n.ImageUrl, out _, out _) ? null : n.ImageUrl;

        private static string PreviewShadow(UiNode n) =>
            UiGlyphs.IsShadow(n.ImageUrl, out var radius, out var spread) ? UiHtml.ShadowCss(radius, spread, n.Fill) : null;

        /// A file from the TV package's shared resources (probe/shared/res), the same images the TV loads.
        private static void ServeResource(HttpListenerResponse response, string relative)
        {
            var root = ResourceRoot();
            var full = root == null ? null : System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
            if (full == null || !full.StartsWith(root, StringComparison.Ordinal) || !System.IO.File.Exists(full))
            {
                response.StatusCode = 404;
                response.OutputStream.Close();
                return;
            }
            var bytes = System.IO.File.ReadAllBytes(full);
            response.ContentType = full.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : full.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? "audio/wav" : "image/jpeg";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }

        private static string resourceRoot;

        private static string ResourceRoot()
        {
            if (resourceRoot != null) return resourceRoot;
            for (var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = System.IO.Path.Combine(dir.FullName, "probe", "shared", "res");
                if (System.IO.Directory.Exists(candidate))
                    return resourceRoot = System.IO.Path.GetFullPath(candidate) + System.IO.Path.DirectorySeparatorChar;
            }
            return null;
        }

        private static void Write(HttpListenerResponse response, string type, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentType = type;
            response.Headers["Cache-Control"] = "no-store";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }
    }
}
