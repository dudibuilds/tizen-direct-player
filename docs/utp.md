# uTP (BEP 29) in managed C#

The RU7100 refuses native code from our package (see AGENTS.md, 2026-11-19), so uTP is written in C# and plugged into
MonoTorrent 3.0.2. Code: `probe/Sources/Utp/`. Tests: `tests/Utp/`.

## Layout

| File | What |
| --- | --- |
| `UtpPacket.cs` | Header codec, extension chain, selective ACK bits, 16-bit sequence arithmetic, packet buffer pool |
| `UtpConnection.cs` | One connection: handshake, send/receive windows, RTT/RTO, SACK + 3-dup-ACK fast resend, LEDBAT, FIN/RESET, keepalive, zero-window probe |
| `UtpContext.cs` | Sans-IO endpoint: demultiplexes by (remote, connection id), accepts SYNs, RESETs strays, timers (`Tick`), ACK batching (`Flush`) |
| `UtpUdpSocket.cs` | The real IPv4 UDP socket (non-blocking, one receive thread, 50 ms timer) and a `Stream` wrapper |
| `UtpMonoTorrent.cs` | MonoTorrent glue: shared UDP socket with the DHT, TCP/uTP race for outgoing peers, uTP accepts on the IPv4 listener, per-transport metering |

The protocol core never reads a clock or touches a socket itself: time comes from `IUtpClock`, datagrams leave through
`IUtpDatagramSink`. Tests run it on a simulated network (`tests/Utp/UtpSimulation.cs`) with a deterministic clock,
delay, jitter (reordering), loss, duplication and a bottleneck queue.

Wire rules follow libutp (checked against its source): the SYN carries the initiator's receive id and the
initiator sends with id + 1; the responder's SYN-ACK is a STATE whose `seq_nr` is the sequence number of its first
DATA packet, so the initiator sets `ack_nr = seq_nr - 1`; selective ACK bit *i* covers `ack_nr + 2 + i`, least
significant bit first; a RESET is matched against the receive id or either neighbouring id.

## In the engine

* One UDP socket on the engine's fixed listen port number carries uTP and the DHT. Peers learn one port (tracker,
  DHT, PEX) and try both TCP and uTP on it, so uTP has to live on the TCP port number, which the DHT already uses for
  UDP. Datagrams are told apart by their first byte: bencoded DHT messages start with `d` (0x64), uTP has version 1 in
  the low nibble. The engine's UPnP UDP mapping for the DHT therefore covers uTP too.
* Outgoing IPv4 peers: TCP and uTP connect at once (MonoTorrent does not expose PEX/extension uTP flags, so there
  is nothing to choose by). If TCP connects first it wins; if uTP connects first, TCP still wins when it connects
  within `UtpTcpGrace` (500 ms), because for a peer reachable both ways TCP is cheaper for the TV (the kernel does the
  work) and is not held back by LEDBAT or libtorrent's mixed-mode limits. uTP keeps the peers TCP cannot reach.
  If TCP fails fast (refused), uTP alone gets 2 s from the start of the attempt (`UtpAloneBudget`): a uTP-only
  peer answers within a round trip or two, and waiting out the engine's 6 s connect timeout held a half-open slot per
  refused peer and slowed starts. A transport that connected but closed before the BitTorrent handshake is remembered for that address for 10
  minutes and skipped on the engine's retry (a libtorrent peer with TCP disabled still accepts the TCP connection,
  then drops it).
* Incoming uTP arrives through the IPv4 listener's `ConnectionReceived`, exactly like TCP. An accepted connection
  whose peer sends nothing after its SYN is dropped after 5 s (libutp drops a `CS_SYN_RECV` socket at its first
  timeout); any connection silent for 5 minutes is reset.
* Receive window `UtpReceiveBufferBytes` = 256 KB per connection: the most a peer has in flight to us and the most
  we hold in managed memory while MonoTorrent is not reading (pause throttles it to ~1 KB/s), so at most ~15 MB for
  60 peers.
* IPv6 stays TCP only.
* Off switch: `TorrentSourceSettings.EnableUtp`; on the TV an empty file `utp-off` in the app data folder; in the
  desktop harness `RU7100_UTP=0`. `RU7100_UTP_GRACE_MS` sets the TCP grace there (0 = first to connect wins).
* Diagnostics (`conn=` in the swarm log lines and `dev.diagnostics`): `tcp=` TCP connects, `in=` incoming TCP,
  `utp=connected/attempts`, `utpIn=` incoming uTP, `won=tcp:N/utp:M` race winners (outgoing peers per transport),
  `tcp-grace=` races uTP reached first but TCP took, `lost-race=tcp:N/utp:M` attempts dropped because the other
  transport won, `hs=tcp:N/utp:M` connections (not distinct peers; incoming churn inflates it) that received a
  handshake, `MB=tcp:x/utp:y` bytes received per transport, failures `utp:timeout` (the engine gave up on a silent
  peer) and `utp:<SocketError>`. `NetworkSummary` adds the shared socket's counters (`utp-socket=`, live uTP bytes,
  duplicates, retransmits, datagrams, RESETs).

## Checks that are not part of `dotnet test`

```sh
# libutp interop (its ucat example), both directions, clean and through a lossy relay (3% / 10% loss + jitter)
git clone https://github.com/bittorrent/libutp /some/where/libutp   # outside this repository
scripts/utp-interop.sh /some/where/libutp [bytes]

# The TV's TorrentSourceOpener against uTP-only libtorrent peers (brew install libtorrent-rasterbar):
# download from a seed (metadata + data, SHA-256 checked), then a leecher downloads from us over incoming uTP
scripts/utp-engine-check.sh [megabytes]
RU7100_UTP=0 scripts/utp-engine-check.sh 8      # control: must fail

# Real swarms, configurations interleaved (CONFIGS="off on grace0"), pinned to an info hash; each run holds
# /tmp/ru7100-net-measure.lock and is tagged with any overlapping start-check
scripts/utp-ab.sh 3 20 -- "movie <imdb id> <info hash>"
```

CPU per MB of the receive path (the TV downloads): `--utp-cat listen <port> /dev/null /dev/null` in the desktop
harness, fed by libutp's `ucat` sending a large file, against `--tcp-sink <port>` fed by `nc` for the managed TCP
baseline (`BENCH_MB=300 scripts/utp-interop.sh ...`).

## Results on the Mac (2026-11-19)

* Interop: libutp `ucat`, both directions (we connect / it connects), clean and through a relay with 3% loss + 20 ms
  jitter and 10% loss + 40 ms jitter: byte-exact both ways in all six cases. libtorrent 2.1.2 (its own uTP), TCP
  disabled: the engine fetched metadata and a 24 MB file from a uTP-only seed (SHA-256 match), and a uTP-only
  leecher downloaded it back from us over incoming uTP.
* Receive-path CPU (Release, libutp sending to our listener on loopback): 10 ms CPU per MB at 5 MB/s (was 19 before
  delayed ACKs and batched read completion), 6.4 ms per MB at saturation (~220 MB/s); managed TCP: 0.7 ms per MB.
  Rough RU7100 extrapolation (Cortex-A9, ~10x slower per core than the Mac): ~100 ms CPU per MB over uTP, i.e. about
  a quarter of a core per 2.5 MB/s that arrives over uTP. Only the uTP share of the traffic pays it.
* Real swarms (desktop start-check, cold, 3 popular sources x 3 rounds, Debug build; another session's
  measurements overlapped several of these runs): uTP adds peers (~40 handshaken uTP connections per start, ~5-15%
  of outgoing peers won over uTP) and carried 5-30% of the bytes. With the first transport to connect winning,
  prebuffer was slower (title A: 11.8-16.5 s vs 8.2-8.9 s TCP only) and sustained throughput lower on one source,
  because uTP took peers TCP also reached. With the 500 ms TCP grace, start and throughput matched TCP only within the
  run-to-run spread (title A prebuffer 4.9-9.1 s, 99-116 Mbit/s vs 8.2-8.9 s, 113-169 Mbit/s). Default: uTP on
  with the grace.
* Start-time acceptance (rank-0 source as the app picks it, cold, uTP on with the grace; seconds until head and tail
  are prebuffered): series episode B 6.8 / 12.4 (TCP only 9.5), series episode C 9.9 / 8.6 (TCP only 8.7), movie D
  4.7 / 5.1 (TCP only 5.3). Before the 2 s uTP-alone budget the same check gave 14.1 / 19.5, 14.5 / 13.3 and
  11.0 / 7.0. The rank-0 source for B is a 4 GB season pack with 75 seeders, borderline for 10 s on either
  transport.
* Sustained reads sometimes stall on a 1 MB range for minutes, with uTP on and off alike (an engine issue, not uTP);
  start-check now reports `SUSTAIN stalled` after 30 s instead of waiting out the HTTP timeout.
