#!/usr/bin/env python3
"""libtorrent (rasterbar) peer restricted to uTP, for scripts/utp-engine-check.sh.

  make  <dir> <megabytes>            create <dir>/sample.mkv (random bytes) and <dir>/sample.torrent; print the info hash
  seed  <dir> <port>                 seed sample.mkv on 127.0.0.1:<port>, uTP only, until killed
  leech <dir> <torrent> <host:port>  download from that one peer, uTP only; exit 0 when complete and hash-checked

libtorrent has its own uTP implementation (not libutp), so this is a second, independent interop partner.
Needs the Python bindings (Homebrew: brew install libtorrent-rasterbar).
"""
import os
import sys
import time

import libtorrent as lt


def settings(port):
    return {
        'listen_interfaces': f'127.0.0.1:{port}',
        'enable_outgoing_tcp': False,
        'enable_incoming_tcp': False,
        'enable_outgoing_utp': True,
        'enable_incoming_utp': True,
        'enable_dht': False,
        'enable_lsd': False,
        'enable_upnp': False,
        'enable_natpmp': False,
        'allow_multiple_connections_per_ip': True,
        'alert_mask': lt.alert.category_t.error_notification | lt.alert.category_t.peer_notification,
    }


def describe(handle):
    s = handle.status()
    peers = handle.get_peer_info()   # all uTP: TCP is disabled in this session
    return (f'progress={s.progress * 100:.1f}% down={s.download_rate / 1e6:.2f} MB/s up={s.upload_rate / 1e6:.2f} MB/s '
            f'peers={len(peers)} uploaded={s.total_upload / 1e6:.1f} MB downloaded={s.total_download / 1e6:.1f} MB')


def make(directory, megabytes):
    os.makedirs(directory, exist_ok=True)
    path = os.path.join(directory, 'sample.mkv')
    with open(path, 'wb') as f:
        f.write(os.urandom(megabytes * 1024 * 1024))
    fs = lt.file_storage()
    import warnings
    warnings.simplefilter('ignore', DeprecationWarning)
    lt.add_files(fs, path)
    t = lt.create_torrent(fs, 256 * 1024, flags=lt.create_torrent.v1_only)
    lt.set_piece_hashes(t, directory)
    info = t.generate()
    with open(os.path.join(directory, 'sample.torrent'), 'wb') as f:
        f.write(lt.bencode(info))
    print(str(lt.torrent_info(os.path.join(directory, 'sample.torrent')).info_hashes().v1))


def seed(directory, port):
    ses = lt.session(settings(port))
    info = lt.torrent_info(os.path.join(directory, 'sample.torrent'))
    handle = ses.add_torrent({'ti': info, 'save_path': directory, 'flags': lt.torrent_flags.seed_mode})
    print(f'seed: {info.info_hashes().v1} on 127.0.0.1:{port} (uTP only)', flush=True)
    while True:
        time.sleep(2)
        for a in ses.pop_alerts():
            if isinstance(a, (lt.peer_connect_alert, lt.peer_disconnected_alert, lt.peer_error_alert)):
                print('seed: ' + a.message(), flush=True)
        print('seed: ' + describe(handle), flush=True)


def leech(directory, torrent, peer):
    os.makedirs(directory, exist_ok=True)
    host, port = peer.rsplit(':', 1)
    ses = lt.session(settings(0))
    handle = ses.add_torrent({'ti': lt.torrent_info(torrent), 'save_path': directory})
    handle.connect_peer((host, int(port)))
    start = time.time()
    while time.time() - start < 120:
        time.sleep(1)
        for a in ses.pop_alerts():
            if isinstance(a, (lt.peer_connect_alert, lt.peer_disconnected_alert, lt.peer_error_alert)):
                print('leech: ' + a.message(), flush=True)
        print('leech: ' + describe(handle), flush=True)
        if handle.status().is_seeding:
            print(f'leech: complete in {time.time() - start:.1f} s (pieces hash-checked by libtorrent)', flush=True)
            return 0
        if not handle.get_peer_info():
            handle.connect_peer((host, int(port)))
    print('leech: incomplete after 120 s', flush=True)
    return 1


if __name__ == '__main__':
    command = sys.argv[1]
    if command == 'make':
        make(sys.argv[2], int(sys.argv[3]))
    elif command == 'seed':
        seed(sys.argv[2], int(sys.argv[3]))
    elif command == 'leech':
        sys.exit(leech(sys.argv[2], sys.argv[3], sys.argv[4]))
    else:
        sys.exit(__doc__)
