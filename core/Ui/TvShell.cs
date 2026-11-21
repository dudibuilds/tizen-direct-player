using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100.Core.Ui
{
    public enum ScreenKind { Home, Search, Details, SourcePicker, Player, SignIn, Addons, TextEntry, Queue, Browse }

    /// Base of every screen view-model. Screens mutate state only on the UI thread (key handlers, Tick,
    /// and Load continuations, which come back through IUiDispatcher).
    public abstract class Screen
    {
        private CancellationTokenSource lifetime = new CancellationTokenSource();

        public TvShell Shell { get; internal set; }
        public abstract ScreenKind Kind { get; }
        public virtual string Title => Kind.ToString();
        public bool IsAlive => !lifetime.IsCancellationRequested;
        /// True while a Load call is running.
        public int PendingLoads { get; private set; }
        public string Error { get; protected set; }

        protected UiServices Services => Shell.Services;
        protected long NowMs => Shell.NowMs;
        protected CancellationToken Lifetime => lifetime.Token;

        /// Return true when handled. Unhandled Back pops the screen.
        public abstract bool HandleKey(KeyPress key);

        protected internal virtual void OnPushed() { }
        protected internal virtual void OnResumed() { }
        protected internal virtual void Tick(long nowMs) { }

        internal void Close()
        {
            lifetime.Cancel();
            OnClosed();
        }

        protected virtual void OnClosed() { }

        protected void Changed() => Shell?.MarkChanged();

        /// Runs fetch off the UI thread's concern and applies the result through the dispatcher.
        /// The result is dropped when the screen has been closed in the meantime, or when `cancel` (supersession by
        /// our own navigation, e.g. a newer filter) fired: our own cancels never surface as errors. A cancel we did
        /// not ask for is a timeout (HttpClient throws TaskCanceledException on its Timeout): it is retried once
        /// quietly, and a second timeout fails as a TimeoutException with a readable message.
        protected void Load<T>(Func<CancellationToken, Task<T>> fetch, Action<T> apply, Action<Exception> failed = null, CancellationToken cancel = default)
        {
            var linked = cancel.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancel) : null;
            var token = linked?.Token ?? lifetime.Token;
            PendingLoads++;
            var shell = Shell;
            // On the TV a fetch's synchronous start (cache reads, parsing, first-use compilation) runs on a worker, so
            // starting loads never holds the UI thread (it froze the launch intro for ~1.8 s).
            if (shell.Services.FetchOffUiThread)
            {
                var inner = fetch;
                fetch = t => Task.Run(() => inner(t), t);
            }
            Attempt(fetch, token, retries: 1).ContinueWith(done => shell.Services.Dispatcher.Post(() =>
            {
                PendingLoads--;
                linked?.Dispose();
                if (token.IsCancellationRequested)
                    return;
                if (done.IsFaulted || done.IsCanceled)
                {
                    var error = Classify(done.Exception?.GetBaseException(), token);
                    if (error == null)
                        return;
                    if (failed != null)
                        failed(error);
                    else
                        shell.ShowToast(error.Message);
                }
                else
                {
                    UiWork.Current = apply.Method.DeclaringType?.Name + "." + apply.Method.Name;
                    apply(done.Result);
                }
                shell.MarkChanged();
            }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        /// One fetch, retried once after a timeout (never after our own cancel or a real failure). Built from
        /// synchronous continuations (no async/await), so a result is applied on the thread that completed it, as before.
        private static Task<T> Attempt<T>(Func<CancellationToken, Task<T>> fetch, CancellationToken token, int retries)
        {
            Task<T> task;
            try { task = fetch(token) ?? Task.FromResult(default(T)); }
            catch (Exception error) { task = FromException<T>(error); }
            if (retries <= 0)
                return task;
            return task.ContinueWith(done =>
            {
                if ((done.IsFaulted || done.IsCanceled) && !token.IsCancellationRequested
                    && (done.IsCanceled || IsTimeout(done.Exception?.GetBaseException())))
                    return Attempt(fetch, token, retries - 1);
                return done;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
        }

        internal static bool IsTimeout(Exception error) =>
            error is TimeoutException || error is OperationCanceledException || error?.InnerException is TimeoutException;

        /// Null for our own cancel (nothing to show); a readable TimeoutException for a timeout; otherwise the error.
        internal static Exception Classify(Exception error, CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return null;
            if (error == null || IsTimeout(error))
                return new TimeoutException("The server took too long to answer. Try again in a moment.", error);
            return error;
        }

        protected void Run(Func<CancellationToken, Task> action, Action then = null, Action<Exception> failed = null)
        {
            Load(async token =>
            {
                await action(token).ConfigureAwait(false);
                return true;
            }, _ => then?.Invoke(), failed);
        }

        private static Task<T> FromException<T>(Exception error)
        {
            var source = new TaskCompletionSource<T>();
            source.SetException(error);
            return source.Task;
        }
    }

    /// Modal yes/no style dialog drawn above the current screen. Back cancels.
    public sealed class ConfirmDialog
    {
        public ConfirmDialog(string title, string message, params DialogButton[] buttons)
        {
            Title = title;
            Message = message;
            Buttons = buttons;
        }

        public string Title { get; }
        public string Message { get; }
        public IReadOnlyList<DialogButton> Buttons { get; }
        public int Focus { get; set; }
        public Action OnCancel { get; set; }

        internal bool HandleKey(TvShell shell, KeyPress key)
        {
            switch (key.Key)
            {
                case RemoteKey.Left:
                case RemoteKey.Up:
                    Focus = Math.Max(0, Focus - 1);
                    return true;
                case RemoteKey.Right:
                case RemoteKey.Down:
                    Focus = Math.Min(Buttons.Count - 1, Focus + 1);
                    return true;
                case RemoteKey.Select:
                    shell.CloseDialog(this);
                    Buttons[Focus].Action?.Invoke();
                    return true;
                case RemoteKey.Back:
                    shell.CloseDialog(this);
                    OnCancel?.Invoke();
                    return true;
                default:
                    return true;   // modal: swallow everything else
            }
        }
    }

    public sealed class DialogButton
    {
        public DialogButton(string label, Action action)
        {
            Label = label;
            Action = action;
        }

        public string Label { get; }
        public Action Action { get; }
    }

    /// Navigation stack, modal dialog, toast, key-repeat detection and timers.
    /// Host loop: HandleKey for every remote key, Tick about every 100-250 ms, repaint when Version changes.
    public sealed class TvShell
    {
        /// Same key again within this window counts as a held-key repeat. Tune on the TV (remote auto-repeat rate).
        public const int RepeatWindowMs = 400;
        public const int ToastMs = 4000;

        private readonly List<Screen> stack = new List<Screen>();
        private RemoteKey lastKey;
        private long lastKeyMs = long.MinValue;
        private int repeatCount;
        private long toastUntilMs;

        public TvShell(UiServices services)
        {
            Services = services ?? throw new ArgumentNullException(nameof(services));
        }

        public UiServices Services { get; }
        public IReadOnlyList<Screen> Stack => stack;
        public Screen Current => stack.Count == 0 ? null : stack[stack.Count - 1];
        public ConfirmDialog Dialog { get; private set; }
        public string Toast { get; private set; }
        /// Increments on every state change; renderers repaint when it differs from the last painted value.
        public int Version { get; private set; }
        public long NowMs => Services.Clock.UtcNow.ToUnixTimeMilliseconds();

        public event EventHandler Changed;

        public void MarkChanged()
        {
            Version++;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Start(Screen root)
        {
            while (stack.Count > 0)
                Pop();
            Push(root);
        }

        public void Push(Screen screen)
        {
            screen.Shell = this;
            stack.Add(screen);
            screen.OnPushed();
            MarkChanged();
        }

        public void Pop()
        {
            if (stack.Count == 0)
                return;
            var top = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            top.Close();
            Current?.OnResumed();
            MarkChanged();
        }

        public void Pop(Screen screen)
        {
            if (Current == screen)
                Pop();
            else if (stack.Remove(screen))
            {
                screen.Close();
                MarkChanged();
            }
        }

        public void Replace(Screen screen)
        {
            if (stack.Count > 0)
            {
                var top = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                top.Close();
            }
            Push(screen);
        }

        public void ShowDialog(ConfirmDialog dialog)
        {
            Dialog = dialog;
            MarkChanged();
        }

        internal void CloseDialog(ConfirmDialog dialog)
        {
            if (Dialog == dialog)
                Dialog = null;
            MarkChanged();
        }

        /// Back on Home (nothing left to go back to) shows this; a second Back within ExitConfirmMs leaves the app.
        public const string ExitHint = "Press Back again to exit";
        public const int ExitConfirmMs = 3000;
        private long exitArmedUntilMs = long.MinValue;

        public void ShowToast(string message)
        {
            Toast = message;
            toastUntilMs = NowMs + ToastMs;
            MarkChanged();
        }

        /// Runs an action that must outlive the current screen; failures become a toast.
        public void RunDetached(Func<CancellationToken, Task> action)
        {
            Task task;
            try
            {
                task = action(CancellationToken.None) ?? Task.CompletedTask;
            }
            catch (Exception error)
            {
                ShowToast(error.Message);
                return;
            }
            task.ContinueWith(done =>
            {
                if (done.IsFaulted)
                    Services.Dispatcher.Post(() => ShowToast(done.Exception.GetBaseException().Message));
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        /// Starts playback: pushes the player screen, which issues PlayAsync.
        public PlayerViewModel StartPlayback(PlayRequest request)
        {
            var player = new PlayerViewModel(request);
            Push(player);
            return player;
        }

        /// Shows the player for a session started elsewhere (queue, agent API).
        public PlayerViewModel AttachPlayer()
        {
            if (Current is PlayerViewModel existing)
                return existing;
            var player = new PlayerViewModel(null);
            Push(player);
            return player;
        }

        /// Returns false when nothing handled the key (Back on the root screen): the host may exit the app.
        public bool HandleKey(RemoteKey key)
        {
            if (key == RemoteKey.None)
                return false;
            var now = NowMs;
            repeatCount = key == lastKey && now - lastKeyMs <= RepeatWindowMs ? repeatCount + 1 : 0;
            lastKey = key;
            lastKeyMs = now;
            Services.LastInteractionMs = now;
            var press = new KeyPress(key, repeatCount, now);
            if (key != RemoteKey.Back)
                exitArmedUntilMs = long.MinValue;   // any other key: the next Back asks again

            bool handled;
            var player = stack.OfType<PlayerViewModel>().LastOrDefault();
            if (player != null && RemoteKeys.IsMediaKey(key) && (Dialog != null || Current != player))
            {
                // Dedicated media keys reach a running player even under a dialog or a picker opened from it.
                if (key == RemoteKey.Stop)
                {
                    Dialog = null;
                    while (Current != player)
                        Pop();
                }
                handled = player.HandleKey(press);
            }
            else if (Dialog != null)
                handled = Dialog.HandleKey(this, press);
            else
            {
                var screen = Current;
                handled = screen != null && screen.HandleKey(press);
                if (!handled && key == RemoteKey.Back && stack.Count > 1)
                {
                    Pop();
                    handled = true;
                }
                else if (!handled && key == RemoteKey.Back && repeatCount == 0)
                {
                    // Nothing left to go back to: the host would leave the app. Ask first; a held Back never confirms.
                    if (now <= exitArmedUntilMs)
                    {
                        exitArmedUntilMs = long.MinValue;
                        Toast = null;
                        MarkChanged();
                        return false;
                    }
                    exitArmedUntilMs = now + ExitConfirmMs;
                    ShowToast(ExitHint);
                    handled = true;
                }
                else if (!handled && key == RemoteKey.Back)
                    handled = true;   // auto-repeat of a held Back
            }
            MarkChanged();
            return handled;
        }

        public void Tick()
        {
            var now = NowMs;
            if (Toast != null && now >= toastUntilMs)
            {
                Toast = null;
                MarkChanged();
            }
            Current?.Tick(now);
        }
    }
}
