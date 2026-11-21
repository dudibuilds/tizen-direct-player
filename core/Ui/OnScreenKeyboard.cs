using System;
using System.Collections.Generic;
using System.Linq;

namespace Ru7100.Core.Ui
{
    public enum KeyboardLayout { English, Symbols }

    public enum KeyAction { Insert, Space, Backspace, Clear, NextLayout, Shift, Submit }

    public sealed class KeyCap
    {
        public KeyCap(string label, KeyAction action = KeyAction.Insert, string insert = null)
        {
            Label = label;
            Action = action;
            Insert = insert ?? (action == KeyAction.Insert ? label : null);
        }

        public string Label { get; }
        public KeyAction Action { get; }
        public string Insert { get; }
    }

    public enum KeyboardResult { None, TextChanged, Submit }

    /// Grid keyboard driven by arrows + center. Search mode: letters and digits.
    /// URL mode: letters with Shift for uppercase, plus a symbols page (: / . - _ ? = & % | , ~ + @ ...),
    /// because add-on manifest URLs carry configuration in their path.
    public sealed class OnScreenKeyboard
    {
        private static readonly string[] English = { "abcdefg", "hijklmn", "opqrstu", "vwxyz-'", "1234567", "890.,:&" };
        private static readonly string[] UrlLetters = { "abcdefg", "hijklmn", "opqrstu", "vwxyz./", "1234567", "890:-_=" };
        private static readonly string[] UrlSymbols = { "?&%,|~+", "@#!*()'", "[]{};$^", "1234567", "890:/._" };

        public OnScreenKeyboard(bool urlMode, string submitLabel)
        {
            UrlMode = urlMode;
            SubmitLabel = submitLabel;
            Text = "";
        }

        public bool UrlMode { get; }
        public string SubmitLabel { get; }
        public string Text { get; set; }
        public KeyboardLayout Layout { get; private set; } = KeyboardLayout.English;
        public bool Shift { get; private set; }
        public int Row { get; private set; }
        public int Column { get; private set; }
        public int MaxLength { get; set; } = 512;

        public IReadOnlyList<IReadOnlyList<KeyCap>> Rows
        {
            get
            {
                string[] source;
                if (UrlMode)
                    source = Layout == KeyboardLayout.Symbols ? UrlSymbols : UrlLetters;
                else
                    source = English;
                var rows = source.Select(r => (IReadOnlyList<KeyCap>)r.Select(c =>
                {
                    var s = c.ToString();
                    return new KeyCap(Shift && char.IsLetter(c) ? s.ToUpperInvariant() : s);
                }).ToList()).ToList();
                rows.Add(ActionRow());
                return rows;
            }
        }

        public KeyCap Focused
        {
            get
            {
                var rows = Rows;
                var row = rows[Math.Min(Row, rows.Count - 1)];
                return row[Math.Min(Column, row.Count - 1)];
            }
        }

        private IReadOnlyList<KeyCap> ActionRow()
        {
            if (UrlMode)
            {
                return new[]
                {
                    new KeyCap(Shift ? "ABC" : "abc", KeyAction.Shift),
                    new KeyCap(Layout == KeyboardLayout.Symbols ? "abc" : "?#&", KeyAction.NextLayout),
                    new KeyCap("Del", KeyAction.Backspace),
                    new KeyCap("Clear", KeyAction.Clear),
                    new KeyCap("https://", KeyAction.Insert, "https://"),
                    new KeyCap("/manifest.json", KeyAction.Insert, "/manifest.json"),
                    new KeyCap(SubmitLabel, KeyAction.Submit)
                };
            }
            return new[]
            {
                new KeyCap("Space", KeyAction.Space),
                new KeyCap("Del", KeyAction.Backspace),
                new KeyCap("Clear", KeyAction.Clear),
                new KeyCap(SubmitLabel, KeyAction.Submit)
            };
        }

        /// Moves focus; returns false when the move would leave the grid (the caller may move focus elsewhere).
        public bool Move(int dx, int dy)
        {
            var rows = Rows;
            Row = Math.Min(Row, rows.Count - 1);
            Column = Math.Min(Column, rows[Row].Count - 1);
            if (dy != 0)
            {
                var row = Row + dy;
                if (row < 0 || row >= rows.Count)
                    return false;
                // Keep the horizontal position proportional when rows differ in width (the action row).
                var fromCount = rows[Row].Count;
                var toCount = rows[row].Count;
                Column = fromCount == toCount ? Column : Math.Min(toCount - 1, (int)Math.Round(Column * (toCount - 1) / (double)Math.Max(1, fromCount - 1)));
                Row = row;
                return true;
            }
            var column = Column + dx;
            if (column < 0 || column >= rows[Row].Count)
                return false;
            Column = column;
            return true;
        }

        public KeyboardResult Press()
        {
            var key = Focused;
            switch (key.Action)
            {
                case KeyAction.Insert:
                    return Append(key.Insert);
                case KeyAction.Space:
                    return Append(" ");
                case KeyAction.Backspace:
                    if (Text.Length == 0)
                        return KeyboardResult.None;
                    Text = Text.Substring(0, Text.Length - 1);
                    return KeyboardResult.TextChanged;
                case KeyAction.Clear:
                    if (Text.Length == 0)
                        return KeyboardResult.None;
                    Text = "";
                    return KeyboardResult.TextChanged;
                case KeyAction.NextLayout:
                    if (!UrlMode)
                        return KeyboardResult.None;
                    Layout = Layout == KeyboardLayout.Symbols ? KeyboardLayout.English : KeyboardLayout.Symbols;
                    Reanchor(KeyAction.NextLayout);
                    return KeyboardResult.None;
                case KeyAction.Shift:
                    Shift = !Shift;
                    Reanchor(KeyAction.Shift);
                    return KeyboardResult.None;
                case KeyAction.Submit:
                    return KeyboardResult.Submit;
                default:
                    return KeyboardResult.None;
            }
        }

        /// Layouts differ in row count: keep focus on the toggle key in the action row after switching.
        private void Reanchor(KeyAction action)
        {
            var rows = Rows;
            Row = rows.Count - 1;
            var actionRow = rows[Row];
            var index = -1;
            for (var i = 0; i < actionRow.Count; i++)
                if (actionRow[i].Action == action)
                    index = i;
            Column = index >= 0 ? index : Math.Min(Column, actionRow.Count - 1);
        }

        private KeyboardResult Append(string value)
        {
            if (string.IsNullOrEmpty(value) || Text.Length + value.Length > MaxLength)
                return KeyboardResult.None;
            Text += value;
            return KeyboardResult.TextChanged;
        }

        /// Focuses the first key with this label, for tests and the preview's typing shortcut.
        public bool FocusLabel(string label)
        {
            var rows = Rows;
            for (var r = 0; r < rows.Count; r++)
                for (var c = 0; c < rows[r].Count; c++)
                    if (rows[r][c].Label == label)
                    {
                        Row = r;
                        Column = c;
                        return true;
                    }
            return false;
        }

        public void SetLayout(KeyboardLayout layout) => Layout = layout;
    }
}
