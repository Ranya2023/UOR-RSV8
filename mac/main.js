// UOR-RC for Mac — the PC side of the UOR-RC phone remote.
// Same phone app, same protocol as the Windows version; Wi-Fi only on the Mac.
const { app, BrowserWindow, Tray, Menu, screen, ipcMain, dialog, nativeImage, desktopCapturer, shell } = require('electron');
const path = require('path'), fs = require('fs'), os = require('os');
const { Settings } = require('./lib/settings');
const { WifiLink, localAddresses } = require('./lib/wifilink');
const { Files } = require('./lib/files');
const { Quiz } = require('./lib/quiz');
const { Present, osa } = require('./lib/present');
const { Input } = require('./lib/input');
const { Annotations, NUMBER_COLORS, newId } = require('./lib/annotations');

const TEST = process.env.UORRC_TEST === '1';          // automated tests: fake presentation, no AppleScript
const MEDIA_DIR = path.join(os.tmpdir(), 'UOR-RC-media');
let settings, wifi, files, quiz, present, input, ann;
let controlWin, overlayWin, viewWin, tray;
const logLines = [];

function log(m) {
  const line = `[${new Date().toTimeString().slice(0, 8)}] ${m}`;
  logLines.push(line); if (logLines.length > 300) logLines.shift();
  if (TEST) console.log(line);
  controlWin && !controlWin.isDestroyed() && controlWin.webContents.send('log', line);
}
const send = o => wifi && wifi.send(o);
const ov = patch => overlayWin && !overlayWin.isDestroyed() && overlayWin.webContents.send('ov', patch);
const vw = msg => viewWin && !viewWin.isDestroyed() && viewWin.webContents.send('view', msg);

// ── which screen shows the presentation (the projector if there is one) ──
function showDisplay() {
  const all = screen.getAllDisplays(), prim = screen.getPrimaryDisplay();
  const chosen = all.find(d => String(d.id) === String(settings.display || ''));
  return chosen || all.find(d => d.id !== prim.id) || prim;
}

// ═════════════════════════ windows ═════════════════════════
function makeOverlay() {
  const d = showDisplay();
  overlayWin = new BrowserWindow({
    ...d.bounds, frame: false, transparent: true, hasShadow: false, focusable: false, resizable: false,
    skipTaskbar: true, show: false, alwaysOnTop: true, backgroundColor: '#00000000', enableLargerThanScreen: true,
    webPreferences: { nodeIntegration: true, contextIsolation: false, backgroundThrottling: false }
  });
  overlayWin.setAlwaysOnTop(true, 'screen-saver', 2);
  overlayWin.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true });
  overlayWin.setIgnoreMouseEvents(true);
  overlayWin.loadFile(path.join(__dirname, 'renderer', 'overlay.html'));
  overlayWin.webContents.once('did-finish-load', () => { overlayWin.showInactive(); pushStage(); });
}
function makeView() {
  const d = showDisplay();
  viewWin = new BrowserWindow({
    ...d.bounds, frame: false, show: false, backgroundColor: '#000000', skipTaskbar: true, resizable: false,
    alwaysOnTop: true, enableLargerThanScreen: true, hasShadow: false,
    webPreferences: { nodeIntegration: true, contextIsolation: false, backgroundThrottling: false, webSecurity: false }
  });
  viewWin.setAlwaysOnTop(true, 'screen-saver', 1);
  viewWin.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true });
  viewWin.loadFile(path.join(__dirname, 'renderer', 'view.html'));
}
function makeControl() {
  controlWin = new BrowserWindow({
    width: 560, height: 700, minWidth: 480, minHeight: 520, title: 'UOR-RC', backgroundColor: '#0F1117',
    show: !TEST || process.env.UORRC_SHOW === '1',
    webPreferences: { nodeIntegration: true, contextIsolation: false }
  });
  controlWin.loadFile(path.join(__dirname, 'renderer', 'control.html'));
  controlWin.on('close', e => { if (!app.isQuitting) { e.preventDefault(); controlWin.hide(); } });
  controlWin.webContents.on('did-finish-load', () => updateControl());
}
function updateControl(extra = {}) {
  if (!controlWin || controlWin.isDestroyed()) return;
  controlWin.webContents.send('status', {
    pin: settings.pin, wifi: wifi && wifi.connected ? wifi.peer : '', ips: localAddresses().map(a => a.address),
    ppt: pptStatus, log: logLines, displays: screen.getAllDisplays().map((d, i) => ({ id: d.id, label: `Display ${i + 1} — ${d.size.width}×${d.size.height}${d.id === screen.getPrimaryDisplay().id ? ' (main)' : ''}` })),
    display: showDisplay().id, ...extra
  });
}

// ═════════════════════════ views (photos, phone screen, whiteboard, PDF, quiz, picker) ═════════════════════════
let viewMode = '';                // '' | media | phone | board | doc | quiz | picker
const V = { media: { index: -1, count: 0, name: '' }, board: { page: 1, pages: 1, bg: 'white' }, doc: { page: 0, pages: 0, name: '' } };
function showView(mode) {
  if (viewMode !== mode) {
    if (viewMode === 'media') vw({ t: 'media', a: 'pause' });
    viewMode = mode;
    vw({ t: 'mode', mode });
  }
  const d = showDisplay();
  viewWin.setBounds(d.bounds);
  if (!viewWin.isVisible()) viewWin.showInactive();
  overlayWin.moveTop();
  lastKey = ''; pushStage(); poll();
}
function hideViews(except = '') {
  if (viewMode && viewMode !== except) {
    if (viewMode === 'media') { vw({ t: 'media', a: 'pause' }); send({ e: 'media', open: false, count: V.media.count }); }
    viewMode = ''; vw({ t: 'mode', mode: '' });
    viewWin.hide();
    suppressPhoneUntil = Date.now() + 1500;
    lastKey = ''; lastThumbSlide = -1; pushStage();
  }
}
let suppressPhoneUntil = 0;

// ═════════════════════════ presentation state → phone ═════════════════════════
let pst = { app: '', open: false, showing: false, slide: 0, total: 0, name: '', file: '', sw: 16, sh: 9 };
let pptStatus = 'PowerPoint / Keynote: checking…';
let lastKey = '', lastThumbSlide = -1, presKey = '', slideKey = '', notesCache = new Map(), titlesSent = false, screenMode = 'normal';
let polling = false;

async function readPresentation() {
  if (TEST) return global.fakeShow || pst;
  return present.state();
}

async function poll() {
  if (polling) return; polling = true;
  try {
    const st = await readPresentation();
    pst = st;
    pptStatus = !st.app ? 'PowerPoint / Keynote: not running' : !st.open ? `${st.app === 'key' ? 'Keynote' : 'PowerPoint'}: no presentation open`
      : st.showing ? `Presenting “${st.name}” — slide ${Math.min(st.slide, st.total)} of ${st.total}` : `Open: “${st.name}” (not presenting)`;
    const pk = (st.file || st.name) + '|' + st.total;
    if (pk !== presKey) { presKey = pk; notesCache.clear(); titlesSent = false; lastThumbSlide = -1; ann.open(st.file || st.name || '__desktop__'); slideKey = ''; }
    let key2 = viewMode ? viewKey() : st.showing ? 'slide' + st.slide : 'screen';
    if (key2 !== slideKey) { slideKey = key2; ov({ anns: ann.for(slideKey) }); send({ e: 'ann', items: ann.for(slideKey) }); }
    const d = showDisplay().bounds;
    const sw = viewMode ? d.width : st.sw, sh = viewMode ? d.height : st.sh;
    const key = JSON.stringify([st.app, st.showing, pk, st.slide, screenMode, viewMode, V.media.index, V.board, V.doc.page, V.doc.pages]);
    if (key !== lastKey) {
      lastKey = key;
      let notes = '';
      if (st.open && st.slide >= 1 && st.slide <= st.total) {
        if (!notesCache.has(st.slide)) notesCache.set(st.slide, TEST ? '' : await present.notes(st.slide));
        notes = notesCache.get(st.slide);
      }
      send({
        e: 'state', ppt: !!st.app, show: st.showing, name: st.name, slide: st.slide, total: st.total, click: -1, clicks: -1,
        screen: screenMode, notes, sw, sh, mi: viewMode === 'media' ? V.media.index : -1, phone: viewMode === 'phone',
        view: viewMode, page: viewMode === 'board' ? V.board.page : viewMode === 'doc' ? V.doc.page : 0,
        pages: viewMode === 'board' ? V.board.pages : viewMode === 'doc' ? V.doc.pages : 0,
        bg: viewMode === 'board' ? V.board.bg : '', doc: viewMode === 'doc' ? V.doc.name : ''
      });
      pushStage();
      updateControl();
    }
    if (st.open && !titlesSent && !TEST) { titlesSent = true; send({ e: 'slides', titles: await present.titles(st.total) }); }
    if (!viewMode && st.open && st.slide !== lastThumbSlide) { lastThumbSlide = st.slide; sendSlideThumbs(st.slide, st.total); }
  } catch (e) { log('poll: ' + e.message); }
  finally { polling = false; }
}

async function sendSlideThumbs(slide, total) {
  if (TEST) return;
  const imgs = await present.slideImages(presKey);
  const t = (i, w) => { try { return nativeImage.createFromPath(imgs[i - 1]).resize({ width: w }).toJPEG(70).toString('base64'); } catch { return ''; } };
  if (slide >= 1 && slide <= imgs.length) send({ e: 'thumb', which: 'cur', slide, img: t(slide, 960) });
  if (slide + 1 <= imgs.length) send({ e: 'thumb', which: 'next', slide: slide + 1, img: t(slide + 1, 400) });
}

function viewKey() {
  return viewMode === 'media' ? 'media:' + V.media.name : viewMode === 'board' ? 'board:' + V.board.page
       : viewMode === 'doc' ? `doc:${V.doc.name}:${V.doc.page}` : viewMode;
}

/** Where the slide sits on the screen (letter-boxed), and the zoom — for the drawing layer. */
function pushStage() {
  const d = showDisplay().bounds;
  const aspect = viewMode ? d.width / d.height : (pst.sw > 0 && pst.sh > 0 ? pst.sw / pst.sh : 16 / 9);
  ov({ aspect, hidden: screenMode !== 'normal' && !viewMode });
}

// ═════════════════════════ zoom maths (same as the phone and Windows) ═════════════════════════
let zoom = { s: 1, x: 0, y: 0 };
function screenToSlide(sx, sy) {
  const tx = zoom.x / 100, ty = zoom.y / 100;
  return [0.5 + (sx - 0.5) / zoom.s - tx, 0.5 + (sy - 0.5) / zoom.s - ty];
}

// ═════════════════════════ phone commands ═════════════════════════
let tool = 'mouse', stroke = null, erased = false, mirrorOn = false, mirrorW = 800, mirrorWaiting = false, mirrorRect = null;

async function handle(line) {
  let m; try { m = JSON.parse(line); } catch { return; }
  const c = m.c, a = m.a;
  switch (c) {
    case 'hello': lastKey = ''; titlesSent = false; lastThumbSlide = -1; slideKey = ''; return poll();
    case 'ping': return send({ e: 'pong' });

    // ── mouse & keyboard ──
    case 'rel': return input.rel(+m.dx || 0, +m.dy || 0);
    case 'btn': return input.button(m.b || 'left', m.a || 'click');
    case 'scroll': return input.scroll(-(m.d | 0));
    case 'key': return input.key(m.k || '', { ctrl: !!m.ctrl, shift: !!m.shift, alt: !!m.alt, cmd: !!m.win }, Math.max(1, m.n | 0));
    case 'type': return input.type(String(m.text || ''));
    case 'abs': { const s = stageRect(); return input.move(s.x + clamp(m.x) * s.w, s.y + clamp(m.y) * s.h); }
    case 'sabs': { const r = mirrorRect || showDisplay().bounds; return input.move(r.x + clamp(m.x) * r.width, r.y + clamp(m.y) * r.height); }

    // ── drawing layer tools ──
    case 'laser': return ov({ laser: { x: m.x, y: m.y, active: !!m.active, ...(m.size ? { size: m.size } : {}), ...(m.color ? { color: m.color } : {}), ...('label' in m ? { label: m.label } : {}) } });
    case 'laser_style': return ov({ laser: { ...('size' in m ? { size: m.size } : {}), ...('color' in m ? { color: m.color } : {}), ...('label' in m ? { label: m.label } : {}) } });
    case 'spot': return ov({ spot: { x: m.x, y: m.y, active: !!m.active, ...styleOf(m) } });
    case 'spot_style': return ov({ spot: styleOf(m) });
    case 'lens': capture(!!m.active || zoom.s > 1); return ov({ lens: { x: m.x, y: m.y, active: !!m.active, ...lensOf(m) } });
    case 'lens_style': return ov({ lens: lensOf(m) });
    case 'zoom': zoom = { s: Math.min(4, Math.max(1, +m.s || 1)), x: +m.x || 0, y: +m.y || 0 }; capture(zoom.s > 1.001 || Math.abs(zoom.x) + Math.abs(zoom.y) > 0.01); return ov({ zoom });
    case 'tool': tool = m.t || 'mouse'; return ov({ laser: { active: false }, spot: { active: false }, lens: { active: false }, preview: null });
    case 'color': return;
    case 'timer': return ov({ timer: { visible: !!m.visible, mode: m.mode === 'up' ? 'up' : 'down', sec: m.sec | 0 } });
    case 'timer_alert': return ov({ alert: { label: String(m.label), ms: m.label === '0' ? 1600 : 900 } });

    // ── numbers / text / ink ──
    case 'ann_preview': {
      const [sx, sy] = screenToSlide(m.x, m.y), list = ann.for(slideKey), count = list.filter(x => x.kind === 'number').length;
      return ov({ preview: { kind: m.kind === 'text' ? 'text' : 'number', x: sx, y: sy, color: m.kind === 'text' ? m.color : NUMBER_COLORS[count % 10], text: String(count + 1) } });
    }
    case 'ann_preview_end': return ov({ preview: null });
    case 'ann_tap': return annTap(m);
    case 'text_save': return textSave(m);
    case 'ann_delete': { const l = ann.for(slideKey); const i = l.findIndex(x => x.id === m.id); if (i >= 0) l.splice(i, 1); return annsChanged(); }
    case 'ink_start': {
      const [sx, sy] = screenToSlide(m.x, m.y);
      stroke = { id: newId(), kind: 'ink', x: sx, y: sy, color: m.color || '#ff3b30', size: Math.max(1, Math.min(80, m.size | 0)), hl: !!m.hl, pts: [sx, sy] };
      ann.for(slideKey).push(stroke); return ov({ anns: ann.for(slideKey) });
    }
    case 'ink_pts':
      if (stroke && Array.isArray(m.p)) { for (let i = 0; i + 1 < m.p.length; i += 2) { const [sx, sy] = screenToSlide(m.p[i], m.p[i + 1]); stroke.pts.push(+sx.toFixed(5), +sy.toFixed(5)); } ov({ anns: ann.for(slideKey) }); }
      return;
    case 'ink_end': stroke = null; return annsChanged();
    case 'ink_erase': {
      const [sx, sy] = screenToSlide(m.x, m.y), l = ann.for(slideKey), before = l.length;
      const r = 18 / 1080 / zoom.s;
      for (let i = l.length - 1; i >= 0; i--) {
        const s = l[i]; if (s.kind !== 'ink') continue;
        for (let k = 0; k + 1 < s.pts.length; k += 2) if (Math.hypot(s.pts[k] - sx, (s.pts[k + 1] - sy) * 9 / 16) < r + s.size / 1080 / 2) { l.splice(i, 1); break; }
      }
      if (l.length !== before) { erased = true; ov({ anns: l }); }
      return;
    }
    case 'ink_erase_end': if (erased) { erased = false; annsChanged(); } return;
    case 'clear': ann.for(slideKey).length = 0; return annsChanged();
    case 'erase': return;

    // ── the show ──
    case 'next': case 'prev': return nav(c);
    case 'goto': if (viewMode === 'doc') return vw({ t: 'doc', a: 'goto', n: m.n }); await present.goto(m.n | 0); return poll();
    case 'start': hideViews(); if (!(await present.start(m.from === 'current'))) input.key('f5'); return setTimeout(poll, 800);
    case 'end': if (!(await present.end())) input.key('esc'); return setTimeout(poll, 500);
    case 'screen': screenMode = screenMode === m.m ? 'normal' : (m.m === 'white' ? 'white' : 'black'); await present.front(); input.key(m.m === 'white' ? 'w' : 'b'); lastKey = ''; return poll();
    case 'back_show': return backToShow();
    case 'desktop': hideViews(); await osa('tell application "System Events" to set visible of (every process whose visible is true and name is not "Finder") to false'); return;

    // ── views ──
    case 'phone_frame':
      if (Date.now() < suppressPhoneUntil) return send({ e: 'phone_ack' });
      if (viewMode !== 'phone') { hideViews('phone'); showView('phone'); }
      vw({ t: 'phone', img: m.img });
      return send({ e: 'phone_ack' });
    case 'phone_view': return vw({ t: 'phone_view', rot: ((m.rot | 0) % 360 + 360) % 360, fill: !!m.fill });
    case 'phone_stop': if (viewMode === 'phone') hideViews(); ov({ audioStop: true }); return poll();
    case 'audio': return ov({ audio: { d: m.d, r: m.r | 0, ch: m.ch | 0 } });
    case 'audio_stop': return ov({ audioStop: true });
    case 'media':
      if (a === 'show') { if (V.media.count > 0) { hideViews('media'); showView('media'); vw({ t: 'media', a: 'resume' }); } return; }
      if (a === 'hide') { if (viewMode === 'media') hideViews(); return; }
      if (a === 'close') { vw({ t: 'media', a: 'close' }); V.media = { index: -1, count: 0, name: '' }; hideViews(); send({ e: 'media', open: false, count: 0 }); return poll(); }
      return vw({ t: 'media', a, v: m.v });
    case 'board': return board(a, m.v);
    case 'doc':
      if (a === 'close') { hideViews(); return poll(); }
      return vw({ t: 'doc', a, n: m.n });
    case 'quiz': return quizCmd(m);
    case 'picker':
      if (a === 'close') { if (viewMode === 'picker') hideViews(); return poll(); }
      if (!Array.isArray(m.names) || !m.names.length) return;
      hideViews('picker'); showView('picker');
      return vw({ t: 'picker', names: m.names, winner: m.winner | 0, title: m.title || '', remaining: m.remaining ?? -1, style: m.style === 'wheel' ? 'wheel' : 'names' });
    case 'view_close': hideViews(); return poll();

    // ── files & mirror ──
    case 'fs': return files.handle(m);
    case 'mirror': mirrorOn = !!m.on; mirrorW = Math.max(320, Math.min(1280, m.w | 0 || 800)); if (mirrorOn) mirrorFrame(); return;
    case 'frame_ack': mirrorWaiting = false; if (mirrorOn) setTimeout(mirrorFrame, mirrorW >= 800 ? 60 : 250); return;
    case 'join_wifi': return joinWifi(m.ssid, m.pass);
  }
}
const clamp = v => Math.max(0, Math.min(1, +v || 0));
const styleOf = m => ({ ...('radius' in m ? { radius: m.radius } : {}), ...('style' in m ? { style: m.style } : {}), ...('confetti' in m ? { confetti: m.confetti } : {}), ...('color' in m ? { color: m.color || null } : {}) });
const lensOf = m => ({ ...('radius' in m ? { radius: m.radius } : {}), ...('zoom' in m ? { zoom: m.zoom } : {}), ...('bright' in m ? { bright: m.bright } : {}), ...('dim' in m ? { dim: m.dim } : {}) });
function stageRect() {
  const d = showDisplay().bounds, aspect = viewMode ? d.width / d.height : pst.sw / pst.sh || 16 / 9;
  let w = d.width, h = w / aspect; if (h > d.height) { h = d.height; w = h * aspect; }
  return { x: d.x + (d.width - w) / 2, y: d.y + (d.height - h) / 2, w, h };
}

async function nav(c) {
  if (viewMode === 'media') return vw({ t: 'media', a: c });
  if (viewMode === 'board') return board(c);
  if (viewMode === 'doc') return vw({ t: 'doc', a: c });
  const ok = c === 'next' ? await present.next() : await present.prev();
  if (!ok) input.key(c === 'next' ? 'pagedown' : 'pageup');     // PDF, browser, any app
  setTimeout(poll, 150);
}

async function backToShow() {
  hideViews();
  suppressPhoneUntil = Date.now() + 2000;
  const st = await readPresentation();
  if (st.showing) await present.front();
  else if (st.open) await present.start(true);
  else log('Back to PowerPoint: no presentation is open.');
  lastKey = ''; lastThumbSlide = -1; poll();
}

// ── numbers & text ──
async function annTap(m) {
  ov({ preview: null });
  const kind = m.kind === 'text' ? 'text' : 'number', list = ann.for(slideKey);
  const hit = await overlayWin.webContents.executeJavaScript(`hitTest(${+m.x}, ${+m.y}, ${JSON.stringify(kind)})`).catch(() => null);
  const [sx, sy] = screenToSlide(m.x, m.y);
  if (kind === 'number') {
    if (hit) { const i = list.findIndex(x => x.id === hit); if (i >= 0) list.splice(i, 1); }
    else { const count = list.filter(x => x.kind === 'number').length; list.push({ id: newId(), kind: 'number', x: sx, y: sy, color: NUMBER_COLORS[count % 10], text: String(count + 1), size: Math.max(16, Math.min(64, m.size | 0 || 28)) }); }
    return annsChanged();
  }
  const ex = hit && list.find(x => x.id === hit);
  if (ex) send({ e: 'edit_text', id: ex.id, text: ex.text }); else send({ e: 'new_text', sx, sy });
}
function textSave(m) {
  const list = ann.for(slideKey), text = String(m.text || '').trim();
  const a = m.id ? list.find(x => x.id === m.id) : null;
  if (a) { if (!text) list.splice(list.indexOf(a), 1); else a.text = text; }
  else if (text) list.push({ id: newId(), kind: 'text', x: +m.sx, y: +m.sy, text, style: ['plain', 'box'].includes(m.style) ? m.style : 'pin', color: m.color || '#eab308', textColor: m.textColor || '#111827', fontSize: Math.max(10, Math.min(32, m.fontSize | 0 || 14)) });
  annsChanged();
}
function annsChanged() { ann.save(); ov({ anns: ann.for(slideKey) }); send({ e: 'ann', items: ann.for(slideKey) }); }

// ── 🧑‍🏫 whiteboard ──
function board(a, v) {
  const B = V.board;
  if (a === 'open') {
    let pages = 1;
    for (const k of ann.keys()) { const n = /^board:(\d+)$/.exec(k); if (n && ann.for(k).length) pages = Math.max(pages, +n[1]); }
    B.pages = Math.max(B.pages, pages); if (v) B.bg = v;
    hideViews('board'); showView('board');
  }
  else if (a === 'next') { if (B.page === B.pages) B.pages++; B.page++; }
  else if (a === 'prev') { if (B.page > 1) B.page--; }
  else if (a === 'bg') B.bg = ['grid', 'black', 'green'].includes(v) ? v : 'white';
  else if (a === 'close') { hideViews(); return poll(); }
  else if (a === 'save') return saveBoard();
  vw({ t: 'board', ...B });
  lastKey = ''; poll();
}
async function saveBoard() {
  const d = showDisplay().bounds, dir = path.join(Files.folder(), 'Whiteboard ' + new Date().toISOString().slice(0, 16).replace('T', ' ').replace(':', '-'));
  fs.mkdirSync(dir, { recursive: true });
  for (let p = 1; p <= V.board.pages; p++) {
    const data = await overlayWin.webContents.executeJavaScript(`renderPage(${JSON.stringify(ann.for('board:' + p))}, ${JSON.stringify(V.board.bg)}, ${d.width}, ${d.height})`);
    fs.writeFileSync(path.join(dir, `page ${p}.png`), Buffer.from(String(data).split(',')[1], 'base64'));
  }
  log(`Whiteboard saved (${V.board.pages} pages): ${dir}`);
  send({ e: 'saved', path: dir, pages: V.board.pages });
}

// ── 🗳️ quiz ──
async function quizCmd(m) {
  switch (m.a) {
    case 'start': {
      if (!(await quiz.start())) { log('The quiz could not start (ports 8088-8095 are busy).'); return; }
      const opts = Array.isArray(m.opts) ? m.opts.map(String) : [];
      while (opts.length < Math.max(2, m.n | 0)) opts.push('');
      quiz.newQuestion(m.q || '', opts, typeof m.correct === 'number' ? m.correct : -1, m.qi | 0, m.qn | 0);
      hideViews('quiz'); showView('quiz');
      vw({ t: 'quiz_setup', url: quiz.bestUrl(), scores: false });
      log('Quiz open at ' + quiz.bestUrl());
      break;
    }
    case 'reveal': quiz.showResults(); break;
    case 'correct': quiz.setCorrect(m.v | 0); break;
    case 'scores': hideViews('quiz'); showView('quiz'); vw({ t: 'quiz_scores' }); break;
    case 'reset': quiz.resetScores(); break;
    case 'show': hideViews('quiz'); showView('quiz'); break;
    case 'close': quiz.close(); hideViews(); break;
  }
  quizUpdated(); poll();
}
function quizUpdated() {
  const counts = quiz.counts(), top = quiz.top(8).map(p => ({ name: p.name, score: p.score }));
  vw({ t: 'quiz', q: quiz.question, opts: quiz.options, counts, reveal: quiz.reveal, correct: quiz.correct, open: quiz.open, qi: quiz.qi, qn: quiz.qn, players: [...quiz.players.values()].filter(p => p.name).length, top });
  send({ e: 'quiz', open: quiz.open, reveal: quiz.reveal, correct: quiz.correct, n: quiz.optionCount, counts, total: quiz.answers.size, players: [...quiz.players.values()].filter(p => p.name).length, showing: viewMode === 'quiz', url: quiz.bestUrl(), qi: quiz.qi, qn: quiz.qn, top });
}

// ── 📑 documents & 🖼 photos/videos ──
async function openDocument(file) {
  const ext = path.extname(file).toLowerCase(), name = path.basename(file);
  try {
    if (['.pptx', '.ppt', '.ppsx', '.pps', '.key', '.odp'].includes(ext)) { hideViews(); await present.openAndPlay(file); log('Presenting ' + name); return setTimeout(poll, 1500); }
    if (['.jpg', '.jpeg', '.png', '.gif', '.webp', '.heic', '.mp4', '.mov', '.m4v', '.webm'].includes(ext)) {
      fs.mkdirSync(MEDIA_DIR, { recursive: true });
      const copy = path.join(MEDIA_DIR, name); if (path.resolve(copy) !== path.resolve(file)) fs.copyFileSync(file, copy);
      return addMedia(copy, 0);
    }
    const pdf = ext === '.pdf' ? file : ['.docx', '.doc', '.rtf', '.odt', '.txt'].includes(ext) ? await present.wordToPdf(file) : null;
    if (!pdf) { log(`Opening ${name} with its normal Mac app (NEXT / ◀ send Page Down / Page Up).`); return shell.openPath(file); }
    V.doc = { page: 0, pages: 0, name };
    hideViews('doc'); showView('doc');
    vw({ t: 'doc', a: 'load', file: pdf, name });
    log('Presenting ' + name);
  } catch (e) { log(`Could not open ${name}: ${e.message}`); }
}
function addMedia(file, index) {
  if (index === 0 && viewMode !== 'media') vw({ t: 'media', a: 'reset' });
  hideViews('media'); showView('media');
  vw({ t: 'media', a: 'add', file });
}

// ── See the screen (PC screen on the phone) ──
async function mirrorFrame() {
  if (!mirrorOn || mirrorWaiting) return;
  try {
    const d = showDisplay(); mirrorRect = d.bounds;
    const w = Math.min(mirrorW, d.size.width), h = Math.round(w * d.size.height / d.size.width);
    const srcs = await desktopCapturer.getSources({ types: ['screen'], thumbnailSize: { width: w, height: h } });
    const s = srcs.find(x => String(x.display_id) === String(d.id)) || srcs[0];
    if (!s) return;
    mirrorWaiting = true;
    send({ e: 'frame', img: s.thumbnail.toJPEG(mirrorW >= 800 ? 60 : 45).toString('base64'), l: d.bounds.x, t: d.bounds.y, w: d.bounds.width, h: d.bounds.height });
    setTimeout(() => { if (mirrorWaiting) { mirrorWaiting = false; mirrorFrame(); } }, 2500);
  } catch (e) { log('Screen picture: ' + e.message + ' (allow UOR-RC in System Settings → Privacy → Screen Recording)'); mirrorOn = false; }
}

// Live zoom / lens need a picture of the screen underneath; the drawing layer hides itself from it.
let capturing = false;
async function capture(on) {
  if (on === capturing) return;
  capturing = on;
  overlayWin.setContentProtection(on);
  if (!on) return ov({ captureSource: null });
  try {
    const d = showDisplay();
    const srcs = await desktopCapturer.getSources({ types: ['screen'], thumbnailSize: { width: 1, height: 1 } });
    const s = srcs.find(x => String(x.display_id) === String(d.id)) || srcs[0];
    ov({ captureSource: s ? s.id : null });
  } catch (e) { log('Zoom / lens need Screen Recording permission: ' + e.message); }
}

async function joinWifi(ssid, pass) {
  if (!ssid) return;
  log(`Joining the phone's hotspot "${ssid}"…`);
  for (const dev of ['en0', 'en1']) {
    const r = await new Promise(res => require('child_process').execFile('networksetup', ['-setairportnetwork', dev, ssid, pass || ''], { timeout: 20000 }, (e, out) => res(e ? null : String(out))));
    if (r !== null && !/error|could not/i.test(r)) { log('Wi-Fi: joined ' + ssid); return; }
  }
  log('Could not join the hotspot automatically — pick it from the Wi-Fi menu (top right).');
}

// ═════════════════════════ IPC from our own windows ═════════════════════════
ipcMain.on('view-event', (_, e) => {
  if (e.t === 'media') {
    V.media = { index: e.index, count: e.count, name: e.name || '' };
    send({ e: 'media', open: viewMode === 'media', index: e.index, count: e.count, kind: e.kind, playing: e.playing, pos: e.pos, dur: e.dur, name: e.name, fill: e.fill, muted: e.muted });
    if (e.changed) { lastKey = ''; poll(); }
  } else if (e.t === 'picked') send({ e: 'picked', name: e.name });
  else if (e.t === 'doc') {
    V.doc = { page: e.page, pages: e.pages, name: e.name || V.doc.name };
    if (e.cur) send({ e: 'thumb', which: 'cur', slide: e.page, img: e.cur });
    if (e.next) send({ e: 'thumb', which: 'next', slide: e.page + 1, img: e.next });
    lastKey = ''; poll();
  } else if (e.t === 'log') log(e.m);
});
ipcMain.on('control', async (_, a) => {
  if (a.t === 'present') {
    const r = await dialog.showOpenDialog(controlWin, { title: 'Present a file', properties: ['openFile'], filters: [{ name: 'Presentable', extensions: ['pdf', 'docx', 'doc', 'rtf', 'odt', 'txt', 'pptx', 'ppt', 'ppsx', 'key', 'jpg', 'jpeg', 'png', 'gif', 'heic', 'mp4', 'mov', 'webm'] }, { name: 'All files', extensions: ['*'] }] });
    if (!r.canceled && r.filePaths[0]) openDocument(r.filePaths[0]);
  } else if (a.t === 'send') {
    if (!wifi.connected) return log('Connect the phone first.');
    const r = await dialog.showOpenDialog(controlWin, { title: 'Send to phone', properties: ['openFile', 'multiSelections'] });
    if (!r.canceled) files.sendFiles(r.filePaths);
  } else if (a.t === 'drop') { if (wifi.connected) files.sendFiles(a.files); else log('Connect the phone first.'); }
  else if (a.t === 'display') { settings.display = a.id; settings.save(); relayout(); }
  else if (a.t === 'folder') shell.openPath(Files.folder());
});
function relayout() {
  const d = showDisplay().bounds;
  overlayWin.setBounds(d); if (viewWin.isVisible()) viewWin.setBounds(d);
  pushStage(); updateControl();
}

// ═════════════════════════ start ═════════════════════════
app.whenReady().then(() => {
  if (process.platform === 'darwin' && !TEST && app.dock) app.dock.hide();      // menu-bar app: can draw over full-screen slide shows
  settings = new Settings(app.getAppPath());
  settings.display = settings.display || '';
  ann = new Annotations(settings.dir); ann.open('__desktop__');
  present = new Present(log);
  input = new Input(log);
  quiz = new Quiz(); quiz.on('changed', quizUpdated);
  makeOverlay(); makeView(); makeControl();
  wifi = new WifiLink(settings);
  wifi.on('log', log);
  wifi.on('line', l => handle(l).catch(e => log('command: ' + e.message)));
  wifi.on('connected', () => { lastKey = ''; updateControl(); tray && tray.setToolTip('UOR-RC – phone connected'); });
  wifi.on('disconnected', () => { mirrorOn = false; hideViews(); ov({ laser: { active: false, label: '' }, spot: { active: false }, lens: { active: false }, timer: { visible: false }, audioStop: true }); files.cancelAll(); updateControl(); });
  files = new Files(send, MEDIA_DIR);
  files.on('log', log);
  files.on('progress', (name, pct, toPhone) => updateControl({ file: (toPhone ? '→ phone: ' : '← phone: ') + name + (pct >= 100 ? '  ✓' : `  ${pct}%`) }));
  files.on('media', (p, index) => addMedia(p, index));
  files.on('doc', p => openDocument(p));
  wifi.start();
  log('UOR-RC started. Settings: ' + settings.dir);
  log('Connect the phone over Wi-Fi: same network or the phone\'s hotspot, then type the PIN on the phone once.');
  try {
    const icon = nativeImage.createFromPath(path.join(__dirname, 'build', 'tray.png'));
    tray = new Tray(icon.isEmpty() ? nativeImage.createEmpty() : icon);
    tray.setToolTip('UOR-RC');
    tray.setContextMenu(Menu.buildFromTemplate([
      { label: 'Show UOR-RC', click: () => { controlWin.show(); controlWin.focus(); } },
      { label: 'Received files folder', click: () => shell.openPath(Files.folder()) },
      { type: 'separator' },
      { label: 'Quit UOR-RC', click: () => { app.isQuitting = true; app.quit(); } }
    ]));
  } catch { }
  screen.on('display-added', relayout); screen.on('display-removed', relayout);
  setInterval(poll, 700);
  poll();
  if (TEST) global.uorrc = { handle, V: () => V, viewMode: () => viewMode, overlayWin: () => overlayWin, viewWin: () => viewWin, controlWin: () => controlWin, quiz: () => quiz, ann: () => ann };
});
app.on('window-all-closed', e => { /* keep running in the menu bar */ });
app.on('before-quit', () => { app.isQuitting = true; try { wifi.stop(); input.stop(); quiz.stop(); } catch { } });
