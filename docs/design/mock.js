// Shared behaviour for the mocks: icon sprite, scale-to-fit, state loop, logo fitting.
// URL switches:  ?still (final state, no motion)  ?state=b (alternate state, no motion)  ?accent=ember|ice
(function () {
  var q = new URLSearchParams(location.search);
  var ACC = { amber: '#FFB23E', ember: '#FF7A45', ice: '#7FD4FF' };
  if (ACC[q.get('accent')]) document.documentElement.style.setProperty('--accent', ACC[q.get('accent')]);
  var still = q.has('still') || q.has('state');
  if (still) document.documentElement.classList.add('still');

  var P = {
    play: '<path d="M7.5 4.6v14.8a.8.8 0 0 0 1.2.7l12-7.4a.8.8 0 0 0 0-1.4l-12-7.4a.8.8 0 0 0-1.2.7z"/>',
    pause: '<rect x="5.5" y="4.5" width="4.6" height="15" rx="1.2"/><rect x="13.9" y="4.5" width="4.6" height="15" rx="1.2"/>',
    plus: '<rect x="10.7" y="4" width="2.6" height="16" rx="1.3"/><rect x="4" y="10.7" width="16" height="2.6" rx="1.3"/>',
    check: '<path d="M9.3 16.6 4.9 12.2a1.3 1.3 0 0 0-1.8 1.8l5.3 5.3a1.3 1.3 0 0 0 1.8 0L21 8.5a1.3 1.3 0 0 0-1.8-1.8z"/>',
    thumb: '<path d="M1 21h4V9H1v12zm22-11c0-1.1-.9-2-2-2h-6.31l.95-4.57.03-.32c0-.41-.17-.79-.44-1.06L14.17 1 7.59 7.59C7.22 7.95 7 8.45 7 9v10c0 1.1.9 2 2 2h9c.83 0 1.54-.5 1.84-1.22l3.02-7.05c.09-.23.14-.47.14-.73v-2z"/>',
    down: '<path d="M5.2 8.3a1.3 1.3 0 0 1 1.8 0l5 5 5-5a1.3 1.3 0 0 1 1.8 1.8l-5.9 5.9a1.3 1.3 0 0 1-1.8 0L5.2 10.1a1.3 1.3 0 0 1 0-1.8z"/>',
    left: '<path d="M15.7 5.2a1.3 1.3 0 0 1 0 1.8l-5 5 5 5a1.3 1.3 0 0 1-1.8 1.8L8 12.9a1.3 1.3 0 0 1 0-1.8l5.9-5.9a1.3 1.3 0 0 1 1.8 0z"/>',
    right: '<path d="M8.3 5.2a1.3 1.3 0 0 0 0 1.8l5 5-5 5a1.3 1.3 0 0 0 1.8 1.8l5.9-5.9a1.3 1.3 0 0 0 0-1.8l-5.9-5.9a1.3 1.3 0 0 0-1.8 0z"/>',
    dots: '<circle cx="5" cy="12" r="2.1"/><circle cx="12" cy="12" r="2.1"/><circle cx="19" cy="12" r="2.1"/>',
    rew: '<path d="M11.4 5.6v12.8a.7.7 0 0 1-1.1.6L1.6 12.6a.7.7 0 0 1 0-1.2L10.3 5a.7.7 0 0 1 1.1.6zM22.6 5.6v12.8a.7.7 0 0 1-1.1.6l-8.7-6.4a.7.7 0 0 1 0-1.2L21.5 5a.7.7 0 0 1 1.1.6z"/>',
    ffw: '<path d="M12.6 5.6v12.8a.7.7 0 0 0 1.1.6l8.7-6.4a.7.7 0 0 0 0-1.2L13.7 5a.7.7 0 0 0-1.1.6zM1.4 5.6v12.8a.7.7 0 0 0 1.1.6l8.7-6.4a.7.7 0 0 0 0-1.2L2.500 5a.7.7 0 0 0-1.1.6z"/>',
    next: '<path d="M4 5.4v13.200a.7.7 0 0 0 1.1.6l10-6.600a.7.7 0 0 0 0-1.200L5.100 4.800a.7.700 0 0 0-1.100.6z"/><rect x="17.200" y="4.500" width="2.800" height="15" rx="1.200"/>',
    subs: '<path d="M4.500 4h15A2.500 2.500 0 0 1 22 6.500v9a2.500 2.500 0 0 1-2.500 2.500H10l-4.300 3.400a.6.600 0 0 1-1-.5V18h-.2A2.500 2.500 0 0 1 2 15.500v-9A2.500 2.500 0 0 1 4.500 4zm1.700 4.600a1 1 0 0 0 0 2h5.600a1 1 0 0 0 0-2zm9 0a1 1 0 0 0 0 2h2.600a1 1 0 0 0 0-2zm-9 3.800a1 1 0 0 0 0 2h2.600a1 1 0 0 0 0-2zm6 0a1 1 0 0 0 0 2h5.600a1 1 0 0 0 0-2z" fill-rule="evenodd"/>',
    eps: '<rect x="2.500" y="4" width="8" height="5.200" rx="1.200"/><rect x="2.500" y="10.600" width="8" height="5.200" rx="1.200"/><rect x="12.500" y="5.400" width="9" height="2.400" rx="1.200"/><rect x="12.500" y="12" width="9" height="2.400" rx="1.200"/><rect x="2.500" y="17.600" width="19" height="2.400" rx="1.200"/>',
    layers: '<path d="M12 2.600 1.800 8.200 12 13.800l10.200-5.600zM4.300 11.900 1.800 13.300 12 18.900l10.200-5.600-2.500-1.400L12 16.100z"/><path d="M4.300 16.400 1.800 17.800 12 23.400l10.200-5.600-2.500-1.400L12 20.600z" opacity=".55"/>',
    gear: '<path d="M19.14 12.940c.040-.3.060-.610.060-.940 0-.320-.020-.640-.070-.940l2.030-1.580c.180-.140.230-.410.120-.610l-1.920-3.320c-.120-.220-.370-.290-.590-.220l-2.390.960c-.5-.380-1.030-.7-1.620-.940l-.360-2.540c-.040-.240-.240-.410-.480-.410h-3.840c-.240 0-.430.170-.470.410l-.360 2.540c-.590.240-1.130.570-1.620.940l-2.390-.960c-.220-.080-.470 0-.590.220L2.740 8.870c-.120.210-.080.470.120.610l2.030 1.580c-.050.3-.090.630-.090.940s.020.640.070.940l-2.030 1.580c-.180.140-.230.410-.120.610l1.920 3.320c.120.220.370.290.590.220l2.390-.960c.5.380 1.030.7 1.620.940l.360 2.540c.050.240.240.410.480.410h3.840c.240 0 .440-.170.470-.410l.360-2.540c.590-.240 1.130-.560 1.620-.940l2.390.960c.220.080.470 0 .590-.220l1.920-3.320c.120-.220.070-.470-.120-.610l-2.010-1.580zM12 15.600c-1.980 0-3.600-1.620-3.600-3.600s1.620-3.600 3.600-3.600 3.600 1.620 3.600 3.600-1.620 3.600-3.600 3.600z"/>',
    film: '<path d="M4.500 4h15A2.500 2.500 0 0 1 22 6.500v11a2.500 2.500 0 0 1-2.500 2.500h-15A2.500 2.500 0 0 1 2 17.500v-11A2.500 2.500 0 0 1 4.500 4zm5.300 4.300v7.400a.5.500 0 0 0 .8.400l5.600-3.700a.5.500 0 0 0 0-.8l-5.600-3.700a.5.500 0 0 0-.8.400z" fill-rule="evenodd"/>',
    star: '<path d="m12 2.600 2.900 6 6.500.9-4.700 4.600 1.100 6.500L12 17.500l-5.800 3.100 1.100-6.500L2.600 9.500l6.500-.9z"/>',
    vol: '<path d="M3 9.500v5a1 1 0 0 0 1 1h3.200l4.600 3.800a.7.700 0 0 0 1.200-.6V5.300a.7.700 0 0 0-1.200-.6L7.200 8.500H4a1 1 0 0 0-1 1zm13.300-2.200a1 1 0 0 0-.7 1.800 3.700 3.700 0 0 1 0 5.800 1 1 0 0 0 1.300 1.500 5.700 5.700 0 0 0 0-8.800 1 1 0 0 0-.6-.3z"/>',
    mark: '<path d="M6.200 2.500h4.300a1.600 1.600 0 0 1 1.400.8l4.700 7.900a1.600 1.600 0 0 1 0 1.600l-4.700 7.900a1.600 1.600 0 0 1-1.400.8H6.200a1.200 1.200 0 0 1-1-1.800l4.800-8.100a.8.800 0 0 0 0-.8L5.200 4.300a1.200 1.200 0 0 1 1-1.800z"/><path d="M15 2.500h2.300a1.600 1.600 0 0 1 1.400.8l4.300 7.900a1.600 1.600 0 0 1 0 1.600l-4.300 7.900a1.600 1.600 0 0 1-1.400.8H15a.6.600 0 0 1-.5-.9l4.600-8.200a.8.800 0 0 0 0-.8l-4.600-8.200a.6.600 0 0 1 .5-.9z" opacity=".55"/>'
  };
  var sprite = '<svg xmlns="http://www.w3.org/2000/svg" style="position:absolute;width:0;height:0;overflow:hidden">';
  for (var k in P) sprite += '<symbol id="i-' + k + '" viewBox="0 0 24 24">' + P[k] + '</symbol>';
  document.body.insertAdjacentHTML('afterbegin', sprite + '</svg>');

  // logos: fit by area into their box so wide and tall logos carry similar weight (DB13)
  function fitLogo(img) {
    var box = img.parentElement, W = box.clientWidth, H = box.clientHeight, a = +box.dataset.area;
    if (!a || !img.naturalWidth) return;
    var s = Math.min(W / img.naturalWidth, H / img.naturalHeight, Math.sqrt(a / (img.naturalWidth * img.naturalHeight)));
    img.style.width = Math.round(img.naturalWidth * s) + 'px'; img.style.height = Math.round(img.naturalHeight * s) + 'px';
    img.style.position = 'absolute'; img.style.left = '0'; img.style.bottom = '0'; img.style.top = 'auto';
  }
  document.querySelectorAll('[data-area] > img').forEach(function (img) { if (img.complete) fitLogo(img); else img.addEventListener('load', function () { fitLogo(img); }); });

  var st = document.querySelector('.stage');
  if (!st) return;
  function fit() {
    var s = Math.min(innerWidth / 1920, innerHeight / 1080);
    st.style.transform = 'translate(' + Math.max(0, (innerWidth - 1920 * s) / 2) + 'px,' + Math.max(0, (innerHeight - 1080 * s) / 2) + 'px) scale(' + s + ')';
  }
  fit(); addEventListener('resize', fit);

  // state loop: class "a" on the stage is the settled state shown in the PNG; without it the stage shows state B.
  if (q.get('state') === 'b') st.classList.remove('a');
  if (!still && st.dataset.loop !== 'off') {
    var holdA = +(st.dataset.holdA || 5200), holdB = +(st.dataset.holdB || 2200);
    st.classList.remove('a');
    (function toA() { setTimeout(function () { st.classList.add('a'); setTimeout(function () { st.classList.remove('a'); toA(); }, holdA); }, holdB); })();
  }
})();
