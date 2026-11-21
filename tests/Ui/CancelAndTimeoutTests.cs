using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    /// "Could not load the catalog: The operation was canceled" (TV, build ac31d75): our own cancels are silent, a
    /// timeout (HttpClient throws TaskCanceledException) retries once quietly, and only a real failure shows.
    public class CancelAndTimeoutTests
    {
        private sealed class ScriptedSource : IBrowseSource
        {
            public readonly Queue<Func<CancellationToken, Task<IReadOnlyList<BrowseItem>>>> Script = new Queue<Func<CancellationToken, Task<IReadOnlyList<BrowseItem>>>>();
            public int Calls;
            public readonly List<CancellationToken> Tokens = new List<CancellationToken>();

            public Task<IReadOnlyList<BrowseItem>> GetAsync(MediaType type, string genre, int skip, CancellationToken token)
            {
                Calls++;
                Tokens.Add(token);
                return Script.Count > 0 ? Script.Dequeue()(token) : Task.FromResult(Page(type, skip));
            }
        }

        private static IReadOnlyList<BrowseItem> Page(MediaType type, int skip) => skip > 0 ? Array.Empty<BrowseItem>() : new[]
        {
            new BrowseItem { Meta = new MetaSummary { Id = "tt1", Name = "One", Type = type, ReleaseInfo = "2020" }, Extras = new MetaExtras() }
        };

        private static Task<IReadOnlyList<BrowseItem>> HttpTimeout() =>
            Task.FromException<IReadOnlyList<BrowseItem>>(new TaskCanceledException("The operation was canceled.", new TimeoutException()));

        private static (UiHarness H, BrowseViewModel Vm) Open(ScriptedSource source)
        {
            var h = new UiHarness();
            h.Shell.Services.Browse = source;
            h.Shell.Start(new HomeViewModel());
            var vm = new BrowseViewModel();
            h.Shell.Push(vm);
            return (h, vm);
        }

        [Fact]
        public void ATimeoutRetriesQuietlyOnce()
        {
            var source = new ScriptedSource();
            source.Script.Enqueue(_ => HttpTimeout());   // first page of the first query times out once
            var (h, vm) = Open(source);
            Assert.Null(vm.Error);
            Assert.Single(vm.Results);
            Assert.DoesNotContain("canceled", h.Text, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TwoTimeoutsShowAReadableError()
        {
            var source = new ScriptedSource();
            for (var i = 0; i < 4; i++) source.Script.Enqueue(_ => HttpTimeout());
            var (_, vm) = Open(source);
            Assert.NotNull(vm.Error);
            Assert.DoesNotContain("canceled", vm.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("too long", vm.Error);
        }

        [Fact]
        public void ASupersededQueryIsCancelledWithoutAnError()
        {
            var source = new ScriptedSource();
            var pending = new TaskCompletionSource<IReadOnlyList<BrowseItem>>();
            source.Script.Enqueue(token => { token.Register(() => pending.TrySetCanceled(token)); return pending.Task; });
            var (h, vm) = Open(source);
            var first = source.Tokens[0];
            h.PickFilter(1);   // Type -> Movies: a newer query supersedes the first
            Assert.True(first.IsCancellationRequested);
            Assert.Null(vm.Error);
            Assert.Equal(MediaType.Movie, vm.Type);
        }

        [Fact]
        public void ARealFailureStillShowsAndClearsOnSuccess()
        {
            var source = new ScriptedSource();
            source.Script.Enqueue(_ => Task.FromException<IReadOnlyList<BrowseItem>>(new InvalidOperationException("HTTP 500")));
            var (h, vm) = Open(source);
            Assert.Contains("HTTP 500", vm.Error);
            h.PickFilter(1);   // another query succeeds: the error goes away
            Assert.Null(vm.Error);
        }
    }
}
