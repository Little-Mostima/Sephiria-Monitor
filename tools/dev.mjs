#!/usr/bin/env node
// Hot-reload dev loop for one mod.
//
//   node tools/dev.mjs curio-pack            # watch; data/l10n edits reload live, C# edits are queued
//   node tools/dev.mjs curio-pack --apply    # ...and a C# edit rebuilds + restarts the game once
//   node tools/dev.mjs curio-pack --once     # build + deploy + restart + reload, then exit
//
// Why: a cold start costs ~30 s and every suite needs one, so the inner loop should not.
// Data (Localization/, data/, costume/) is re-read by the mod's own ReloadData() via POST /reload;
// only a C# change needs the process to restart.

import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import {
  REPO, getJson, tryJson, health, isGameRunning, killGameAndWait, launchGame,
  waitBridge, arm, waitWorld, deployMod, sleep,
} from './runner/lib.mjs';

const argv = process.argv.slice(2);
const name = argv.find((a) => !a.startsWith('--'));
const once = argv.includes('--once');
const apply = argv.includes('--apply');
if (!name) {
  console.error('usage: node tools/dev.mjs <mod> [--once] [--apply]');
  process.exit(2);
}

const dir = path.join(REPO, 'work', name);
if (!fs.existsSync(path.join(dir, 'metadata.json'))) {
  console.error(`no mod at work/${name} (metadata.json missing)`);
  process.exit(2);
}

const DATA_DIRS = ['Localization', 'data', 'costume'];
const SRC_DIR = 'src';

function stamp(rel) {
  const p = path.join(dir, rel);
  if (!fs.existsSync(p)) return 0;
  let newest = 0;
  const walk = (d) => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const full = path.join(d, e.name);
      if (e.isDirectory()) walk(full);
      else newest = Math.max(newest, fs.statSync(full).mtimeMs);
    }
  };
  walk(p);
  return newest;
}

function snapshot() {
  const out = {};
  for (const d of [...DATA_DIRS, SRC_DIR]) out[d] = stamp(d);
  return out;
}

function build() {
  const r = spawnSync(process.execPath, [path.join(REPO, 'tools', 'build-mod.mjs'), name, '--deploy'], { stdio: 'inherit' });
  return r.status === 0;
}

async function reload() {
  const h = await tryJson('/health');
  if (!h || !h.ok) {
    console.log('  bridge down — nothing to reload (start the game first)');
    return false;
  }
  const r = await tryJson('/reload');
  if (!r) {
    console.log('  /reload unavailable (plugin older than 0.22.0?)');
    return false;
  }
  const list = r.reloaded || [];
  const mine = list.filter((x) => x && /reload/.test(x.reload || ''));
  console.log(`  reload: ${list.length} mod(s) answered` + (mine.length ? ` — ${mine.map((x) => `${x.mod}:${x.reload}`).join(', ')}` : ''));
  return true;
}

async function restart() {
  console.log('  restarting the game (hidden desktop)…');
  await killGameAndWait({ timeoutMs: 30000 });
  launchGame({ consoleWindow: false });
  await waitBridge(150000);
  await arm(120);
  await waitWorld({ timeoutMs: 180000, log: (m) => console.log('  ' + m) });
}

async function cycle(reason) {
  console.log(`\n[${new Date().toLocaleTimeString()}] ${reason}`);
  if (!build()) {
    console.log('  build failed — keeping the old dll');
    return;
  }
  if (!isGameRunning()) {
    console.log('  game not running — starting it');
    launchGame({ consoleWindow: false });
    await waitBridge(150000);
    await arm(120);
    await waitWorld({ timeoutMs: 180000, log: (m) => console.log('  ' + m) });
  } else {
    await restart();
  }
  await sleep(1500);
  await reload();
}

const main = async () => {
  console.log(`dev loop: work/${name}${apply ? ' (C# edits restart)' : ' (C# edits queued)'}`);
  if (once) {
    await cycle('once: build + deploy + restart + reload');
    process.exit(0);
  }
  let prev = snapshot();
  let dirtySrc = false;
  for (;;) {
    await sleep(1500);
    const cur = snapshot();
    const dataChanged = DATA_DIRS.filter((d) => cur[d] !== prev[d]);
    const srcChanged = cur[SRC_DIR] !== prev[SRC_DIR];
    prev = cur;
    if (dataChanged.length) {
      console.log(`\n[${new Date().toLocaleTimeString()}] ${dataChanged.join(', ')} changed — hot reload`);
      await reload();
    }
    if (srcChanged) {
      dirtySrc = true;
      console.log(`\n[${new Date().toLocaleTimeString()}] src changed — ${apply ? 'rebuilding' : 'queued (run with --apply to rebuild+restart)'}`);
      if (apply) {
        dirtySrc = false;
        await cycle('src changed');
      }
    }
    if (dirtySrc && !apply) {
      // keep the flag visible without spamming
    }
  }
};

main().catch((ex) => {
  console.error(`dev loop failed: ${ex.message}`);
  process.exit(1);
});
