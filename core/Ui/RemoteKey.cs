using System;

namespace Ru7100.Core.Ui
{
    /// Abstract remote keys. Every screen is driven by these alone, so the physical remote reaches every feature.
    public enum RemoteKey
    {
        None,
        Up,
        Down,
        Left,
        Right,
        Select,       // center button
        Back,
        PlayPause,
        Play,
        Pause,
        Stop,
        Rewind,
        FastForward,
        Next,
        Previous,
        Menu,
        Info,
        Red,
        Green,
        Yellow,
        Blue,
        ChannelUp,    // CH+ (no screen uses it yet)
        ChannelDown,  // CH-
        Digit0, Digit1, Digit2, Digit3, Digit4, Digit5, Digit6, Digit7, Digit8, Digit9
    }

    public static class RemoteKeys
    {
        /// Maps a Tizen/EFL key name (EcoreKeyEventArgs.KeyName) to a remote key. Unknown names map to None.
        public static RemoteKey FromTizen(string keyName)
        {
            switch (keyName ?? "")
            {
                case "Up": case "KP_Up": return RemoteKey.Up;
                case "Down": case "KP_Down": return RemoteKey.Down;
                case "Left": case "KP_Left": return RemoteKey.Left;
                case "Right": case "KP_Right": return RemoteKey.Right;
                case "Return": case "Enter": case "KP_Enter": case "select": return RemoteKey.Select;
                case "XF86Back": case "Escape": case "BackSpace": return RemoteKey.Back;
                case "XF86PlayBack": case "XF86Playback": return RemoteKey.PlayPause;
                case "XF86AudioPlay": return RemoteKey.Play;
                case "XF86AudioPause": return RemoteKey.Pause;
                case "XF86AudioStop": return RemoteKey.Stop;
                case "XF86AudioRewind": return RemoteKey.Rewind;
                case "XF86AudioForward": return RemoteKey.FastForward;
                case "XF86AudioNext": return RemoteKey.Next;
                case "XF86AudioPrev": return RemoteKey.Previous;
                case "XF86SimpleMenu": case "XF86Menu": case "Menu": return RemoteKey.Menu;
                case "XF86Info": return RemoteKey.Info;
                case "XF86Red": return RemoteKey.Red;
                case "XF86Green": return RemoteKey.Green;
                case "XF86Yellow": return RemoteKey.Yellow;
                case "XF86Blue": return RemoteKey.Blue;
                case "XF86RaiseChannel": return RemoteKey.ChannelUp;
                case "XF86LowerChannel": return RemoteKey.ChannelDown;
            }
            if (keyName != null && keyName.Length == 1 && keyName[0] >= '0' && keyName[0] <= '9')
                return RemoteKey.Digit0 + (keyName[0] - '0');
            return RemoteKey.None;
        }

        public static bool IsMediaKey(RemoteKey key)
        {
            switch (key)
            {
                case RemoteKey.PlayPause:
                case RemoteKey.Play:
                case RemoteKey.Pause:
                case RemoteKey.Stop:
                case RemoteKey.Rewind:
                case RemoteKey.FastForward:
                case RemoteKey.Next:
                case RemoteKey.Previous:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// One key event as seen by a screen. RepeatCount counts consecutive presses of the same key
    /// arriving within TvShell.RepeatWindowMs of each other (held key auto-repeat).
    public struct KeyPress
    {
        public KeyPress(RemoteKey key, int repeatCount, long atMs)
        {
            Key = key;
            RepeatCount = repeatCount;
            AtMs = atMs;
        }

        public RemoteKey Key { get; }
        public int RepeatCount { get; }
        public long AtMs { get; }
        public bool IsRepeat => RepeatCount > 0;
    }
}
