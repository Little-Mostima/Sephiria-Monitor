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
if (dll && !fs.existsSync(dll)) fail(`missing ${dll} — build the mod first`);

const modId = ws.modId || `com.mostima.${meta.modName.toLowerCase()}`;
const entryClass = meta.entryClass;
if (dll && !/^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$/.test(entryClass || '')) fail(`bad entryClass "${entryClass}"`);

const pkg = path.join(outRoot, modId);
fs.rmSync(pkg, { recursive: true, force: true });
fs.mkdirSync(pkg, { recursive: true });
if (dll) fs.copyFileSync(dll, path.join(pkg, meta.dllFile));

const modules = dll ? [{ type: 'dll', path: meta.dllFile, entryClass }] : [];
const locDir = path.join(modDir, 'Localization');
if (fs.existsSync(locDir)) {
  fs.cpSync(locDir, path.join(pkg, 'Localization'), { recursive: true });
  modules.push({ type: 'localization', path: 'Localization' });
}

if (modules.length === 0) fail('package has no modules (need a dllFile or Localization/ folder)');

const preview = ws.preview || 'thumbnail.png';
if (fs.existsSync(path.join(modDir, preview))) fs.copyFileSync(path.join(modDir, preview), path.join(pkg, preview));
else console.warn(`pack: no ${preview} — the workshop page will show an empty thumbnail`);

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
    dependencies: [],
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

console.log(`packed  ${modId} v${meta.modVersion}`);
console.log(`  dir   ${pkg}`);
if (zipPath) console.log(`  zip   ${zipPath}  (${fs.statSync(zipPath).size} B)`);
console.log(`  modules: ${modules.map((m) => m.type).join(', ')}`);
