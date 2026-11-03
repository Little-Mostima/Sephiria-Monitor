// e_validation: verify generated floor stitching over every procedural floor.
// Model (from EnhancedProceduralFloorGenerator.cs:1308,1361-1404):
//   a passage bit opens into the adjacent cell; if that cell holds a room the two rooms
//   connect directly, otherwise the generator carves a corridor up to 14 cells deep
//   (int num = 14) in that direction, so a room within 14 cells along the ray connects too.
import fs from 'node:fs';
import path from 'node:path';
import { sleep, REPO } from './lib.mjs';

const BASE = 'http://127.0.0.1:47901';
const j = async (p) => (await fetch(BASE + p)).json();

const DIRS = {
  ONE: ['U', 'R', 'D', 'L'],
  TWO_H: ['U', 'U', 'R', 'D', 'D', 'L'],
  TWO_V: ['U', 'R', 'R', 'D', 'L', 'L'],
  FOUR: ['U', 'U', 'R', 'R', 'D', 'D', 'L', 'L'],
};
const STEP = { U: [0, 1], R: [1, 0], D: [0, -1], L: [-1, 0] };
const CORRIDOR_DEPTH = 14;

function checkFloor(rooms, roads) {
  const all = [...rooms, ...roads];
  const res = { rooms: rooms.length, roads: roads.length, bits: 0, direct: 0, viaCorridor: 0, deadEnds: 0, asymmetric: [], overlaps: [], components: [], problems: [] };
  if (!all.length) { res.problems.push('empty'); return res; }
  const byCell = new Map();
  for (const r of all) byCell.set(r.gridPos.join(','), r);

  for (let i = 0; i < all.length; i++) {
    for (let k = i + 1; k < all.length; k++) {
      const a = all[i].rect, b = all[k].rect;
      if (a[0] < b[2] && b[0] < a[2] && a[1] < b[3] && b[1] < a[3]) res.overlaps.push(`${all[i].name} / ${all[k].name}`);
    }
  }

  const adj = new Map(all.map((r) => [r.gridPos.join(','), new Set()]));
  const link = (a, b) => { adj.get(a).add(b); adj.get(b).add(a); };

  for (const r of all) {
    const dirs = DIRS[r.type] || [];
    const self = r.gridPos.join(',');
    for (let b = 0; b < 8; b++) {
      if (!(r.passage & (1 << b))) continue;
      res.bits++;
      const d = dirs[b];
      if (!d) { res.problems.push(`${r.name}: bit${b} no direction for ${r.type}`); continue; }
      const [dx, dy] = STEP[d];
      let hit = null, dist = 0;
      for (let k = 1; k <= CORRIDOR_DEPTH; k++) {
        const key = [r.gridPos[0] + dx * k, r.gridPos[1] + dy * k].join(',');
        if (byCell.has(key)) { hit = byCell.get(key); dist = k; break; }
      }
      if (!hit) { res.deadEnds++; continue; }
      if (dist === 1) res.direct++; else res.viaCorridor++;
      link(self, hit.gridPos.join(','));
      // symmetry: does the neighbour open back toward this room?
      const back = DIRS[hit.type] || [];
      let opensBack = false;
      for (let c = 0; c < 8; c++) {
        if (!(hit.passage & (1 << c))) continue;
        const nd = back[c];
        if (!nd) continue;
        const [nx, ny] = STEP[nd];
        for (let k = 1; k <= CORRIDOR_DEPTH; k++) {
          if (hit.gridPos[0] + nx * k === r.gridPos[0] && hit.gridPos[1] + ny * k === r.gridPos[1]) { opensBack = true; break; }
        }
        if (opensBack) break;
      }
      if (!opensBack) res.asymmetric.push(`${r.name}(${d}) -> ${hit.name} (no return passage)`);
    }
  }

  const seen = new Set();
  for (const r of all) {
    const k0 = r.gridPos.join(',');
    if (seen.has(k0)) continue;
    const comp = [];
    const q = [k0];
    seen.add(k0);
    while (q.length) {
      const cur = q.shift();
      comp.push(byCell.get(cur).name);
      for (const n of adj.get(cur) || []) if (!seen.has(n)) { seen.add(n); q.push(n); }
    }
    res.components.push(comp);
  }
  res.components.sort((a, b) => b.length - a.length);
  return res;
}

const all = await j('/floors');
const names = (all.floors || all.items || []).map((f) => f.name).filter(Boolean);
const out = { startedAt: new Date().toISOString(), floorCount: names.length, floors: [] };
const seenFloors = new Set();
for (const f of names) {
  const g = await j(`/goto?floor=${encodeURIComponent(f)}`);
  await sleep(2200);
  const r = await j('/rooms');
  if (!r.rooms) continue;
  if (seenFloors.has(r.floor)) continue;
  seenFloors.add(r.floor);
  const res = checkFloor(r.rooms, r.roads || []);
  out.floors.push({ requested: f, floor: r.floor, moved: g.moved !== false, ...res });
  console.log(String(r.floor).padEnd(30), 'rooms', String(res.rooms).padStart(2), 'bits', String(res.bits).padStart(2), 'direct', String(res.direct).padStart(2), 'corr', res.viaCorridor, 'dead', res.deadEnds, 'asym', res.asymmetric.length, 'comps', res.components.map((c) => c.length).join('+'), 'overlap', res.overlaps.length);
}
const dir = path.join(REPO, 'work', '_runs', 'floorstitch-' + new Date().toISOString().slice(0, 10));
fs.mkdirSync(dir, { recursive: true });
fs.writeFileSync(path.join(dir, 'floorstitch.json'), JSON.stringify(out, null, 1));
console.log('saved', path.join(dir, 'floorstitch.json'));
