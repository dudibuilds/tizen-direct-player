using System;
using System.Collections.Generic;
using System.Linq;

namespace Ru7100.Core.Ui
{
    /// Turns the shell's state into positioned nodes. Shared by the ElmSharp painter and the browser preview,
    /// so both show the same layout. Pure: reads view-models, never changes them.
    public static partial class UiLayout
    {
        private const int M = UiScale.SafeX;
        private const int CW = UiScale.ContentW;
        private const int RightEdge = UiScale.Right;

        public static UiFrame Build(TvShell shell)
        {
            var frame = new UiFrame();
            frame.ScreenKey = shell.Current == null ? "" : shell.Current.Kind + "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(shell.Current);
            switch (shell.Current)
            {
                case HomeViewModel home:
                    if (home.Intro == null || home.Intro.ShowHome(home.LayoutNowMs))
                        Home(frame, home);
                    break;
                case SearchViewModel search: Search(frame, search); break;
                case DetailsViewModel details: Details(frame, details); break;
                case SourcePickerViewModel picker: Sources(frame, picker); break;
                case PlayerViewModel player: Player(frame, player); break;
                case SignInViewModel signIn: SignIn(frame, signIn); break;
                case AddonsViewModel addons: Addons(frame, addons); break;
                case TextEntryViewModel entry: TextEntry(frame, entry); break;
                case QueueViewModel queue: Queue(frame, queue); break;
                case BrowseViewModel browse: Browse(frame, browse); break;
            }
            // The launch intro covers Home while it loads, then lands its logo on Home's menu (LaunchIntro).
            if (shell.Current is HomeViewModel launching && launching.Intro != null)
                launching.Intro.Draw(frame, launching.LayoutNowMs);
            if (shell.Current?.Error != null && shell.Current.Kind != ScreenKind.Player)
            {
                var h = UiScale.LineH(UiScale.Secondary);
                frame.Box("screen:error:bg", 0, UiScale.Bottom - h - 16, UiFrame.Width, UiFrame.Height - (UiScale.Bottom - h - 16), UiTheme.Background);
                Line(frame, "screen:error", M, UiScale.Bottom - h, CW, shell.Current.Error, UiScale.Secondary, "#FF6B6B");
            }
            if (shell.Dialog != null)
            {
                var start = frame.Nodes.Count;
                Dialog(frame, shell.Dialog);
                Animate(frame, start, UiEnter.Grow, 160);
            }
            if (shell.Toast != null)
            {
                const int th = 88;
                var w = Math.Min(1400, 96 + UiScale.TextW(shell.Toast, UiScale.Body));
                var start = frame.Nodes.Count;
                var ty = UiScale.Bottom - th - 16;
                frame.Shadow("toast:shadow", (UiFrame.Width - w) / 2, ty, w, th, UiTheme.RadiusPill, 32, 10);
                frame.Box("toast:bg", (UiFrame.Width - w) / 2, ty, w, th, UiTheme.PanelFocused + "F5").R(UiTheme.RadiusPill);
                LineIn(frame, "toast:text", (UiFrame.Width - w) / 2 + 32, ty, th, w - 64, shell.Toast, UiScale.Body, align: UiAlign.Center);
                Animate(frame, start, UiEnter.SlideUp, 200);
            }
            return frame;
        }

        // ---- shared pieces ----

        /// Sets enter/exit animation on nodes added since `start`.
        internal static void Animate(UiFrame f, int start, UiEnter enter, int exitMs)
        {
            for (var i = start; i < f.Nodes.Count; i++)
            {
                f.Nodes[i].Enter = enter;
                f.Nodes[i].ExitMs = exitMs;
            }
        }

        /// One line of text in a box exactly one TV line tall (UiScale.LineH), so the painter never clips it.
        /// The page ink of this frame: UiTheme.Background, or a service page's tinted ink.
        private static string Ink(UiFrame f) => f.Background ?? UiTheme.Background;

        private static UiNode Line(UiFrame f, string key, int x, int y, int w, string text, int size, string color = null, bool bold = false, UiAlign align = UiAlign.Start) =>
            f.Text(key, x, y, w, UiScale.LineH(size), text, size, color, bold, align);

        /// One line of text centred vertically in a row (boxY, boxH).
        private static UiNode LineIn(UiFrame f, string key, int x, int boxY, int boxH, int w, string text, int size, string color = null, bool bold = false, UiAlign align = UiAlign.Start) =>
            Line(f, key, x, boxY + (boxH - UiScale.LineH(size)) / 2, w, text, size, color, bold, align);

        /// Pill button by default; pass a radius for squarer keys (keyboard).
        private static void Button(UiFrame f, string key, int x, int y, int w, int h, string label, bool focused, int size = UiScale.Button, int radius = UiTheme.RadiusPill, string fill = UiTheme.Panel)
        {
            f.Box(key + ":bg", x, y, w, h, focused ? UiTheme.FocusFill : fill).R(radius);
            LineIn(f, key + ":label", x + 12, y, h, w - 24, label, size, focused ? UiTheme.TextOnFocus : UiTheme.Text, focused, UiAlign.Center);
        }

        private static int ButtonWidth(string label, int size = UiScale.Button) => Math.Max(UiScale.ButtonMinW, 2 * UiScale.ButtonPadX + UiScale.TextW(label, size));

        /// Screen title and optional subtitle; returns the y where content can start.
        private static int Header(UiFrame f, string title, string subtitle = null)
        {
            var y = UiScale.SafeTop;
            Line(f, "header:title", M, y, CW, title, UiScale.Title, bold: true);
            y += UiScale.LineH(UiScale.Title);
            if (subtitle != null)
            {
                Line(f, "header:subtitle", M, y, CW, subtitle, UiScale.Body, UiTheme.TextDim);
                y += UiScale.LineH(UiScale.Body);
            }
            return y + UiScale.GapLarge;
        }

        /// First index of a window of `visible` items that keeps `focus` in view.
        private static int Window(int focus, int count, int visible, int lead = 1)
        {
            if (count <= visible)
                return 0;
            return Math.Max(0, Math.Min(focus - lead, count - visible));
        }

        private static void ProgressBar(UiFrame f, string key, int x, int y, int w, int h, double ratio, string fill = UiTheme.Accent, string track = "#FFFFFF33")
        {
            f.Box(key + ":track", x, y, w, h, track).R(UiTheme.RadiusPill);
            var filled = (int)Math.Round(w * Math.Max(0, Math.Min(1, ratio)));
            if (filled > 0)
                f.Box(key + ":fill", x, y, filled, h, fill).R(UiTheme.RadiusPill);
        }

        // ---- Search and keyboards ----

        /// Width of a keyboard of `cellW` px keys with `gap` px between them (7 keys per row).
        private static int KeyboardWidth(int cellW, int gap) => 7 * cellW + 6 * gap;

        /// Key widths of one keyboard row: letter rows are uniform; rows with word keys ("Clear", "/manifest.json")
        /// give each key room for its label, then share what is left so the row spans the grid.
        private static int[] KeyWidths(IReadOnlyList<KeyCap> row, int cellW, int gap)
        {
            var gridWidth = KeyboardWidth(cellW, gap);
            var available = gridWidth - (row.Count - 1) * gap;
            var widths = new int[row.Count];
            if (row.All(k => k.Label.Length <= 1))
            {
                for (var i = 0; i < widths.Length; i++)
                    widths[i] = row.Count == 7 ? cellW : available / row.Count;
                return widths;
            }
            for (var i = 0; i < widths.Length; i++)
                widths[i] = Math.Max(cellW * 6 / 10, UiScale.TextW(row[i].Label, UiScale.Small) + 32);
            var sum = widths.Sum();
            for (var i = 0; i < widths.Length; i++)
                widths[i] = sum > available ? widths[i] * available / sum : widths[i] + (available - sum) / widths.Length;
            return widths;
        }

        private static void Keyboard(UiFrame f, string prefix, OnScreenKeyboard kb, bool focused, int x0, int y0, int cellW, int cellH, int gap)
        {
            var rows = kb.Rows;
            for (var r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                var widths = KeyWidths(row, cellW, gap);
                var x = x0;
                for (var c = 0; c < row.Count; c++)
                {
                    var w = widths[c];
                    var isFocused = focused && r == kb.Row && c == Math.Min(kb.Column, row.Count - 1);
                    var label = row[c].Label;
                    var size = label.Length > 1 ? (UiScale.TextW(label, UiScale.Small) > w - 24 ? UiScale.Hint : UiScale.Small) : UiScale.KeyCap;
                    Button(f, prefix + ":" + r + ":" + c, x, y0 + r * (cellH + gap), w, cellH, label, isFocused, size, 14);
                    x += w + gap;
                }
            }
        }

        /// The end of `text` that fits `lines` lines of a `width` px box, led by "…" when cut (the caret end stays visible).
        private static string Tail(string text, int width, int size, int lines)
        {
            var capacity = lines * Math.Max(8, (int)(width / (size * UiFormat.GlyphWidth))) - 2;
            return text.Length <= capacity ? text : "…" + text.Substring(text.Length - capacity + 1);
        }

        private static void TextEntry(UiFrame f, TextEntryViewModel vm)
        {
            var top = Header(f, vm.Heading, vm.Prompt) - 16;
            const int lines = 2;
            var fieldH = lines * UiScale.LineH(UiScale.Body) + 32;
            f.Box("entry:field", M, top, CW, fieldH, UiTheme.Panel).R(UiTheme.RadiusCard);
            var text = Tail(vm.Keyboard.Text + "_", CW - 64, UiScale.Body, lines);
            f.Text("entry:text", M + 32, top + 16, CW - 64, lines * UiScale.LineH(UiScale.Body), text, UiScale.Body, wrap: true);
            const int cellW = 140, cellH = 82, gap = 10;   // wide keys: URL keyboards have long word keys
            var kbTop = top + fieldH + 24;
            var kbX = (UiFrame.Width - KeyboardWidth(cellW, gap)) / 2;
            Keyboard(f, "kb", vm.Keyboard, !vm.Busy, kbX, kbTop, cellW, cellH, gap);
            if (vm.Busy)
                Line(f, "entry:busy", M, kbTop, kbX - M - 24, "Working…", UiScale.Body, UiTheme.TextDim);
        }

        // ---- Sign-in, add-ons, queue ----

        private static void SignIn(UiFrame f, SignInViewModel vm)
        {
            var y = Header(f, "Sign in to Stremio");
            Line(f, "signin:step1", M, y, CW, "1. On your phone or computer, open:", UiScale.Body, UiTheme.TextDim);
            y += UiScale.LineH(UiScale.Body) + 8;
            Line(f, "signin:link", M, y, CW, vm.Link ?? "…", UiScale.Title, bold: true);
            y += UiScale.LineH(UiScale.Title) + 24;
            Line(f, "signin:step2", M, y, CW, "2. Enter this code:", UiScale.Body, UiTheme.TextDim);
            y += UiScale.LineH(UiScale.Body) + 16;
            const int codeSize = 120, codeH = 210;
            f.Box("signin:codebg", M, y, 900, codeH, UiTheme.Panel).R(UiTheme.RadiusPanel);
            LineIn(f, "signin:code", M, y, codeH, 900, vm.Code ?? (vm.Error == null ? "…" : "—"), codeSize, bold: true, align: UiAlign.Center);
            y += codeH + 32;
            Line(f, "signin:wait", M, y, CW, vm.SignedIn ? "Signed in" : "Waiting for approval. This screen updates on its own.", UiScale.Body, UiTheme.TextDim);
            y += UiScale.LineH(UiScale.Body) + 32;
            var x = M;
            for (var i = 0; i < vm.Buttons.Count; i++)
            {
                var w = ButtonWidth(vm.Buttons[i]);
                Button(f, "signin:button:" + i, x, y, w, UiScale.ButtonH, vm.Buttons[i], vm.ButtonIndex == i);
                x += w + 24;
            }
        }

        private static void Addons(UiFrame f, AddonsViewModel vm)
        {
            var top = Header(f, "Add-ons", vm.Loading ? "Loading…" : vm.Addons.Count + " installed");
            const int rowH = 124;
            var visible = (UiScale.Bottom - top + 12) / rowH;
            var count = vm.Addons.Count + 1;
            var first = Window(vm.Index, count, visible);
            for (var i = first; i < count && i < first + visible; i++)
            {
                var y = top + (i - first) * rowH;
                var focused = i == vm.Index;
                var key = "addon:" + i;
                f.Box(key + ":bg", M, y, CW, rowH - 12, focused ? UiTheme.Focus : UiTheme.Panel).R(UiTheme.RadiusCard);
                var color = focused ? UiTheme.TextOnFocus : UiTheme.Text;
                if (i == 0)
                {
                    LineIn(f, key + ":name", M + 32, y, rowH - 12, CW - 64, "+ Install add-on from URL", UiScale.Body, color, true);
                    continue;
                }
                var a = vm.Addons[i - 1];
                Line(f, key + ":name", M + 32, y + 8, CW - 64, a.Name ?? a.Id, UiScale.Body, color, true);
                Line(f, key + ":url", M + 32, y + 8 + UiScale.LineH(UiScale.Body), CW - 64, (a.TransportUrl ?? "") + "   ·   " + string.Join(", ", a.Resources), UiScale.Small, focused ? "#333333" : UiTheme.TextDim);
            }
        }

        private static void Queue(UiFrame f, QueueViewModel vm)
        {
            var q = vm.Queue;
            var top = Header(f, "Queue", vm.Loading ? "Loading…" : q.Items.Count == 0 ? "The queue is empty. Add episodes from a show's page." : q.Items.Count + " items · " + q.Status) - 8;
            var x = M;
            for (var i = 0; i < vm.Buttons.Count; i++)
            {
                var label = vm.ButtonLabel(vm.Buttons[i]);
                var w = ButtonWidth(label);
                Button(f, "queue:button:" + i, x, top, w, UiScale.ButtonH, label, vm.Index < 0 && vm.ButtonIndex == i);
                x += w + 24;
            }
            const int rowH = UiScale.ListRowH;
            var listTop = top + UiScale.ButtonH + 32;
            var visible = (UiScale.Bottom - listTop + 12) / rowH;
            var first = Window(Math.Max(0, vm.Index), q.Items.Count, visible);
            for (var i = first; i < q.Items.Count && i < first + visible; i++)
            {
                var item = q.Items[i];
                var y = listTop + (i - first) * rowH;
                var focused = i == vm.Index;
                var key = "queue:item:" + i;
                f.Box(key + ":bg", M, y, CW, rowH - 12, focused ? UiTheme.Focus : UiTheme.Panel).R(UiTheme.RadiusCard);
                var current = i == q.CurrentIndex && (q.Status == "playing" || q.Status == "paused");
                var text = (current ? "Now: " : (i + 1) + ". ") + item.Name + "  " + UiFormat.Episode(item.Season, item.Episode) + (string.IsNullOrEmpty(item.Title) ? "" : " · " + item.Title);
                LineIn(f, key + ":text", M + 32, y, rowH - 12, CW - 64, text, UiScale.Body, focused ? UiTheme.TextOnFocus : i < q.CurrentIndex ? UiTheme.TextDim : UiTheme.Text, current);
            }
        }

        // Dialog: a centred panel; buttons sit in one row when they fit the content width, otherwise they stack.
        internal const int DialogX = 400;
        internal const int DialogW = UiFrame.Width - 2 * DialogX;   // 1120
        private const int DialogPad = 56;

        private static void Dialog(UiFrame f, ConfirmDialog d)
        {
            const int contentX = DialogX + DialogPad, contentW = DialogW - 2 * DialogPad;
            const int buttonH = UiScale.ButtonH, stackH = 76, stackStep = 88, messageLines = 2;
            var widths = d.Buttons.Select(b => ButtonWidth(b.Label)).ToList();
            var inRow = widths.Sum() + 24 * (widths.Count - 1) <= contentW;
            var hasMessage = !string.IsNullOrEmpty(d.Message);
            var titleH = UiScale.LineH(UiScale.Heading);
            var messageH = messageLines * UiScale.LineH(UiScale.Body);
            var buttonsTop = 44 + titleH + (hasMessage ? 16 + messageH : 0) + 36;
            var height = inRow ? buttonsTop + buttonH + 48 : buttonsTop + d.Buttons.Count * stackStep + 36;
            var top = Math.Max(UiScale.SafeTop, (UiFrame.Height - height) / 2);
            f.Box("dialog:scrim", 0, 0, UiFrame.Width, UiFrame.Height, UiTheme.Scrim);
            f.Shadow("dialog:shadow", DialogX, top, DialogW, height, UiTheme.RadiusPanel);
            f.Box("dialog:bg", DialogX, top, DialogW, height, UiTheme.Panel).R(UiTheme.RadiusPanel);
            Line(f, "dialog:title", contentX, top + 44, contentW, d.Title, UiScale.Heading, bold: true);
            if (hasMessage)
                f.Paragraph("dialog:message", contentX, top + 44 + titleH + 16, contentW, messageLines, d.Message, UiScale.Body, UiTheme.TextDim);
            var x = contentX;
            for (var i = 0; i < d.Buttons.Count; i++)
            {
                if (inRow)
                {
                    Button(f, "dialog:button:" + i, x, top + buttonsTop, widths[i], buttonH, d.Buttons[i].Label, d.Focus == i, fill: UiTheme.PanelFocused);
                    x += widths[i] + 24;
                }
                else
                    Button(f, "dialog:button:" + i, contentX, top + buttonsTop + i * stackStep, contentW, stackH, d.Buttons[i].Label, d.Focus == i, fill: UiTheme.PanelFocused);
            }
        }
    }
}
