import WaveSurfer from './wavesurfer.esm.js';
import RegionsPlugin from './plugins/regions.esm.js';
import TimelinePlugin from './plugins/timeline.esm.js';

let ws = null;
let regions = null;
let region = null;
let inSec = null;
let outSec = null;
let loopEnabled = false;
let currentFileName = null; // 元ファイル名
let fnameUserEdited = false;

const $ = (id) => document.getElementById(id);
const fmt3 = (s) => (s == null ? '' : s.toFixed(3));
const setStatus = (t) => { $('status').textContent = t || ''; };

// ---------- shortcut config ----------
const ACTIONS = [
  { id: 'playPause', label: '再生/停止', def: 'Space' },
  { id: 'setIn',     label: 'IN マーカー', def: 'KeyI' },
  { id: 'setOut',    label: 'OUT マーカー', def: 'KeyO' },
  { id: 'export',    label: '書き出し', def: 'KeyE' },
  { id: 'loop',      label: 'ループ切替', def: 'KeyL' },
  { id: 'seekBack',  label: '5秒戻る', def: 'ArrowLeft' },
  { id: 'seekFwd',   label: '5秒進む', def: 'ArrowRight' },
];
const DEFAULT_KEYS = Object.fromEntries(ACTIONS.map(a => [a.id, a.def]));
const KEYS_LS = 'madcutter_keys_v1';
const OPT_LS  = 'madcutter_opts_v1';
let keys = loadKeys();
let opts = loadOpts();

function loadKeys() {
  try {
    const raw = localStorage.getItem(KEYS_LS);
    if (!raw) return { ...DEFAULT_KEYS };
    return { ...DEFAULT_KEYS, ...JSON.parse(raw) };
  } catch { return { ...DEFAULT_KEYS }; }
}
function saveKeys() { localStorage.setItem(KEYS_LS, JSON.stringify(keys)); }

const DEFAULT_OPTS = { showFnamePreview: false };
function loadOpts() {
  try {
    const raw = localStorage.getItem(OPT_LS);
    if (!raw) return { ...DEFAULT_OPTS };
    return { ...DEFAULT_OPTS, ...JSON.parse(raw) };
  } catch { return { ...DEFAULT_OPTS }; }
}
function saveOpts() { localStorage.setItem(OPT_LS, JSON.stringify(opts)); }

function keyLabel(code) {
  if (!code) return '-';
  return code.replace(/^Key/, '')
    .replace('ArrowLeft','←').replace('ArrowRight','→')
    .replace('ArrowUp','↑').replace('ArrowDown','↓');
}
function refreshHint() {
  $('hint').textContent =
    `${keyLabel(keys.playPause)}=再生/停止 ・ ${keyLabel(keys.setIn)}=IN ・ ${keyLabel(keys.setOut)}=OUT ・ ` +
    `${keyLabel(keys.loop)}=ループ ・ ${keyLabel(keys.export)}=書き出し ・ ` +
    `${keyLabel(keys.seekBack)}/${keyLabel(keys.seekFwd)}=5秒 ・ ホイール=ズーム ・ Shift+Click=IN ・ Alt+Click=OUT`;
}
function applyOptVisibility() {
  $('fnameRow').style.display = opts.showFnamePreview ? '' : 'none';
}

// ---------- fname ----------
function stripExt(n){ return n ? n.replace(/\.[^.]+$/, '') : ''; }
function currentExt() {
  const f = $('fmt').value;
  return f === 'aac' ? 'm4a' : f;
}
function suggestedFname() {
  if (!currentFileName) return '';
  return stripExt(currentFileName) + '_cut.' + currentExt();
}
function refreshFnameSuggestion() {
  if (!fnameUserEdited) $('fnameInput').value = suggestedFname();
}

// ---------- state helpers ----------
function syncInInput()  { $('inInput').value  = fmt3(inSec); }
function syncOutInput() { $('outInput').value = fmt3(outSec); }

// ---------- wavesurfer ----------
function initWs() {
  ws = WaveSurfer.create({
    container: '#wave',
    waveColor: '#4a90e2',
    progressColor: '#9ad',
    cursorColor: '#fff',
    height: 180,
    minPxPerSec: 50,
    plugins: [
      TimelinePlugin.create({
        height: 20,
        timeInterval: 1,
        primaryLabelInterval: 5,
        style: { fontSize: '10px', color: '#aaa' },
      }),
    ],
  });
  regions = ws.registerPlugin(RegionsPlugin.create());

  ws.on('decode', () => { $('durV').textContent = ws.getDuration().toFixed(2) + 's'; });
  ws.on('timeupdate', (t) => {
    $('curV').textContent = t.toFixed(2);
    if (loopEnabled && inSec != null && outSec != null && outSec > inSec) {
      if (t >= outSec - 0.001 || t < inSec - 0.001) {
        ws.setTime(inSec);
      }
    }
  });

  ws.setVolume(parseFloat($('vol').value));
  ws.setPlaybackRate(parseFloat($('speed').value));

  $('wave').addEventListener('wheel', (e) => {
    if (!ws) return;
    e.preventDefault();
    const cur = ws.options.minPxPerSec || 50;
    const next = Math.max(10, Math.min(2000, cur * (e.deltaY < 0 ? 1.25 : 0.8)));
    ws.zoom(next);
  }, { passive: false });

  let lastMod = { shift:false, alt:false };
  $('wave').addEventListener('mousedown', (e) => {
    lastMod.shift = e.shiftKey;
    lastMod.alt = e.altKey;
  }, true);
  ws.on('click', (relX) => {
    const dur = ws.getDuration();
    if (!dur) return;
    const t = Math.max(0, Math.min(dur, relX * dur));
    if (lastMod.shift) {
      inSec = t;
      syncInInput();
      if (outSec != null && outSec > inSec) updateRegion();
      lastMod.shift = false;
    } else if (lastMod.alt) {
      outSec = t;
      syncOutInput();
      if (inSec != null && outSec > inSec) updateRegion();
      lastMod.alt = false;
    }
  });
}

function updateRegion() {
  if (!ws || inSec == null || outSec == null) return;
  if (region) { region.remove(); region = null; }
  if (outSec <= inSec) return;
  region = regions.addRegion({
    start: inSec, end: outSec,
    color: 'rgba(154, 221, 255, 0.25)',
    drag: false, resize: true,
  });
  region.on('update-end', () => {
    inSec = region.start; outSec = region.end;
    syncInInput(); syncOutInput();
  });
}

function setIn() {
  if (!ws) return;
  inSec = ws.getCurrentTime();
  syncInInput();
  if (outSec != null && outSec > inSec) updateRegion();
}
function setOut() {
  if (!ws) return;
  outSec = ws.getCurrentTime();
  syncOutInput();
  if (inSec != null && outSec > inSec) updateRegion();
}
function toggleLoop() {
  loopEnabled = !loopEnabled;
  $('loop').classList.toggle('on', loopEnabled);
  setStatus(loopEnabled ? 'ループ ON' : 'ループ OFF');
}
function doExport() {
  if (inSec == null || outSec == null || outSec <= inSec) {
    setStatus('IN/OUT を設定してください');
    return;
  }
  const format = $('fmt').value;
  const sel = $('br').value || 'none:0';
  const [mode, vStr] = sel.split(':');
  const value = parseInt(vStr, 10);
  const fname = opts.showFnamePreview ? ($('fnameInput').value || '').trim() : '';
  window.chrome.webview.postMessage({ type: 'export', in: inSec, out: outSec, format, mode, value, fname });
}

// ---------- 形式/品質 ----------
const QUALITY_OPTS = {
  mp3: [
    ['cbr:320','CBR 320 kbps'],
    ['cbr:256','CBR 256 kbps'],
    ['cbr:192','CBR 192 kbps'],
    ['cbr:128','CBR 128 kbps'],
    ['cbr:96','CBR 96 kbps'],
    ['vbr:0','VBR V0 (約245k)'],
    ['vbr:2','VBR V2 (約190k)'],
    ['vbr:4','VBR V4 (約165k)'],
  ],
  aac: [
    ['cbr:320','CBR 320 kbps'],
    ['cbr:256','CBR 256 kbps'],
    ['cbr:192','CBR 192 kbps'],
    ['cbr:128','CBR 128 kbps'],
    ['cbr:96','CBR 96 kbps'],
  ],
  ogg: [
    ['vbr:9','VBR q9 (約320k)'],
    ['vbr:7','VBR q7 (約224k)'],
    ['vbr:5','VBR q5 (約160k)'],
    ['vbr:3','VBR q3 (約112k)'],
    ['vbr:1','VBR q1 (約64k)'],
  ],
  wav: [
    ['none:0','16bit 44.1kHz (無劣化)'],
  ],
};
const DEFAULT_QUALITY = { mp3:'cbr:192', aac:'cbr:192', ogg:'vbr:5', wav:'none:0' };

function rebuildQuality() {
  const f = $('fmt').value;
  const list = QUALITY_OPTS[f] || [];
  const sel = $('br');
  sel.innerHTML = '';
  for (const [v, label] of list) {
    const o = document.createElement('option');
    o.value = v; o.textContent = label;
    sel.appendChild(o);
  }
  sel.value = DEFAULT_QUALITY[f] || (list[0] && list[0][0]) || '';
  sel.disabled = f === 'wav';
}
$('fmt').addEventListener('change', () => {
  rebuildQuality();
  refreshFnameSuggestion();
});

// ---------- IN/OUT 数値入力 ----------
function clampTime(v) {
  if (!ws) return v;
  const d = ws.getDuration();
  if (!d) return Math.max(0, v);
  return Math.max(0, Math.min(d, v));
}
$('inInput').addEventListener('change', () => {
  const v = parseFloat($('inInput').value);
  if (Number.isNaN(v)) { inSec = null; if (region) { region.remove(); region = null; } return; }
  inSec = clampTime(v);
  syncInInput();
  updateRegion();
});
$('outInput').addEventListener('change', () => {
  const v = parseFloat($('outInput').value);
  if (Number.isNaN(v)) { outSec = null; if (region) { region.remove(); region = null; } return; }
  outSec = clampTime(v);
  syncOutInput();
  updateRegion();
});

// ---------- fname 入力 ----------
$('fnameInput').addEventListener('input', () => { fnameUserEdited = true; });
$('fnameInput').addEventListener('dblclick', () => { fnameUserEdited = false; refreshFnameSuggestion(); });

// ---------- 音量/速度 ----------
$('vol').addEventListener('input', () => {
  const v = parseFloat($('vol').value);
  $('volV').textContent = Math.round(v * 100) + '%';
  if (ws) ws.setVolume(v);
});
$('speed').addEventListener('change', () => {
  if (ws) ws.setPlaybackRate(parseFloat($('speed').value));
});

// ---------- keybinding ----------
let listeningFor = null;

function handleAction(id) {
  if (!ws) return;
  switch (id) {
    case 'playPause': ws.playPause(); break;
    case 'setIn':     setIn(); break;
    case 'setOut':    setOut(); break;
    case 'export':    doExport(); break;
    case 'loop':      toggleLoop(); break;
    case 'seekBack':  ws.setTime(Math.max(0, ws.getCurrentTime() - 5)); break;
    case 'seekFwd':   ws.setTime(Math.min(ws.getDuration(), ws.getCurrentTime() + 5)); break;
  }
}

function isTypingTarget(el) {
  if (!el) return false;
  const tag = (el.tagName || '').toLowerCase();
  return tag === 'input' || tag === 'select' || tag === 'textarea';
}

document.addEventListener('keydown', (e) => {
  if (listeningFor) {
    e.preventDefault();
    if (e.code === 'Escape') { stopListening(); return; }
    for (const [k, v] of Object.entries(keys)) {
      if (k !== listeningFor && v === e.code) keys[k] = '';
    }
    keys[listeningFor] = e.code;
    saveKeys();
    stopListening();
    renderKbTable();
    refreshHint();
    return;
  }
  if ($('modalBg').classList.contains('show')) return;
  if (isTypingTarget(e.target)) return;
  if (e.code === 'Space') e.preventDefault();
  for (const a of ACTIONS) {
    if (keys[a.id] && keys[a.id] === e.code) { handleAction(a.id); return; }
  }
});

function startListening(id, btn) {
  stopListening();
  listeningFor = id;
  btn.classList.add('listening');
  btn.textContent = '押して...';
}
function stopListening() {
  listeningFor = null;
  renderKbTable();
}

function renderKbTable() {
  const tbl = $('kbtbl');
  tbl.innerHTML = '';
  for (const a of ACTIONS) {
    const tr = document.createElement('tr');
    const td1 = document.createElement('td'); td1.textContent = a.label;
    const td2 = document.createElement('td');
    const btn = document.createElement('button');
    btn.className = 'keybtn';
    btn.textContent = keys[a.id] ? keyLabel(keys[a.id]) : '(未割当)';
    btn.onclick = () => startListening(a.id, btn);
    td2.appendChild(btn);
    tr.appendChild(td1); tr.appendChild(td2);
    tbl.appendChild(tr);
  }
}

$('settings').onclick = () => {
  renderKbTable();
  $('optFname').checked = !!opts.showFnamePreview;
  $('modalBg').classList.add('show');
};
$('optFname').addEventListener('change', () => {
  opts.showFnamePreview = $('optFname').checked;
  saveOpts();
  applyOptVisibility();
  refreshFnameSuggestion();
});
$('kbClose').onclick = () => { stopListening(); $('modalBg').classList.remove('show'); };
$('kbReset').onclick = () => { keys = { ...DEFAULT_KEYS }; saveKeys(); renderKbTable(); refreshHint(); };
$('modalBg').addEventListener('click', (e) => { if (e.target.id === 'modalBg') { stopListening(); $('modalBg').classList.remove('show'); } });

// ---------- 履歴 ----------
let recentList = [];
function renderRecent() {
  const p = $('recentPanel');
  p.innerHTML = '';
  if (recentList.length === 0) {
    const d = document.createElement('div');
    d.className = 'recentItem';
    d.style.opacity = '.5';
    d.textContent = '(履歴なし)';
    p.appendChild(d);
  } else {
    for (const path of recentList) {
      const d = document.createElement('div');
      d.className = 'recentItem';
      d.title = path;
      const parts = path.split(/[\\/]/);
      const base = parts[parts.length - 1];
      const dir = parts.slice(0, -1).join('\\');
      d.innerHTML = `<b>${base}</b> <span style="opacity:.5;font-size:11px;">${dir}</span>`;
      d.onclick = () => {
        $('recentPanel').classList.remove('show');
        window.chrome.webview.postMessage({ type: 'openPath', path });
      };
      p.appendChild(d);
    }
  }
  const foot = document.createElement('div');
  foot.className = 'recentFoot';
  const b = document.createElement('button');
  b.textContent = '履歴クリア';
  b.onclick = () => window.chrome.webview.postMessage({ type: 'clearRecent' });
  foot.appendChild(b);
  p.appendChild(foot);
}
$('recent').onclick = (e) => {
  e.stopPropagation();
  renderRecent();
  $('recentPanel').classList.toggle('show');
};
document.addEventListener('click', (e) => {
  if (!$('recentPanel').contains(e.target) && e.target.id !== 'recent') {
    $('recentPanel').classList.remove('show');
  }
});

// ---------- buttons ----------
$('open').onclick = () => window.chrome.webview.postMessage({ type: 'pickFile' });
$('play').onclick = () => ws && ws.playPause();
$('setIn').onclick = setIn;
$('setOut').onclick = setOut;
$('loop').onclick = toggleLoop;
$('export').onclick = doExport;
$('clearCache').onclick = () => window.chrome.webview.postMessage({ type: 'clearCache' });

// ---------- host msgs ----------
window.chrome.webview.addEventListener('message', (ev) => {
  const d = ev.data;
  if (!d || !d.type) return;
  if (d.type === 'load') {
    inSec = outSec = null;
    if (region) { region.remove(); region = null; }
    $('inInput').value = ''; $('outInput').value = '';
    $('name').textContent = d.name;
    currentFileName = d.name;
    fnameUserEdited = false;
    refreshFnameSuggestion();
    if (!ws) initWs();
    ws.load(d.url);
    setStatus('読み込み完了');
  } else if (d.type === 'status') {
    setStatus(d.text);
  } else if (d.type === 'recent') {
    recentList = d.list || [];
  }
});

applyOptVisibility();
rebuildQuality();
initWs();
refreshHint();
refreshFnameSuggestion();
