// Harvest what the running game actually registered for the test packs, so the asset
// library quotes the game instead of the mod source.
//
//   node tools/harvest.mjs [--out <dir>] [--port 47901]
//
// Writes (default out = _re/assets):
//   relic/mod-items.tsv      every item id >= 900000 the game knows (relics + tablets + potions)
//   weapon/mod-weapons.tsv   every weapon id >= 900000 with its fire data summary
//   relic/mod-l10n.tsv       every mod text key with zh-CN / en-US / ja-JP / ko-KR
//   relic/mod-harvest.json   raw summary (counts, anomalies)
import fs from 'node:fs';
import path from 'node:path';
import { REPO } from './runner/lib.mjs';

const args = process.argv.slice(2);
const argOf = (name, fallback) => {
  const i = args.indexOf(name);
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback;
};
const PORT = Number(argOf('--port', process.env.SEPHIRIA_PORT || 47901));
const OUT = path.resolve(argOf('--out', path.join(REPO, '..', '..', '_re', 'assets')));
const BASE = `http://127.0.0.1:${PORT}`;
const LANGS = ['zh-CN', 'en-US', 'ja-JP', 'ko-KR'];
const PREFIXES = ['TagWeave', 'Forge', 'Apex', 'Brew', 'Blade', 'Mixed', 'Vital', 'Shop', 'Status', 'Effect', 'Panel', 'Enhance', 'RelicLab', 'Curio'];

async function get(pathname) {
  const res = await fetch(BASE + pathname);
  if (!res.ok) throw new Error(`${pathname} -> HTTP ${res.status}`);
  return res.json();
}

const tsv = (rows) => rows.map((r) => r.map((v) => String(v ?? '').replace(/\t/g, ' ').replace(/\r?\n/g, ' ')).join('\t')).join('\n') + '\n';

const relics = await get('/relics?limit=600');
const weapons = await get('/weapons?limit=400');
const moddata = await get('/moddata?min=900000&limit=400');

const items = (relics.rows || []).filter((r) => r.id >= 900000);
const wrows = (weapons.items || []).filter((w) => (w.id || 0) >= 900000);

const itemRows = [['id', 'name', 'type', 'rarity', 'cost', 'categories', 'prefab', 'className', 'maxLevel', 'unique', 'effectCount']];
for (const r of items) {
  itemRows.push([r.id, r.name, r.itemType, r.rarity, r.cost, (r.categories || []).join('+'), r.prefab, r.className, r.maxLevel, r.isUniqueEffect, r.effectStringCount]);
}

const weaponRows = [['id', 'name', 'weaponType', 'prefab', 'component', 'enhanceFromId', 'enhancesTo', 'attackWeightPerSwing', 'isRanged', 'finalComboIdx', 'basic', 'dash', 'special']];
for (const w of wrows) {
  weaponRows.push([w.id, w.name, w.weaponType, w.prefab, w.component, w.enhanceFromId, (w.enhancesTo || []).join(','), w.attackWeightPerSwing, w.isRangedWeapon, w.finalComboIdx, (w.basic || []).length, (w.dash || []).length, (w.special || []).length]);
}

const l10n = new Map();
for (const prefix of PREFIXES) {
  for (const lang of LANGS) {
    let res;
    try { res = await get(`/l10n?prefix=${encodeURIComponent(prefix)}&lang=${encodeURIComponent(lang)}&limit=400`); } catch { continue; }
    for (const k of res.keys || []) {
      if (!l10n.has(k.key)) l10n.set(k.key, {});
      l10n.get(k.key)[lang] = k.value;
    }
  }
}
const l10nRows = [['key', ...LANGS]];
for (const [key, vals] of [...l10n.entries()].sort((a, b) => a[0].localeCompare(b[0]))) {
  l10nRows.push([key, ...LANGS.map((l) => vals[l] ?? '')]);
}

const missing = l10nRows.slice(1).filter((r) => LANGS.some((_, i) => !r[i + 1]));
const anomalies = [];
for (const r of items) {
  if (!r.name || /^[A-Za-z_]+$/.test(r.name)) anomalies.push(`item ${r.id}: name looks like a raw key (${r.name})`);
  if (!r.cost) anomalies.push(`item ${r.id}: cost 0`);
  if (!(r.categories || []).length) anomalies.push(`item ${r.id}: no categories`);
}
for (const w of wrows) {
  if (!w.name || /^[A-Za-z_]+$/.test(w.name)) anomalies.push(`weapon ${w.id}: name looks like a raw key (${w.name})`);
}

fs.mkdirSync(path.join(OUT, 'relic'), { recursive: true });
fs.mkdirSync(path.join(OUT, 'weapon'), { recursive: true });
fs.writeFileSync(path.join(OUT, 'relic', 'mod-items.tsv'), tsv(itemRows));
fs.writeFileSync(path.join(OUT, 'weapon', 'mod-weapons.tsv'), tsv(weaponRows));
fs.writeFileSync(path.join(OUT, 'relic', 'mod-l10n.tsv'), tsv(l10nRows));
fs.writeFileSync(path.join(OUT, 'relic', 'mod-harvest.json'), JSON.stringify({
  at: new Date().toISOString(),
  port: PORT,
  relics: { total: relics.items, rows: (relics.rows || []).length, mod: items.length },
  weapons: { total: (weapons.items || []).length, mod: wrows.length },
  moddata: { count: moddata.count, scanned: moddata.scanned },
  l10n: { keys: l10n.size, missingInSomeLanguage: missing.length },
  anomalies,
}, null, 2) + '\n');

console.log(`items ${items.length} / weapons ${wrows.length} / l10n keys ${l10n.size} (missing ${missing.length})`);
console.log(`wrote ${path.join(OUT, 'relic', 'mod-items.tsv')}`);
console.log(`wrote ${path.join(OUT, 'weapon', 'mod-weapons.tsv')}`);
console.log(`wrote ${path.join(OUT, 'relic', 'mod-l10n.tsv')}`);
if (anomalies.length) console.log('anomalies:\n  ' + anomalies.join('\n  '));
