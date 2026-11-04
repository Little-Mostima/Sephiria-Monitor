#!/usr/bin/env node
// Damage bench: one command from "game somewhere" to a table of per-event damage.
//
// Why this exists: measuring one weapon took 34 tool calls and about six minutes, and almost all of
// that was spent rediscovering things the project already knew - the town and the greatsword school
// have no damageable enemies (friendly NPCs take no friendly fire), the only target is the training
// dummy on the hidden TrainingSchool floor, jumping to a procedural floor fails outside a run, and a
// name substring lands on the wrong floor. Those facts live in this file now.
//
//   node tools/bench.mjs --weapon 1105 --hits 100
//   node tools/bench.mjs --hits 10 --interval 0 --floor TrainingSchool --target DamageDummy
//
// Everything the bridge reports is kept verbatim in the run directory; the table is derived from the
// event stream, never from /combat (that one is a lifetime counter and cannot be sliced per hit).
import fs from 'node:fs';
import path from 'node:path';
import { RUNS } from './config.mjs';
import { getJson, tryJson, sleep, health, launchAndWait, killGameAndWait } from './runner/lib.mjs';
import { enterWorld } from './session.mjs';

const args = process.argv.slice(2);
const has = (n) => args.includes(n);
const flag = (n, d) => {
  const i = args.indexOf(n);
  return i >= 0 && args[i + 1] && !args[i + 1].startsWith('--') ? args[i + 1] : d;
};

const weapon = Number(flag('--weapon', 0));
const hits = Number(flag('--hits', 100));
const interval = Number(flag('--interval', 1250));
const slot = Number(flag('--slot', 0));
const floor = flag('--floor', 'TrainingSchool');
const target = flag('--target', 'DamageDummy');
const [ox, oy] = flag('--offset', '0,-1.6').split(',').map(Number);
const verbose = has('--verbose');
const log = (m) => console.log(m);

const stamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
const outDir = path.join(RUNS, 'bench-' + stamp);

// ---------- get into a position where a swing can land ----------
if (!(await health(3000))) {
  log('bridge down, launching the game');
  await launchAndWait({ log });
}
const world = await enterWorld({ slot, log: log });
log('world ' + world.scene + ' via ' + world.via);

const go = await tryJson('/goto?hidden=1&name=' + encodeURIComponent(floor), 30000);
if (!go || go.error) throw new Error('goto ' + floor + ' failed: ' + JSON.stringify(go));
log('floor ' + (go.floor || go.scene || floor));

if (weapon) {
  const eq = await tryJson('/equip?weapon=' + weapon, 15000);
  if (!eq || eq.error) throw new Error('equip ' + weapon + ' failed: ' + JSON.stringify(eq));
  log('weapon ' + JSON.stringify(eq.weapon || eq.name || eq));
}

// /tp?name= only reaches merchants, so find the unit with /targets and jump to its coordinates.
const found = await tryJson('/targets?all=1&r=0&q=' + encodeURIComponent(target), 15000);
const pick = found && Array.isArray(found.targets) ? found.targets[0] : null;
if (!pick) throw new Error('no damageable unit matches "' + target + '" - /targets?all=1&q=' + target);
log(`target ${pick.name} (${pick.type}) hp ${pick.hp} on ${pick.floor || '?'} at ${pick.distance}m`);
const dest = await tryJson(`/tp?x=${pick.pos[0] + ox}&y=${pick.pos[1] + oy}`, 15000);
if (!dest || dest.error) throw new Error('tp to target failed: ' + JSON.stringify(dest));

// ---------- swing ----------
const base = await getJson('/events?n=200');
let cursor = base.cursor ?? 0;
const combatBefore = await getJson('/combat');
const events = [];
const flush = async () => {
  const r = await tryJson(`/events?n=200&after=${cursor}`, 8000);
  if (!r || !Array.isArray(r.events)) return;
  cursor = r.cursor ?? cursor;
  for (const e of r.events) events.push(e);
};

const started = Date.now();
for (let i = 0; i < hits; i++) {
  const res = await tryJson('/attack?dx=0&dy=1&release=1', 8000);
  if (!res || res.error) throw new Error('attack ' + (i + 1) + ' failed: ' + JSON.stringify(res));
  if (verbose) log('hit ' + (i + 1));
  await sleep(interval);
  // The event ring is small (a few hundred); a long loop must drain it as it goes or the first hits
  // are evicted and the totals silently come out short.
  if ((i + 1) % 20 === 0) await flush();
}
await sleep(Math.max(400, interval));
await flush();
const elapsedMs = Date.now() - started;
const combatAfter = await getJson('/combat');

// ---------- aggregate ----------
const damage = events.filter((e) => e.kind === 'damage' && e.byPlayer !== false && !e.isPlayer);
const byId = new Map();
for (const e of damage) {
  const k = e.id || '(unknown)';
  let row = byId.get(k);
  if (!row) {
    row = { id: k, n: 0, raw: 0, dealt: 0, min: Infinity, max: -Infinity, crit: 0, element: e.element || '', type: e.damageType || '', from: e.fromType || '' };
    byId.set(k, row);
  }
  row.n++;
  row.raw += Number(e.raw) || 0;
  row.dealt += Number(e.dealt) || 0;
  row.min = Math.min(row.min, Number(e.raw) || 0);
  row.max = Math.max(row.max, Number(e.raw) || 0);
  if (e.crit) row.crit++;
}
const rows = [...byId.values()].sort((a, b) => b.dealt - a.dealt);
const dealtTotal = rows.reduce((s, r) => s + r.dealt, 0);
const rawTotal = rows.reduce((s, r) => s + r.raw, 0);
const hitCount = damage.filter((e) => String(e.result) === 'Success').length;
const taken = events.filter((e) => e.kind === 'damage' && e.isPlayer).length;

// ---------- report ----------
const fix = (v, d = 2) => Number(v).toFixed(d);
log('');
log(`BENCH  ${hits} hit(s) · ${interval} ms · ${fix(elapsedMs / 1000, 1)} s · ${fix(elapsedMs / Math.max(1, hits) / 1000)} s/hit`);
log('event'.padEnd(38) + 'n'.padStart(5) + 'raw/hit'.padStart(10) + 'raw'.padStart(10) + 'dealt'.padStart(9) + 'crit'.padStart(6));
for (const r of rows) {
  const spread = r.min === r.max ? '' : `${fix(r.min)}-${fix(r.max)}`;
  log(String(r.id).padEnd(38) + String(r.n).padStart(5) + fix(r.raw / r.n).padStart(10) + fix(r.raw, 1).padStart(10) + String(r.dealt).padStart(9) + String(r.crit).padStart(6) + (spread ? '  spread ' + spread : ''));
}
log(''.padEnd(38) + ''.padStart(5) + ('raw/hit ' + fix(rawTotal / Math.max(1, hits))).padStart(10) + fix(rawTotal, 1).padStart(10) + String(dealtTotal).padStart(9));
log('');
log(`hits landed ${hitCount}/${damage.length} events · player took ${taken} hit(s)`);
// /combat keeps its own tally (bySide.playerDealt, accumulated in the same event handler), so the two
// views must agree; a mismatch means the ring buffer dropped something before it was drained.
if (combatBefore && combatAfter) {
  const before = Number(combatBefore.bySide?.playerDealt) || 0;
  const delta = (Number(combatAfter.bySide?.playerDealt) || 0) - before;
  log(`/combat playerDealt delta ${delta} (event sum ${dealtTotal})`);
  if (delta !== dealtTotal) log('  note: the two views disagree - events were probably evicted before they were drained');
}
if (!damage.length) log('no damage events - the swings are missing the target; check --target/--offset');

fs.mkdirSync(outDir, { recursive: true });
fs.writeFileSync(path.join(outDir, 'events.json'), JSON.stringify({ cursor, events }, null, 1));
const summary = {
  stamp, weapon, hits, interval, slot, floor, target, offset: [ox, oy],
  elapsedMs, hitsLanded: hitCount, dealtTotal, rawTotal, taken,
  combat: { before: combatBefore, after: combatAfter },
  rows,
};
fs.writeFileSync(path.join(outDir, 'summary.json'), JSON.stringify(summary, null, 1));
const md = [
  '# bench ' + stamp,
  '',
  `${hits} hit(s), ${interval} ms apart, ${fix(elapsedMs / 1000, 1)} s total; weapon ${weapon || '(as equipped)'}; ${floor} / ${target}.`,
  '',
  '| event | n | raw/hit | raw | dealt | crit | element / type |',
  '|---|---|---|---|---|---|---|',
  ...rows.map((r) => `| \`${r.id}\` | ${r.n} | ${fix(r.raw / r.n)} | ${fix(r.raw, 1)} | ${r.dealt} | ${r.crit} | ${r.element} / ${r.type} |`),
  '',
  `Total dealt ${dealtTotal}; hits landed ${hitCount}; player took ${taken} hit(s).`,
  '',
  'Raw events: `events.json`. The table is derived from the event stream, not from /combat.',
];
fs.writeFileSync(path.join(outDir, 'summary.md'), md.join('\n') + '\n');
log('');
log('written ' + path.relative(process.cwd(), outDir));
if (has('--kill')) {
  await killGameAndWait();
  log('game closed');
}
