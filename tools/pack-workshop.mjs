#!/usr/bin/env node
// Pack an AddOns mod folder into a Workshop package (nested metadata schema) + zip.
//
//   node tools/pack-workshop.mjs work/runsave-guard [--out <dir>] [--no-zip]
//
// Inputs
//   work/<mod>/workshop.json   ← title/description/tags/modId/author (+ optional preview file name)
//   work/<mod>/metadata.json   ← flat AddOns metadata (modName/modVersion/modAuthor/dllFile/entryClass)
//   work/<mod>/dist/*          ← built dll (+ anything else that belongs in the package)
//   work/<mod>/Localization/   ← optional, copied as a localization module
//
// Output: <out>/<modId>/metadata.json + dll + optional Localization/ + thumbnail, and <modId>-<version>.zip
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const args = process.argv.slice(2);
const modDirArg = args.find((a) => !a.startsWith('--'));
if (!modDirArg) {
  console.error('usage: node tools/pack-workshop.mjs work/<mod> [--out <dir>] [--no-zip]');
  process.exit(2);
}
const noZip = args.includes('--no-zip');
const outIdx = args.indexOf('--out');
const REPO = path.resolve(path.dirname(new URL(import.meta.url).pathname).replace(/^\/([A-Za-z]:)/, '$1'), '..');
const modDir = path.resolve(modDirArg);
const outRoot = outIdx >= 0 ? path.resolve(args[outIdx + 1]) : path.join(REPO, 'dist', 'workshop');

const readJson = (p) => JSON.parse(fs.readFileSync(p, 'utf8'));
const fail = (msg) => { console.error('pack: ' + msg); process.exit(1); };

const meta = readJson(path.join(modDir, 'metadata.json'));
const ws = readJson(path.join(modDir, 'workshop.json'));
const distDir = path.join(modDir, 'dist');
// A package may be localization-only (no dll): then metadata.json has no dllFile/entryClass.
const dll = meta.dllFile ? path.join(distDir, meta.dllFile) : null;
if (dll && !fs.existsSync(dll)) fail(`missing ${dll} - build the mod first`);

const modId = ws.modId || `com.mostima.${meta.modName.toLowerCase()}`;
const entryClass = meta.entryClass;
if (dll && !/^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$/.test(entryClass || '')) fail(`bad entryClass "${entryClass}"`);

const pkg = path.join(outRoot, modId);
fs.rmSync(pkg, { recursive: true, force: true });
fs.mkdirSync(pkg, { recursive: true });
if (dll) fs.copyFileSync(dll, path.join(pkg, meta.dllFile));

// Review policy (team-horay/sephiria_modding docs/zh-CN/publishing.md): every file in the folder is
// reviewed, so build intermediates and copies of game assemblies must never ship.
const FORBIDDEN_FILES = ['.pdb', '.deps.json'];
const GAME_ASSEMBLIES = new Set([
  'assembly-csharp.dll', '0harmony.dll', 'mirror.dll', 'unityengine.dll',
  'unityengine.coremodule.dll', 'unityengine.ui.dll', 'unityengine.audiomodule.dll',
]);
const MAX_CODE_BYTES = 20 * 1024 * 1024; // code mods must stay under 20 MB after zipping

function scanForbidden(dir) {
  const bad = [];
  const walk = (d) => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const full = path.join(d, e.name);
      if (e.isDirectory()) {
        if (e.name === 'obj' || e.name === 'bin') bad.push(path.relative(dir, full) + '/');
        walk(full);
      } else {
        const lower = e.name.toLowerCase();
        if (FORBIDDEN_FILES.some((ext) => lower.endsWith(ext))) bad.push(path.relative(dir, full));
        if (GAME_ASSEMBLIES.has(lower)) bad.push(path.relative(dir, full) + ' (game assembly copy)');
      }
    }
  };
  walk(dir);
  return bad;
}

const modules = dll ? [{ type: 'dll', path: meta.dllFile, entryClass }] : [];
const locDir = path.join(modDir, 'Localization');
if (fs.existsSync(locDir)) {
  fs.cpSync(locDir, path.join(pkg, 'Localization'), { recursive: true });
  modules.push({ type: 'localization', path: 'Localization' });
}

// costume modules: workshop.json "costumes": ["costume"] (folder holding <one subfolder per costume>)
for (const folder of (ws.costumes || [])) {
  const src = path.join(modDir, folder);
  if (!fs.existsSync(src) || !fs.statSync(src).isDirectory()) fail(`costume module folder missing: ${folder}`);
  fs.cpSync(src, path.join(pkg, folder), { recursive: true });
  modules.push({ type: 'costume', path: folder });
}

// data modules: workshop.json "data": ["data"] (folder names, relative to the mod dir)
for (const folder of (ws.data || [])) {
  const src = path.join(modDir, folder);
  if (!fs.existsSync(src) || !fs.statSync(src).isDirectory()) fail(`data module folder missing: ${folder}`);
  fs.cpSync(src, path.join(pkg, folder), { recursive: true });
  const mod = { type: 'data', path: folder };
  const consumer = (ws.dataConsumers || {})[folder];
  if (consumer) mod.consumer = consumer;
  modules.push(mod);
}

if (modules.length === 0) fail('package has no modules (need a dllFile, Localization/ or data/ folder)');

const preview = ws.preview || 'thumbnail.png';
if (fs.existsSync(path.join(modDir, preview))) fs.copyFileSync(path.join(modDir, preview), path.join(pkg, preview));
else console.warn(`pack: no ${preview} - the workshop page will show an empty thumbnail`);

const forbidden = scanForbidden(pkg);
if (forbidden.length) fail('package contains files the review rejects: ' + forbidden.join(', '));

const nested = {
  info: {
    title: ws.title,
    description: ws.description,
    preview,
  },
  tags: ws.tags || [],
  mod: {
    schemaVersion: 1,
    modId,
    version: meta.modVersion,
    author: ws.author || meta.modAuthor,
    multiplayer: ws.multiplayer || { installation: 'everyone' },
    modules,
    libraries: [],
    dependencies: (ws.dependencies || []).map((d) => ({ modId: d.modId, version: d.version, ...(d.workshopId ? { workshopId: d.workshopId } : {}) })),
  },
};
fs.writeFileSync(path.join(pkg, 'metadata.json'), JSON.stringify(nested, null, 2) + '\n');

let zipPath = null;
if (!noZip) {
  zipPath = path.join(outRoot, `${modId}-${meta.modVersion}.zip`);
  fs.rmSync(zipPath, { force: true });
  // Compress-Archive via powershell: zip straight from the package dir (single root-level metadata.json).
  try {
    execFileSync('powershell.exe', ['-NoProfile', '-Command',
      `Compress-Archive -Path '${pkg}\\*' -DestinationPath '${zipPath}' -Force`], { stdio: 'pipe' });
  } catch (ex) {
    fail('zip failed: ' + (ex.stderr ? ex.stderr.toString() : ex.message));
  }
}

if (zipPath && dll && fs.statSync(zipPath).size > MAX_CODE_BYTES) {
  fail(`code package is ${(fs.statSync(zipPath).size / 1048576).toFixed(1)} MB - review limit is 20 MB`);
}

console.log(`packed  ${modId} v${meta.modVersion}`);
console.log(`  dir   ${pkg}`);
if (zipPath) console.log(`  zip   ${zipPath}  (${fs.statSync(zipPath).size} B)`);
console.log(`  modules: ${modules.map((m) => m.type).join(', ')}`);
