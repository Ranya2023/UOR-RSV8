// Controls PowerPoint for Mac or Keynote through AppleScript (macOS asks once:
// "UOR-RC wants to control Microsoft PowerPoint / Keynote" → OK).
// PowerPoint / Keynote play the show themselves, so animations and transitions are exactly theirs.
const { execFile } = require('child_process');
const fs = require('fs'), path = require('path'), os = require('os');

const isMac = process.platform === 'darwin';
function osa(script, ms = 5000) {
  return new Promise(res => {
    if (!isMac) return res(null);
    execFile('osascript', ['-e', script], { timeout: ms }, (err, out) => res(err ? null : String(out).trim()));
  });
}
const PPT = 'Microsoft PowerPoint', KEY = 'Keynote';

class Present {
  constructor(log) { this.log = log; this.app = ''; this.exportCache = new Map(); }

  /** Which presentation app is running (PowerPoint first). */
  async detect() {
    const r = await osa(`set a to ""
if application "${PPT}" is running then set a to "ppt"
if a is "" and application "${KEY}" is running then set a to "key"
return a`, 3000);
    this.app = r || '';
    return this.app;
  }

  /** {app, open, showing, slide, total, name, file, sw, sh} */
  async state() {
    const app = await this.detect();
    if (app === 'ppt') {
      const r = await osa(`tell application "${PPT}"
  if (count of presentations) = 0 then return "0"
  set p to active presentation
  set total to count of slides of p
  set showing to ((count of slide show windows) > 0)
  set idx to 0
  if showing then
    try
      set idx to slide index of slide of slide show view of slide show window 1
    end try
  else
    try
      set idx to slide index of slide of view of document window 1
    end try
  end if
  set sw to 16
  set sh to 9
  try
    set sw to slide width of page setup of p
    set sh to slide height of page setup of p
  end try
  set fp to ""
  try
    set fp to full name of p
  end try
  return "1|" & total & "|" & idx & "|" & showing & "|" & (name of p) & "|" & fp & "|" & sw & "|" & sh
end tell`);
      return parse('ppt', r);
    }
    if (app === 'key') {
      const r = await osa(`tell application "${KEY}"
  if (count of documents) = 0 then return "0"
  set d to front document
  set total to count of slides of d
  set idx to 0
  try
    set idx to slide number of current slide of d
  end try
  set fp to ""
  try
    set fp to POSIX path of (file of d as alias)
  end try
  set sw to 16
  set sh to 9
  try
    set sw to width of d
    set sh to height of d
  end try
  return "1|" & total & "|" & idx & "|" & playing & "|" & (name of d) & "|" & fp & "|" & sw & "|" & sh
end tell`);
      return parse('key', r);
    }
    return { app: '', open: false, showing: false, slide: 0, total: 0, name: '', file: '', sw: 16, sh: 9 };
  }

  async next() {
    if (this.app === 'ppt') return ok(await osa(`tell application "${PPT}" to go to next slide (slide show view of slide show window 1)`));
    if (this.app === 'key') return ok(await osa(`tell application "${KEY}" to show next`));
    return false;
  }
  async prev() {
    if (this.app === 'ppt') return ok(await osa(`tell application "${PPT}" to go to previous slide (slide show view of slide show window 1)`));
    if (this.app === 'key') return ok(await osa(`tell application "${KEY}" to show previous`));
    return false;
  }
  async goto(n) {
    if (this.app === 'ppt') return ok(await osa(`tell application "${PPT}" to go to slide (slide show view of slide show window 1) number ${n | 0}`));
    if (this.app === 'key') return ok(await osa(`tell application "${KEY}" to tell front document to show slide ${n | 0}`)) ||
                                   ok(await osa(`tell application "${KEY}" to tell front document to set current slide to slide ${n | 0}`));
    return false;
  }
  async start(fromCurrent) {
    if (this.app === 'ppt') {
      if (!fromCurrent) return ok(await osa(`tell application "${PPT}"
  activate
  set s to slide show settings of active presentation
  set range type of s to slide show range all
  run slide show s
end tell`));
      return ok(await osa(`tell application "${PPT}"
  activate
  set p to active presentation
  set idx to 1
  try
    set idx to slide index of slide of view of document window 1
  end try
  set s to slide show settings of p
  set range type of s to slide show range
  set starting slide of s to idx
  set ending slide of s to (count of slides of p)
  run slide show s
  set range type of s to slide show range all
end tell`));
    }
    if (this.app === 'key') return ok(await osa(fromCurrent
      ? `tell application "${KEY}" to start front document from current slide of front document`
      : `tell application "${KEY}" to start front document from slide 1 of front document`));
    return false;
  }
  async end() {
    if (this.app === 'ppt') return ok(await osa(`tell application "${PPT}" to exit slide show (slide show view of slide show window 1)`));
    if (this.app === 'key') return ok(await osa(`tell application "${KEY}" to stop front document`));
    return false;
  }
  /** Bring the running show to the front (after Desktop / photos / whiteboard…). */
  async front() {
    if (this.app === 'ppt') return ok(await osa(`tell application "${PPT}" to activate`));
    if (this.app === 'key') return ok(await osa(`tell application "${KEY}" to activate`));
    return false;
  }
  async notes(idx) {
    if (this.app === 'key') return (await osa(`tell application "${KEY}" to get presenter notes of slide ${idx | 0} of front document`)) || '';
    if (this.app === 'ppt') return (await osa(`tell application "${PPT}"
  try
    return content of text range of text frame of place holder 2 of notes page of slide ${idx | 0} of active presentation
  on error
    return ""
  end try
end tell`)) || '';
    return '';
  }
  async titles(total) {
    if (this.app === 'key') {
      const r = await osa(`tell application "${KEY}"
  set out to ""
  repeat with s in slides of front document
    set t to ""
    try
      set t to object text of default title item of s
    end try
    set out to out & t & (ASCII character 30)
  end repeat
  return out
end tell`, 8000);
      return r ? r.split('\u001e').slice(0, total).map(t => t.replace(/\s+/g, ' ').trim().slice(0, 80)) : [];
    }
    return new Array(total).fill('');
  }

  /**
   * Picture of every slide (for the phone's previews and touchpad). Exported once
   * per presentation into a temporary folder: [path of slide 1, slide 2, …].
   */
  async slideImages(key) {
    if (this.exportCache.has(key)) return this.exportCache.get(key);
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'uorrc-slides-'));
    if (this.app === 'ppt')
      await osa(`tell application "${PPT}" to save active presentation in (POSIX file "${dir}/deck") as save as JPG`, 60000);
    else if (this.app === 'key')
      await osa(`tell application "${KEY}" to export front document to (POSIX file "${dir}/deck") as slide images with properties {image format:JPEG, skipped slides:false}`, 60000);
    const files = [];
    const walk = d => { for (const f of fs.readdirSync(d)) { const p = path.join(d, f); fs.statSync(p).isDirectory() ? walk(p) : /\.(jpe?g|png)$/i.test(f) && files.push(p); } };
    try { walk(dir); } catch { }
    const num = f => { const m = path.basename(f).match(/(\d+)(?!.*\d)/); return m ? +m[1] : 0; };
    files.sort((a, b) => num(a) - num(b));
    this.exportCache.set(key, files);
    return files;
  }

  /** Open a PowerPoint / Keynote file and start it. */
  async openAndPlay(file) {
    const f = file.replace(/"/g, '\\"');
    if (/\.key$/i.test(file)) return ok(await osa(`tell application "${KEY}"
  activate
  set d to open (POSIX file "${f}")
  delay 1
  start d from slide 1 of d
end tell`, 30000));
    return ok(await osa(`tell application "${PPT}"
  activate
  open (POSIX file "${f}")
  delay 1
  run slide show slide show settings of active presentation
end tell`, 30000));
  }

  /** Word → PDF with Microsoft Word for Mac (null if Word isn't installed). */
  async wordToPdf(file) {
    const out = path.join(os.tmpdir(), path.basename(file).replace(/\.\w+$/, '') + '.pdf').replace(/"/g, '');
    const r = await osa(`tell application "Microsoft Word"
  set d to open file name (POSIX file "${file.replace(/"/g, '\\"')}")
  save as d file name (POSIX file "${out}") file format format PDF
  close d saving no
end tell
return "ok"`, 60000);
    return r === 'ok' && fs.existsSync(out) ? out : null;
  }
}
function parse(app, r) {
  if (!r || r === '0') return { app, open: false, showing: false, slide: 0, total: 0, name: '', file: '', sw: 16, sh: 9 };
  const [, total, idx, showing, name, file, sw, sh] = r.split('|');
  return { app, open: true, showing: showing === 'true', slide: +idx || 0, total: +total || 0, name: name || '', file: file || '', sw: +sw || 16, sh: +sh || 9 };
}
const ok = r => r !== null;
module.exports = { Present, osa };
