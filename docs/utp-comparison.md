# Managed uTP implementations compared (2026-11-20)

Question: is any existing C# uTP (BEP 29) implementation better than ours (`probe/Sources/Utp/`, see `docs/utp.md`),
measured the same way on the Mac, and would porting it or borrowing parts of it make TV starts faster?

Short answer: **no**. See the verdict at the end.

## Candidates

| Candidate | Source (commit) | License | uTP code | Target |
| --- | --- | --- | --- | --- |
| **Ours** | `probe/Sources/Utp/` | (this repo) | 1,917 lines protocol + 581 MonoTorrent glue | netstandard2.0, C# 8, on the TV |
| MonoTorrent main | github.com/alanmcgovern/monotorrent `d66fb44` | MIT | 3,336 lines (`MonoTorrent.Connections.Peer/Utp/`), plus engine plumbing that exists only in main | net8.0, C# 12 |
| PeerSharp 5.4.0 | github.com/ligenq/PeerSharp `ca12a84` | MIT | 3,021 lines (`Internals/Utp/`) + 456-line `UdpListener` | net10.0, C# 12+ |
| bzTorrent 4.0.0 | github.com/bizzehdee/bzTorrent `39ea15f` | BSD 3-clause | 1,422 lines (`IO/UTP*.cs`) | netstandard2.0 |
| TorrentSwifter | github.com/Whinarn/TorrentSwifter `d5d749e` | MIT | **none**: its README lists BEP 29 as an unchecked to-do and the only peer transport is `PeerConnectionTCP.cs` | netstandard2.0 |

TorrentSwifter was dropped after that look. The real-swarm test (C in the brief) was dropped at the user's request;
the code-level comparison below is what the verdict rests on.

## How it was measured

`scripts/utp-compare.sh /path/to/libutp /path/to/clones` builds one small driver per candidate
(`bench/UtpCompare/<Candidate>/`, all sharing `bench/UtpCompare/Shared/Driver.cs`), each calling the candidate's own
socket/connection API with its default settings and its own UDP layer. Same conditions for every candidate:

* Partner: libutp's `ucat` (the same patched build as `scripts/utp-interop.sh`), on 127.0.0.1.
* Impairment: the same seeded relay as the interop check (drops and delays datagrams; jitter reorders), extended to
  count wire bytes per direction.
* Transfers end by **byte count, not FIN** (BitTorrent never half-closes; MonoTorrent main has no public half-close
  and bzTorrent drops data after a FIN). The sender waits until its data is acknowledged before closing. `ucat` gets
  a stdin that stays open, so it never closes first.
* An accepting side starts writing after the peer's first data, as a BitTorrent listener does after the handshake
  (PeerSharp's accepted stream rejects writes before that; without this, every PeerSharp listen case failed).
* Two-way: 4 MB each way at once, SHA-256 checked both ways, both connection directions (we connect / ucat
  connects), clean, 20 ms jitter only, 3% loss + 20 ms jitter, 10% loss + 40 ms jitter.
* Receive (the TV's download path): ucat sends, the candidate only reads. 300 MB clean for throughput, CPU and
  allocations per MB; 16 MB through 3% loss + 20 ms jitter.
* Release builds, .NET 10, Apple Silicon Mac; candidates interleaved within each case so load drift hits all.
  CPU and allocation figures are whole-process for the receive phase.

`scripts/utp-compare-engine.sh` runs MonoTorrent main's engine (uTP only) against uTP-only libtorrent 2.1.2 the same
way `scripts/utp-engine-check.sh` runs ours (test B).

## A. Against libutp's ucat

### Receive path (ucat sends, candidate reads; what the TV does)

300 MB, clean loopback, three runs each:

| | Ours | MonoTorrent main | PeerSharp |
| --- | --- | --- | --- |
| Throughput (MB/s) | **161 / 207 / 216** | 109 / 111 / 128 | 99 / 104 / 109 |
| CPU per MB received (ms) | **7.3 / 7.4 / 8.1** | 53.3 / 53.4 / 53.7 | 57.1 / 61.7 / 63.1 |
| Allocated per MB received (KB) | **147 / 149 / 149** | 1,320 / 1,320 / 1,330 | 1,725 / 1,744 / 1,747 |
| Time to first MB (ms) | 15-25 | 16-18 | 15-17 |

At the old rule of thumb in `docs/utp.md` (RU7100 core ~10x slower than the Mac), ours costs ~75 ms CPU per MB on the
TV; the other two would cost ~0.55-0.6 s per MB, i.e. a whole Cortex-A9 core for ~1.7 MB/s of uTP download.

16 MB through 3% loss + 20 ms jitter, three runs each:

| | Ours | MonoTorrent main | PeerSharp |
| --- | --- | --- | --- |
| Result | complete 3/3 | **stalled 3/3** (no progress for 30 s after 0.5 / 0.85 / 1.5 MB, while libutp kept retransmitting: 2.5-4.1 MB on the wire) | complete 3/3 |
| Throughput (MB/s) | 0.68 / 0.86 / 0.86 | - | 0.89 / 0.95 / 1.22 |
| Time to first MB (ms) | 586 / 606 / 615 | - | 579 / 590 / 1,868 |
| CPU per MB (ms) | 58 / 71 / 74 | 509-1,066 | 209 / 236 / 291 |
| Wire bytes per payload byte | 1.55-1.58 | - | 1.58-1.59 |

PeerSharp is ahead here by ~10-40%. The sender is libutp in both cases and the wire overhead is the same, so the
difference is in how the receiver's ACKs pace libutp; PeerSharp sends one ACK per data packet, ours delays in-order
ACKs (every 8 packets or 10 ms). ACK_EXPERIMENT_PLACEHOLDER

### Both directions at once (4 MB each way, SHA-256 checked)

Seconds to receive / seconds until our sent data was acknowledged. "we connect" = the candidate connects to ucat,
"they connect" = ucat connects to the candidate. One run per cell unless two are shown.

| Case | Ours | MonoTorrent main | PeerSharp |
| --- | --- | --- | --- |
| clean, we connect | 0.05 / 0.02 | 0.04 / 0.11 | 0.06 / 0.07 |
| clean, they connect | 0.05 / 0.02 | 0.04 / 0.11 | 0.09 / 0.11 |
| 20 ms jitter, we connect | 1.4 / 3.5 | 1.3 / **54.7** | 4.6 / 2.2 |
| 20 ms jitter, they connect | 1.4 / 3.5 | 1.4 / **>60** (done at 63.5) | 1.4 / 4.8 |
| 3% loss + 20 ms jitter, we connect | 5.5 / 7.2; 6.3 / 10.2 | **FAIL x2**: receive stalled at 0.46 / 0.75 MB, then the connection reset itself (4 timeouts) | 5.6 / 8.5; 11.0 / 8.6 |
| 3% loss + 20 ms jitter, they connect | 1.6 / 6.0 | **FAIL**: 3.3 / sent 3.06 of 4 MB in 93 s | 6.5 / 9.9 |
| 10% loss + 40 ms jitter, we connect | 48.0 / 62.1 | **FAIL**: reset, 0.33 MB sent | 45.4 / 101.3 |
| 10% loss + 40 ms jitter, they connect | 40.5 / 44.6 | **FAIL**: reset after 8.9 s | 48.8 / 88.1 |
| bzTorrent, clean, either direction | | | **FAIL**: received 6.9 KB / 68 KB, then nothing for 30 s |

Ours passed all 13 two-way runs (both SHA checks each). PeerSharp passed all of its runs once the driver waited for
the first data on accepted connections. On the send side ours was faster than PeerSharp in 4 of the 6 impaired runs
(up to 2x at 10% loss) and slower in 2 (jitter, we connect: 3.5 vs 2.2 s; one 3% run: 10.2 vs 8.6 s). MonoTorrent
main receives clean data fastest in this short test (0.04 vs 0.05 s for 4 MB) but loses that in the 300 MB run
above. CPU per MB in the two-way runs: ours 14-15 clean / 61-264 impaired, MonoTorrent main 54-70 / 250-2,170,
PeerSharp 91-116 / 236-823. Connecting took tens of ms for all three on loopback (ours 11-52 ms, MonoTorrent main
34-78 ms, PeerSharp 25-65 ms).

Earlier smoke and diagnostic runs (2 MB) showed the same pattern: MonoTorrent main needed 20.5-20.9 s to send 2 MB
with 3% loss and **no** jitter (RTT under 1 ms), and 43.6 s with 20 ms jitter and no loss.

## B. Engine level against libtorrent 2.1.2 (its own uTP, TCP disabled)

Only practical for MonoTorrent main (the one drop-in for our engine). 24 MB, 127.0.0.1, three runs each:

| | Our engine (`scripts/utp-engine-check.sh`) | MonoTorrent main engine (`scripts/utp-compare-engine.sh`) |
| --- | --- | --- |
| Download from a uTP-only seed | PASS 3/3, 0.5-0.6 s after the source opened at 0.4 s (Debug build) | PASS 3/3, 1.03-1.04 s including start, first piece at 548-570 ms |
| Upload to a uTP-only leecher over incoming uTP | PASS 3/3, 6.0 s | PASS 3/3, 2.0 s |

The upload gap is our engine's configured 4 MiB/s `MaximumUploadRate` (`TorrentSourceOpener`; the leecher gained
exactly 4.2 MB per second), not uTP. Clean loopback hides the weaknesses that A shows; both engines interoperate with
libtorrent's uTP.

## D. Code review

| | Ours | MonoTorrent main | PeerSharp | bzTorrent |
| --- | --- | --- | --- | --- |
| LEDBAT | yes (100 ms target, 3000 B/RTT gain, base-delay history) | yes (delay history, slow start + LEDBAT gain, grows only when window-limited) | yes (libtorrent-style, settings-driven) | broken: unsigned `target - delay` underflows, raw timestamp diff used as queuing delay (no base delay, wrong sign), window halved every 100 ms regardless, capped at 16 KB |
| Initial window | 4 x 1380 B | 1 packet of 512 B (`DefaultInitialPacketSize`), MTU probing grows it | 2,600 B, slow start | 16 KB |
| Selective ACK | send + receive | send + receive | send + receive | none (extension ignored) |
| Delayed ACK | every 8 packets or 10 ms; immediate on out-of-order, FIN, gap | every 2nd packet, else on the next scheduler pass (delay 0 by default); immediate on out-of-order | none: one STATE per DATA packet | none |
| Fast resend | 3 dup ACKs / SACK | 3 duplicate indications, one window cut per window | 3 dup ACKs / SACK, libtorrent rules | none (RTO only, 100 ms poll) |
| RTO | RFC 6298, Karn, 0.5-60 s, 50 ms tick | RFC 6298, Karn, 0.5-60 s, heap scheduler; window reset to 1 MTU and MTU to the floor on every timeout | RFC 6298, 0.5-10 s, checked on a 500 ms manager tick | fixed backoff, 100 ms poll |
| MTU | fixed 1380 B payload (1400 B datagrams) | path MTU discovery with DF probes and ICMP feedback | MTU probing | none: `Send` puts the whole buffer in one datagram |
| Stray packets / RESET | RESET to strays, RESET matched on either neighbouring id | RESET to strays, neighbouring-id match, recent-reset table | strays silently dropped | no RESET handling |
| One UDP socket with the DHT | yes (first byte) | yes (shared `UdpListener`, version-nibble filter) | yes (receiver fan-out) | no |
| Incoming connections | yes | yes (`IPeerConnectionListener`) | yes (`OnNewConnection`) | yes, demultiplexed by endpoint only |
| Half-close (FIN, then keep reading) | yes | no public API (`SendFinAsync` internal; a received FIN stops sends) | yes (internal) | FIN processed on arrival, out of order, data after it lost |
| Threads / allocation per packet | one receive thread, pooled buffers, sans-IO core | channel per connection + async send loop, `new byte[254]` SACK buffer per DATA packet | `Pipe` + bounded channel per stream, `byte[]` per datagram | `byte[]` per datagram, `Task`/callbacks |
| Retarget to netstandard2.0 / C# 8 + MonoTorrent 3.0.2 | done, on the TV | ~4-6 days: C# 12 and net8 APIs (`PeriodicTimer`, `TimeSpan.TotalMicroseconds`, records, `init`), depends on main-only plumbing (`UdpListener` with DF, `CompactEndPoint`, `UtpMemoryPool`, ReusableTasks 5, factories/`PeerTransport`) that 3.0.2 lacks, so our glue (TCP/uTP race, DHT sharing) stays | ~4-6 days: C# 12, `TimeProvider`/`ITimer`, Pipelines, Logging abstractions, its own settings and handshake limiter; plus an `IPeerConnection` adapter | already netstandard2.0, but not usable (see A) |
| License | ours | MIT | MIT | BSD 3-clause |

Bugs and weak points seen while running it (A):

* **MonoTorrent main**: as a sender facing reordering or loss it collapses into retransmission timeouts: 20 ms of
  jitter alone made 4 MB take 46-55 s to send (ours 3.5 s), and with loss it often reached its 4-timeout limit and
  reset the connection. Likely cause from the code: the window restarts at one 512-1438 B packet after every
  timeout, so with fewer than four packets in flight losses cannot trigger fast resend and each one costs a ≥0.5 s
  RTO. As a receiver it stalled for good after ~0.5 MB in the 16 MB lossy download.
* **PeerSharp**: correct in every case once the driver waits for the peer's first data on accepted connections, but
  it ACKs every packet and runs everything through a `Pipe`, so it costs ~8x our CPU per MB.
* **bzTorrent**: not wire-compatible with libutp in practice: receiving stalls after ~5-50 packets even on clean
  loopback; its README says real-world interop was never verified.
