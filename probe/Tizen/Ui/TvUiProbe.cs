using System;
using System.Collections.Generic;
using ElmSharp;
using Ru7100.Core.Ui;

namespace Ru7100.TvUi
{
    /// Dev-only strip of test objects along the top edge (flag "probe"). Each 150 px cell draws the same thing a
    /// different way, with a number under it, so one photo tells which primitives this TV actually shows:
    ///   1 Rectangle, opaque red                     2 Rectangle, white at 50 %
    ///   3 elm Image: rounded PNG, no colour set     4 elm Image: rounded PNG, colour set to green
    ///   5 elm Image: rounded PNG as 9-patch, blue   6 EvasImage (raw): rounded PNG, filled, no colour
    ///   7 EvasImage (raw): rounded PNG, amber       8 elm Image: vertical ramp, white
    ///   9 EvasImage (raw): vertical ramp, white     10 elm Image: first cached poster, no colour set
    ///   11 EvasImage (raw): first cached poster     12 elm Image: poster, painter's opaque fade colour
    ///   13 elm Image: poster, painter's 50 % fade colour (darker than half-transparent = colour premultiplied twice)
    /// Results (visibility, geometry, load result) are in the diagnostics dump under `probe`.
    internal sealed class TvUiProbe
    {
        private readonly List<EvasObject> objects = new List<EvasObject>();
        private readonly List<object> report = new List<object>();

        public object Report => report.ToArray();

        public TvUiProbe(Window window, UiAssets assets, string posterDirectory)
        {
            var round = assets.RoundedRect(12);
            var ramp = assets.Ramp(true, false);
            string poster = null;
            try
            {
                foreach (var file in System.IO.Directory.GetFiles(posterDirectory, "*.jpg"))
                {
                    poster = file;
                    break;
                }
            }
            catch (Exception)
            {
            }
            const int cell = 145, size = 110, top = 8;
            var strip = new Rectangle(window) { Geometry = new Rect(0, 0, UiFrame.Width, 170), Color = new Color(40, 40, 40, 255) };
            Keep(strip, 0, "strip", null);
            for (var i = 1; i <= 13; i++)
            {
                var x = 20 + (i - 1) * cell;
                var rect = new Rect(x, top, size, size);
                string error = null;
                bool? loaded = null;
                EvasObject made = null;
                try
                {
                    switch (i)
                    {
                        case 1: made = new Rectangle(window) { Geometry = rect, Color = new Color(255, 0, 0, 255) }; break;
                        case 2: made = new Rectangle(window) { Geometry = rect, Color = new Color(128, 128, 128, 128) }; break;
                        case 3: made = ElmImage(window, rect, round, null, 0, out loaded); break;
                        case 4: made = ElmImage(window, rect, round, new Color(0, 200, 0, 255), 0, out loaded); break;
                        case 5: made = ElmImage(window, rect, round, new Color(0, 90, 255, 255), 12, out loaded); break;
                        case 6: made = RawImage(window, rect, round, null); break;
                        case 7: made = RawImage(window, rect, round, new Color(255, 180, 0, 255)); break;
                        case 8: made = ElmImage(window, rect, ramp, null, 0, out loaded); break;
                        case 9: made = RawImage(window, rect, ramp, null); break;
                        case 10: if (poster != null) made = ElmImage(window, rect, poster, null, 0, out loaded); break;
                        case 11: if (poster != null) made = RawImage(window, rect, poster, null); break;
                        case 12: if (poster != null) made = ElmImage(window, rect, poster, ElmFramePainter.Premultiplied(255, 255, 255, 255), 0, out loaded); break;
                        case 13: if (poster != null) made = ElmImage(window, rect, poster, ElmFramePainter.Premultiplied(255, 255, 255, 128), 0, out loaded); break;
                    }
                }
                catch (Exception e)
                {
                    error = e.GetType().Name + ": " + e.Message;
                }
                if (made != null)
                    Keep(made, i, null, loaded);
                else
                    report.Add(new { cell = i, made = false, error });
                var label = new Label(window) { Geometry = new Rect(x, top + size + 2, size, 40), IsAutoMirroredMode = false, IsMirroredMode = false };
                label.Text = "<align=center><font_size=30><color=#FFFFFFFF>" + i + "</color></font_size></align>";
                Keep(label, -i, null, null);
            }
        }

        private static EvasObject ElmImage(Window window, Rect rect, string file, Color? color, int border, out bool? loaded)
        {
            var image = new Image(window) { IsFixedAspect = false, CanFillOutside = false, IsAutoMirroredMode = false, IsMirroredMode = false };
            image.Geometry = rect;
            loaded = image.Load(file);
            if (border > 0)
                image.SetBorder(border, border, border, border);
            if (color != null)
                image.Color = color.Value;
            return image;
        }

        private static EvasObject RawImage(Window window, Rect rect, string file, Color? color)
        {
            var image = new EvasImage(window) { IsFilled = true, File = file };
            image.Geometry = rect;
            if (color != null)
                image.Color = color.Value;
            return image;
        }

        private void Keep(EvasObject obj, int cell, string name, bool? loaded)
        {
            obj.Show();
            obj.RaiseTop();
            objects.Add(obj);
            if (cell <= 0 && name == null)
                return;
            var g = obj.Geometry;
            report.Add(new { cell, name, made = true, type = obj.GetType().Name, loaded, visible = obj.IsVisible, layer = obj.Layer, x = g.X, y = g.Y, w = g.Width, h = g.Height });
        }

        /// Keeps the strip above everything the painter raises.
        public void RaiseAll()
        {
            foreach (var obj in objects)
                obj.RaiseTop();
        }

        public void Dispose()
        {
            foreach (var obj in objects)
                obj.Unrealize();
            objects.Clear();
        }
    }
}
