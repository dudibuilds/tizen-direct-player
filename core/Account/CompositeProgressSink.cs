using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100.Core.Account
{
    /// Fans playback events out to several sinks in order (Stremio first, then Trakt, matching
    /// "upsert, then scrobble"). Each sink keeps its own outbox and error state; a failure in one
    /// sink never stops the others. Cancellation still propagates.
    public sealed class CompositeProgressSink : IProgressSink
    {
        readonly IReadOnlyList<IProgressSink> sinks;

        public CompositeProgressSink(params IProgressSink[] sinks)
        {
            this.sinks = (sinks ?? Array.Empty<IProgressSink>()).Where(s => s != null).ToList();
        }

        public IReadOnlyList<IProgressSink> Sinks => sinks;
        public Exception LastError { get; private set; }

        public Task PlaybackStartedAsync(MetaDetail meta, VideoInfo video, long resumeMs, CancellationToken token)
            => EachAsync(s => s.PlaybackStartedAsync(meta, video, resumeMs, token), token);

        public Task ProgressAsync(MetaDetail meta, VideoInfo video, long positionMs, long durationMs, CancellationToken token)
            => EachAsync(s => s.ProgressAsync(meta, video, positionMs, durationMs, token), token);

        public Task PausedAsync(MetaDetail meta, VideoInfo video, long positionMs, long durationMs, CancellationToken token)
            => EachAsync(s => s.PausedAsync(meta, video, positionMs, durationMs, token), token);

        public Task CompletedAsync(MetaDetail meta, VideoInfo video, CancellationToken token)
            => EachAsync(s => s.CompletedAsync(meta, video, token), token);

        public Task StoppedAsync(MetaDetail meta, VideoInfo video, long positionMs, long durationMs, bool markedWatched, CancellationToken token)
            => EachAsync(s => s.StoppedAsync(meta, video, positionMs, durationMs, markedWatched, token), token);

        async Task EachAsync(Func<IProgressSink, Task> call, CancellationToken token)
        {
            foreach (var sink in sinks)
            {
                try
                {
                    await call(sink).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    LastError = error;
                }
            }
        }
    }
}
