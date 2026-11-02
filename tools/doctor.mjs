#!/usr/bin/env node
// Environment doctor: one command that answers "is this machine able to build, run and test?"
//
//   node tools/doctor.mjs            # human table
//   node tools/doctor.mjs --json
//
// Exit code 1 when any check is "fail"; warnings do not fail.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

const asJson = process.argv.includes('--json');
const REPO = path.resolve(path.dirname(new URL(import.meta.url).pathname).replace(/^\/([A-Za-z]:)/, '$1'), '..');
const GAME = process.env.SEPHIRIA_GAME_DIR || 'D:\\steam\\steamapps\\common\\Sephiria';
const DOTNET = process.env.SEPHIRIA_DOTNET || 'dotnet';
const SAVES = process.env.SEPHIRIA_SAVES_DIR || path.join(os.homedir(), 'Documents', 'Saved Games', 'Sephiria');
const PORT = 47901;

const rows = [];
const add = (name, state, detail) => rows.push({ name, state, detail });
const exists = (p) => fs.existsSync(p);

async function getJson(p) {
  try {
    const res = await fetch(`http://127.0.0.1:${PORT}${p}`, { signal: AbortSignal.timeout(2500) });
    return await res.json();
  } catch {
    return null;
  }
}

// 1 game install
{
  const managed = path.join(GAME, 'Sephiria_Data', 'Managed');
  const asm = path.join(managed, 'Assembly-CSharp.dll');
  if (!exists(GAME)) add('game', 'fail', `not found: ${GAME} (set SEPHIRIA_GAME_DIR)`);
  else if (!exists(asm)) add('game', 'fail', `Sephiria_Data\\Managed\\Assembly-CSharp.dll missing under ${GAME}`);
  else {
    const size = (fs.statSync(asm).size / (1024 * 1024)).toFixed(1);
    add('game', 'ok', `${GAME}  Assembly-CSharp ${size} MB`);
  }
}

// 2 dotnet SDK
{
  if (!exists(DOTNET)) add('dotnet', 'fail', `not found: ${DOTNET} (set SEPHIRIA_DOTNET)`);
  else {
    const res = spawnSync(DOTNET, ['--version'], { encoding: 'utf8' });
    const v = ((res.stdout || '') + (res.stderr || '')).trim().split('\n')[0];
    add('dotnet', res.status === 0 ? 'ok' : 'warn', `${DOTNET} -> ${v || 'no version output'}`);
  }
}

// 3 node version (runner needs 18+, we use global fetch/AbortSignal)
{
  const major = Number(process.versions.node.split('.')[0]);
  add('node', major >= 18 ? 'ok' : 'fail', `v${process.versions.node} (need >= 18 for fetch/AbortSignal)`);
}

// 4 plugin deployed + version match with source
{
  const dll = path.join(GAME, 'BepInEx', 'plugins', 'SephiriaMonitor', 'SephiriaMonitor.dll');
  const src = path.join(REPO, 'SephiriaMonitor', 'MonitorPlugin.cs');
  if (!exists(dll)) add('plugin', 'fail', `not installed: ${dll} (run install.ps1)`);
  else {
    let sourceVersion = '?';
    try {
      const m = fs.readFileSync(src, 'utf8').match(/Version\s*=\s*"([\d.]+)"/);
      if (m) sourceVersion = m[1];
    } catch {}
    const deployed = fs.statSync(dll).mtime.toISOString().slice(0, 16).replace('T', ' ');
    add('plugin', 'ok', `dll built ${deployed}; source says ${sourceVersion} (rebuild if you edited the plugin)`);
  }
}

// 5 bridge
{
  const health = await getJson('/health');
  if (!health) add('bridge', 'warn', `no answer on 127.0.0.1:${PORT} - start the game (and read a save or use /arm)`);
  else add('bridge', 'ok', `v${health.version} scene=${health.scene} session=${(health.session || '').split('\\').pop()}`);
}

// 6 saves
{
  if (!exists(SAVES)) add('saves', 'fail', `not found: ${SAVES} (set SEPHIRIA_SAVES_DIR; Documents may be redirected)`);
  else {
    const files = fs.readdirSync(SAVES).filter((f) => /\.sav$/i.test(f));
    const tmp = files.filter((f) => /TMP/i.test(f));
    add('saves', files.length ? 'ok' : 'warn', `${files.length} file(s), ${tmp.length} run/tmp slot(s) in ${SAVES}`);
  }
}

// 7 mod channels
{
  const addons = path.join(GAME, 'AddOns');
  const local = path.join(GAME, 'WorkshopLocal');
  const list = (p) => (exists(p) ? fs.readdirSync(p).filter((n) => !n.startsWith('.')) : []);
  add('addons', 'ok', exists(addons) ? list(addons).join(', ') || 'empty' : 'AddOns\\ absent (fine)');
  add('workshoplocal', 'ok', exists(local) ? list(local).join(', ') || 'empty' : 'WorkshopLocal\\ absent (fine)');
}

// 8 repo health
{
  const git = spawnSync('git', ['-C', REPO, 'status', '--porcelain'], { encoding: 'utf8' });
  if (git.status !== 0) add('git', 'warn', 'git not on PATH or not a repo');
  else {
    const dirty = (git.stdout || '').trim().split('\n').filter(Boolean).length;
    add('git', 'ok', dirty ? `${dirty} uncommitted path(s)` : 'clean');
  }
  const work = path.join(REPO, 'work');
  const mods = exists(work) ? fs.readdirSync(work).filter((n) => !n.startsWith('_') && exists(path.join(work, n, 'metadata.json'))) : [];
  add('mods', 'ok', mods.length ? mods.join(', ') : 'no mods under work\\ yet');
}

const failed = rows.filter((r) => r.state === 'fail');
if (asJson) {
  console.log(JSON.stringify({ ok: failed.length === 0, checks: rows }, null, 2));
} else {
  for (const r of rows) console.log(`${r.state === 'ok' ? 'ok  ' : r.state === 'warn' ? 'warn' : 'FAIL'}  ${r.name.padEnd(14)} ${r.detail}`);
  console.log(failed.length ? `\nDOCTOR: ${failed.length} blocking problem(s)` : '\nDOCTOR OK');
}
process.exit(failed.length ? 1 : 0);
