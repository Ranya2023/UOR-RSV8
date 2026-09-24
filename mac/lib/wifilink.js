// Wi-Fi link — same protocol as the Windows version (the phone app needs no change):
//  1. the Mac broadcasts a UDP beacon every second (port 47801),
//  2. the phone answers with a proof that it knows the PIN,
//  3. the Mac connects OUT to the phone over TCP and they talk one JSON object per line.
// macOS never has to accept incoming connections, so its firewall never asks.
const dgram = require('dgram'), net = require('net'), os = require('os'), crypto = require('crypto');
const { EventEmitter } = require('events');

const BEACON_PORT = 47801;
const proof = (pin, nonce) => crypto.createHash('sha256').update(`${pin}:${nonce}`).digest('hex').slice(0, 16);

function localAddresses() {
  const out = [];
  for (const list of Object.values(os.networkInterfaces()))
    for (const a of list || []) if (a.family === 'IPv4' && !a.internal) out.push(a);
  return out;
}

function broadcastTargets() {
  const t = new Set(['255.255.255.255']);
  for (const a of localAddresses()) {
    const ip = a.address.split('.').map(Number), mask = a.netmask.split('.').map(Number);
    t.add(ip.map((b, i) => (b | (~mask[i] & 255)) & 255).join('.'));
    // on a phone hotspot the phone is the gateway — usually x.x.x.1 of that network
    t.add(ip.map((b, i) => (b & mask[i])).map((b, i) => (i === 3 ? b + 1 : b)).join('.'));
  }
  return [...t];
}

class WifiLink extends EventEmitter {
  constructor(settings) {
    super();
    this.settings = settings;
    this.nonces = [];
    this.sock = null;       // TCP socket to the phone
    this.connecting = false;
    this.peer = '';
  }
  get connected() { return !!this.sock; }

  start() {
    this.udp = dgram.createSocket({ type: 'udp4', reuseAddr: true });
    this.udp.on('error', e => this.emit('log', 'Wi-Fi: ' + e.message));
    this.udp.on('message', (buf, rinfo) => this.onReply(buf, rinfo));
    this.udp.bind(0, () => {
      try { this.udp.setBroadcast(true); } catch { }
      this.timer = setInterval(() => this.beacon(), 1000);
      this.beacon();
    });
    this.emit('log', 'Wi-Fi: looking for the phone on ' + (localAddresses().map(a => a.address).join(', ') || 'no network'));
  }

  beacon() {
    const nonce = crypto.randomBytes(6).toString('hex');
    this.nonces.unshift(nonce); this.nonces.length = Math.min(this.nonces.length, 12);
    const msg = Buffer.from(JSON.stringify({
      remco: 1, type: 'beacon', id: this.settings.id, name: os.hostname().replace(/\.local$/, ''),
      bt: '', nonce, connected: this.connected
    }));
    const targets = broadcastTargets();
    if (this.settings.phoneIp) targets.push(this.settings.phoneIp);
    for (const t of targets) { try { this.udp.send(msg, BEACON_PORT, t); } catch { } }
  }

  onReply(buf, rinfo) {
    let m; try { m = JSON.parse(buf.toString('utf8')); } catch { return; }
    if (m.type !== 'reply' || m.id !== this.settings.id) return;
    if (!this.nonces.some(n => proof(this.settings.pin, n) === m.proof)) return;   // wrong PIN
    if (this.connected || this.connecting) return;
    this.connecting = true;
    const port = Number(m.port) || 47802;
    const s = net.connect({ host: rinfo.address, port, timeout: 3000 });
    s.setNoDelay(true);
    s.once('connect', () => {
      s.setTimeout(8000);               // the phone pings every 2 s; silence = Wi-Fi lost
      s.write(JSON.stringify({ e: 'pc_hello', id: this.settings.id, name: os.hostname(), bt: '', proof: proof(this.settings.pin, m.pnonce || '') }) + '\n');
      this.sock = s; this.connecting = false;
      this.peer = `${m.phone || 'phone'} (${rinfo.address})`;
      this.emit('connected', this.peer);
      this.emit('log', 'Phone connected over Wi-Fi: ' + this.peer);
    });
    let rest = '';
    s.on('data', d => {
      rest += d.toString('utf8');
      let i;
      while ((i = rest.indexOf('\n')) >= 0) {
        const line = rest.slice(0, i).trim(); rest = rest.slice(i + 1);
        if (line) this.emit('line', line);
      }
    });
    const drop = () => {
      this.connecting = false;
      if (this.sock === s) { this.sock = null; this.emit('disconnected'); this.emit('log', 'Wi-Fi connection closed. Waiting for the phone…'); }
      try { s.destroy(); } catch { }
    };
    s.on('timeout', drop); s.on('error', drop); s.on('close', drop);
  }

  send(obj) {
    if (!this.sock) return;
    try { this.sock.write(JSON.stringify(obj) + '\n'); } catch { }
  }

  stop() {
    clearInterval(this.timer);
    try { this.udp.close(); } catch { }
    try { this.sock && this.sock.destroy(); } catch { }
  }
}
module.exports = { WifiLink, localAddresses, proof };
