#!/usr/bin/env node
// C# formatting is part of the contract, not a matter of taste: `dotnet format` is the single
// definition of "formatted". The compiler never complains about indentation, so drift only shows
// up when a human reads the file - by then it is a re-review of code that was already "done".
//
//   node tools/format.mjs          # check (exit 1 when a file needs formatting)
//   node tools/format.mjs --fix    # rewrite the files
//   node tools/format.mjs --json
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

import { DOTNET, REPO } from './config.mjs';

const proj = path.join(REPO, 'SephiriaMonitor', 'SephiriaMonitor.csproj');
const fix = process.argv.includes('--fix');
const asJson = process.argv.includes('--json');

if (!fs.existsSync(proj)) {
  console.error('format: ' + proj + ' not found');
  process.exit(1);
}

const args = ['format', proj, ...(fix ? [] : ['--verify-no-changes']), '-v', 'q'];
// --no-restore only works once the project has been restored; fall back to a restoring run.
if (fs.existsSync(path.join(REPO, 'SephiriaMonitor', 'obj', 'project.assets.json'))) args.push('--no-restore');

const t0 = Date.now();
const res = spawnSync(DOTNET, args, { encoding: 'utf8' });
const ms = Date.now() - t0;
const out = ((res.stdout || '') + (res.stderr || '')).replace(/\r/g, '');

// dotnet format reports one line per finding: path(line,col): error WHITESPACE: ...
const files = new Map();
for (const m of out.matchAll(/([^\s:]+\.cs)\((\d+),(\d+)\): error (\w+):/g)) {
  const f = path.relative(REPO, m[1]).replace(/\\/g, '/');
  if (!files.has(f)) files.set(f, []);
  files.get(f).push(Number(m[2]));
}

if (fix) {
  if (res.status === 0) {
    const n = files.size;
    console.log(n ? 'format: fixed ' + n + ' file(s) in ' + ms + ' ms' : 'format: already formatted (' + ms + ' ms)');
    process.exit(0);
  }
  console.error('format: dotnet format failed\n' + out.trim().split('\n').slice(-6).join('\n'));
  process.exit(1);
}

const ok = res.status === 0 && files.size === 0;
if (asJson) {
  console.log(JSON.stringify({ ok, ms, files: [...files].map(([f, lines]) => ({ file: f, lines: lines.length })) }, null, 2));
} else if (ok) {
  console.log('format: clean (' + ms + ' ms)');
} else {
  const list = [...files].map(([f, lines]) => f + ' (' + lines.length + ' line(s), first ' + lines[0] + ')');
  console.error('format: ' + files.size + ' file(s) need formatting - run node tools/do.mjs format --fix');
  for (const l of list.slice(0, 8)) console.error('  ' + l);
  if (list.length > 8) console.error('  (+' + (list.length - 8) + ' more)');
  if (!files.size) console.error(out.trim().split('\n').slice(-4).join('\n'));
}
process.exit(ok ? 0 : 1);
