using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using ElmSharp;
using Ru7100.Core.Ui;

namespace Ru7100.TvUi
{
    /// Draws a UiFrame with ElmSharp: Rectangle for boxes, Label (textblock markup) for text, EvasImage for posters and shapes.
    /// Objects are kept by node key and only touched when their properties change.
    internal sealed class ElmFramePainter
    {
        private sealed class Entry
        {
            public EvasObject Object;
            public UiNodeKind Kind;
            /// The node last applied to the object; the next frame is compared against it field by field.
            public UiNode Applied;
            /// Created (or re-created) since the last restack: it sits on top of everything.
            public bool Fresh = true;
            public string ImageUrl;
            /// A rounded box whose 9-patch image could not be loaded: drawn as a plain rectangle instead.
            public bool PlainBox;
            /// Image: whether the last file load succeeded (diagnostics).
            public bool? Loaded;
            /// Image: decoded pixel size of the loaded file (0 until loaded).
            public int ImageW, ImageH;
            /// Image (logos): opaque part of the loaded picture; null = fit the whole picture.
            public PixelRect? Opaque;
            /// Image: the node last applied, so an async load can place the picture.
            public UiNode Node;
            /// Wrapped text: the lines last set after cutting to the box (Fit).
            public string FittedText;
            /// Kept hidden in the parking lot (its key left the frame); reused as is when the key comes back.
            public bool Parked;
            /// Image fill state last set (9-patch border, filled flag), so a glide only moves the object.
            public int Border = -1;
            public bool? Filled;
        }

        /// A downloaded picture waiting to be handed to the toolkit (ImageHandoff, main loop).
        private sealed class ReadyImage
        {
            public Entry Entry;
            public EvasImage Image;
            public UiNode Node;
            public string Url, Path, Key;
            public PixelRect? Opaque;
            public long Bytes;
        }

        /// Generated shape images (file://) that EFL failed to load; boxes using them fall back to rectangles.
        private readonly HashSet<string> brokenAssets = new HashSet<string>();

        private readonly Window window;
        private readonly PosterCache posters;
        private readonly Rectangle background;
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private List<string> lastOrder = new List<string>();
        private List<string> spareOrder = new List<string>();
        private readonly HashSet<string> seenKeys = new HashSet<string>();
        private string lastBackground = "";
        private readonly HashSet<string> coveredKeys = new HashSet<string>();

        private readonly ITrailerPlayer trailers;
        private string trailerKey;

        /// Work done by the last Paint call (and by image loads that completed since the one before).
        public struct Counters
        {
            public int Nodes, Created, Removed, Updated, Moved, TextSet, Restacked, FileLoads;
            public double FileLoadMs;
        }

        private Counters counters;
        private readonly System.Diagnostics.Stopwatch loadClock = new System.Diagnostics.Stopwatch();

        /// Returns the counters gathered since the last call and starts over.
        private readonly List<string> handedOver = new List<string>();

        /// Pictures handed to the toolkit since the last call ("key 1920x1080"): the toolkit decodes them inside its
        /// next render, on the main loop, so a late tick right after names its likely cause.
        public string TakeHandedOver()
        {
            if (handedOver.Count == 0)
                return null;
            var text = string.Join(", ", handedOver);
            handedOver.Clear();
            return text;
        }

        public Counters TakeCounters()
        {
            var taken = counters;
            counters = default(Counters);
            return taken;
        }

        public int ObjectCount => entries.Count;

        /// True while the viewer navigates (a key in the last moments or an animation running): fewer pixels are
        /// handed to the toolkit per frame then. Null: always the resting budget.
        public Func<bool> Navigating { get; set; }

        /// Pictures waiting for the toolkit (diagnostics).
        public int QueuedImages => handoff.Count;

        public ElmFramePainter(Window window, PosterCache posters, ITrailerPlayer trailers = null)
        {
            this.window = window;
            this.posters = posters;
            this.trailers = trailers ?? new StillImageTrailerPlayer();
            background = new Rectangle(window) { Geometry = new Rect(0, 0, UiFrame.Width, UiFrame.Height) };
            background.Show();
        }

        /// What the painter believes is on screen, for diagnostics: every image and box in stacking order with
        /// its geometry, colour, visibility, layer and the nearest object above it that covers its centre.
        /// Call on the main loop.
        public object Dump(int limit = 400)
        {
            var list = new List<object>();
            var keys = lastOrder;
            var rects = new Rect[keys.Count];
            for (var i = 0; i < keys.Count; i++)
                rects[i] = entries.TryGetValue(keys[i], out var e) ? e.Object.Geometry : new Rect(0, 0, 0, 0);
            for (var i = 0; i < keys.Count && list.Count < limit; i++)
            {
                if (!entries.TryGetValue(keys[i], out var entry) || entry.Kind == UiNodeKind.Text)
                    continue;
                var obj = entry.Object;
                var g = rects[i];
                int cx = g.X + g.Width / 2, cy = g.Y + g.Height / 2;
                string coveredBy = null;
                for (var j = i + 1; j < keys.Count; j++)
                {
                    var o = rects[j];
                    if (o.Width > 0 && o.Height > 0 && cx >= o.X && cx < o.X + o.Width && cy >= o.Y && cy < o.Y + o.Height)
                    {
                        coveredBy = keys[j] + (entries[keys[j]].Kind == UiNodeKind.Text ? " (text)" : "");
                        if (entries[keys[j]].Kind != UiNodeKind.Text)
                            break;
                    }
                }
                var c = obj.Color;
                string inner = null;
                if (obj is EvasImage && !entry.PlainBox)
                    inner = "pixels " + entry.ImageW + "x" + entry.ImageH;
                list.Add(new
                {
                    z = i, key = keys[i], type = obj.GetType().Name, kind = entry.Kind.ToString(), plainBox = entry.PlainBox,
                    x = g.X, y = g.Y, w = g.Width, h = g.Height, rgba = c.R + "," + c.G + "," + c.B + "," + c.A,
                    visible = obj.IsVisible, layer = obj.Layer, file = entry.ImageUrl == null ? null : System.IO.Path.GetFileName(entry.ImageUrl),
                    loaded = entry.Loaded, inner, coveredBy
                });
            }
            var bg = background.Geometry;
            return new
            {
                objects = keys.Count, listed = list.Count,
                background = new { x = bg.X, y = bg.Y, w = bg.Width, h = bg.Height, rgba = background.Color.R + "," + background.Color.G + "," + background.Color.B + "," + background.Color.A, visible = background.IsVisible, layer = background.Layer },
                window = new { w = window.ScreenSize.Width, h = window.ScreenSize.Height, alpha = window.Alpha },
                flags = TvUiDevFlags.Describe(),
                nodes = list
            };
        }

        /// Text nodes whose drawn text is larger than their box (clipped or ellipsised on screen). Main loop only.
        public List<object> TextOverflow()
        {
            var list = new List<object>();
            foreach (var key in lastOrder)
            {
                if (!entries.TryGetValue(key, out var entry) || entry.Kind != UiNodeKind.Text || !(entry.Object is Label label) || entry.Node == null || string.IsNullOrEmpty(entry.Node.Text))
                    continue;
                try
                {
                    var part = label.EdjeObject["elm.text"];
                    var native = part.TextBlockNativeSize;
                    var formatted = part.TextBlockFormattedSize;
                    var g = label.Geometry;
                    var tooWide = !entry.Node.Wrap && native.Width > g.Width + 1;
                    var tooTall = formatted.Height > g.Height + 1;
                    if (tooWide || tooTall)
                        list.Add(new { key, text = entry.Node.Text, size = entry.Node.FontSize, bold = entry.Node.Bold, box = g.Width + "x" + g.Height, native = native.Width + "x" + native.Height, formatted = formatted.Width + "x" + formatted.Height, tooWide, tooTall });
                }
                catch (Exception error)
                {
                    list.Add(new { key, error = error.Message });
                    break;
                }
            }
            return list;
        }

        public void Paint(UiFrame frame)
        {
            // Video hole: while the trailer player shows frames, the page background and whatever lies under the
            // trailer rect (nodes before the Trailer node) are drawn at the player's cover opacity, so the still art
            // crossfades with the hardware video plane underneath (scrims drawn after the Trailer node stay). With no
            // Trailer node left (fading out after navigation) the nodes it covered last keep fading back.
            //
            // A trailer smaller than the screen (Home's pop-out card) can't hide the page: there a transparent
            // rectangle with the COPY render op is stacked at the Trailer node's place and punches a hole through
            // everything below it (page background, neighbouring cards, the billboard) exactly at the video rect.
            // Only the nodes lying wholly inside that rect (the card's still art) crossfade with the video; they are
            // stacked just above the hole. Navigation removes the Trailer node and the hole closes at once.
            var trailerAt = frame.Nodes.FindIndex(n => n.Kind == UiNodeKind.Trailer);
            if (trailerAt >= 0 && TvUiDevFlags.NoCardHole && !FullScreen(frame.Nodes[trailerAt]))
                trailerAt = -1;
            var video = trailers as Ru7100Probe.Trailer.IVideoHoleSource;
            var holeOpen = video != null && video.ShowingVideo;
            var cover = holeOpen ? Clamp01(video.CoverOpacity) : 1;
            var cardHole = holeOpen && !(video.HoleX <= 0 && video.HoleY <= 0 && video.HoleW >= UiFrame.Width && video.HoleH >= UiFrame.Height);
            if (!holeOpen || cardHole && trailerAt < 0)
                coveredKeys.Clear();
            else if (trailerAt >= 0)
            {
                coveredKeys.Clear();
                for (var at = 0; at < trailerAt; at++)
                {
                    var n = frame.Nodes[at];
                    if (n.Key == null)
                        continue;
                    var touches = n.X < video.HoleX + video.HoleW && video.HoleX < n.X + n.W && n.Y < video.HoleY + video.HoleH && video.HoleY < n.Y + n.H;
                    var inside = n.X >= video.HoleX - 2 && n.Y >= video.HoleY - 2 && n.X + n.W <= video.HoleX + video.HoleW + 2 && n.Y + n.H <= video.HoleY + video.HoleH + 2;
                    if (cardHole ? inside : touches)
                        coveredKeys.Add(n.Key);
                }
            }
            var backgroundState = !holeOpen ? frame.Background : cardHole ? frame.Background + "@card" : frame.Background + "@" + Math.Round(cover, 2);
            if (backgroundState != lastBackground)
            {
                lastBackground = backgroundState;
                if (frame.Background == null || holeOpen && !cardHole && cover <= 0.005)
                {
                    // Player or a showing trailer: let the hardware video plane show through.
                    background.Hide();
                    window.BackgroundColor = new Color(0, 0, 0, 0);
                }
                else
                {
                    background.Color = ParseColor(frame.Background, cardHole ? 1 : cover);
                    background.Show();
                    window.BackgroundColor = holeOpen ? new Color(0, 0, 0, 0) : Color.Black;
                }
            }

            if (frame.Background == null && parkedPictures > 0)
                ReleaseParkedPictures();   // the player: nothing to scroll back to for a while
            counters.Nodes = frame.Nodes.Count;
            // Reused every frame (no per-frame allocation): the key set, and two order lists that swap roles.
            var seen = seenKeys;
            seen.Clear();
            var order = spareOrder;
            order.Clear();
            UiNode trailer = null;
            // Card hole: the still art inside the video rect waits until the hole is stacked, then goes above it.
            var deferred = cardHole && trailerAt >= 0 ? new List<UiNode>() : null;
            for (var at = 0; at < frame.Nodes.Count; at++)
            {
                var node = frame.Nodes[at];
                if (node.Kind == UiNodeKind.Trailer)
                {
                    if (at != trailerAt)
                        continue;   // only one trailer plays (the layout emits one at most)
                    trailer = node;   // not an EFL object: handed to the trailer player
                    if (deferred != null)
                    {
                        PlaceHole(node, seen, order);
                        foreach (var under in deferred)
                            Place(under, seen, order);
                        deferred = null;
                    }
                    continue;
                }
                if (holeOpen && node.Key != null && (trailerAt < 0 || at < trailerAt) && coveredKeys.Contains(node.Key))
                {
                    if (cover <= 0.005 && !cardHole)
                        continue;   // under the trailer: the video covers it
                    node = node.Clone();
                    node.Opacity *= cover;   // crossfading with the video (card: kept at 0 so it glides back without a reload)
                    if (deferred != null)
                    {
                        deferred.Add(node);
                        continue;
                    }
                }
                Place(node, seen, order);
            }

            if (entries.Count > seen.Count)
            {
                departed.Clear();
                foreach (var key in entries.Keys)
                    if (!seen.Contains(key))
                        departed.Add(key);
                foreach (var stale in departed)
                {
                    Park(stale, entries[stale]);
                    entries.Remove(stale);
                    counters.Removed++;
                }
            }

            if (trailer == null && trailerKey != null)
            {
                trailers.Hide();
                trailerKey = null;
            }
            else if (trailer != null)
            {
                if (trailer.Key != trailerKey)
                {
                    trailers.Show(trailer.TitleId, trailer.VideoId, trailer.X, trailer.Y, trailer.W, trailer.H, trailer.Muted);
                    trailerKey = trailer.Key;
                }
                else
                    trailers.SetMuted(trailer.Muted);
            }

            Restack(order);
        }

        private const string HoleKey = "\u0001video-hole";

        private static bool FullScreen(UiNode n) => n.X <= 0 && n.Y <= 0 && n.W >= UiFrame.Width && n.H >= UiFrame.Height;

        /// Creates or updates the object for one node and adds it to the stacking order.
        private void Place(UiNode node, HashSet<string> seen, List<string> order)
        {
            if (node.Key == null || !seen.Add(node.Key))
                return;
            // Dev flags (TvUiDevFlags): leave out generated gradients/corner masks, or draw every shape as a rectangle.
            var generated = node.Kind == UiNodeKind.Image && node.ImageUrl != null && node.ImageUrl.StartsWith("file://", StringComparison.Ordinal);
            if (generated && node.NinePatch == 0 && TvUiDevFlags.SkipGradients)
                return;
            order.Add(node.Key);
            if (!entries.TryGetValue(node.Key, out var entry) || entry.Kind != node.Kind)
            {
                if (entry == null)
                    entry = Unpark(node.Key, node.Kind);   // a key that left recently comes back as it was
                if (entry == null || entry.Kind != node.Kind)
                {
                    if (entry != null)
                        entry.Object.Unrealize();
                    entry = new Entry { Kind = node.Kind, Object = Create(node.Kind) };
                    counters.Created++;
                }
                entries[node.Key] = entry;
            }
            if (node.Kind == UiNodeKind.Image && node.NinePatch > 0 && !entry.PlainBox && (TvUiDevFlags.PlainBoxes || brokenAssets.Contains(node.ImageUrl)))
                MakePlainBox(entry);
            Update(entry, node);
        }

        // ---- parking: objects whose key left the frame are kept hidden for a while ----
        //
        // A row scrolled out and back (Down then Up, Right then Left) would otherwise destroy and re-create the same
        // cards and hand their pictures to the toolkit again. Parked objects are hidden (not drawn) and come back as
        // they were. Only card-sized pictures are kept (card art, posters, logos; shapes are tiny), at most 24 of
        // up to 0.3 MP: ~12 MB of decoded pixels for typical cards, ~29 MB at worst. A backdrop is released at once,
        // and every parked picture is released when the player takes the screen (memory for the torrent cache).

        private const int ParkLimit = 160;
        private const int ParkedPictureLimit = 24;
        private const int ParkedPicturePixels = 300_000;
        private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, Entry>>> parked = new Dictionary<string, LinkedListNode<KeyValuePair<string, Entry>>>();
        private readonly LinkedList<KeyValuePair<string, Entry>> parkOrder = new LinkedList<KeyValuePair<string, Entry>>();
        private int parkedPictures;
        private readonly List<string> departed = new List<string>();

        private static bool IsPicture(Entry e) => e.Kind == UiNodeKind.Image && !e.PlainBox && e.ImageUrl != null && !e.ImageUrl.StartsWith("file://", StringComparison.Ordinal);

        private void Park(string key, Entry entry)
        {
            var picture = IsPicture(entry);
            if (key == HoleKey || picture && (entry.Loaded != true || (long)entry.ImageW * entry.ImageH > ParkedPicturePixels))
            {
                entry.Object.Unrealize();
                return;
            }
            entry.Object.Hide();
            entry.Parked = true;
            entry.Fresh = true;   // restacked when it comes back
            parked[key] = parkOrder.AddLast(new KeyValuePair<string, Entry>(key, entry));
            if (picture)
                parkedPictures++;
            while (parkOrder.Count > ParkLimit || parkedPictures > ParkedPictureLimit)
            {
                // Oldest first; when only pictures are over their limit, the oldest picture goes.
                var victim = parkOrder.First;
                if (parkOrder.Count <= ParkLimit)
                    while (victim != null && !IsPicture(victim.Value.Value))
                        victim = victim.Next;
                if (victim == null)
                    break;
                Drop(victim);
            }
        }

        private void ReleaseParkedPictures()
        {
            for (var node = parkOrder.First; node != null;)
            {
                var next = node.Next;
                if (IsPicture(node.Value.Value))
                    Drop(node);
                node = next;
            }
        }

        private void Drop(LinkedListNode<KeyValuePair<string, Entry>> node)
        {
            parkOrder.Remove(node);
            parked.Remove(node.Value.Key);
            if (IsPicture(node.Value.Value))
                parkedPictures--;
            node.Value.Value.Object.Unrealize();
        }

        /// The parked object for `key`, shown again, or null. A parked object of another kind comes back hidden
        /// for the caller to release.
        private Entry Unpark(string key, UiNodeKind kind)
        {
            if (!parked.TryGetValue(key, out var node))
                return null;
            var entry = node.Value.Value;
            if (IsPicture(entry))
                parkedPictures--;
            parkOrder.Remove(node);
            parked.Remove(key);
            entry.Parked = false;
            if (entry.Kind != kind)
                return entry;
            // Pictures show only once loaded; a new source in Update hides and reloads it.
            if (entry.Kind != UiNodeKind.Image || entry.PlainBox || entry.Loaded == true)
                entry.Object.Show();
            return entry;
        }

        /// The card trailer's video hole: a fully transparent rectangle drawn with the COPY render op, so it replaces
        /// (not blends with) every pixel below it and the hardware video plane shows through at the trailer rect.
        private void PlaceHole(UiNode trailer, HashSet<string> seen, List<string> order)
        {
            if (!seen.Add(HoleKey))
                return;
            order.Add(HoleKey);
            if (!entries.TryGetValue(HoleKey, out var entry))
            {
                entry = new Entry { Kind = UiNodeKind.Box, Object = new Rectangle(window) { RenderOperation = RenderOp.Copy } };
                entries[HoleKey] = entry;
                counters.Created++;
            }
            Update(entry, new UiNode { Key = HoleKey, Kind = UiNodeKind.Box, X = trailer.X, Y = trailer.Y, W = trailer.W, H = trailer.H, Fill = "#00000000" });
        }

        private readonly HashSet<string> restacked = new HashSet<string>();

        /// Brings the canvas stacking in line with `order`, touching only objects that are new or changed place:
        /// each of those goes right above its predecessor. Objects whose relative order is unchanged are left alone
        /// (a focus move restacks a handful of objects instead of every object on screen).
        private void Restack(List<string> order)
        {
            var old = lastOrder;
            var j = 0;
            EvasObject below = background;
            restacked.Clear();
            for (var i = 0; i < order.Count; i++)
            {
                var key = order[i];
                var entry = entries[key];
                while (j < old.Count && (restacked.Contains(old[j]) || !entries.TryGetValue(old[j], out var other) || other.Fresh))
                    j++;
                if (!entry.Fresh && j < old.Count && old[j] == key)
                    j++;
                else
                {
                    entry.Object.StackAbove(below);
                    restacked.Add(key);
                    counters.Restacked++;
                }
                entry.Fresh = false;
                below = entry.Object;
            }
            spareOrder = old;
            lastOrder = order;
        }

        private EvasObject Create(UiNodeKind kind)
        {
            switch (kind)
            {
                case UiNodeKind.Box:
                    return new Rectangle(window);
                case UiNodeKind.Image:
                    // Raw Evas image, not the elm Image widget: on the RU7100 (Tizen 5) elm Image objects load their file
                    // but never draw (verified with the on-TV test card: elm cells blank, EvasImage cells visible).
                    return new EvasImage(window) { IsSmoothScaled = true };
                default:
                    return new Label(window);
            }
        }

        /// Swaps a rounded box's image object for a square rectangle, so a broken shape image never leaves a hole.
        private void MakePlainBox(Entry entry)
        {
            entry.Object.Unrealize();
            entry.Object = new Rectangle(window);
            entry.PlainBox = true;
            entry.Applied = null;
            entry.Fresh = true;   // a new object sits on top: restacked on this paint
        }

        /// Same text and style (the box size aside).
        private static bool SameText(UiNode a, UiNode b) =>
            a.Text == b.Text && a.FontSize == b.FontSize && a.Color == b.Color && a.Bold == b.Bold && a.Align == b.Align && a.Wrap == b.Wrap
            && a.Outline == b.Outline && a.Stroke == b.Stroke && a.Fill == b.Fill;

        /// Applies a node to its object, touching only what differs from the node applied last: a glide moves the
        /// object, a fade recolours it, and text markup is rebuilt only when the text or its style changes.
        private void Update(Entry entry, UiNode node)
        {
            var last = entry.Applied;
            if (ReferenceEquals(last, node))
                return;   // a node at rest arrives as the same object: nothing changed
            var first = last == null;
            var geometry = first || last.X != node.X || last.Y != node.Y || last.W != node.W || last.H != node.H;
            var opacity = first || last.Opacity != node.Opacity;
            var obj = entry.Object;
            if (entry.PlainBox || node.Kind == UiNodeKind.Box)
            {
                var fill = opacity || last.Fill != node.Fill;
                if (!geometry && !fill)
                    return;
                entry.Applied = node;
                counters.Updated++;
                if (geometry)
                    obj.Geometry = new Rect(node.X, node.Y, node.W, node.H);
                if (fill)
                    obj.Color = ParseColor(node.Fill, node.Opacity);
                if (first)
                    obj.Show();
                return;
            }
            if (node.Kind == UiNodeKind.Image)
            {
                var source = first || entry.ImageUrl != node.ImageUrl;
                var placing = geometry || source || last.Contain != node.Contain || last.AnchorBottom != node.AnchorBottom || last.CropX != node.CropX || last.CropY != node.CropY || last.NinePatch != node.NinePatch || last.Stretch != node.Stretch;
                var tint = opacity || last.Fill != node.Fill;
                if (!placing && !tint)
                    return;
                entry.Applied = node;
                entry.Node = node;
                counters.Updated++;
                var image = (EvasImage)obj;
                if (source)
                {
                    entry.ImageUrl = node.ImageUrl;
                    entry.ImageW = entry.ImageH = 0;
                    entry.Opaque = null;
                    entry.Border = -1;   // a new file: its fill state is set again once
                    entry.Filled = null;
                    if (node.ImageUrl != null && node.ImageUrl.StartsWith("file://", StringComparison.Ordinal))
                    {
                        var path = node.ImageUrl.Substring(7);
                        entry.Loaded = LoadFile(entry, image, path);
                        if (entry.Loaded == true)
                        {
                            TvUiDiagnostics.AssetLoaded(path, entry.ImageW, entry.ImageH);
                            image.Show();
                        }
                        else
                        {
                            TvUiDiagnostics.AssetLoadFailed(path);
                            brokenAssets.Add(node.ImageUrl);
                            if (node.NinePatch > 0)
                            {
                                MakePlainBox(entry);
                                Update(entry, node);
                                return;
                            }
                            obj.Hide();   // a gradient or corner mask that cannot load is left out
                        }
                    }
                    else
                    {
                        obj.Hide();
                        LoadImage(entry, image, node, node.ImageUrl, node.Key);
                    }
                }
                if (placing)
                    Place(entry, image, node);
                // Shapes are white alpha images tinted with the node colour; pictures get a white fade.
                if (tint || source)
                    obj.Color = node.Fill != null ? ParseColor(node.Fill, node.Opacity) : Fade(node.Opacity);
                return;
            }

            var content = first || !SameText(last, node);
            // Wrapped text in a box that grows or shrinks (a card's fallback title while it scales on focus): the
            // markup is rebuilt only when the lines cut to the new box differ; otherwise only the wrap width follows.
            string fitted = null;
            var rewrap = false;
            if (!content && node.Wrap && (last.W != node.W || last.H != node.H))
            {
                fitted = Fit(node).Text;
                if (fitted != entry.FittedText)
                    content = true;
                else
                    rewrap = last.W != node.W;
            }
            if (!geometry && !opacity && !content)
                return;
            entry.Applied = node;
            entry.Node = node;
            counters.Updated++;
            if (geometry)
                obj.Geometry = new Rect(node.X, node.Y, node.W, node.H);
            if (rewrap)
                ((Label)obj).LineWrapWidth = node.W;
            if (content)
            {
                var label = (Label)obj;
                label.LineWrapType = node.Wrap ? WrapType.Mixed : WrapType.None;
                label.LineWrapWidth = node.Wrap ? node.W : 0;
                // Stroked text is a display numeral (Top 10): it is never cut to "…" when a digit is a hair wider than its slot.
                label.IsEllipsis = !node.Wrap && node.Stroke == null;
                // Wrapped text sits at the top of its box and is cut to the lines that fit (TV line height is
                // taller than the browser's), so a long synopsis never overflows above or below the box.
                label.SetVerticalTextAlignment("elm.text", node.Wrap ? 0.0 : 0.5);
                var shown = node.Wrap ? Fit(node) : node;
                entry.FittedText = node.Wrap ? shown.Text : null;
                label.Text = Markup(shown);
                counters.TextSet++;
            }
            if (opacity)
                obj.Color = Fade(node.Opacity);
            if (first)
                obj.Show();
        }

        /// Loads a file into a raw Evas image; false when EFL could not decode it (size stays 0).
        private bool LoadFile(Entry entry, EvasImage image, string path)
        {
            loadClock.Restart();
            try { image.File = path; }
            catch (Exception) { return false; }
            var size = image.Size;
            counters.FileLoads++;
            counters.FileLoadMs += loadClock.Elapsed.TotalMilliseconds;
            entry.ImageW = size.Width;
            entry.ImageH = size.Height;
            return size.Width > 0 && size.Height > 0;
        }

        /// Positions the picture inside its node box. Evas images tile their fill rectangle, so: shapes and stretched
        /// images fill the box (9-patch border for rounded shapes); "cover" uses a fill larger than the box (the object
        /// clips it); "contain" shrinks the object itself to the fitted size so nothing tiles.
        private static void Place(Entry entry, EvasImage image, UiNode node)
        {
            var box = new Rect(node.X, node.Y, node.W, node.H);
            if (node.NinePatch > 0 || node.Stretch || entry.ImageW <= 0 || entry.ImageH <= 0 || node.W <= 0 || node.H <= 0)
            {
                image.Geometry = box;
                SetFilled(entry, image, true);
                if (node.NinePatch > 0 && entry.Border != node.NinePatch)
                {
                    image.SetBorder(node.NinePatch, node.NinePatch, node.NinePatch, node.NinePatch);
                    entry.Border = node.NinePatch;
                }
                return;
            }
            if (node.Contain)
            {
                // Left-aligned like the browser; the opaque part of a logo is fitted (its transparent margins fall
                // outside the box and draw nothing), and logos sit on the box's bottom edge.
                var fit = ImageTrim.Fit(node.X, node.Y, node.W, node.H, entry.ImageW, entry.ImageH, entry.Opaque, node.AnchorBottom);
                image.Geometry = new Rect(fit.X, fit.Y, fit.W, fit.H);
                SetFilled(entry, image, true);
                return;
            }
            double sx = (double)node.W / entry.ImageW, sy = (double)node.H / entry.ImageH;
            var scale = Math.Max(sx, sy);
            var fw = (int)Math.Round(entry.ImageW * scale);
            var fh = (int)Math.Round(entry.ImageH * scale);
            image.Geometry = box;
            SetFilled(entry, image, false);
            // Same anchor as the browser's object-position: CropX/CropY pick which part survives the crop.
            image.SetFill(new Rect((int)Math.Round((node.W - fw) * node.CropX), (int)Math.Round((node.H - fh) * node.CropY), fw, fh));
        }

        private static void SetFilled(Entry entry, EvasImage image, bool filled)
        {
            if (entry.Filled == filled)
                return;
            image.IsFilled = filled;
            entry.Filled = filled;
        }

        private int pendingImages;

        /// Images asked for but not yet handed to the toolkit (download, logo trim, decode post). The launch intro
        /// waits for this to reach zero before its logo flies into Home, so Home's decodes never land mid-flight.
        public int PendingImages => System.Threading.Volatile.Read(ref pendingImages);

        private readonly ImageHandoff<ReadyImage> handoff = new ImageHandoff<ReadyImage>();
        private IntPtr pumpTimer;
        /// Fitted logo copies already made (source path -> copy, or null when the source needs none), so a logo seen
        /// on many cards is cut once and never probed again.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> fittedLogos = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

        private void LoadImage(Entry entry, EvasImage image, UiNode node, string url, string key)
        {
            if (string.IsNullOrEmpty(url))
                return;
            System.Threading.Interlocked.Increment(ref pendingImages);
            // Never blocks: a cache hit completes at once from memory, a miss downloads on a worker. Either way the
            // rest runs on the thread pool, and the picture reaches the toolkit through the hand-over budget.
            posters.GetAsync(url, lifetime.Token).ContinueWith(done =>
            {
                var queued = false;
                try
                {
                    if (done.Status != System.Threading.Tasks.TaskStatus.RanToCompletion || done.Result == null || entry.ImageUrl != url)
                        return;   // failed (counted and traced by PosterCache.OnError), or the node wants another picture now
                    var path = done.Result;
                    // Logos, off the main loop: a card-sized logo gets a copy cut to its opaque part and scaled to card
                    // size (the toolkit decodes whatever file it is handed on the main loop, and an 800x310 PNG per card
                    // is the next largest decode after the backdrops). Larger logos are fitted by their opaque part.
                    PixelRect? opaque = null;
                    if (node.Contain)
                    {
                        var fitted = node.W <= ImageTrim.CardLogoW && node.H <= ImageTrim.CardLogoH ? FittedLogo(path) : null;
                        if (fitted != null)
                            path = fitted;
                        else
                            opaque = ImageTrim.OpaqueBounds(path);
                    }
                    long bytes;
                    try { bytes = new System.IO.FileInfo(path).Length; }
                    catch (Exception) { bytes = -1; }
                    var ready = new ReadyImage { Entry = entry, Image = image, Node = node, Url = url, Path = path, Key = key, Opaque = opaque, Bytes = bytes };
                    queued = true;
                    if (handoff.Add(ready))
                        EcoreMainloop.PostAndWakeUp(PumpPosted);
                }
                catch (Exception)
                {
                    // ImageTrim never throws by contract; anything else leaves the card on its placeholder.
                }
                finally
                {
                    if (!queued)
                        System.Threading.Interlocked.Decrement(ref pendingImages);   // exactly once per request
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
        }

        private string FittedLogo(string path)
        {
            // The copy is deleted with its source on eviction: trust the memo only while the file is still there
            // (checked here, on the worker).
            if (fittedLogos.TryGetValue(path, out var known) && (known == null || System.IO.File.Exists(known)))
                return known;
            var fitted = ImageTrim.FittedCopy(path);
            if (fitted != null)
            {
                long bytes = 0;
                try { bytes = new System.IO.FileInfo(fitted).Length; } catch (Exception) { }
                posters.AttachDerived(path, fitted, bytes);
            }
            if (fittedLogos.Count > 2000)
                fittedLogos.Clear();
            fittedLogos[path] = fitted;
            return fitted;
        }

        /// Main loop: the first pump after the queue was idle; while pictures remain, one pump per frame follows.
        private void PumpPosted()
        {
            if (!PumpImages() || pumpTimer != IntPtr.Zero)
                return;
            pumpTimer = EcoreMainloop.AddTimer(TvUiHost.TickSeconds, () =>
            {
                var more = PumpImages();
                if (!more)
                    pumpTimer = IntPtr.Zero;
                return more;
            });
        }

        /// Hands waiting pictures to the toolkit within this frame's pixel budget, visible ones first.
        private bool PumpImages()
        {
            if (lifetime.IsCancellationRequested)
                return false;
            var navigating = Navigating?.Invoke() ?? false;
            return handoff.Pump(
                r => r.Entry.ImageUrl != r.Url || !r.Image.IsRealized,
                r => !r.Entry.Parked && OnScreen(r.Entry.Node ?? r.Node),
                HandOver,
                navigating ? ImageHandoff<ReadyImage>.NavigatingPixels : ImageHandoff<ReadyImage>.RestingPixels,
                r => System.Threading.Interlocked.Decrement(ref pendingImages));
        }

        private static bool OnScreen(UiNode n) => n != null && n.Opacity > 0 && n.X < UiFrame.Width && n.X + n.W > 0 && n.Y < UiFrame.Height && n.Y + n.H > 0;

        /// One picture to the toolkit (main loop); returns its pixel count for the budget.
        private long HandOver(ReadyImage r)
        {
            long pixels = 1;
            System.Threading.Interlocked.Decrement(ref pendingImages);
            try
            {
                LaunchProfile.Timed("image " + r.Key, () => pixels = Apply(r));
            }
            catch (Exception error)
            {
                // One bad picture never stops the queue (the next ones still arrive).
                Tizen.Log.Error("RU7100Ui", "Image hand-over failed for " + r.Key + ": " + error.Message);
            }
            return pixels;
        }

        private long Apply(ReadyImage r)
        {
            var entry = r.Entry;
            var image = r.Image;
            entry.Opaque = r.Opaque;
            entry.Loaded = LoadFile(entry, image, r.Path);
            if (entry.Loaded != true)
            {
                TvUiDiagnostics.ImageLoadFailed(r.Key, r.Path, r.Bytes);
                posters.Invalidate(r.Url);   // a missing or broken file: the next request downloads it again
                return 1;
            }
            TvUiDiagnostics.ImageLoaded(r.Key, r.Path, entry.ImageW, entry.ImageH, r.Bytes);
            if (handedOver.Count < 16)
                handedOver.Add(r.Key + " " + entry.ImageW + "x" + entry.ImageH);
            entry.Border = -1;
            entry.Filled = null;
            Place(entry, image, entry.Node ?? r.Node);
            if (!entry.Parked)
                image.Show();
            return (long)entry.ImageW * entry.ImageH;
        }

        /// Evas textblock markup.
        internal static string Markup(UiNode n)
        {
            var b = new StringBuilder();
            switch (n.Align)
            {
                case UiAlign.Center: b.Append("<align=center>"); break;
                case UiAlign.End: b.Append("<align=right>"); break;
                default: b.Append("<align=left>"); break;
            }
            b.Append("<font_size=").Append(n.FontSize.ToString(CultureInfo.InvariantCulture)).Append('>');
            if (n.Bold)
                b.Append("<font_weight=Bold>");
            b.Append("<color=").Append(TextblockColor(n.Color)).Append('>');
            if (n.Stroke != null)   // EFL has no wide text stroke: a soft outline in the stroke colour around the fill
                b.Append("<style=soft_outline outline_color=").Append(TextblockColor(n.Stroke)).Append('>');
            else if (n.Outline)
                b.Append("<style=outline_soft_shadow outline_color=#000000FF shadow_color=#000000C0>");
            if (n.Fill != null)
                b.Append("<backing=on backing_color=").Append(TextblockColor(n.Fill)).Append('>');
            var lines = (n.Text ?? "").Replace("\r\n", "\n").Split('\n');
            b.Append(string.Join("<br>", lines.Select(line => WebUtility.HtmlEncode(line ?? ""))));
            if (n.Fill != null)
                b.Append("</backing>");
            if (n.Outline || n.Stroke != null)
                b.Append("</style>");
            b.Append("</color>");
            if (n.Bold)
                b.Append("</font_weight>");
            b.Append("</font_size></align>");
            return b.ToString();
        }

        private static string TextblockColor(string hex) => string.IsNullOrEmpty(hex) ? "#FFFFFFFF" : hex.Length == 7 ? hex + "FF" : hex;

        internal static Color ParseColor(string hex, double opacity = 1)
        {
            if (string.IsNullOrEmpty(hex) || hex[0] != '#' || hex.Length < 7)
                return new Color(0, 0, 0, 0);
            int Part(int at) => int.Parse(hex.Substring(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var a = (int)Math.Round((hex.Length >= 9 ? Part(7) : 255) * Clamp01(opacity));
            return Premultiplied(Part(1), Part(3), Part(5), a);
        }

        /// Evas object colours are premultiplied: (r·a/255, g·a/255, b·a/255, a). Passing straight colours with
        /// a < 255 brightens translucent layers (the milky/lavender tint seen on the TV). Every object colour —
        /// rectangles, tinted 9-patches/ramps/corner masks and label/image fades — goes through here. Textblock
        /// markup colours (<color=#RRGGBBAA>) are straight and stay as they are.
        internal static Color Premultiplied(int r, int g, int b, int a) =>
            new Color(r * a / 255, g * a / 255, b * a / 255, a);

        /// Colour multiplier for labels and images (white with alpha = opacity), premultiplied.
        private static Color Fade(double opacity) => Premultiplied(255, 255, 255, (int)Math.Round(255 * Clamp01(opacity)));

        /// Cuts wrapped text to the lines that fit the box at the TV's line height (~1.45 × font size), with "…".
        private static UiNode Fit(UiNode node)
        {
            var text = node.Text ?? "";
            if (text.IndexOf('\n') >= 0)
                return node;   // explicit line breaks (subtitles): the layout sized the box for them
            var cut = UiFormat.ClampLines(text, node.W, node.FontSize, UiFormat.LinesIn(node.H, node.FontSize));
            if (cut.Length == text.Length)
                return node;
            var copy = node.Clone();
            copy.Text = cut;
            return copy;
        }

        private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

        public void Dispose()
        {
            trailers.Hide();
            lifetime.Cancel();
            if (pumpTimer != IntPtr.Zero)
                EcoreMainloop.RemoveTimer(pumpTimer);
            pumpTimer = IntPtr.Zero;
            foreach (var entry in entries.Values)
                entry.Object.Unrealize();
            entries.Clear();
            while (parkOrder.First != null)
                Drop(parkOrder.First);
            background.Unrealize();
        }
    }
}
