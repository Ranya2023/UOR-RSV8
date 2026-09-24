// Numbers, text labels and ink per slide / per page (same as the Windows version).
const fs = require('fs'), path = require('path'), crypto = require('crypto');
const NUMBER_COLORS = ['#f87171', '#fb923c', '#fbbf24', '#a3e635', '#34d399', '#22d3ee', '#60a5fa', '#a78bfa', '#f472b6', '#fb7185'];

class Annotations {
  constructor(dir) { this.dir = path.join(dir, 'annotations'); this.file = ''; this.data = {}; }
  open(key) {
    fs.mkdirSync(this.dir, { recursive: true });
    const f = path.join(this.dir, crypto.createHash('sha1').update(String(key).toLowerCase()).digest('hex').slice(0, 16) + '.json');
    if (f === this.file) return;
    this.file = f;
    try { this.data = JSON.parse(fs.readFileSync(f, 'utf8')); } catch { this.data = {}; }
  }
  for(k) { return this.data[k] || (this.data[k] = []); }
  keys() { return Object.keys(this.data); }
  save() {
    if (!this.file) return;
    const keep = {};
    for (const [k, v] of Object.entries(this.data)) if (v.length && !k.startsWith('media:') && k !== 'phone') keep[k] = v;
    try { fs.writeFileSync(this.file, JSON.stringify(keep)); } catch { }
  }
}
const newId = () => crypto.randomBytes(5).toString('hex');
module.exports = { Annotations, NUMBER_COLORS, newId };
