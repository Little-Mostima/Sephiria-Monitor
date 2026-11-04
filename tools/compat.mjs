#!/usr/bin/env node
// Mod compatibility: static conflict scan + pairwise runtime matrix + full coexistence.
//   node tools/compat.mjs [--static] [--pairs] [--all] [--json]
import fs from 'node:fs';
import path from 'node:path';
import {
  REPO, WORK, sleep, getJson, installPackage, removePackage,
  killGameAndWait, launchGame, waitBridge,
} from './runner/lib.mjs';
import { enterWorld } from './session.mjs';

const args = process.argv.slice(2);
const flag = (f) => args.includes(f);
const anyMode = flag('--static') || flag('--pairs') || flag('--all');
const doStatic = flag('--static') || !anyMode;
const doPairs = flag('--pairs');
const doFull = flag('--all');
const JSON_OUT = flag('--json');

function idsOf(cs) {
  const set = new Set();
  for (const m of cs.matchAll(/\b9\d{5}\b/g)) set.add(m[0]);
  return [...set].sort();
}

function discover() {
  const out = [];
  for (const name of fs.readdirSync(WORK)) {
    const dir = path.join(WORK, name);
    if (name.startsWith('_') || !fs.statSync(dir).isDirectory()) continue;
    const metaPath = path.join(dir, 'metadata.json');
    if (!fs.existsSync(metaPath)) continue;
    const meta = JSON.parse(fs.readFileSync(metaPath, 'utf8').replace(/^\uFEFF/, ''));
    const wsPath = path.join(dir, 'workshop.json');
    const ws = fs.existsSync(wsPath) ? JSON.parse(fs.readFileSync(wsPath, 'utf8').replace(/^\uFEFF/, '')) : null;
    const srcDir = path.join(dir, 'src');
    let cs = '';
    if (fs.existsSync(srcDir)) {
      for (const f of fs.readdirSync(srcDir)) if (f.endsWith('.cs')) cs += fs.readFileSync(path.join(srcDir, f), 'utf8') + '\n';
    }
    const zip = ws ? path.join(REPO, 'dist', 'workshop', `${ws.modId}-${meta.modVersion}.zip`) : null;
    const compatPath = path.join(dir, 'compat.json');
    const extra = fs.existsSync(compatPath) ? JSON.parse(fs.readFileSync(compatPath, 'utf8')) : {};
    out.push({ name, dir, meta, ws, cs, zip: zip && fs.existsSync(zip) ? zip : null, marker: extra.marker || null, ids: idsOf(cs) });
  }
  return out.sort((a, b) => a.name.localeCompare(b.name));
}

const HARMONY = /\[HarmonyPatch\(\s*typeof\(\s*([\w.]+)\s*\)\s*,\s*(?:"([^"]+)"|nameof\(\s*([\w.]+)\s*\))/g;
const SAVEKEY = /\.(?:Set|Get)(?:String|Int|Float|Bool|Long)\(\s*"([^"]+)"/g;

function staticScan(mods) {
  const buckets = { id: new Map(), assembly: new Map(), entry: new Map(), harmony: new Map(), saveKey: new Map() };
  const push = (bucket, key, name) => {
    if (!buckets[bucket].has(key)) buckets[bucket].set(key, new Set());
    buckets[bucket].get(key).add(name);
  };
  for (const m of mods) {
    for (const id of m.ids) push('id', id, m.name);
    if (m.meta.dllFile) push('assembly', m.meta.dllFile.toLowerCase(), m.name);
    if (m.meta.entryClass) push('entry', m.meta.entryClass, m.name);
    for (const h of m.cs.matchAll(HARMONY)) push('harmony', `${h[1]}.${h[2] || h[3]}`, m.name);
    for (const k of m.cs.matchAll(SAVEKEY)) push('saveKey', k[1], m.name);
  }
  const conflicts = [];
  const kinds = [['id', 'content id', true], ['assembly', 'assembly name', true], ['entry', 'entryClass', true], ['harmony', 'Harmony patch target', true], ['saveKey', 'save key', false]];
  for (const [bucket, label, hard] of kinds) {
    for (const [key, names] of buckets[bucket]) {
      if (names.size > 1) conflicts.push({ kind: label, hard, key, mods: [...names].sort() });
    }
  }
  return { buckets, conflicts };
}

function printStatic(mods, res) {
  console.log('MODS');
  for (const m of mods) console.log(`  ${m.name.padEnd(16)} ids=${(m.ids.join(',') || '-').padEnd(34)} zip=${m.zip ? 'yes' : 'no '}`);
  const hard = res.conflicts.filter((c) => c.hard);
  const soft = res.conflicts.filter((c) => !c.hard);
  console.log(`\nCONFLICTS  hard=${hard.length}  warn=${soft.length}`);
  for (const c of [...hard, ...soft]) {
    console.log(`  ${(c.hard ? 'FAIL' : 'warn').padEnd(4)} ${c.kind.padEnd(22)} ${c.key.padEnd(30)} ${c.mods.join(' + ')}`);
  }
  if (!hard.length) console.log('  none - no two mods claim the same id / assembly / entryClass / patch target');
  return hard.length === 0;
}

const DEFAULT_MARKERS = {
  'curio-pack': '[CurioPack] registered potion 900010',
  'runsave-guard': '[RunSaveGuard]',
  'data-consumer': '[DataConsumer] packages=',
  'data-probe': 'Data Probe',
  'l10n-probe': 'Localization Probe',
  'costume-probe': '[WorkshopCostume] ✓ Costume Probe',
  'engraving-fix': '[EngravingFix]',
};
const BAD = /Exception|NullReference|어셈블리 이름 충돌|모듈 로드 실패|Failed to load|failed to activate/i;
const OURS = /CurioPack|RunSaveGuard|DataConsumer|EngravingFix|Data Probe|Localization Probe|Costume Probe|WorkshopModule|WorkshopModRuntime|AddOnLoader/i;

function logLines(json) {
  const arr = Array.isArray(json) ? json : json.lines || [];
  return arr.map((l) => (typeof l === 'string' ? l : l.msg || l.message || ''));
}

async function combo(mods) {
  for (const m of mods) if (m.ws) removePackage(m.ws.modId);
  const installed = [];
  for (const m of mods) if (m.zip) installed.push(installPackage(m.zip).id);
  killGameAndWait();
  launchGame({ hidden: !process.env.SEPHIRIA_VISIBLE });
  await waitBridge(120000);
  try {
    await enterWorld({ timeoutMs: 120000 });
  } catch (e) {
    const lines = logLines(await getJson('/logs?n=300').catch(() => ({})));
    killGameAndWait();
    for (const id of installed) removePackage(id);
    return { ok: false, stage: 'world', missing: [], errors: lines.filter((l) => BAD.test(l)).slice(-5) };
  }
  await sleep(6000);
  const lines = logLines(await getJson('/logs?n=1500').catch(() => ({})));
  const hay = lines.join('\n');
  const missing = [];
  for (const m of mods) {
    const marker = m.marker || DEFAULT_MARKERS[m.name];
    if (!marker) continue;
    const hit = marker.startsWith('/') ? new RegExp(marker.slice(1, -1)).test(hay) : hay.includes(marker);
    if (!hit) missing.push(`${m.name} (${marker})`);
  }
  const errors = lines.filter((l) => OURS.test(l) && BAD.test(l)).slice(0, 5);
  killGameAndWait();
  for (const id of installed) removePackage(id);
  return { ok: missing.length === 0 && errors.length === 0, stage: 'ready', missing, errors, lines: lines.length };
}

(async () => {
  const mods = discover();
  const report = { at: new Date().toISOString(), mods: mods.map((m) => m.name) };
  let ok = true;
  if (doStatic) {
    const stat = staticScan(mods);
    ok = printStatic(mods, stat) && ok;
    report.static = stat.conflicts;
  }
  if (doPairs) {
    const usable = mods.filter((m) => m.zip);
    console.log(`\nPAIRS  ${usable.length} packaged mods -> ${(usable.length * (usable.length - 1)) / 2} combos`);
    const results = [];
    for (let i = 0; i < usable.length; i++) {
      for (let j = i + 1; j < usable.length; j++) {
        const pair = [usable[i], usable[j]];
        const t0 = Date.now();
        let res;
        try { res = await combo(pair); } catch (e) { res = { ok: false, error: String(e.message || e) }; }
        const secs = ((Date.now() - t0) / 1000).toFixed(0);
        results.push({ pair: pair.map((p) => p.name), ...res });
        const detail = res.ok ? '' : JSON.stringify({ missing: res.missing, errors: res.errors, error: res.error }).slice(0, 200);
        console.log(`  ${(res.ok ? 'PASS' : 'FAIL').padEnd(4)} ${pair.map((p) => p.name).join(' + ').padEnd(32)} ${secs}s ${detail}`);
        ok = res.ok && ok;
      }
    }
    report.pairs = results;
  }
  if (doFull) {
    const usable = mods.filter((m) => m.zip);
    console.log(`\nFULL  all ${usable.length} packaged mods together`);
    const t0 = Date.now();
    let res;
    try { res = await combo(usable); } catch (e) { res = { ok: false, error: String(e.message || e) }; }
    console.log(`  ${res.ok ? 'PASS' : 'FAIL'}  ${((Date.now() - t0) / 1000).toFixed(0)}s ${JSON.stringify({ missing: res.missing, errors: res.errors, error: res.error }).slice(0, 300)}`);
    report.full = res;
    ok = res.ok && ok;
    if (!res.ok) {
      console.log('  isolating the culprit - remove one mod at a time:');
      for (const drop of usable) {
        const subset = usable.filter((m) => m.name !== drop.name);
        let r;
        try { r = await combo(subset); } catch (e) { r = { ok: false, error: String(e.message || e) }; }
        console.log(`    without ${drop.name.padEnd(16)} -> ${r.ok ? 'PASS (culprit)' : 'FAIL'}`);
        if (r.ok) { report.culprit = drop.name; break; }
      }
    }
  }
  const dir = path.join(WORK, '_compat', new Date().toISOString().replace(/[:.]/g, '-'));
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, 'report.json'), JSON.stringify(report, null, 2));
  console.log(`\nreport: ${dir}`);
  if (JSON_OUT) console.log(JSON.stringify(report));
  process.exit(ok ? 0 : 1);
})().catch((e) => { console.error(e); process.exit(2); });