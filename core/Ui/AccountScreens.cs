using System;
using System.Collections.Generic;
using System.Linq;

namespace Ru7100.Core.Ui
{
    /// Device-link sign-in (parity A1): shows the code and URL, polls every 3 s, renews the code before it expires.
    public sealed class SignInViewModel : Screen
    {
        public const int PollIntervalMs = 3000;
        public const int CodeLifetimeMs = 9 * 60 * 1000;

        private long nextPollMs;
        private long codeExpiresMs;
        private bool polling;

        public override ScreenKind Kind => ScreenKind.SignIn;
        public override string Title => "Sign in to Stremio";
        public string Code { get; private set; }
        public string Link { get; private set; }
        public bool SignedIn { get; private set; }
        public int ButtonIndex { get; private set; }
        public IReadOnlyList<string> Buttons { get; } = new[] { "New code", "Cancel" };

        protected internal override void OnPushed() => NewCode();

        private void NewCode()
        {
            Code = null;
            Link = null;
            Error = null;
            Load(token => Services.Account.BeginLinkAsync(token), result =>
            {
                Code = result.Code;
                Link = result.Link;
                codeExpiresMs = NowMs + CodeLifetimeMs;
                nextPollMs = NowMs + PollIntervalMs;
            }, error => Error = "Could not get a sign-in code: " + error.Message);
        }

        protected internal override void Tick(long nowMs)
        {
            if (Code == null || polling || SignedIn)
                return;
            if (nowMs >= codeExpiresMs)
            {
                NewCode();
                return;
            }
            if (nowMs < nextPollMs)
                return;
            polling = true;
            var code = Code;
            Load(token => Services.Account.CompleteLinkAsync(code, token), done =>
            {
                polling = false;
                nextPollMs = NowMs + PollIntervalMs;
                if (!done)
                    return;
                SignedIn = true;
                Shell.Pop(this);
                Shell.ShowToast("Signed in to Stremio");
            }, _ =>
            {
                polling = false;
                nextPollMs = NowMs + PollIntervalMs;
            });
        }

        public override bool HandleKey(KeyPress key)
        {
            switch (key.Key)
            {
                case RemoteKey.Back: return false;
                case RemoteKey.Left: ButtonIndex = 0; return true;
                case RemoteKey.Right: ButtonIndex = 1; return true;
                case RemoteKey.Select:
                    if (ButtonIndex == 0)
                        NewCode();
                    else
                        Shell.Pop(this);
                    return true;
            }
            return true;
        }
    }

    /// Installed add-ons. Row 0 installs by URL (keyboard); Select on an add-on asks to remove it.
    public sealed class AddonsViewModel : Screen
    {
        public override ScreenKind Kind => ScreenKind.Addons;
        public override string Title => "Add-ons";
        public IReadOnlyList<AddonDescriptor> Addons { get; private set; } = Array.Empty<AddonDescriptor>();
        public bool Loading { get; private set; } = true;
        /// 0 = "Install from URL", 1.. = Addons[index - 1].
        public int Index { get; private set; }

        protected internal override void OnPushed() => Refresh();
        protected internal override void OnResumed() => Refresh();

        private void Refresh()
        {
            Load(token => Services.Addons.GetInstalledAsync(token), list =>
            {
                Loading = false;
                Addons = list ?? Array.Empty<AddonDescriptor>();
                Index = Math.Min(Index, Addons.Count);
            }, error =>
            {
                Loading = false;
                Error = error.Message;
            });
        }

        public override bool HandleKey(KeyPress key)
        {
            switch (key.Key)
            {
                case RemoteKey.Back: return false;
                case RemoteKey.Up: Index = Math.Max(0, Index - 1); return true;
                case RemoteKey.Down: Index = Math.Min(Addons.Count, Index + 1); return true;
                case RemoteKey.Select:
                    if (Index == 0)
                    {
                        var registry = Services.Addons;
                        Shell.Push(new TextEntryViewModel("Install add-on", "Type the add-on's manifest URL", "Install", async (url, token) =>
                        {
                            var added = await registry.InstallAsync(url, token).ConfigureAwait(false);
                            return "Installed " + (added?.Name ?? url);
                        }, "https://"));
                    }
                    else
                    {
                        var addon = Addons[Index - 1];
                        Shell.ShowDialog(new ConfirmDialog("Remove " + addon.Name + "?", addon.TransportUrl,
                            new DialogButton("Keep", null),
                            new DialogButton("Remove", () => Run(token => Services.Addons.RemoveAsync(addon.Id, token), () =>
                            {
                                Shell.ShowToast("Removed " + addon.Name);
                                Refresh();
                            }))));
                    }
                    return true;
            }
            return true;
        }
    }

    public enum QueueButton { Start, Skip, Clear }

    /// Ordered episode queue. Buttons row on top; Select on an item asks to remove it.
    public sealed class QueueViewModel : Screen
    {
        public override ScreenKind Kind => ScreenKind.Queue;
        public override string Title => "Queue";
        public QueueSnapshot Queue { get; private set; } = new QueueSnapshot();
        public bool Loading { get; private set; } = true;
        /// -1 = buttons row, otherwise item index.
        public int Index { get; private set; } = -1;
        public int ButtonIndex { get; private set; }
        public IReadOnlyList<QueueButton> Buttons { get; } = new[] { QueueButton.Start, QueueButton.Skip, QueueButton.Clear };

        public string ButtonLabel(QueueButton button)
        {
            switch (button)
            {
                case QueueButton.Start: return Queue.Status == "playing" ? "Show player" : Queue.Status == "ready" ? "Start queue" : "Resume queue";
                case QueueButton.Skip: return "Skip current";
                default: return "Clear queue";
            }
        }

        protected internal override void OnPushed() => Refresh();
        protected internal override void OnResumed() => Refresh();

        private void Refresh()
        {
            Load(token => Services.Queue.GetAsync(token), q =>
            {
                Loading = false;
                Queue = q ?? new QueueSnapshot();
                Index = Math.Min(Index, Queue.Items.Count - 1);
            }, error =>
            {
                Loading = false;
                Error = error.Message;
            });
        }

        public override bool HandleKey(KeyPress key)
        {
            switch (key.Key)
            {
                case RemoteKey.Back: return false;
                case RemoteKey.Up: Index = Math.Max(-1, Index - 1); return true;
                case RemoteKey.Down: Index = Math.Min(Queue.Items.Count - 1, Index + 1); return true;
                case RemoteKey.Left:
                    if (Index < 0) ButtonIndex = Math.Max(0, ButtonIndex - 1);
                    return true;
                case RemoteKey.Right:
                    if (Index < 0) ButtonIndex = Math.Min(Buttons.Count - 1, ButtonIndex + 1);
                    return true;
                case RemoteKey.Select:
                    if (Index >= 0)
                    {
                        var at = Index;
                        var item = Queue.Items[at];
                        Shell.ShowDialog(new ConfirmDialog("Remove from queue?", item.Name + " " + UiFormat.Episode(item.Season, item.Episode),
                            new DialogButton("Keep", null),
                            new DialogButton("Remove", () => Run(token => Services.Queue.RemoveAtAsync(at, token), Refresh))));
                        return true;
                    }
                    switch (Buttons[ButtonIndex])
                    {
                        case QueueButton.Start:
                            if (Queue.Status == "playing")
                                Shell.AttachPlayer();
                            else if (Queue.Items.Count > 0)
                                Run(token => Services.Queue.StartAsync(token), () => Shell.AttachPlayer());
                            break;
                        case QueueButton.Skip:
                            Run(token => Services.Queue.SkipAsync(token), Refresh);
                            break;
                        case QueueButton.Clear:
                            Shell.ShowDialog(new ConfirmDialog("Clear the queue?", null,
                                new DialogButton("Keep", null),
                                new DialogButton("Clear", () => Run(token => Services.Queue.ClearAsync(token), Refresh))));
                            break;
                    }
                    return true;
            }
            return true;
        }
    }
}
