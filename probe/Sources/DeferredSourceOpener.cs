using System;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;

namespace Ru7100Probe
{
    /// The torrent engine, built on first use instead of at launch: its constructor (MonoTorrent's first load, the
    /// cache sweep) costs about 0.6 s on the TV, so the app hands this forwarding opener to the session and builds the
    /// real one either when something first opens a source or when the warm-up runs after Home shows.
    internal sealed class DeferredSourceOpener : IReportingSourceOpener
    {
        private readonly Lazy<TorrentSourceOpener> real;

        public DeferredSourceOpener(Func<TorrentSourceOpener> create)
        {
            real = new Lazy<TorrentSourceOpener>(create, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// The engine if it has been built, else null (diagnostics and trimming must not build it).
        public TorrentSourceOpener IfCreated => real.IsValueCreated ? real.Value : null;

        /// Builds the engine (once) and returns it.
        public TorrentSourceOpener Value => real.Value;

        public async Task<IOpenedSource> OpenAsync(StreamCandidate candidate, CancellationToken token)
        {
            var opener = real.IsValueCreated ? real.Value : await Task.Run(() => real.Value, token).ConfigureAwait(false);
            return await opener.OpenAsync(candidate, token).ConfigureAwait(false);
        }

        public async Task<IOpenedSource> OpenAsync(StreamCandidate candidate, Action<OpenProgress> progress, CancellationToken token)
        {
            var opener = real.IsValueCreated ? real.Value : await Task.Run(() => real.Value, token).ConfigureAwait(false);
            return await opener.OpenAsync(candidate, progress, token).ConfigureAwait(false);
        }
    }
}
