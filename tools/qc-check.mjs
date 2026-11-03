#!/usr/bin/env node
// Static hygiene checks for mods under work/. Fast (no game, no dotnet).
//
//   node tools/qc-check.mjs work/tag-weave
//   node tools/qc-check.mjs --all
//   node tools/qc-check.mjs --all --json
//
// Checks: metadata shape, csproj <-> metadata agreement, Localization shipped inside dist,
// four-language key parity, 9xxxxx id collisions between mods, source hygiene,
// and regression suites that assert absolute numbers without isolateAddons + newWorld.
// Exit code 1 only on errors; warnings are advisory.

import fs from 'node:fs';
import path from 'node:path';

const REPO = path.resolve(path.dirname(new URL(import.meta.url).pathname).replace(/^\/([A-Za-z]:)/, '$1'), '..');
const WORK = path.join(REPO, 'work');
const args = process.argv.slice(2);
const json = args.includes('--json');
const all = args.includes('--all');
const targets = args.filter((a) => !a.startsWith('--'));

const REQUIRED_KEYS = ['modName', 'modVersion', 'modAuthor', 'dllFile', 'entryClass'];
const LANGS = ['zh-CN', 'en-US', 'ja-JP', 'ko-KR'];

const read = (p) => fs.readFileSync(p, 'utf8');
const readJson = (p) => JSON.parse(read(p));
const listDirs = (p) => fs.readdirSync(p, { withFileTypes: true }).filter((e) => e.isDirectory()).map((e) => e.name);
const listFiles = (dir, ext) => {
  if (!fs.existsSync(dir)) return [];
  const out = [];
  const walk = (d) => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const p = path.join(d, e.name);
      if (e.isDirectory()) walk(p);
      else if (p.endsWith(ext)) out.push(p);
    }
  };
  walk(dir);
  return out;
};

// mod folder -> { errors, warnings, ids }
const mods = new Map();
const ensure = (name) => {
  if (!mods.has(name)) mods.set(name, { errors: [], warnings: [], ids: new Map() });
  return mods.get(name);
};

const dirs = all
  ? listDirs(WORK).filter((d) => fs.existsSync(path.join(WORK, d, 'metadata.json')))
  : targets.map((t) => t.replace(/^work[\\/]/, ''));

if (!dirs.length) {
  console.error('usage: node tools/qc-check.mjs <mod> | --all [--json]');
  process.exit(2);
}

for (const name of dirs) {
  const dir = path.join(WORK, name);
  const r = ensure(name);
  const err = (m) => r.errors.push(m);
  const warn = (m) => r.warnings.push(m);

  let meta = null;
  try { meta = readJson(path.join(dir, 'metadata.json')); } catch (e) { err('metadata.json unreadable: ' + e.message); }

  if (meta) {
    const keys = Object.keys(meta);
    const missing = REQUIRED_KEYS.filter((k) => !(k in meta));
    const extra = keys.filter((k) => !REQUIRED_KEYS.includes(k));
    if (missing.length) err('metadata.json missing key(s): ' + missing.join(', '));
    if (extra.length) err('metadata.json extra key(s): ' + extra.join(', '));
    for (const k of REQUIRED_KEYS) {
      if (k in meta && (typeof meta[k] !== 'string' || !meta[k].trim())) err(`metadata.${k} must be a non-empty string`);
    }
  }

  const csprojs = listFiles(path.join(dir, 'src'), '.csproj');
  if (csprojs.length !== 1) err(`expected exactly one .csproj in src/, found ${csprojs.length}`);
  let csproj = '';
  if (csprojs.length === 1) {
    csproj = read(csprojs[0]);
    const asm = /<AssemblyName>([^<]+)<\/AssemblyName>/.exec(csproj);
    if (!asm) err('csproj has no <AssemblyName>');
    else if (meta && meta.dllFile && `${asm[1]}.dll` !== meta.dllFile) {
      err(`dllFile ${meta.dllFile} != AssemblyName ${asm[1]}.dll`);
    }
  }

  // entryClass = Namespace.Class must exist in src
  if (meta && typeof meta.entryClass === 'string') {
    const [ns, cls] = meta.entryClass.split('.');
    const src = listFiles(path.join(dir, 'src'), '.cs').map(read).join('\n');
    if (!cls || !new RegExp(`class\\s+${cls.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`).test(src)) {
      err(`entryClass class ${cls} not found in src/*.cs`);
    } else if (ns && !new RegExp(`namespace\\s+${ns.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`).test(src)) {
      err(`entryClass namespace ${ns} not found in src/*.cs`);
    }
  }

  // Localization must ship inside dist (csproj ItemGroup) and keep four languages in parity
  const locDir = path.join(dir, 'Localization');
  if (fs.existsSync(locDir)) {
    if (!/CopyToOutputDirectory/.test(csproj) || !/LinkBase\s*=\s*"Localization"/.test(csproj)) {
      err('Localization/ exists but csproj does not copy it into dist (None Include + LinkBase="Localization" + CopyToOutputDirectory)');
    }
    const files = LANGS.map((l) => path.join(locDir, l + '.json'));
    const missingLangs = LANGS.filter((l) => !fs.existsSync(path.join(locDir, l + '.json')));
    if (missingLangs.length) err('Localization missing language file(s): ' + missingLangs.join(', '));
    const maps = {};
    for (const f of files) {
      if (!fs.existsSync(f)) continue;
      try { maps[path.basename(f, '.json')] = readJson(f); } catch (e) { err(`Localization/${path.basename(f)} invalid JSON: ${e.message}`); }
    }
    const base = Object.keys(maps[LANGS[0]] || {});
    for (const l of LANGS.slice(1)) {
      const m = maps[l];
      if (!m) continue;
      const missing = base.filter((k) => !(k in m));
      const extra = Object.keys(m).filter((k) => !base.includes(k));
      if (missing.length) err(`Localization/${l}.json missing ${missing.length} key(s), e.g. ${missing.slice(0, 3).join(', ')}`);
      if (extra.length) err(`Localization/${l}.json has ${extra.length} extra key(s), e.g. ${extra.slice(0, 3).join(', ')}`);
    }
  }

  // ids: every 9xxxxx literal in src or suite belongs to this mod; collisions are errors
  const idSources = [...listFiles(path.join(dir, 'src'), '.cs'), ...listFiles(path.join(dir, 'tests'), '.json')];
  for (const f of idSources) {
    const text = read(f);
    for (const m of text.matchAll(/\b(9\d{5})\b/g)) {
      const id = m[1];
      if (!r.ids.has(id)) r.ids.set(id, new Set());
      r.ids.get(id).add(path.basename(f));
    }
  }

  // source hygiene
  const cs = listFiles(path.join(dir, 'src'), '.cs').map((f) => ({ f, text: read(f) }));
  const joined = cs.map((c) => c.text).join('\n');
  if (/PlayerPrefs/.test(joined)) err('src uses PlayerPrefs (saves must not be written)');
  if (/effectsString\s*=\s*"/.test(joined)) warn('src writes a literal effectsString; let RelicKit generate it from EffectKeys');
  if (/Instantiate\s*\([^)]*[Pp]refab\b/.test(joined)) warn('src instantiates a prefab; cloning a NetworkIdentity prefab breaks Mirror spawn — use item.resourcePrefab');
  if (cs.some((c) => /class RelicKit\b/.test(c.text)) && !/RelicKit\.Log\s*=\s*Log/.test(joined)) {
    warn('RelicKit present but `RelicKit.Log = Log;` not assigned (mod logs stay silent)');
  }

  // suites asserting absolute numbers need isolation + a fresh world
  for (const f of listFiles(path.join(dir, 'tests'), '.json')) {
    let suite = null;
    try { suite = readJson(f); } catch { continue; }
    const steps = Array.isArray(suite.steps) ? suite.steps : [];
    const hasIso = steps.some((s) => s && typeof s.isolateAddons === 'string');
    const hasNew = steps.some((s) => s && s.newWorld);
    const numeric = steps.some((s) => s && s.expect && typeof s.expect.value === 'number');
    if (numeric && (!hasIso || !hasNew)) {
      warn(`${path.basename(f)} asserts numeric values without both isolateAddons and newWorld (other packs/old saves pollute absolute numbers)`);
    }
  }
}

// cross-mod id collisions
const owner = new Map();
for (const [name, r] of mods) {
  for (const id of r.ids.keys()) {
    if (!owner.has(id)) owner.set(id, []);
    owner.get(id).push(name);
  }
}
for (const [id, names] of owner) {
  if (names.length > 1) for (const n of names) ensure(n).errors.push(`id ${id} also used by ${names.filter((x) => x !== n).join(', ')}`);
}

const rows = [];
let errors = 0;
let warnings = 0;
for (const [name, r] of mods) {
  errors += r.errors.length;
  warnings += r.warnings.length;
  rows.push({ name, errors: r.errors, warnings: r.warnings, ids: r.ids.size });
}

if (json) {
  console.log(JSON.stringify({ repo: REPO, rows, errors, warnings }, null, 2));
} else {
  for (const row of rows) {
    if (!row.errors.length && !row.warnings.length) {
      console.log(`ok    ${row.name}  (${row.ids} id(s))`);
      continue;
    }
    console.log(`${row.errors.length ? 'FAIL' : 'warn'}  ${row.name}  (${row.ids} id(s))`);
    for (const e of row.errors) console.log(`      error: ${e}`);
    for (const w of row.warnings) console.log(`      warn:  ${w}`);
  }
  console.log(`\n${rows.length} mod(s), ${errors} error(s), ${warnings} warning(s)`);
}
process.exit(errors ? 1 : 0);
