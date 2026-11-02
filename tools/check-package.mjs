#!/usr/bin/env node
// Validate a built Workshop package (.zip) against what WorkshopManifestReader/WorkshopDllActivator
// actually enforce. Catches the mistakes that would otherwise only surface inside the game.
//
//   node tools/check-package.mjs dist/workshop/<modId>-<ver>.zip
//   node tools/check-package.mjs dist/workshop/<modId>          # unpacked folder
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { spawnSync } from 'node:child_process';

const target = process.argv[2];
if (!target) {
  console.error('usage: node tools/check-package.mjs <package.zip|package-dir>');
  process.exit(2);
}
const isZip = fs.statSync(target).isFile();

let root = path.resolve(target);
let tmp = null;
if (isZip) {
  tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'wkcheck-'));
  root = tmp;
  const res = spawnSync('powershell.exe', ['-NoProfile', '-Command',
    `Expand-Archive -LiteralPath '${path.resolve(target)}' -DestinationPath '${tmp}' -Force`], { stdio: 'pipe' });
  if (res.status !== 0) {
    console.error('cannot expand zip: ' + ((res.stderr || '').toString().trim() || 'unknown error'));
    process.exit(1);
  }
}

const errors = [];
const warns = [];
const err = (m) => errors.push(m);
const warn = (m) => warns.push(m);

const rel = (p) => p.replace(/\\/g, '/');
const insideRoot = (p) => {
  const abs = path.resolve(root, p);
  return abs === root || abs.startsWith(root + path.sep);
};

// metadata at the package root (a nested single folder is tolerated but reported)
let metaPath = path.join(root, 'metadata.json');
if (!fs.existsSync(metaPath)) {
  const dirs = fs.readdirSync(root, { withFileTypes: true }).filter((d) => d.isDirectory());
  if (dirs.length === 1 && fs.existsSync(path.join(root, dirs[0].name, 'metadata.json'))) {
    root = path.join(root, dirs[0].name);
    metaPath = path.join(root, 'metadata.json');
    warn(`package root has one nested folder "${dirs[0].name}" — uploads usually expect metadata.json at the zip root`);
  }
}
if (!fs.existsSync(metaPath)) { err('metadata.json missing at package root'); report(); }

let meta;
try {
  // strip a UTF-8 BOM: PowerShell 5.1's `Set-Content -Encoding UTF8` writes one, and the game's
  // Newtonsoft reader tolerates it, so a mod author may ship it without noticing.
  const raw = fs.readFileSync(metaPath, 'utf8').replace(/^\uFEFF/, '');
  meta = JSON.parse(raw);
} catch (e) { err('metadata.json is not valid JSON: ' + e.message); report(); }

// ---- info / tags ----
if (!meta.info || typeof meta.info !== 'object') err('info missing');
else {
  const t = meta.info.title;
  const d = meta.info.description;
  if (!t || typeof t !== 'object' || !Object.keys(t).length) err('info.title missing or empty');
  if (!d || typeof d !== 'object' || !Object.keys(d).length) warn('info.description missing or empty');
  if (meta.info.preview && !fs.existsSync(path.join(root, meta.info.preview))) warn(`info.preview points at a missing file: ${meta.info.preview}`);
}
if (meta.tags !== undefined && !Array.isArray(meta.tags)) err('tags must be an array');

// ---- mod block ----
const mod = meta.mod;
if (!mod || typeof mod !== 'object') { err('mod missing'); report(); }
if (Number(mod.schemaVersion) !== 1) err(`mod.schemaVersion must be 1 (got ${mod.schemaVersion})`);
if (!mod.modId) err('mod.modId missing');
if (!/^\d+\.\d+\.\d+$/.test(String(mod.version || ''))) err('mod.version must be semver x.y.z');
if (!mod.author) warn('mod.author missing');

const modules = mod.modules;
if (!Array.isArray(modules) || !modules.length) err('mod.modules must be a non-empty array');
else {
  const seenPaths = new Set();
  const seenEntries = new Set();
  const allowed = new Set(['dll', 'localization', 'costume', 'data']);
  modules.forEach((m, i) => {
    const at = `modules[${i}]`;
    if (!m || typeof m !== 'object') return err(at + ': not an object');
    if (!allowed.has(m.type)) err(`${at}: unsupported type "${m.type}"`);
    if (!m.path) return err(at + ': path missing');
    if (path.isAbsolute(m.path) || /^[A-Za-z]:/.test(m.path)) err(`${at}: path must be relative`);
    if (m.path.split(/[\\/]/).includes('..')) err(`${at}: path escapes the package`);
    if (!insideRoot(m.path)) err(`${at}: path resolves outside the package`);
    if (!fs.existsSync(path.join(root, m.path))) err(`${at}: path does not exist in the package: ${m.path}`);
    const key = rel(m.path).toLowerCase();
    if (seenPaths.has(key)) err(`${at}: duplicate path ${m.path}`);
    seenPaths.add(key);

    if (m.type === 'dll') {
      if (!/\.dll$/i.test(m.path)) err(`${at}: dll module path must end with .dll`);
      const entry = m.entryClass;
      if (!entry) err(at + ': dll module needs entryClass');
      else if (!/^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$/.test(entry)) err(`${at}: entryClass has an invalid shape: ${entry}`);
      else if (seenEntries.has(entry)) err(`${at}: duplicate entryClass ${entry}`);
      seenEntries.add(entry);
    } else if (m.entryClass !== undefined) {
      err(`${at}: ${m.type} modules must not declare entryClass`);
    }
  });
}

// ---- size ----
function packageBytes() {
  const walk = (dir) => fs.readdirSync(dir, { withFileTypes: true }).reduce((n, d) => n + (d.isDirectory() ? walk(path.join(dir, d.name)) : fs.statSync(path.join(dir, d.name)).size), 0);
  try { return walk(root); } catch { return 0; }
}
let bytes = packageBytes();
if (bytes > 50 * 1024 * 1024) warn(`package is ${(bytes / 1048576).toFixed(1)} MB — large uploads are slow and may hit limits`);

report();

function report() {
  if (bytes === undefined) bytes = packageBytes();
  const modId = meta && meta.mod ? meta.mod.modId : '?';
  const version = meta && meta.mod ? meta.mod.version : '?';
  if (tmp) { try { fs.rmSync(tmp, { recursive: true, force: true }); } catch {} }
  for (const w of warns) console.log('warn  ' + w);
  for (const e of errors) console.log('FAIL  ' + e);
  console.log(`\n${errors.length ? 'PACKAGE INVALID' : 'PACKAGE OK'}  ${modId} v${version}  (${(bytes / 1024).toFixed(1)} KB)`);
  process.exit(errors.length ? 1 : 0);
}
