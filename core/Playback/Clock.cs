using System;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100.Core.Playback
{
    /// Time source for every playback timer (hedging, budgets, cooldowns, monitor). Tests inject a manual clock.
    public interface IClock
    {
        DateTimeOffset UtcNow { get; }
        Task Delay(TimeSpan delay, CancellationToken token);
    }

    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public Task Delay(TimeSpan delay, CancellationToken token) =>
            delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, token);
    }

    internal static class TaskHelpers
    {
        /// Awaits a task, abandoning it (and observing its exception) if the token fires first.
        public static async Task<T> WithCancellation<T>(this Task<T> task, CancellationToken token)
        {
            if (task.IsCompleted)
                return await task.ConfigureAwait(false);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                var first = await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
                if (first != task)
                {
                    Observe(task);
                    token.ThrowIfCancellationRequested();
                }
                return await task.ConfigureAwait(false);
            }
        }

        public static async Task WithCancellation(this Task task, CancellationToken token)
        {
            await WithCancellationCore(task, token).ConfigureAwait(false);
        }

        private static async Task<bool> WithCancellationCore(Task task, CancellationToken token)
        {
            if (!task.IsCompleted)
            {
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (token.Register(() => cancelled.TrySetResult(true)))
                {
                    var first = await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
                    if (first != task)
                    {
                        Observe(task);
                        token.ThrowIfCancellationRequested();
                    }
                }
            }
            await task.ConfigureAwait(false);
            return true;
        }

        public static void Observe(Task task) =>
            task.ContinueWith(t => { var _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        /// Cancels the returned source after `budget` on the given clock (or when the parent token fires).
        public static CancellationTokenSource Budget(IClock clock, TimeSpan budget, CancellationToken parent)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(parent);
            var token = source.Token;
            _ = BudgetTimer(clock, budget, source, token);
            return source;
        }

        private static async Task BudgetTimer(IClock clock, TimeSpan budget, CancellationTokenSource source, CancellationToken token)
        {
            try
            {
                await clock.Delay(budget, token).ConfigureAwait(false);
                source.Cancel();
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
