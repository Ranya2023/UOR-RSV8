// Portable settings: a "UOR-RC-data" folder next to UOR-RC.app (like the Windows version),
// or ~/Library/Application Support/UOR-RC when that folder can't be written.
const fs = require('fs'), path = require('path'), os = require('os'), crypto = require('crypto');

function pickDir(appPath) {
  const candidates = [];
  if (appPath) {
    // …/UOR-RC.app/Contents/Resources/app.asar → folder that contains UOR-RC.app
    const i = appPath.indexOf('.app' + path.sep);
    const beside = i >= 0 ? path.dirname(appPath.slice(0, i + 4)) : path.dirname(appPath);
    candidates.push(path.join(beside, 'UOR-RC-data'));
  }
  candidates.push(path.join(os.homedir(), 'Library', 'Application Support', 'UOR-RC'));
  for (const d of candidates) {
    try {
      fs.mkdirSync(d, { recursive: true });
      const probe = path.join(d, '.write-test');
      fs.writeFileSync(probe, 'ok'); fs.unlinkSync(probe);
      return d;
    } catch { /* try the next one */ }
  }
  return os.tmpdir();
}

class Settings {
  constructor(appPath) {
    this.dir = pickDir(appPath);
    this.file = path.join(this.dir, 'settings.json');
    let s = {};
    try { s = JSON.parse(fs.readFileSync(this.file, 'utf8')); } catch { }
    this.id = s.id || crypto.randomBytes(6).toString('hex');
    this.pin = s.pin || String(1000 + crypto.randomInt(9000));
    this.phoneIp = s.phoneIp || '';
    this.save();
  }
  save() {
    try { fs.writeFileSync(this.file, JSON.stringify({ id: this.id, pin: this.pin, phoneIp: this.phoneIp }, null, 2)); } catch { }
  }
}
module.exports = { Settings };
