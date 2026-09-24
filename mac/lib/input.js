// Mouse & keyboard on the Mac through a tiny native helper (uorrc-helper, bundled in the app).
// macOS asks once for "Accessibility" permission for UOR-RC.
const { spawn } = require('child_process');
const fs = require('fs'), path = require('path');

class Input {
  constructor(log) {
    this.log = log;
    const candidates = [
      process.resourcesPath && path.join(process.resourcesPath, 'uorrc-helper'),
      path.join(__dirname, '..', 'helper', 'uorrc-helper')
    ].filter(Boolean);
    const bin = candidates.find(p => { try { return fs.statSync(p).isFile(); } catch { return false; } });
    if (!bin || process.platform !== 'darwin') { this.proc = null; if (process.platform === 'darwin') log('Input helper missing — mouse / keyboard control is off.'); return; }
    this.proc = spawn(bin, [], { stdio: ['pipe', 'ignore', 'pipe'] });
    this.proc.stderr.on('data', d => log('helper: ' + String(d).trim()));
    this.proc.on('exit', () => { this.proc = null; });
  }
  send(o) { if (this.proc) try { this.proc.stdin.write(JSON.stringify(o) + '\n'); } catch { } }
  move(x, y) { this.send({ t: 'move', x, y }); }
  rel(dx, dy) { this.send({ t: 'rel', dx, dy }); }
  button(b, a) { this.send({ t: 'btn', b, a }); }
  scroll(d) { this.send({ t: 'scroll', d }); }
  key(k, mods = {}, n = 1) { this.send({ t: 'key', k, n, ...mods }); }
  type(s) { this.send({ t: 'type', s }); }
  stop() { try { this.proc && this.proc.kill(); } catch { } }
}
module.exports = { Input };
