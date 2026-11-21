using System;
using System.Threading.Tasks;

namespace Ru7100.Core.Ui
{
    /// Single text field with the URL keyboard (add-on manifest URLs). Submit runs the callback; success pops.
    public sealed class TextEntryViewModel : Screen
    {
        private readonly Func<string, System.Threading.CancellationToken, Task<string>> submit;

        /// submit returns a success message for the toast, or throws with a message shown inline.
        public TextEntryViewModel(string title, string prompt, string submitLabel, Func<string, System.Threading.CancellationToken, Task<string>> submit, string initial = "")
        {
            Heading = title;
            Prompt = prompt;
            this.submit = submit;
            Keyboard = new OnScreenKeyboard(true, submitLabel) { Text = initial ?? "" };
        }

        public override ScreenKind Kind => ScreenKind.TextEntry;
        public override string Title => Heading;
        public string Heading { get; }
        public string Prompt { get; }
        public OnScreenKeyboard Keyboard { get; }
        public bool Busy { get; private set; }

        public override bool HandleKey(KeyPress key)
        {
            if (key.Key == RemoteKey.Back)
                return false;
            if (Busy)
                return true;
            switch (key.Key)
            {
                case RemoteKey.Up: Keyboard.Move(0, -1); break;
                case RemoteKey.Down: Keyboard.Move(0, 1); break;
                case RemoteKey.Left: Keyboard.Move(-1, 0); break;
                case RemoteKey.Right: Keyboard.Move(1, 0); break;
                case RemoteKey.Select:
                    if (Keyboard.Press() == KeyboardResult.Submit && Keyboard.Text.Trim().Length > 0)
                    {
                        Busy = true;
                        Error = null;
                        var text = Keyboard.Text.Trim();
                        Load(token => submit(text, token), message =>
                        {
                            Busy = false;
                            Shell.Pop(this);
                            if (!string.IsNullOrEmpty(message))
                                Shell.ShowToast(message);
                        }, error =>
                        {
                            Busy = false;
                            Error = error.Message;
                        });
                    }
                    else
                        Error = null;
                    break;
            }
            return true;
        }
    }
}
