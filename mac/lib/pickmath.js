// How far the 🎲 picker spins so it always stops exactly on the chosen name (same as Windows).
function steps(count, start, winner) {
  const laps = count <= 3 ? 8 : count < 10 ? 3 : 1;
  const base = Math.max(22, laps * count);
  return base + (((winner - (start + base) % count) + count) % count);
}
const landing = (count, start, s) => (start + s) % count;
if (typeof module !== 'undefined') module.exports = { steps, landing };
