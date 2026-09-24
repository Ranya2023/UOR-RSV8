// 🗳️ Live quiz (Kahoot-style) — same behaviour and the same student page as the Windows version.
const http = require('http'), fs = require('fs'), path = require('path'), crypto = require('crypto');
const { EventEmitter } = require('events');
const { localAddresses } = require('./wifilink');
const PAGE = fs.readFileSync(path.join(__dirname, 'student.html'), 'utf8');

class Quiz extends EventEmitter {
  constructor() {
    super();
    this.players = new Map();      // id → {name, score, last, lastCorrect, answered}
    this.answers = new Map();      // id → {choice, at}
    this.qid = ''; this.question = ''; this.options = []; this.correct = -1;
    this.open = false; this.reveal = false; this.qi = 0; this.qn = 0; this.port = 0;
  }
  get optionCount() { return Math.max(2, this.options.length); }

  start() {
    if (this.server) return Promise.resolve(true);
    const tryPort = p => new Promise(res => {
      const srv = http.createServer((q, r) => this.serve(q, r));
      srv.once('error', () => res(null));
      srv.listen(p, '0.0.0.0', () => res(srv));
    });
    return (async () => {
      for (let p = 8088; p <= 8095; p++) { const s = await tryPort(p); if (s) { this.server = s; this.port = p; return true; } }
      return false;
    })();
  }
  stop() { try { this.server && this.server.close(); } catch { } this.server = null; }

  newQuestion(q, options, correct, qi, qn) {
    this.qid = crypto.randomBytes(4).toString('hex');
    this.question = String(q || '').trim();
    this.options = options && options.length >= 2 ? options.map(String) : ['', ''];
    this.correct = correct >= 0 && correct < this.options.length ? correct : -1;
    this.qi = qi | 0; this.qn = qn | 0;
    this.answers.clear(); this.open = true; this.reveal = false; this.askedAt = Date.now();
    this.emit('changed');
  }
  showResults() {
    if (this.reveal) return;
    this.reveal = true; this.open = false;
    for (const [id, a] of this.answers) {
      const p = this.players.get(id); if (!p) continue;
      p.answered++; p.lastCorrect = this.correct >= 0 && a.choice === this.correct; p.last = 0;
      if (p.lastCorrect) { const secs = Math.max(0, (a.at - this.askedAt) / 1000); p.last = Math.round(1000 * (1 - Math.min(1, secs / 30) / 2)); p.score += p.last; }
    }
    this.emit('changed');
  }
  setCorrect(i) { this.correct = i >= 0 && i < this.optionCount ? i : -1; this.showResults(); this.emit('changed'); }
  close() { this.open = false; this.emit('changed'); }
  resetScores() { for (const p of this.players.values()) { p.score = 0; p.last = 0; p.answered = 0; } this.emit('changed'); }
  counts() { const c = new Array(this.optionCount).fill(0); for (const a of this.answers.values()) if (a.choice >= 0 && a.choice < c.length) c[a.choice]++; return c; }
  top(n) { return [...this.players.values()].filter(p => p.name).sort((a, b) => b.score - a.score || a.name.localeCompare(b.name)).slice(0, n); }
  bestUrl() {
    const ips = localAddresses().map(a => a.address);
    const ip = ips.find(a => a.startsWith('172.20.10.')) ||        // iPhone hotspot
               ips.find(a => /^(192\.168\.|10\.|172\.)/.test(a)) || ips[0] || '127.0.0.1';
    return `http://${ip}:${this.port}`;
  }

  serve(req, res) {
    const u = new URL(req.url, 'http://x'); const q = Object.fromEntries(u.searchParams);
    const json = o => { res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }); res.end(JSON.stringify(o)); };
    switch (u.pathname) {
      case '/': case '/index.html':
        res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' }); return res.end(PAGE);
      case '/state': return json(this.state(q.id));
      case '/join': {
        const id = q.id || '', name = String(q.name || '').trim().slice(0, 24);
        if (id.length < 4 || id.length > 64 || !name) return json({ ok: false });
        const p = this.players.get(id) || { name: '', score: 0, last: 0, lastCorrect: false, answered: 0 };
        p.name = name; this.players.set(id, p); this.emit('changed'); return json({ ok: true });
      }
      case '/vote': {
        const c = parseInt(q.c, 10), id = q.id || '';
        let ok = false;
        if (this.open && q.q === this.qid && id.length > 3 && id.length < 64 && c >= 0 && c < this.optionCount) {
          if (!this.players.has(id)) this.players.set(id, { name: '', score: 0, last: 0, lastCorrect: false, answered: 0 });
          if (!this.answers.has(id)) { this.answers.set(id, { choice: c, at: Date.now() }); ok = true; }   // first answer counts
        }
        if (ok) this.emit('changed');
        return json({ ok });
      }
      default: res.writeHead(404); res.end('not found');
    }
  }
  state(id) {
    const me = id ? this.players.get(id) : null;
    const order = [...this.players.values()].filter(p => p.name).sort((a, b) => b.score - a.score);
    const mine = id && this.answers.has(id) ? this.answers.get(id).choice : null;
    return {
      qid: this.qid, q: this.question, opts: this.options, n: this.optionCount, open: this.open, reveal: this.reveal,
      correct: this.reveal ? this.correct : -1, total: this.answers.size, counts: this.reveal ? this.counts() : null,
      qi: this.qi, qn: this.qn, joined: !!me, name: me ? me.name : '', score: me ? me.score : 0, last: me ? me.last : 0,
      ok: me ? me.lastCorrect : false, rank: me ? order.indexOf(me) + 1 : 0, players: order.length, mine,
      top: order.slice(0, 5).map(p => ({ name: p.name, score: p.score }))
    };
  }
}
module.exports = { Quiz };
