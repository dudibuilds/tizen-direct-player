# Third-party notices

- **MonoTorrent** (MIT), NuGet dependency: https://github.com/alanmcgovern/monotorrent
- **Newtonsoft.Json** (MIT), NuGet dependency.
- **uTP.** `probe/Sources/Utp/` implements BEP 29 in managed C# for this project, using the specification and **libutp** (MIT, https://github.com/bittorrent/libutp) as behavioural references; comments note where behaviour deliberately matches libutp. The interop scripts build libutp separately and test against it; no libutp source is included in this repository.
- **Sintel** (© Blender Foundation, CC BY 3.0) is the public test torrent used by `desktop/TransportCheck`.
- **Data services** used at runtime: Cinemeta and OpenSubtitles Stremio add-ons, IMDb (trailers and, on the Mac, IMDb's non-commercial datasets, under IMDb's terms for personal, non-commercial use), and optionally TMDB and Trakt with your own keys.
