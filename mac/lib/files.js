// Files both ways with the phone — same protocol as the Windows version.
// Received files go to a "UOR-RC" folder on the Desktop.
const fs = require('fs'), path = require('path'), os = require('os'), crypto = require('crypto');
const { EventEmitter } = require('events');
const CHUNK = 48 * 1024, WINDOW = 8;

class Files extends EventEmitter {
  constructor(send, mediaDir) {
    super();
    this.send = send; this.mediaDir = mediaDir;
    this.incoming = new Map(); this.waiters = new Map(); this.cancelled = new Set();
    this.queue = Promise.resolve();
  }
  static folder() {
    const d = path.join(os.homedir(), 'Desktop', 'UOR-RC');
    fs.mkdirSync(d, { recursive: true });
    return d;
  }
  static unique(p) {
    if (!fs.existsSync(p)) return p;
    const dir = path.dirname(p), ext = path.extname(p), stem = path.basename(p, ext);
    for (let i = 2; ; i++) { const q = path.join(dir, `${stem} (${i})${ext}`); if (!fs.existsSync(q)) return q; }
  }
  handle(m) {
    const id = m.id;
    switch (m.t) {
      case 'start': {
        let name = String(m.name || 'file').replace(/[\/\\:*?"<>|]/g, '_').slice(0, 150) || 'file';
        const present = m.present === true, doc = m.kind === 'doc';
        const dir = present && !doc ? this.mediaDir : Files.folder();
        fs.mkdirSync(dir, { recursive: true });
        const p = Files.unique(path.join(dir, name));
        try {
          this.incoming.set(id, { fd: fs.openSync(p, 'w'), path: p, name: path.basename(p), size: Number(m.size) || 0, got: 0, present, doc, index: Number(m.index) || 0 });
          this.emit('progress', path.basename(p), 0, false);
          this.send({ e: 'fs', t: 'ack', id, seq: -1 });
        } catch (e) { this.send({ e: 'fs', t: 'cancel', id }); this.emit('log', 'Cannot save file: ' + e.message); }
        break;
      }
      case 'chunk': {
        const inc = this.incoming.get(id); if (!inc) break;
        try {
          const data = Buffer.from(m.d || '', 'base64');
          fs.writeSync(inc.fd, data); inc.got += data.length;
          if (inc.size > 0) this.emit('progress', inc.name, Math.floor(inc.got * 100 / inc.size), false);
          this.send({ e: 'fs', t: 'ack', id, seq: m.seq | 0 });
        } catch (e) { this.abort(id); this.send({ e: 'fs', t: 'cancel', id }); }
        break;
      }
      case 'end': {
        const inc = this.incoming.get(id); if (!inc) break;
        this.incoming.delete(id);
        fs.closeSync(inc.fd);
        this.emit('progress', inc.name, 100, false);
        this.send({ e: 'fs', t: 'done', id, ok: true });
        if (inc.doc) this.emit('doc', inc.path);
        else if (inc.present) this.emit('media', inc.path, inc.index);
        else { this.emit('log', 'Saved: ' + inc.path); this.emit('received', inc.path); }
        break;
      }
      case 'cancel': this.abort(id); this.cancelled.add(id); this.release(id, WINDOW + 1); break;
      case 'ack': this.release(id, 1); break;
    }
  }
  abort(id) {
    const inc = this.incoming.get(id); if (!inc) return;
    this.incoming.delete(id);
    try { fs.closeSync(inc.fd); fs.unlinkSync(inc.path); } catch { }
  }
  cancelAll() { for (const id of [...this.incoming.keys()]) this.abort(id); for (const id of this.waiters.keys()) { this.cancelled.add(id); this.release(id, WINDOW + 1); } }
  // tiny semaphore per transfer
  release(id, n) { const w = this.waiters.get(id); if (!w) return; w.permits += n; while (w.permits > 0 && w.q.length) { w.permits--; w.q.shift()(true); } }
  acquire(id, ms) {
    const w = this.waiters.get(id);
    if (w.permits > 0) { w.permits--; return Promise.resolve(true); }
    return new Promise(res => { const t = setTimeout(() => { const i = w.q.indexOf(done); if (i >= 0) w.q.splice(i, 1); res(false); }, ms); const done = ok => { clearTimeout(t); res(ok); }; w.q.push(done); });
  }
  sendFiles(paths) {
    for (const p of paths) this.queue = this.queue.then(() => this.sendOne(p)).catch(e => this.emit('log', 'Sending failed: ' + e.message));
  }
  async sendOne(p) {
    if (!fs.existsSync(p) || !fs.statSync(p).isFile()) return;
    const id = crypto.randomBytes(5).toString('hex'), size = fs.statSync(p).size, name = path.basename(p);
    this.waiters.set(id, { permits: 0, q: [] });
    try {
      this.emit('log', `Sending to phone: ${name} (${Math.round(size / 1024)} KB)`);
      this.send({ e: 'fs', t: 'start', id, name, size });
      if (!(await this.acquire(id, 15000)) || this.cancelled.has(id)) { this.emit('log', "The phone didn't accept the file."); return; }
      this.release(id, WINDOW);
      const fd = fs.openSync(p, 'r'); const buf = Buffer.alloc(CHUNK);
      let seq = 0, sent = 0, last = -1, n;
      try {
        while ((n = fs.readSync(fd, buf, 0, CHUNK, null)) > 0) {
          if (!(await this.acquire(id, 20000)) || this.cancelled.has(id)) { this.send({ e: 'fs', t: 'cancel', id }); return; }
          this.send({ e: 'fs', t: 'chunk', id, seq: seq++, d: buf.subarray(0, n).toString('base64') });
          sent += n; const pct = size ? Math.floor(sent * 100 / size) : 100;
          if (pct !== last) { last = pct; this.emit('progress', name, pct, true); }
        }
      } finally { fs.closeSync(fd); }
      this.send({ e: 'fs', t: 'end', id });
      this.emit('log', 'Sent to phone: ' + name); this.emit('progress', name, 100, true);
    } finally { this.waiters.delete(id); this.cancelled.delete(id); }
  }
}
module.exports = { Files };
