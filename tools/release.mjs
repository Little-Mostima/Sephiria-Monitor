#!/usr/bin/env node
// Release helper: hash the workshop zip, write RELEASE.md, optionally stage it for a trial upload.
//
//   node tools/release.mjs work/<mod>            # hash + RELEASE.md
//   node tools/release.mjs work/<mod> --stage    # also copy the package into <game>\WorkshopLocal\<modId>
//
// Staging is what the in-game Steam Workshop panel scans (`WorkshopModManager.RescanLocalPackages`),
// so `--stage` is the last local step before clicking "업로드" in the Title screen panel.
// This script never touches Steam and never uploads anything.

import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { GAME, REPO, health, isGameRunning } from './runner/lib.mjs';

const args = process.argv.slice(2);
const modDir = path.resolve(args.find((a) => !a.startsWith('--')) || '');
const stage = args.includes('--stage');

if (!modDir || !fs.existsSync(modDir)) {
  console.error('usage: node tools/release.mjs work/<mod> [--stage]');
  process.exit(2);
}

const readJson = (p) => JSON.parse(fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, ''));
const fail = (msg) => {
  console.error('RELEASE INVALID: ' + msg);
  process.exit(1);
};

const metaPath = path.join(modDir, 'metadata.json');
const shopPath = path.join(modDir, 'workshop.json');
if (!fs.existsSync(metaPath)) fail('metadata.json missing in ' + modDir);
const meta = readJson(metaPath);
const shop = fs.existsSync(shopPath) ? readJson(shopPath) : {};
const modId = meta.modId || shop.modId;
if (!modId) fail('metadata.json has no modId');

const version = meta.modVersion || shop.version || '0.0.0';
const zipCandidates = [
  path.join(REPO, 'dist', 'workshop', `${modId}-${version}.zip`),
  path.join(modDir, 'dist', 'workshop', `${modId}-${version}.zip`)
];
const zip = zipCandidates.find((p) => fs.existsSync(p));
if (!zip) fail(`zip missing in ${zipCandidates.map((p) => path.relative(process.cwd(), p)).join(' or ')} (run preflight first)`);

const bytes = fs.readFileSync(zip);
const sha = crypto.createHash('sha256').update(bytes).digest('hex');

// Reject a release when the documented hard limits would fail the in-game preparer.
const titleOf = (v) => (typeof v === 'string' ? v : v?.['en-US'] || v?.default || Object.values(v || {})[0] || '');
const title = titleOf(shop.title);
const desc = titleOf(shop.description);
const problems = [];
if (!title) problems.push('title empty');
if (title.length > 128) problems.push(`title ${title.length} > 128`);
if (desc.length > 7999) problems.push(`description ${desc.length} > 7999`);
for (const tag of shop.tags || []) {
  if (String(tag).length > 255) problems.push(`tag too long: ${tag}`);
  if (String(tag).includes(',')) problems.push(`tag contains comma: ${tag}`);
}
const thumb = path.join(modDir, shop.preview || 'thumbnail.png');
if (!fs.existsSync(thumb)) problems.push('thumbnail missing: ' + thumb);
else {
  const ext = path.extname(thumb).toLowerCase();
  if (!['.png', '.jpg', '.jpeg'].includes(ext)) problems.push('thumbnail must be png/jpg');
  if (fs.statSync(thumb).size >= 1024 * 1024) problems.push('thumbnail >= 1 MB');
}
if (problems.length) fail(problems.join('\n  '));

let pluginVersion = 'unknown';
let gameVersion = 'unknown';
try {
  const h = await health(1500);
  pluginVersion = h.version || pluginVersion;
  gameVersion = h.game || gameVersion;
} catch {}

const stamp = new Date().toISOString().replace('T', ' ').slice(0, 19);
const rel = `# Release — ${modId} v${version}

- built: ${stamp}
- game: ${gameVersion}
- plugin: ${pluginVersion}
- package: \`${path.relative(process.cwd(), zip)}\` (${bytes.length.toLocaleString()} bytes)
- sha256: \`${sha}\`

## Pre-upload checklist

- [x] preflight passed (static / build / package / suites)
- [x] package validated by \`tools/check-package.mjs\`
- [x] title ${title.length}/128, description ${desc.length}/7999, ${(shop.tags || []).length} tag(s)
- [x] thumbnail ${path.basename(thumb)} ${fs.statSync(thumb).size.toLocaleString()} bytes
- [ ] local injection confirmed in game log (\`로컬 패키지 주입\` + \`✓ [Active]\`)
- [ ] AddOns cleared during the test (no other mod loaded)
- [ ] uploaded from the in-game Steam Workshop panel

## Trial upload

1. \`node tools/release.mjs ${path.relative(process.cwd(), modDir)} --stage\` (needs the game closed)
2. launch the game → Title → Steam Workshop panel → local entry → "업로드"
3. watch the state text: \`검수 요청 전\` → \`검수 중\` → (\`ReadyToPublish\`) → upload again to publish
4. record the outcome here (state, ticket text, reason_code if rejected)
`;
fs.writeFileSync(path.join(modDir, 'RELEASE.md'), rel, 'utf8');
console.log(`RELEASE OK ${modId} v${version} sha256=${sha.slice(0, 16)}… ${(bytes.length / 1024).toFixed(1)} KB`);

if (!stage) process.exit(0);

if (await isGameRunning()) {
  console.error('RELEASE STAGE SKIPPED: game is running (the dll is locked). Close it and retry.');
  process.exit(1);
}
const target = path.join(GAME, 'WorkshopLocal', modId);
fs.rmSync(target, { recursive: true, force: true });
fs.mkdirSync(target, { recursive: true });
const tmp = path.join(REPO, 'work', '_state', '_unzip');
fs.rmSync(tmp, { recursive: true, force: true });
execFileSync('powershell.exe', ['-NoProfile', '-Command',
  `Expand-Archive -LiteralPath '${zip}' -DestinationPath '${tmp}' -Force`], { stdio: 'inherit' });
const root = fs.existsSync(path.join(tmp, 'metadata.json')) ? tmp : path.join(tmp, modId);
fs.cpSync(root, target, { recursive: true });
fs.rmSync(tmp, { recursive: true, force: true });
console.log(`RELEASE STAGED -> ${target}`);
