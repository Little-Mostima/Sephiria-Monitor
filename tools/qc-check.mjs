#!/usr/bin/env node
// Static hygiene checks for mods under work/. Fast (no game, no dotnet).
//
//   node tools/qc-check.mjs work/tag-weave
//   node tools/qc-check.mjs --all
//   node tools/qc-check.mjs --all --json
//   node tools/qc-check.mjs --all --fix   (insert isolateAddons + newWorld into flagged suites)
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
const fix = args.includes('--fix');
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

// A target is either a name under work/ or any path (preflight passes work/<mod> too). Cross-mod id
// collisions need every sibling that carries a metadata.json, otherwise "one mod at a time" silently
// loses the only check that looks outside the folder.
const peers = listDirs(WORK)
  .filter((d) => fs.existsSync(path.join(WORK, d, 'metadata.json')))
  .map((d) => ({ name: d, dir: path.join(WORK, d) }));
const requested = all
  ? peers
  : targets.map((t) => {
      // A bare name means work/<name> (the old contract); a path is taken as given.
      const abs = path.isAbsolute(t) ? t : path.resolve(REPO, t);
      const dir = fs.existsSync(abs) ? abs : path.join(WORK, t);
      return { name: path.basename(dir), dir };
    });
const reportNames = new Set(requested.map((x) => x.name));
const scan = [...peers];
for (const x of requested) if (!scan.some((y) => y.dir === x.dir)) scan.push(x);

if (!requested.length) {
  console.error('usage: node tools/qc-check.mjs <mod> | --all [--json]');
  process.exit(2);
}

for (const { name, dir } of scan) {
  const r = ensure(name);
  const err = (m) => r.errors.push(m);
  const warn = (m) => r.warnings.push(m);

  let meta = null;
  try { meta = readJson(path.join(dir, 'metadata.json')); } catch (e) { err('metadata.json unreadable: ' + e.message); }

  // A content-only package (translations / data, no code) legitimately has no csproj and no dllFile.
  const csprojs = listFiles(path.join(dir, 'src'), '.csproj');
  const contentOnly = csprojs.length === 0;
  const keysNeeded = contentOnly ? ['modName', 'modVersion', 'modAuthor'] : REQUIRED_KEYS;

  if (meta) {
    const keys = Object.keys(meta);
    const missing = keysNeeded.filter((k) => !(k in meta));
    const extra = keys.filter((k) => !keysNeeded.includes(k));
    if (missing.length) err('metadata.json missing key(s): ' + missing.join(', '));
    if (extra.length) err('metadata.json extra key(s): ' + extra.join(', '));
    for (const k of keysNeeded) {
      if (k in meta && (typeof meta[k] !== 'string' || !meta[k].trim())) err(`metadata.${k} must be a non-empty string`);
    }
  }

  if (!contentOnly && csprojs.length !== 1) err(`expected exactly one .csproj in src/, found ${csprojs.length}`);
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

  // suites asserting absolute stat numbers need isolation + a fresh world
  const STAT_PATH = /^\/(state|cstat|combat|damage|stats)\b/i;
  let deployFolder = null;
  try {
    const mj = readJson(path.join(dir, 'mod.json'));
    const addon = (mj.deploy && mj.deploy.addons && mj.deploy.addons[0]) || null;
    deployFolder = addon && addon.folder ? addon.folder : null;
  } catch { /* no mod.json */ }
  for (const f of listFiles(path.join(dir, 'tests'), '.json')) {
    let suite = null;
    try { suite = readJson(f); } catch { continue; }
    const steps = Array.isArray(suite.steps) ? suite.steps : [];
    const hasIso = steps.some((s) => s && typeof s.isolateAddons === 'string');
    const hasNew = steps.some((s) => s && s.newWorld);
    const statNumeric = steps.some((s) => s && s.expect && typeof s.expect.value === 'number' && STAT_PATH.test(String(s.expect.path || '')));
    if (!statNumeric || (hasIso && hasNew)) continue;
    if (!fix) {
      warn(`${path.basename(f)} asserts absolute stat numbers without both isolateAddons and newWorld (other packs/old saves pollute them)`);
      continue;
    }
    if (!deployFolder) { warn(`${path.basename(f)} needs isolation but mod.json has no deploy folder`); continue; }
    const lines = read(f).split(/\r?\n/);
    const stepEnd = (idx) => {
      let start = idx;
      while (start > 0 && !lines[start].includes('{')) start--;
      let depth = 0;
      for (let i = start; i < lines.length; i++) {
        for (const ch of lines[i]) {
          if (ch === '{') depth++;
          else if (ch === '}') depth--;
        }
        if (depth === 0) return i;
      }
      return idx;
    };
    const stepsIdx = lines.findIndex((l) => /"steps"\s*:\s*\[/.test(l));
    if (stepsIdx < 0) { warn(`${path.basename(f)} has no steps array`); continue; }
    const firstStep = lines.slice(stepsIdx + 1).find((l) => l.trim().startsWith('{'));
    const indent = firstStep ? /^\s*/.exec(firstStep)[0] : '    ';
    // isolateAddons goes first (before deploy/install); newWorld goes after the last restart
    // so a later restart cannot drop back into the old save.
    const restartIdxs = lines.map((l, i) => (/"restart"\s*:\s*true/.test(l) ? i : -1)).filter((i) => i >= 0);
    const newAt = restartIdxs.length ? stepEnd(restartIdxs[restartIdxs.length - 1]) + 1 : stepsIdx + 1;
    const inserts = [];
    if (!hasIso) inserts.push({ at: stepsIdx + 1, text: `${indent}{ "isolateAddons": "${deployFolder}" },` });
    if (!hasNew) inserts.push({ at: newAt, text: `${indent}{ "newWorld": { "slot": 2 } },` });
    inserts.sort((a, b) => b.at - a.at);
    for (const ins of inserts) lines.splice(ins.at, 0, ins.text);
    const next = lines.join('\n');
    try { JSON.parse(next); } catch (e) { warn(`${path.basename(f)} fix would break JSON: ${e.message}`); continue; }
    fs.writeFileSync(f, next);
    console.log(`fixed ${path.relative(REPO, f)} (+${inserts.length} step(s))`);
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
  if (!reportNames.has(name)) continue;
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
