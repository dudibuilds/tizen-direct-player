using System.Collections.Generic;

namespace Ru7100.Preview
{
    internal static class PreviewPage
    {
        /// Browser key → remote key → Tizen key name on the RU7100.
        public static readonly IReadOnlyList<string[]> KeyMap = new[]
        {
            new[] { "Arrow keys", "Up / Down / Left / Right", "Up, Down, Left, Right" },
            new[] { "Enter", "Select (center)", "Return" },
            new[] { "Backspace / Escape", "Back", "XF86Back" },
            new[] { "Space", "PlayPause", "XF86PlayBack" },
            new[] { "P", "Play", "XF86AudioPlay" },
            new[] { "K", "Pause", "XF86AudioPause" },
            new[] { "S", "Stop", "XF86AudioStop" },
            new[] { "[", "Rewind", "XF86AudioRewind" },
            new[] { "]", "FastForward", "XF86AudioForward" },
            new[] { "N", "Next", "XF86AudioNext" },
            new[] { "M", "Menu (card actions, rate)", "XF86SimpleMenu" },
            new[] { "I", "Info (title page)", "XF86Info" },
            new[] { "Y / B", "Yellow / Blue colour keys", "XF86Yellow, XF86Blue" },
            new[] { "PageUp / PageDown", "Channel up / down", "XF86RaiseChannel, XF86LowerChannel" }
        };

        public static readonly string Html = @"<!doctype html>
<html lang=""en""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>RU7100 UI preview</title>
<style>
:root { color-scheme: dark; }
* { box-sizing: border-box; }
body { margin: 0; background: #0b0b0b; color: #ddd; font-family: 'Helvetica Neue', Arial, sans-serif; display: flex; height: 100vh; overflow: hidden; }
#tv { flex: 1; position: relative; display: flex; align-items: center; justify-content: center; min-width: 0; }
#frame { position: relative; width: 1920px; height: 1080px; transform-origin: 0 0; background: #000; box-shadow: 0 0 0 2px #333; overflow: hidden; }
#video, #placeholder { position: absolute; inset: 0; width: 1920px; height: 1080px; }
#video { object-fit: contain; background: #000; }
#placeholder { display: none; align-items: center; justify-content: center; flex-direction: column; background: repeating-linear-gradient(45deg, #1d2633, #1d2633 40px, #222c3a 40px, #222c3a 80px); font-size: 48px; color: #9fb3c8; }
#placeholder b { font-size: 120px; color: #fff; font-variant-numeric: tabular-nums; }
#stage { position: absolute; inset: 0; }
#side { width: 300px; padding: 16px; background: #151515; border-left: 1px solid #262626; overflow-y: auto; font-size: 13px; }
#side h2 { font-size: 14px; margin: 14px 0 8px; color: #fff; }
.remote { display: grid; grid-template-columns: repeat(3, 1fr); gap: 6px; }
.remote button { background: #2a2a2a; color: #eee; border: 1px solid #3a3a3a; border-radius: 8px; padding: 10px 4px; font-size: 13px; cursor: pointer; }
.remote button:hover { background: #3a3a3a; }
.remote .center { background: #e50914; border-color: #e50914; color: #fff; font-weight: 700; }
table { width: 100%; border-collapse: collapse; }
td { padding: 3px 4px; border-bottom: 1px solid #222; vertical-align: top; }
td:last-child { color: #888; font-family: ui-monospace, Menlo, monospace; font-size: 11px; }
pre { white-space: pre-wrap; font-size: 11px; color: #aaa; background: #0e0e0e; padding: 8px; border-radius: 6px; max-height: 280px; overflow: auto; }
#fbbar { display: flex; gap: 6px; align-items: center; }
#fbtoggle { flex: 1; background: #2a2a2a; color: #fff; border: 1px solid #444; border-radius: 8px; padding: 10px; font-size: 14px; font-weight: 700; cursor: pointer; }
#fbtoggle.on { background: #ffcc00; color: #111; border-color: #ffcc00; }
#fbsend { background: #e50914; color: #fff; border: 0; border-radius: 8px; padding: 10px 12px; font-size: 14px; font-weight: 700; cursor: pointer; }
#fbsend:disabled { background: #333; color: #777; cursor: default; }
#fblist { list-style: none; margin: 8px 0 0; padding: 0; }
#fblist li { display: flex; gap: 6px; align-items: flex-start; padding: 6px 4px; border-bottom: 1px solid #222; cursor: pointer; }
#fblist li:hover { background: #1c1c1c; }
#fblist .n { background: #ff2d2d; color: #fff; border-radius: 10px; min-width: 20px; text-align: center; font-weight: 700; font-size: 12px; }
#fblist .c { flex: 1; color: #ddd; white-space: pre-wrap; word-break: break-word; }
#fblist .x { color: #888; border: 0; background: none; cursor: pointer; font-size: 15px; }
#fbstatus { color: #8fd18f; font-size: 12px; margin-top: 6px; min-height: 16px; }
#frame.fbmode { cursor: crosshair; }
#frame.fbmode::after { content: 'Feedback mode — click anything to comment · F to exit'; position: absolute; top: 0; left: 50%; transform: translateX(-50%); background: #ffcc00; color: #111; font: 700 26px Arial; padding: 8px 22px; border-radius: 0 0 14px 14px; z-index: 100000; pointer-events: none; }
.fbpin { position: absolute; width: 56px; height: 56px; margin: -28px 0 0 -28px; border: 6px solid #ff2d2d; border-radius: 50%; box-shadow: 0 0 0 3px rgba(0,0,0,.6); z-index: 99998; pointer-events: none; }
.fbpin b { position: absolute; left: 44px; top: -34px; background: #ff2d2d; color: #fff; font: 700 28px Arial; padding: 0 10px; border-radius: 16px; }
#fbpop { position: absolute; z-index: 99999; width: 640px; background: #1b1b1b; border: 2px solid #ffcc00; border-radius: 14px; padding: 16px; box-shadow: 0 12px 40px rgba(0,0,0,.6); display: none; }
#fbpop textarea { width: 100%; height: 150px; background: #0f0f0f; color: #fff; border: 1px solid #444; border-radius: 8px; font: 28px Arial; padding: 10px; resize: none; }
#fbpop .hint { color: #aaa; font: 20px Arial; margin-top: 8px; }
#fbpop .what { color: #ffcc00; font: 20px ui-monospace, Menlo, monospace; margin-bottom: 8px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
@media (max-width: 800px) { body { flex-direction: column; height: auto; overflow: auto; } #tv { height: 56vw; flex: none; } #side { width: 100%; } }
</style></head>
<body>
<div id=""tv""><div id=""frame"">
  <video id=""video"" muted playsinline preload=""auto""></video>
  <div id=""placeholder""><span>Simulated playback</span><b id=""clock"">0:00</b></div>
  <div id=""stage""></div>
  <div id=""fbpins""></div>
  <div id=""fbpop""><div class=""what"" id=""fbwhat""></div><textarea id=""fbtext"" placeholder=""What bothers you here?""></textarea><div class=""hint"">Enter to save · Shift+Enter new line · Esc cancel</div></div>
</div></div>
<aside id=""side"">
  <div id=""mode"" style=""color:#888;font-size:12px""></div>
  <h2>Feedback</h2>
  <div id=""fbbar""><button id=""fbtoggle"" title=""F"">✎ Feedback (F)</button><button id=""fbsend"" disabled>Send all</button></div>
  <ul id=""fblist""></ul>
  <div id=""fbstatus""></div>
  <h2>Remote</h2>
  <div class=""remote"">
    <button data-k=""Menu"">Menu</button><button data-k=""Up"">▲</button><button data-k=""Back"">Back</button>
    <button data-k=""Left"">◀</button><button data-k=""Select"" class=""center"">OK</button><button data-k=""Right"">▶</button>
    <button data-k=""Rewind"">⏪</button><button data-k=""Down"">▼</button><button data-k=""FastForward"">⏩</button>
    <button data-k=""Play"">Play</button><button data-k=""PlayPause"">⏯</button><button data-k=""Pause"">Pause</button>
    <button data-k=""Stop"">Stop</button><button data-k=""Next"">Next</button><button id=""reset"">Reset</button>
  </div>
  <h2>Keyboard</h2>
  <table id=""keymap""></table>
  <h2>State</h2>
  <pre id=""text""></pre>
</aside>
<script>
const keyMap = { ArrowUp:'Up', ArrowDown:'Down', ArrowLeft:'Left', ArrowRight:'Right', Enter:'Select', Backspace:'Back', Escape:'Back',
  ' ':'PlayPause', p:'Play', k:'Pause', s:'Stop', '[':'Rewind', ']':'FastForward', n:'Next', m:'Menu', i:'Info', y:'Yellow', b:'Blue',
  PageUp:'ChannelUp', PageDown:'ChannelDown' };
const frame = document.getElementById('frame'), stage = document.getElementById('stage'), video = document.getElementById('video');
const placeholder = document.getElementById('placeholder'), clock = document.getElementById('clock');
let version = -1, videoState = null, lastSeek = -1, videoBroken = false;

function fit() {
  const box = document.getElementById('tv').getBoundingClientRect();
  const scale = Math.min(box.width / 1920, box.height / 1080);
  frame.style.transform = 'scale(' + scale + ')';
  frame.style.marginRight = (1920 * scale - 1920) + 'px';
  frame.style.marginBottom = (1080 * scale - 1080) + 'px';
}
window.addEventListener('resize', fit); fit();

async function send(key) {
  const r = await fetch('/key', { method: 'POST', body: key });
  document.getElementById('text').textContent = await r.text();
}
document.querySelectorAll('[data-k]').forEach(b => b.addEventListener('click', () => send(b.dataset.k)));
document.getElementById('reset').addEventListener('click', () => fetch('/reset', { method: 'POST', body: '' }));
// A YouTube iframe must never keep keyboard focus: the remote (keyboard) would go dead until a click.
document.body.tabIndex = -1;
function reclaimFocus() { if (document.activeElement && document.activeElement.tagName === 'IFRAME') { document.activeElement.blur(); document.body.focus(); } }
window.addEventListener('blur', () => setTimeout(() => { reclaimFocus(); window.focus(); }, 0));
setInterval(reclaimFocus, 500);
document.addEventListener('keydown', e => {
  if (e.target && (e.target.tagName === 'TEXTAREA' || e.target.tagName === 'INPUT')) return;
  if ((e.key === 'f' || e.key === 'F') && !e.metaKey && !e.ctrlKey) { e.preventDefault(); setFeedbackMode(!fbMode); return; }
  const k = keyMap[e.key] || keyMap[e.key.toLowerCase()];
  if (!k || e.metaKey || e.ctrlKey) return;
  e.preventDefault(); send(k);
});

fetch('/keymap').then(r => r.json()).then(rows => {
  document.getElementById('keymap').innerHTML = '<tr><td><b>Key</b></td><td><b>Remote</b></td><td>Tizen</td></tr>' +
    rows.map(r => '<tr><td>' + r[0] + '</td><td>' + r[1] + '</td><td>' + r[2] + '</td></tr>').join('');
});

function fmt(ms) { const s = Math.floor(ms / 1000); return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); }

video.addEventListener('error', () => { videoBroken = true; syncVideo(); });
// Real playback (--live): the server's BrowserPlayer drives this element and reads back position/duration.
let realOpen = -1, realError = null, playedFrom = -1;
// HLS (live channels): Safari plays it natively; Chrome gets hls.js, loaded on first use. Preview-only: the TV's
// AVPlay plays HLS itself.
let hls = null, hlsLive = false, hlsLoading = null;
function isHls(url) { return /\.m3u8(\?|$)/i.test(url || ''); }
function loadHlsJs() {
  if (window.Hls) return Promise.resolve(window.Hls);
  if (!hlsLoading) hlsLoading = new Promise((resolve, reject) => {
    const s = document.createElement('script');
    s.src = 'https://cdn.jsdelivr.net/npm/hls.js@1.5.17/dist/hls.min.js';
    s.onload = () => resolve(window.Hls); s.onerror = () => reject(new Error('hls.js did not load'));
    document.head.appendChild(s);
  });
  return hlsLoading;
}
function dropHls() { if (hls) { try { hls.destroy(); } catch (e) {} hls = null; } hlsLive = false; }
function openReal(url) {
  dropHls();
  if (!isHls(url)) { video.setAttribute('src', url); video.load(); return; }
  video.removeAttribute('src'); video.load();
  const open = realOpen;
  // hls.js wherever Media Source works: Chromium may answer canPlayType 'maybe' for HLS and then fail to parse it.
  loadHlsJs().then(Hls => {
    if (open !== realOpen) return;
    if (!Hls.isSupported()) { video.setAttribute('src', url); video.load(); return; }
    hls = new Hls({ lowLatencyMode: false });
    hls.on(Hls.Events.LEVEL_LOADED, (e, data) => { hlsLive = !!(data.details && data.details.live); });
    hls.on(Hls.Events.ERROR, (e, data) => { if (data.fatal) realError = 'The stream failed in the browser (' + data.details + ').'; });
    hls.loadSource(url); hls.attachMedia(video);
  }).catch(() => { if (open === realOpen) { video.setAttribute('src', url); video.load(); } });
}
video.addEventListener('playing', () => { if (playedFrom < 0) playedFrom = video.currentTime; });
video.addEventListener('seeking', () => { playedFrom = -1; });
video.addEventListener('loadedmetadata', () => {
  if (videoState && videoState.mode === 'real' && video.videoWidth === 0)
    realError = 'Preview can’t decode this video (likely HEVC). This source plays on the TV.';
});
function realFailure() {
  if (realError) return realError;
  // Chrome plays the picture and stays silent when it can't decode the audio (Dolby Digital / DD+ / DTS, common in
  // 1080p releases). After a few seconds of playback with zero decoded audio bytes, report it so another source is tried.
  // A few seconds of playback means time played since playback (re)started, not the position: a resume at 21 min
  // has currentTime 1260 from its first frame, which used to fail every resumed source at once.
  if (typeof video.webkitAudioDecodedByteCount === 'number' && !video.paused && playedFrom >= 0 && video.currentTime - playedFrom > 4
      && video.webkitAudioDecodedByteCount === 0)
    return 'Preview can’t play this source’s audio (likely Dolby Digital). It plays on the TV. Trying another source…';
  if (!video.error) return null;
  return video.error.code === 4 || video.error.code === 3
    ? 'Preview can’t decode this source (HEVC video or EAC3/DTS audio). It plays on the TV.'
    : 'The stream failed in the browser (' + (video.error.message || video.error.code) + ').';
}
setInterval(() => {
  const v = videoState;
  if (!v || v.mode !== 'real' || !v.active || v.open !== realOpen) return;
  // A live stream reports duration -1 once its picture plays: it has no end, and the player must not wait for one.
  const live = (hlsLive || video.duration === Infinity) && video.readyState >= 2;
  const body = { open: v.open, position: video.currentTime || 0, duration: live ? -1 : isFinite(video.duration) ? video.duration : 0, ended: video.ended, error: realFailure() };
  fetch('/video-report', { method: 'POST', body: JSON.stringify(body) }).catch(() => {});
}, 500);
function showMessage(text, big) {
  video.style.display = 'none'; placeholder.style.display = 'flex';
  placeholder.firstChild.textContent = text; clock.textContent = big || '';
}
function syncVideo() {
  const v = videoState;
  const active = v && v.active && v.url;
  if (!active) { video.pause(); dropHls(); video.style.display = 'none'; placeholder.style.display = 'none'; return; }
  if (v.mode === 'real') {
    if (v.open !== realOpen) {
      realOpen = v.open; realError = null; videoBroken = false; lastSeek = -1; playedFrom = -1;
      video.muted = false; openReal(v.url);
    }
    const failure = v.error || realFailure();
    // The app's start card says what happens next (Chrome can't decode that source, trying another…): no second
    // message underneath it.
    if (failure) { video.style.display = 'none'; placeholder.style.display = 'none'; return; }
    video.style.display = 'block'; placeholder.style.display = 'none';
    if (v.seekSeq !== lastSeek) { lastSeek = v.seekSeq; try { video.currentTime = v.positionMs / 1000; } catch (e) {} }
    if (v.playing && video.paused) video.play().catch(() => {});
    if (!v.playing && !video.paused) video.pause();
    return;
  }
  if (videoBroken) {
    showMessage('Simulated playback', fmt(v.positionMs)); return;
  }
  video.style.display = 'block'; placeholder.style.display = 'none';
  if (video.getAttribute('src') !== v.url) { video.setAttribute('src', v.url); lastSeek = -1; }
  const clip = video.duration > 0 ? video.duration * 1000 : v.clipMs;
  const target = (v.positionMs % clip) / 1000;
  if (v.seekSeq !== lastSeek || Math.abs(video.currentTime - target) > 2.5) {
    lastSeek = v.seekSeq;
    try { video.currentTime = target; } catch (e) {}
  }
  if (v.playing && video.paused) video.play().catch(() => {});
  if (!v.playing && !video.paused) video.pause();
}

// Keyed painter: the same job as the ElmSharp painter. Elements persist per node key; each frame only
// updates geometry/opacity, and content when it changed. Frames come pre-animated from the server (UiAnimator).
const els = new Map();
function css(c) {
  if (!c) return 'transparent';
  if (c.length === 9) { const n = parseInt(c.slice(1), 16); return 'rgba(' + ((n >>> 24) & 255) + ',' + ((n >>> 16) & 255) + ',' + ((n >>> 8) & 255) + ',' + ((n & 255) / 255).toFixed(3) + ')'; }
  return c;
}
function esc(s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); }
function textHtml(n) {
  const lines = String(n.s || '').split('\n');
  return lines.map(l => {
    if (!n.f && !n.ol) return esc(l);
    let st = '';
    if (n.f) st += 'background:' + css(n.f) + ';padding:2px 12px;box-decoration-break:clone;-webkit-box-decoration-break:clone;';
    if (n.ol) st += 'text-shadow:-2px -2px 0 #000,2px -2px 0 #000,-2px 2px 0 #000,2px 2px 0 #000,3px 4px 6px rgba(0,0,0,.8);';
    return '<span dir=auto style=' + JSON.stringify(st) + '>' + esc(l) + '</span>';
  }).join('<br>');
}
function ytSrc(id, muted) { var m = (muted || !(navigator.userActivation && navigator.userActivation.hasBeenActive)) ? 1 : 0; return 'https://www.youtube-nocookie.com/embed/' + id + '?autoplay=1&mute=' + m + '&controls=0&loop=1&playlist=' + id + '&modestbranding=1&playsinline=1&enablejsapi=1&rel=0&cc_load_policy=0&iv_load_policy=3&disablekb=1&fs=0&origin=' + encodeURIComponent(location.origin); }
function ytCommand(frameEl, func) { try { frameEl.contentWindow.postMessage(JSON.stringify({ event: 'command', func: func, args: [] }), '*'); } catch (e) {} }
// Trailers like the TV: IMDb's full-length MP4 in a <video> that fills the area (no player bars); YouTube embed only
// when IMDb has none. Sound fades in to the preview level (60%) once frames show.
function activated() { return !!(navigator.userActivation && navigator.userActivation.hasBeenActive); }
// Like the TV player: tell the app once the trailer's picture is on screen (it turns the trailer view compact).
function trailerStarted(box) { if (box._started) return; box._started = true; fetch('/trailer-started', { method: 'POST', body: box._vid || '' }).catch(() => {}); }
async function startTrailer(box, titleId, ytId, muted) {
  box.innerHTML = ''; box._v = null; box._yt = null; box._started = false; box._vid = ytId;
  let t = null;
  if (titleId) { try { t = await (await fetch('/trailer?title=' + encodeURIComponent(titleId))).json(); } catch (e) {} }
  if (box._ident !== (titleId || '') + '|' + (ytId || '')) return;   // focus moved on while looking it up
  if (t && t.url && !t.hls) {
    const v = document.createElement('video');
    v.src = t.url; v.autoplay = true; v.playsInline = true; v.preload = 'auto';
    v.style.cssText = 'width:100%;height:100%;object-fit:cover;display:block;opacity:0;transition:opacity .6s';
    v.muted = muted || !activated(); v.volume = 0;
    v.addEventListener('playing', () => { v.style.opacity = 1; fadeVolume(v, 0.6); trailerStarted(box); });
    box.appendChild(v); box._v = v; v.play().catch(() => { v.muted = true; v.play().catch(() => {}); });
    return;
  }
  if (!ytId) return;
  const f = document.createElement('iframe');
  f.setAttribute('allow', 'autoplay; encrypted-media'); f.setAttribute('referrerpolicy', 'strict-origin-when-cross-origin');
  f.setAttribute('tabindex', '-1'); f.style.cssText = 'border:0;width:100%;height:100%;pointer-events:none';
  f.src = ytSrc(ytId, muted); box.appendChild(f); box._yt = f; box._muted = muted || !activated();
  f.onload = () => { try { f.contentWindow.postMessage(JSON.stringify({ event: 'command', func: 'unloadModule', args: ['captions'] }), '*'); } catch (e) {} if (!box._wantMuted) ytCommand(f, 'unMute'); };
}
function fadeVolume(v, target) { let x = 0; const id = setInterval(() => { x = Math.min(target, x + target / 15); v.volume = x; if (x >= target) clearInterval(id); }, 100); }
function applyTrailerMute(box, muted) {
  if (box._v) { box._v.muted = muted; if (!muted && box._v.volume === 0) fadeVolume(box._v, 0.6); }
  else if (box._yt && box._muted !== muted) { ytCommand(box._yt, muted ? 'mute' : 'unMute'); box._muted = muted; }
}
function paint(state) {
  stage.style.background = state.background ? css(state.background) : 'transparent';
  const seen = new Set();
  (state.nodes || []).forEach((n, i) => {
    seen.add(n.k);
    const kind = n.t || 0;
    let el = els.get(n.k);
    if (el && el._kind !== kind) { el.remove(); el = null; }
    if (!el) {
      el = document.createElement('div');
      el._kind = kind; el.dataset.key = n.k; el.style.position = 'absolute';
      if (kind === 2) { const img = document.createElement('img'); img.style.cssText = 'width:100%;height:100%;display:block'; img.onerror = () => ruLogoError(img); img.onload = () => { if (img.dataset.ct) ruFitLogo(img); }; el.appendChild(img); }
      if (kind === 3) { el.style.pointerEvents = 'none'; el.style.background = 'transparent'; }
      stage.appendChild(el); els.set(n.k, el);
    }
    const s = el.style;
    s.left = (n.x || 0) + 'px'; s.top = (n.y || 0) + 'px'; s.width = (n.w || 0) + 'px'; s.height = (n.h || 0) + 'px';
    s.opacity = n.o || 0; s.zIndex = i + 1;
    s.borderRadius = (n.r || 0) + 'px'; s.overflow = n.r || kind === 1 ? 'hidden' : '';   // every frame: text clips (ellipsis) even when its content is unchanged
    if (kind === 2 && n.ct) ruFitLogo(el.firstChild);   // logos follow their box as it moves (opaque-part fit, TrimScript)
    const sig = JSON.stringify([kind === 1 ? n.h : 0, n.sk, n.f, n.s, n.fs, n.c, n.b, n.a, n.wr, n.ol, n.img, n.ct, n.ab, n.cx, n.cy, n.v, n.ti, n.m, n.hz, n.mk, n.sh]);
    if (el._sig === sig) return;
    el._sig = sig;
    if (kind === 0) s.background = css(n.f);
    else if (kind === 4) s.background = 'linear-gradient(to ' + (n.hz ? 'right' : 'bottom') + ',' + css(n.f) + ',' + css(n.c) + ')';
    else if (kind === 2 && n.mk) {   // glyph: an SVG mask tinted with the node colour, like the TV's tinted PNG
      el.innerHTML = ''; s.background = css(n.f || '#FFFFFF'); s.webkitMask = 'url(""' + n.img + '"") center/100% 100% no-repeat';
    } else if (kind === 2 && n.sh) {   // soft shadow: the TV stretches a 9-slice image
      el.innerHTML = '<div style=""' + n.sh + '""></div>'; s.overflow = 'visible';
    } else if (kind === 2) {
      const img = el.firstChild;
      img.dataset.ct = n.ct ? '1' : ''; img.dataset.ab = n.ab ? '1' : '';
      if (!n.ct) { const hidden = img.style.visibility; img.style.cssText = 'width:100%;height:100%;display:block;object-fit:cover;object-position:' + ((n.cx ?? 0.5) * 100) + '% ' + ((n.cy ?? 0.5) * 100) + '%'; img.style.visibility = hidden; }
      if (img.getAttribute('src') !== n.img) {
        img.style.visibility = 'visible';
        if (n.ct) img.crossOrigin = 'anonymous'; else img.removeAttribute('crossorigin');
        img.setAttribute('src', n.img);
      }
      if (n.ct) ruFitLogo(img);
    } else if (kind === 3) {
      el._wantMuted = !!n.m;
      const ident = (n.ti || '') + '|' + (n.v || '');
      if (el._ident !== ident) { el._ident = ident; startTrailer(el, n.ti, n.v, !!n.m); }
      else applyTrailerMute(el, !!n.m);
    } else {
      const align = n.a === 1 ? 'center' : n.a === 2 ? 'right' : 'left';
      s.fontSize = (n.fs || 32) + 'px'; s.color = css(n.c); s.fontWeight = n.b ? '700' : '400'; s.textAlign = align;
      s.webkitTextStroke = n.sk ? '4px ' + css(n.sk) : ''; s.paintOrder = n.sk ? 'stroke fill' : '';
      s.lineHeight = n.wr ? '1.45' : (n.h || 0) + 'px'; s.overflow = 'hidden';   // one-line text is centred in its box, as on the TV
      s.whiteSpace = n.wr ? 'pre-wrap' : 'nowrap'; s.textOverflow = n.wr ? 'clip' : 'ellipsis';
      // Wrapped text shows whole lines only (same 1.45 line height as the TV); extra lines end in an ellipsis.
      const clamp = n.wr && !n.f && !n.ol;
      s.display = clamp ? '-webkit-box' : ''; s.webkitBoxOrient = clamp ? 'vertical' : '';
      s.webkitLineClamp = clamp ? String(Math.max(1, Math.floor(((n.h || 0) + (n.fs || 32) * 0.2) / ((n.fs || 32) * 1.45)))) : '';
      el.innerHTML = textHtml(n);
    }
  });
  for (const [k, el] of els) if (!seen.has(k)) { el.remove(); els.delete(k); }
}

// The trailer stays invisible (still art shows) until YouTube reports it is actually playing;
// an embed error keeps the still art.
window.addEventListener('message', e => {
  if (!/youtube/.test(e.origin)) return;
  let d; try { d = JSON.parse(e.data); } catch (err) { return; }
  for (const el of els.values()) {
    if (el._kind !== 3 || !el._yt || el._yt.contentWindow !== e.source) continue;
    const state = d.event === 'onStateChange' ? d.info : d.event === 'infoDelivery' && d.info ? d.info.playerState : undefined;
    if (state === 1) { el.style.visibility = 'visible'; trailerStarted(el); }
    if (d.event === 'onError') el.style.visibility = 'hidden';
  }
});
let lastText = '', bootId = null;
// ---- Feedback mode: pin notes on the screen, send them all at once ----
let fbMode = false, fbDraft = null, fbNotes = [];
const fbpop = document.getElementById('fbpop'), fbtext = document.getElementById('fbtext');
function stageScale() { return frame.getBoundingClientRect().width / 1920; }
function setFeedbackMode(on) {
  fbMode = on; frame.classList.toggle('fbmode', on);
  document.getElementById('fbtoggle').classList.toggle('on', on);
  document.getElementById('fbtoggle').textContent = on ? '✎ Feedback ON (F)' : '✎ Feedback (F)';
  if (!on) cancelDraft();
  renderPins();
}
document.getElementById('fbtoggle').addEventListener('click', () => setFeedbackMode(!fbMode));
function nodeAt(clientX, clientY) {
  for (const el of document.elementsFromPoint(clientX, clientY)) {
    if (el.dataset && el.dataset.key && stage.contains(el)) {
      // Prefer the element that carries visible text; else the first keyed one.
      const text = (el.innerText || el.textContent || '').trim();
      return { key: el.dataset.key, text: text.slice(0, 160) };
    }
  }
  return { key: null, text: '' };
}
frame.addEventListener('click', e => {
  if (!fbMode || fbpop.contains(e.target)) return;
  e.preventDefault(); e.stopPropagation();
  const r = frame.getBoundingClientRect(), sc = stageScale();
  const x = Math.round((e.clientX - r.left) / sc), y = Math.round((e.clientY - r.top) / sc);
  const hit = nodeAt(e.clientX, e.clientY);
  fbDraft = { x, y, key: hit.key, text: hit.text };
  document.getElementById('fbwhat').textContent = (hit.key || 'background') + (hit.text ? ' — ' + hit.text.replace(/\s+/g, ' ') : '');
  fbpop.style.left = Math.min(1920 - 660, Math.max(10, x + 40)) + 'px';
  fbpop.style.top = Math.min(1080 - 260, Math.max(10, y - 40)) + 'px';
  fbpop.style.display = 'block'; fbtext.value = ''; renderPins(); setTimeout(() => fbtext.focus(), 0);
}, true);
function cancelDraft() { fbDraft = null; fbpop.style.display = 'none'; renderPins(); document.body.focus(); }
fbtext.addEventListener('keydown', async e => {
  if (e.key === 'Escape') { e.preventDefault(); cancelDraft(); return; }
  if (e.key === 'Enter' && !e.shiftKey) {
    e.preventDefault();
    const comment = fbtext.value.trim();
    if (!comment || !fbDraft) { cancelDraft(); return; }
    const d = fbDraft; cancelDraft();
    await fetch('/feedback/add', { method: 'POST', body: JSON.stringify({ x: d.x, y: d.y, key: d.key, text: d.text, comment }) });
    loadNotes();
  }
  e.stopPropagation();
});
async function loadNotes() {
  try { fbNotes = await (await fetch('/feedback')).json(); } catch (e) { fbNotes = []; }
  const list = document.getElementById('fblist');
  list.innerHTML = '';
  fbNotes.forEach((n, i) => {
    const li = document.createElement('li');
    li.innerHTML = '<span class=""n"">' + (i + 1) + '</span><span class=""c""></span><button class=""x"" title=""Delete"">✕</button>';
    li.querySelector('.c').textContent = n.Comment + (n.hasShot ? '' : ' ⏳');
    li.querySelector('.x').addEventListener('click', async ev => { ev.stopPropagation(); await fetch('/feedback/delete', { method: 'POST', body: JSON.stringify({ id: n.Id }) }); loadNotes(); });
    li.addEventListener('click', async () => {
      const text = prompt('Edit note ' + (i + 1), n.Comment);
      if (text !== null && text.trim()) { await fetch('/feedback/update', { method: 'POST', body: JSON.stringify({ id: n.Id, comment: text.trim() }) }); loadNotes(); }
    });
    list.appendChild(li);
  });
  const send = document.getElementById('fbsend');
  send.disabled = fbNotes.length === 0;
  send.textContent = fbNotes.length ? 'Send all (' + fbNotes.length + ')' : 'Send all';
  renderPins();
}
function renderPins() {
  const box = document.getElementById('fbpins');
  box.innerHTML = '';
  if (!fbMode) return;
  const all = fbNotes.map((n, i) => ({ x: n.X, y: n.Y, label: i + 1 }));
  if (fbDraft) all.push({ x: fbDraft.x, y: fbDraft.y, label: '+' });
  all.forEach(p => { const d = document.createElement('div'); d.className = 'fbpin'; d.style.left = p.x + 'px'; d.style.top = p.y + 'px'; d.innerHTML = '<b>' + p.label + '</b>'; box.appendChild(d); });
}
document.getElementById('fbsend').addEventListener('click', async () => {
  if (!fbNotes.length) return;
  const pending = fbNotes.filter(n => !n.hasShot).length;
  if (pending) { document.getElementById('fbstatus').textContent = 'Finishing ' + pending + ' screenshot(s)…'; await new Promise(r => setTimeout(r, 4000)); }
  const r = await (await fetch('/feedback/send', { method: 'POST', body: '{}' })).json();
  document.getElementById('fbstatus').textContent = r.folder ? 'Sent ' + fbNotes.length + ' notes to Claude ✓' : 'Nothing to send';
  setFeedbackMode(false); loadNotes();
  setTimeout(() => document.getElementById('fbstatus').textContent = '', 8000);
});
setInterval(() => { if (fbNotes.some(n => !n.hasShot)) loadNotes(); }, 2500);
loadNotes();
async function poll() {
  try {
    const r = await fetch('/state' + (version >= 0 ? '?since=' + version : ''));
    const s = await r.json();
    if (bootId && s.boot && s.boot !== bootId) { location.reload(); return; }   // server restarted (dotnet watch): fresh code
    bootId = s.boot || bootId;
    version = s.version;
    paint(s);
    if (s.text !== lastText) { lastText = s.text; document.getElementById('text').textContent = s.text; }
    document.getElementById('mode').textContent = s.live ? 'Live add-on data' : 'Fake data';
    videoState = s.video;
    syncVideo();
  } catch (e) { await new Promise(r => setTimeout(r, 1000)); }
  poll();
}
poll();
</script>
" + Ru7100.Core.Ui.UiHtml.TrimScript + @"
</body></html>";
    }
}
