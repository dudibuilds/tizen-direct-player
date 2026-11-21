using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100.Core.Playback
{
    public sealed class RaceSettings
    {
        public int MaxConcurrent { get; set; } = 5;          // TV tuning: metadata attempts are cheap; dead swarms are common
        public int InitialConcurrency { get; set; } = 4;
        public TimeSpan HedgeDelay { get; set; } = TimeSpan.FromMilliseconds(400);
        public TimeSpan ReserveDelay { get; set; } = TimeSpan.FromSeconds(3);
        public int ReserveExtraSlots { get; set; } = 3;
        /// Race cut: when a candidate reports it is healthy (RaceLead.Claim), the others are stopped so the leader gets
        /// the bandwidth, CPU and connection slots of the shared torrent engine. Stopped candidates are not failures:
        /// they are re-queued and launched again if the leader fails or has not finished within LeadStallAfter.
        public bool CutOnLead { get; set; } = true;
        /// How long a leader may take from its claim to success before the cut candidates race again beside it.
        /// A healthy swarm prebuffers in a few seconds (Mac harness 2-8 s after metadata); 12 s means it is not healthy.
        public TimeSpan LeadStallAfter { get; set; } = TimeSpan.FromSeconds(12);
        /// A torrent candidate claims the lead once it has metadata and at least this many connected peers delivering at
        /// least LeadMinBytesPerSecond in total. 256 KiB/s is about a 2 Mbit/s floor: data is clearly flowing, though a
        /// stream needs more; the stall budget catches a leader that does not keep it up.
        public int LeadMinPeers { get; set; } = 3;
        public double LeadMinBytesPerSecond { get; set; } = 256 * 1024;

        public bool IsHealthyLead(OpenProgress progress) =>
            progress != null && progress.HasMetadata && progress.Peers >= LeadMinPeers && progress.DownloadBytesPerSecond >= LeadMinBytesPerSecond;
    }

    /// Given to each racing candidate: Claim() says "I have what I need to win" (e.g. torrent metadata and a healthy
    /// swarm), which stops the other candidates when RaceSettings.CutOnLead is on. Repeated claims are ignored.
    public sealed class RaceLead
    {
        private readonly Action claim;
        private int claimed;
        private int cut;
        internal RaceLead(Action claim) { this.claim = claim; }
        /// True once the race stopped this candidate for another one's lead: its cancellation is not a stall.
        public bool WasCut => Volatile.Read(ref cut) == 1;
        internal void MarkCut() => Interlocked.Exchange(ref cut, 1);
        public bool Claimed => Volatile.Read(ref claimed) == 1;
        public void Claim()
        {
            if (Interlocked.Exchange(ref claimed, 1) == 0)
                claim?.Invoke();
        }
    }

    public sealed class RaceFailure<TCandidate>
    {
        public RaceFailure(TCandidate candidate, Exception error) { Candidate = candidate; Error = error; }
        public TCandidate Candidate { get; }
        public Exception Error { get; }
    }

    public sealed class RaceResult<TCandidate, TResult>
    {
        public bool HasWinner { get; internal set; }
        public TCandidate WinnerCandidate { get; internal set; }
        public TResult Winner { get; internal set; }
        public List<RaceFailure<TCandidate>> Failures { get; } = new List<RaceFailure<TCandidate>>();
        /// Candidates that were started (launched) during the race, in launch order.
        public List<TCandidate> Launched { get; } = new List<TCandidate>();
        /// Candidates stopped because another one claimed the lead (not failures), in cut order.
        public List<TCandidate> Cut { get; } = new List<TCandidate>();
        /// The candidate that claimed the lead first, if any.
        public bool HasLeader { get; internal set; }
        public TCandidate Leader { get; internal set; }
    }

    /// Port of raceCandidates (stream-startup.mjs:106-176, G2): hedged start, first success wins,
    /// the reserve joins after ReserveDelay with extra slots, late winners are discarded.
    public static class CandidateRace
    {
        public static Task<RaceResult<TCandidate, TResult>> RunAsync<TCandidate, TResult>(
            IReadOnlyList<TCandidate> initial,
            Func<TCandidate, CancellationToken, Task<TResult>> prepare,
            IClock clock,
            RaceSettings settings,
            IReadOnlyList<TCandidate> reserve,
            Action<TCandidate, TResult> discardLate,
            CancellationToken token) =>
            RunAsync<TCandidate, TResult>(initial, (candidate, lead, candidateToken) => prepare(candidate, candidateToken), clock, settings,
                reserve, discardLate, token);

        /// As above; <paramref name="prepare"/> also gets a RaceLead to claim the lead with (race cut).
        public static Task<RaceResult<TCandidate, TResult>> RunAsync<TCandidate, TResult>(
            IReadOnlyList<TCandidate> initial,
            Func<TCandidate, RaceLead, CancellationToken, Task<TResult>> prepare,
            IClock clock,
            RaceSettings settings,
            IReadOnlyList<TCandidate> reserve,
            Action<TCandidate, TResult> discardLate,
            CancellationToken token,
            Action<int> runningChanged = null)
        {
            var race = new Race<TCandidate, TResult>(initial, prepare, clock, settings ?? new RaceSettings(),
                reserve ?? Array.Empty<TCandidate>(), discardLate, token, runningChanged);
            return race.Start();
        }

        private sealed class Race<TCandidate, TResult>
        {
            private readonly object gate = new object();
            private readonly List<TCandidate> candidates;
            private readonly IReadOnlyList<TCandidate> reserve;
            private readonly Func<TCandidate, RaceLead, CancellationToken, Task<TResult>> prepare;
            private readonly IClock clock;
            private readonly RaceSettings settings;
            private readonly Action<TCandidate, TResult> discardLate;
            private readonly Action<int> runningChanged;
            private int lastReported = -1;
            private readonly CancellationToken outer;
            private readonly CancellationTokenSource stop = new CancellationTokenSource();
            private readonly Dictionary<int, CancellationTokenSource> running = new Dictionary<int, CancellationTokenSource>();
            private readonly Dictionary<int, TCandidate> runningCandidates = new Dictionary<int, TCandidate>();
            private readonly Dictionary<int, RaceLead> leads = new Dictionary<int, RaceLead>();
            /// Launch indices stopped by a lead claim; their outcome is ignored and the candidate re-queued.
            private readonly HashSet<int> cut = new HashSet<int>();
            private readonly List<TCandidate> requeue = new List<TCandidate>();
            private int leader = -1;      // launch index of the current leader
            private bool holding;         // no new launches while a leader is within its stall budget
            private readonly RaceResult<TCandidate, TResult> result = new RaceResult<TCandidate, TResult>();
            private readonly TaskCompletionSource<RaceResult<TCandidate, TResult>> completion =
                new TaskCompletionSource<RaceResult<TCandidate, TResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int next;
            private int slots;
            private bool settled;
            private CancellationTokenRegistration outerRegistration;

            public Race(IReadOnlyList<TCandidate> initial, Func<TCandidate, RaceLead, CancellationToken, Task<TResult>> prepare,
                IClock clock, RaceSettings settings, IReadOnlyList<TCandidate> reserve,
                Action<TCandidate, TResult> discardLate, CancellationToken outer, Action<int> runningChanged)
            {
                this.runningChanged = runningChanged;
                candidates = initial.ToList();
                this.prepare = prepare;
                this.clock = clock;
                this.settings = settings;
                this.reserve = reserve;
                this.discardLate = discardLate;
                this.outer = outer;
                slots = settings.MaxConcurrent;
            }

            public Task<RaceResult<TCandidate, TResult>> Start()
            {
                outer.ThrowIfCancellationRequested();
                if (candidates.Count == 0)
                {
                    settled = true;
                    completion.TrySetResult(result);
                    return completion.Task;
                }
                outerRegistration = outer.Register(Abort);
                for (var i = 0; i < Math.Min(settings.InitialConcurrency, settings.MaxConcurrent); i++)
                    Launch();
                _ = HedgeLoop();
                if (reserve.Count > 0)
                    _ = ReserveTimer();
                return completion.Task;
            }

            private void Abort()
            {
                List<CancellationTokenSource> cancel;
                lock (gate)
                {
                    if (settled)
                        return;
                    settled = true;
                    cancel = running.Values.ToList();
                }
                Finish();
                foreach (var source in cancel)
                    TryCancel(source);
                completion.TrySetCanceled(outer);
            }

            private void Finish()
            {
                TryCancel(stop);
                outerRegistration.Dispose();
            }

            private void Launch()
            {
                TCandidate candidate;
                CancellationTokenSource source;
                int launchIndex;
                lock (gate)
                {
                    if (settled || holding || next >= candidates.Count || running.Count >= slots)
                        return;
                    var index = next++;
                    candidate = candidates[index];
                    source = CancellationTokenSource.CreateLinkedTokenSource(outer);
                    running[index] = source;
                    runningCandidates[index] = candidate;
                    result.Launched.Add(candidate);
                    launchIndex = index;
                }
                ReportRunning();
                _ = Task.Run(() => Run(launchIndex, candidate, source));
            }

            /// Tells the caller how many candidates are opening in parallel (status text), on changes only.
            private void ReportRunning()
            {
                if (runningChanged == null)
                    return;
                int count;
                lock (gate)
                {
                    if (settled)
                        return;
                    count = running.Count;
                    if (count == lastReported)
                        return;
                    lastReported = count;
                }
                try { runningChanged(count); } catch { }
            }

            private async Task Run(int index, TCandidate candidate, CancellationTokenSource source)
            {
                TResult value;
                try
                {
                    var lead = new RaceLead(settings.CutOnLead ? () => OnClaim(index, candidate) : (Action)null);
                    lock (gate)
                        leads[index] = lead;
                    value = await prepare(candidate, lead, source.Token).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    OnFailure(index, candidate, error);
                    return;
                }
                OnSuccess(index, candidate, value);
            }

            private void OnSuccess(int index, TCandidate candidate, TResult value)
            {
                List<CancellationTokenSource> losers;
                lock (gate)
                {
                    running.Remove(index);
                    runningCandidates.Remove(index);
                    leads.Remove(index);
                    cut.Remove(index);
                    if (settled)
                        losers = null;
                    else
                    {
                        settled = true;
                        result.HasWinner = true;
                        result.WinnerCandidate = candidate;
                        result.Winner = value;
                        losers = running.Values.ToList();
                    }
                }
                if (losers == null)
                {
                    try { discardLate?.Invoke(candidate, value); } catch { }
                    return;
                }
                Finish();
                foreach (var loser in losers)
                    TryCancel(loser);
                completion.TrySetResult(result);
            }

            private void OnFailure(int index, TCandidate candidate, Exception error)
            {
                bool exhausted;
                var relaunch = 1;
                lock (gate)
                {
                    running.Remove(index);
                    runningCandidates.Remove(index);
                    leads.Remove(index);
                    if (cut.Remove(index))
                    {
                        // Stopped for the leader: not a failure. It races again if the leader fails or stalls.
                        if (settled)
                            return;
                        if (holding)
                            requeue.Add(candidate);
                        else
                            candidates.Insert(next, candidate);
                    }
                    else
                    {
                        if (settled)
                            return;
                        result.Failures.Add(new RaceFailure<TCandidate>(candidate, error));
                        if (index == leader)
                        {
                            relaunch = ReleaseHoldLocked();
                            leader = -1;      // another candidate may claim the lead now
                        }
                    }
                }
                for (var i = 0; i < relaunch; i++)
                    Launch();
                ReportRunning();
                lock (gate)
                {
                    exhausted = !settled && running.Count == 0 && next >= candidates.Count;
                    if (exhausted)
                        settled = true;
                }
                if (exhausted)
                {
                    Finish();
                    completion.TrySetResult(result);
                }
            }

            /// First claim while others run: they are stopped and re-queued; launching pauses for LeadStallAfter.
            private void OnClaim(int index, TCandidate candidate)
            {
                List<CancellationTokenSource> stopped;
                lock (gate)
                {
                    if (settled || leader >= 0 || !running.ContainsKey(index))
                        return;
                    leader = index;
                    holding = true;
                    result.HasLeader = true;
                    result.Leader = candidate;
                    stopped = new List<CancellationTokenSource>();
                    foreach (var pair in running)
                    {
                        if (pair.Key == index || cut.Contains(pair.Key))
                            continue;
                        cut.Add(pair.Key);
                        result.Cut.Add(runningCandidates[pair.Key]);
                        if (leads.TryGetValue(pair.Key, out var stoppedLead))
                            stoppedLead.MarkCut();
                        stopped.Add(pair.Value);
                    }
                }
                foreach (var source in stopped)
                    TryCancel(source);
                _ = LeadStallTimer(index);
            }

            private async Task LeadStallTimer(int index)
            {
                try
                {
                    await clock.Delay(settings.LeadStallAfter, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                int relaunch;
                lock (gate)
                {
                    if (settled || leader != index || !holding)
                        return;
                    // The leader keeps running; the others join it again.
                    relaunch = ReleaseHoldLocked();
                }
                for (var i = 0; i < relaunch; i++)
                    Launch();
            }

            /// Must hold gate. Ends the hold and puts the cut candidates back at the front of the queue; returns how many
            /// launches to attempt.
            private int ReleaseHoldLocked()
            {
                holding = false;
                candidates.InsertRange(next, requeue);
                var count = requeue.Count;
                requeue.Clear();
                return Math.Max(1, count);
            }

            private async Task HedgeLoop()
            {
                try
                {
                    while (true)
                    {
                        await clock.Delay(settings.HedgeDelay, stop.Token).ConfigureAwait(false);
                        lock (gate)
                            if (settled)
                                return;
                        Launch();
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }

            private async Task ReserveTimer()
            {
                try
                {
                    await clock.Delay(settings.ReserveDelay, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                lock (gate)
                {
                    if (settled)
                        return;
                    candidates.AddRange(reserve);
                    slots += settings.ReserveExtraSlots;
                }
                for (var i = 0; i < reserve.Count; i++)
                    Launch();
            }

            private static void TryCancel(CancellationTokenSource source)
            {
                try { source.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
            }
        }
    }

    /// Port of ProgressDeadline (stream-startup.mjs:54-80, G3): initial budget, extended on progress, hard maximum.
    public sealed class ProgressDeadline : IDisposable
    {
        private readonly IClock clock;
        private readonly CancellationTokenSource source;
        private readonly TimeSpan extension;
        private readonly DateTimeOffset maximumAt;
        private readonly object gate = new object();
        private readonly CancellationTokenSource watchStop = new CancellationTokenSource();
        private DateTimeOffset deadlineAt;

        public ProgressDeadline(IClock clock, CancellationToken parent, TimeSpan initial, TimeSpan extension, TimeSpan maximum)
        {
            this.clock = clock;
            this.extension = extension;
            source = CancellationTokenSource.CreateLinkedTokenSource(parent);
            token = source.Token;   // cached: CancellationTokenSource.Token throws once disposed
            var now = clock.UtcNow;
            maximumAt = now + maximum;
            deadlineAt = Min(maximumAt, now + initial);
            _ = Watch();
        }

        private readonly CancellationToken token;
        public CancellationToken Token => token;
        public bool Expired { get; private set; }

        public void Progress()
        {
            lock (gate)
                deadlineAt = Min(maximumAt, Max(deadlineAt, clock.UtcNow + extension));
        }

        private async Task Watch()
        {
            try
            {
                while (true)
                {
                    TimeSpan wait;
                    lock (gate)
                        wait = deadlineAt - clock.UtcNow;
                    if (wait <= TimeSpan.Zero)
                    {
                        Expired = true;
                        source.Cancel();
                        return;
                    }
                    using (var either = CancellationTokenSource.CreateLinkedTokenSource(source.Token, watchStop.Token))
                        await clock.Delay(wait, either.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
        private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

        /// Stops the timer without cancelling Token: work that outlives the start (late losers) must not look
        /// as if the whole start had been cancelled.
        public void Dispose()
        {
            try { watchStop.Cancel(); } catch (ObjectDisposedException) { }
            watchStop.Dispose();
            source.Dispose();
        }
    }
}
