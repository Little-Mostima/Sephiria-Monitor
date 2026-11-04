#!/usr/bin/env node
// Build the monitor plugin and deploy it into the game.
//
//   node tools/plugin.mjs              build (warnings are errors) + deploy dll + hooks.json
//   node tools/plugin.mjs --no-deploy  build only
//   node tools/plugin.mjs --check      build only, quiet (guard uses this)
//
// Why: the plugin used to be built by hand and copied by hand, so the deployed dll could lag
// behind the source for days and the only symptom was "the fix did not work in game".
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { REPO, PLUGINS, DOTNET } from './config.mjs';

const args = process.argv.slice(2);
const check = args.includes('--check');
const deploy = !check && !args.includes('--no-deploy');
const proj = path.join(REPO, 'SephiriaMonitor');
const t0 = Date.now();

const res = spawnSync(DOTNET, ['build', proj, '-c', 'Release', '--nologo', '-v', 'q', '-warnaserror'], {
  encoding: 'utf8',
});
const out = ((res.stdout || '') + (res.stderr || '')).trim();
if (res.status !== 0) {
  console.error(out.split(/\r?\n/).filter((l) => /error|warning/i.test(l)).slice(0, 8).join('\n') || out);
  console.error('plugin: build failed');
  process.exit(1);
}

const bin = path.join(proj, 'bin', 'Release');
const dll = path.join(bin, 'SephiriaMonitor.dll');
const hooks = path.join(bin, 'hooks.json');
const src = fs.readFileSync(path.join(proj, 'MonitorPlugin.cs'), 'utf8');
const version = (src.match(/Version\s*=\s*"([\d.]+)"/) || [])[1] || '?';
const kb = (fs.statSync(dll).size / 1024).toFixed(0);
if (!check) console.log('plugin: built v' + version + ' ' + kb + ' KB in ' + (Date.now() - t0) + ' ms');

if (deploy) {
  const dest = path.join(PLUGINS, 'SephiriaMonitor');
  fs.mkdirSync(dest, { recursive: true });
  try {
    fs.copyFileSync(dll, path.join(dest, 'SephiriaMonitor.dll'));
    fs.copyFileSync(hooks, path.join(dest, 'hooks.json'));
  } catch (e) {
    // The game keeps the dll mapped; copying over it fails with EBUSY/EPERM.
    console.error('plugin: deploy failed (' + e.code + ') - the game is running, stop it first:');
    console.error('        node tools\\launch-bg.mjs --kill');
    process.exit(1);
  }
  console.log('plugin: deployed -> ' + dest);
}
