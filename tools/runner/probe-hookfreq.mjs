// e_hookfreq: measure how often the documented combat hook points are called in real combat.
import fs from 'node:fs';
import path from 'node:path';
import { sleep, REPO } from './lib.mjs';

const BASE = 'http://127.0.0.1:47901';
const j = async (p) => (await fetch(BASE + p)).json();
const SECONDS = Number(process.env.HOOKFREQ_SECONDS || 60);

async function waitScene(scene, tries = 90) {
  for (let i = 0; i < tries; i++) {
    const h = await j('/health');
    if (h.scene === scene) return true;
    await sleep(1000);
  }
  return false;
}

async function nearestEnemy() {
  const r = await j('/objects?type=UnitAvatar&limit=60');
  const me = r.items.find((x) => x.type === 'PlayerAvatar');
  const foes = r.items.filter((x) => x.type !== 'PlayerAvatar' && x.active);
  if (!me || !foes.length) return null;
  let best = null;
  for (const f of foes) {
    const d = Math.hypot(f.pos[0] - me.pos[0], f.pos[1] - me.pos[1]);
    if (!best || d < best.d) best = { ...f, d };
  }
  return { me, foe: best };
}

const out = { startedAt: new Date().toISOString(), seconds: SECONDS };
out.health = await j('/health');
if (out.health.scene !== 'MainWorld') {
  out.enter = await j('/enter?slot=SLOT2');
  out.main = await waitScene('MainWorld');
  await sleep(3000);
}
out.goto = await j('/goto?floor=Moleland_HardBattle_0');
await sleep(4000);
out.hostileBefore = (await j('/player')).hostile;
out.equip = await j('/equip?weapon=10');
await sleep(600);

out.start = await j('/hookfreq?action=start');
const t0 = Date.now();
let swings = 0;
let misses = 0;
while (Date.now() - t0 < SECONDS * 1000) {
  const near = await nearestEnemy();
  if (!near) {
    misses++;
    await sleep(500);
    continue;
  }
  const { me, foe } = near;
  const dx = foe.pos[0] - me.pos[0];
  const dy = foe.pos[1] - me.pos[1];
  const len = Math.hypot(dx, dy) || 1;
  await j(`/tp?x=${(foe.pos[0] - (dx / len) * 1.1).toFixed(2)}&y=${(foe.pos[1] - (dy / len) * 1.1).toFixed(2)}`);
  await sleep(120);
  await j(`/attack?dx=${(dx / len).toFixed(3)}&dy=${(dy / len).toFixed(3)}&dash=0&up=0`);
  await sleep(450);
  await j('/attackup');
  swings++;
  await sleep(120);
}
out.swings = swings;
out.misses = misses;
out.stop = await j('/hookfreq?action=stop');
out.hostileAfter = (await j('/player')).hostile;

const dir = path.join(REPO, 'work', '_runs', 'hookfreq-' + new Date().toISOString().slice(0, 10));
fs.mkdirSync(dir, { recursive: true });
fs.writeFileSync(path.join(dir, 'hookfreq.json'), JSON.stringify(out, null, 1));
console.log(JSON.stringify({ swings, misses, seconds: out.stop.seconds, hooks: out.stop.hooks }, null, 1));
