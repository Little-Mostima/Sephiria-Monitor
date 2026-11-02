#!/usr/bin/env node
// One-command gate for a mod: static checks -> build -> package -> run its scenario suites.
//
//   node tools/preflight.mjs work/runsave-guard [--skip-tests] [--skip-build] [--json]
//
// Exit code 0 only when every stage passes.
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

const args = process.argv.slice(2);
const target = args.find((a) => !a.startsWith('--'));
if (!target) {
  console.error('usage: node tools/preflight.mjs work/<mod> [--skip-tests] [--skip-build] [--json]');
  process.exit(2);
}
const skipTests = args.includes('--skip-tests');
const skipBuild = args.includes('--skip-build');
const asJson = args.includes('--json');
const REPO = path.resolve(path.dirname(new URL(import.meta.url).pathname).replace(/^\/([A-Za-z]:)/, '$1'), '..');
const modDir = path.resolve(target);
const stages = [];

const record = (name, ok, detail) => {
  stages.push({ name, ok, detail });
  if (!asJson) console.log(`${ok ? 'ok  ' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`);
};

const readJson = (p) => JSON.parse(fs.readFileSync(p, 'utf8'));
const exists = (p) => fs.existsSync(p);

// 1. static checks
{
  const problems = [];
  for (const f of ['metadata.json', 'mod.json', path.join('src', path.basename(modDir) + '.csproj')]) {
    if (!exists(path.join(modDir, f))) {
      // csproj name is not derivable reliably, so only require it when exactly one exists
      if (!f.endsWith('.csproj')) problems.push('missing ' + f);
    }
  }
  const csprojs = exists(path.join(modDir, 'src')) ? fs.readdirSync(path.join(modDir, 'src')).filter((n) => n.endsWith('.csproj')) : [];
  if (csprojs.length !== 1) problems.push(`expected exactly one .csproj in src/, found ${csprojs.length}`);
  let meta = null;
  try { meta = readJson(path.join(modDir, 'metadata.json')); } catch { problems.push('metadata.json is not valid JSON'); }
  if (meta) {
    for (const k of ['modName', 'modVersion', 'modAuthor', 'dllFile', 'entryClass']) if (!meta[k]) problems.push('metadata.json missing ' + k);
    if (meta.entryClass && !/^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$/.test(meta.entryClass)) problems.push('entryClass does not look like a type name');
    if (!/^\d+\.\d+\.\d+$/.test(meta.modVersion || '')) problems.push('modVersion must be semver (x.y.z)');
  }
  const suites = exists(path.join(modDir, 'tests')) ? fs.readdirSync(path.join(modDir, 'tests')).filter((n) => n.endsWith('.suite.json')) : [];
  if (!suites.length) problems.push('no tests/*.suite.json — a mod without a scenario cannot be regressed');
  record('static', problems.length === 0, problems.join('; ') || `${csprojs.length} csproj, ${suites.length} suite(s)`);
  if (problems.length) finish();
}

// 2. build
if (!skipBuild) {
  const script = path.join(modDir, 'build.ps1');
  const res = spawnSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script], { stdio: 'pipe', encoding: 'utf8' });
  const out = ((res.stdout || '') + (res.stderr || '')).trim().split('\n').slice(-3).join(' | ');
  record('build', res.status === 0, res.status === 0 ? 'dll up to date' : out);
  if (res.status !== 0) finish();
} else {
  record('build', true, 'skipped');
}

// 3. package (no zip, just validate the nested manifest can be produced)
{
  const res = spawnSync(process.execPath, [path.join(REPO, 'tools', 'pack-workshop.mjs'), modDir, '--no-zip'], { stdio: 'pipe', encoding: 'utf8' });
  const out = ((res.stdout || '') + (res.stderr || '')).trim().split('\n').slice(-2).join(' | ');
  record('package', res.status === 0, res.status === 0 ? out : out);
  if (res.status !== 0) finish();
}

// 4. scenario suites
if (!skipTests) {
  const suites = fs.readdirSync(path.join(modDir, 'tests')).filter((n) => n.endsWith('.suite.json'));
  for (const suite of suites) {
    const res = spawnSync(process.execPath, [path.join(REPO, 'tools', 'runner', 'run-suite.mjs'), path.join(modDir, 'tests', suite), '--no-watchdog'], { stdio: 'pipe', encoding: 'utf8' });
    const text = ((res.stdout || '') + (res.stderr || '')).trim();
    const last = text.split('\n').filter((l) => /PASS|FAIL|FAILED/.test(l)).slice(-1)[0] || text.split('\n').slice(-1)[0];
    record('suite:' + suite.replace('.suite.json', ''), res.status === 0, last);
  }
} else {
  record('suite', true, 'skipped');
}

finish();

function finish() {
  const failed = stages.filter((s) => !s.ok);
  if (asJson) console.log(JSON.stringify({ mod: path.basename(modDir), ok: failed.length === 0, stages }, null, 2));
  else console.log(failed.length === 0 ? '\nPREFLIGHT OK' : `\nPREFLIGHT FAILED (${failed.length} stage(s))`);
  process.exit(failed.length === 0 ? 0 : 1);
}
