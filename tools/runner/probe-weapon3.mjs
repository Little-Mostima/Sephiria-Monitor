// v_g7: compare damage raw of weapon 10 (1.75x) vs 400 (1.1x) by firing each at live enemies.
import { sleep } from './lib.mjs';

const BASE = 'http://127.0.0.1:47901';
const j = async (p) => (await fetch(BASE + p)).json();

async function waitScene(scene, tries = 60) {
  for (let i = 0; i < tries; i++) {
    const h = await j('/health');
    if (h.scene === scene) return true;
    await sleep(1000);
  }
  return false;
}

async function nearestEnemy() {
  const r = await j('/objects?type=UnitAvatar&limit=40');
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

async function swing(weaponId, holdMs) {
  const eq = await j(`/equip?weapon=${weaponId}`);
  await sleep(500);
  const near = await nearestEnemy();
  if (!near) return { eq, error: 'no enemy' };
  const { me, foe } = near;
  const dx = foe.pos[0] - me.pos[0];
  const dy = foe.pos[1] - me.pos[1];
  const len = Math.hypot(dx, dy) || 1;
  await j(`/tp?x=${(foe.pos[0] - (dx / len) * 1.1).toFixed(2)}&y=${(foe.pos[1] - (dy / len) * 1.1).toFixed(2)}`);
  await sleep(350);
  const before = await j('/damage?limit=1');
  const cursor = before.cursor || 0;
  const atk = await j(`/attack?dx=${(dx / len).toFixed(3)}&dy=${(dy / len).toFixed(3)}&dash=0&up=0`);
  await sleep(holdMs);
  await j('/attackup');
  await sleep(900);
  const after = await j('/damage?limit=200');
  const events = (after.events || []).filter((e) => (e.eseq || 0) > cursor && e.byPlayer);
  return { eq, atk, foe: foe.name, events };
}

async function run(weaponId, swings, holdMs) {
  const all = [];
  const detail = [];
  for (let i = 0; i < swings; i++) {
    const r = await swing(weaponId, holdMs);
    detail.push({ foe: r.foe, n: (r.events || []).length, raws: (r.events || []).map((e) => e.raw) });
    for (const e of r.events || []) all.push(e.raw);
  }
  return { weaponId, detail, raws: all };
}

const out = {};
out.enter = await j('/enter?slot=SLOT2');
out.main = await waitScene('MainWorld');
await sleep(3000);
out.goto = await j('/goto?floor=Moleland_Battle_1');
await sleep(4000);
out.hostile = (await j('/player')).hostile;
out.w10 = await run(10, 5, 450);
out.w400 = await run(400, 5, 700);

const median = (a) => {
  if (!a.length) return null;
  const s = [...a].sort((x, y) => x - y);
  return s[Math.floor(s.length / 2)];
};
const m10 = median(out.w10.raws);
const m400 = median(out.w400.raws);
out.summary = {
  n10: out.w10.raws.length,
  n400: out.w400.raws.length,
  median10: m10,
  median400: m400,
  ratio: m10 && m400 ? +(m10 / m400).toFixed(3) : null,
  expected: 1.591,
};
console.log(JSON.stringify(out, null, 1));
