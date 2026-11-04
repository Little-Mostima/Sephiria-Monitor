#!/usr/bin/env node
// One-command gate for a mod: static checks -> build -> package -> run its scenario suites.
//
//   node tools/preflight.mjs work/runsave-guard [--skip-tests] [--skip-build] [--json]
//
// Exit code 0 only when every stage passes.
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { discoverSuites, tierRank, describeTier } from './suites.mjs';

const args = process.argv.slice(2);
const target = args.find((a) => !a.startsWith('--'));
if (!target && !args.includes('--all')) {
  console.error('usage: node tools/preflight.mjs work/<mod>|--all [--skip-tests] [--skip-build] [--json]');
  process.exit(2);
}
const skipTests = args.includes('--skip-tests');
const tierIdx = args.indexOf('--tier');
const tier = tierIdx >= 0 ? args[tierIdx + 1] : 'full'; // smoke | fast | full
const skipBuild = args.includes('--skip-build');
const asJson = args.includes('--json');
const reuse = args.includes('--reuse');
const suiteRetries = Number((args.find((a) => a.startsWith('--suite-retries=')) || '--suite-retries=1').split('=')[1]) || 0;
const REPO = path.resolve(path.dirname(new URL(import.meta.url).pathname).replace(/^\/([A-Za-z]:)/, '$1'), '..');
const modDir = target ? (path.isAbsolute(target) ? target : (fs.existsSync(path.resolve(target)) ? path.resolve(target) : path.join(REPO, 'work', target))) : null;
const stages = [];

// --all: gate every mod under work/ (one directory per mod, metadata.json present).
if (args.includes('--all')) {
  const WORK = path.join(REPO, 'work');
  const mods = fs.readdirSync(WORK, { withFileTypes: true })
    .filter((d) => d.isDirectory() && !d.name.startsWith('_') && fs.existsSync(path.join(WORK, d.name, 'metadata.json')))
    .map((d) => path.join(WORK, d.name));
  const failed = [];
  for (const m of mods) {
    console.log(`\n=== ${path.basename(m)}`);
    const childArgs = [fileURLToPath(import.meta.url), m];
    if (skipTests) childArgs.push('--skip-tests');
    if (skipBuild) childArgs.push('--skip-build');
    if (asJson) childArgs.push('--json');
    if (tier !== 'full') childArgs.push('--tier', tier);
    if (reuse) childArgs.push('--reuse');
    const r = spawnSync(process.execPath, childArgs, { stdio: 'inherit' });
    if (r.status !== 0) failed.push(path.basename(m));
  }
  console.log(failed.length ? `\nPREFLIGHT FAILED: ${failed.join(', ')}` : `\nPREFLIGHT OK - ${mods.length} mod(s)`);
  process.exit(failed.length ? 1 : 0);
}
const record = (name, ok, detail) => {
  stages.push({ name, ok, detail });
  if (!asJson) console.log(`${ok ? 'ok  ' : 'FAIL'}  ${name}${detail ? '  - ' + detail : ''}`);
};
// A skipped stage is not a pass: it is recorded as ok (it must not fail the gate) but printed as
// "skip" and counted, so "watch never ran a suite" can never hide behind a wall of green again.
const skipped = [];
const skip = (name, detail) => {
  skipped.push(name);
  stages.push({ name, ok: true, skipped: true, detail });
  if (!asJson) console.log(`skip  ${name}${detail ? '  - ' + detail : ''}`);
};

const readJson = (p) => JSON.parse(fs.readFileSync(p, 'utf8'));
const exists = (p) => fs.existsSync(p);

// 1. static checks - one implementation only: qc-check.mjs. This stage used to carry its own copy of
// the metadata / id rules, which is exactly how "do test passes but do qc fails" became possible.
let contentOnly = false;
{
  const problems = [];
  // workshop.json is optional: fixtures (control suites, guards) have none and legitimately skip packaging.
  const csprojs = exists(path.join(modDir, 'src')) ? fs.readdirSync(path.join(modDir, 'src')).filter((n) => n.endsWith('.csproj')) : [];
  // A package with no code (translations / data only) has no csproj and no dllFile: that is legal.
  contentOnly = csprojs.length === 0;
  // mod.json only describes an AddOns deploy; a content-only package has nothing to deploy there.
  for (const f of contentOnly ? ['metadata.json'] : ['metadata.json', 'mod.json']) {
    if (!exists(path.join(modDir, f))) problems.push('missing ' + f);
  }

  const qc = spawnSync(process.execPath, [path.join(REPO, 'tools', 'qc-check.mjs'), modDir, '--json'], { encoding: 'utf8' });
  let parsed = null;
  try { parsed = JSON.parse((qc.stdout || '').trim()); } catch { problems.push('qc-check.mjs did not return JSON' + (qc.stderr ? ': ' + qc.stderr.trim().split('\n').pop() : '')); }
  if (parsed) {
    for (const row of parsed.rows || []) {
      for (const e of row.errors || []) problems.push(e);
      for (const w of row.warnings || []) console.log(`warn  ${row.name}: ${w}`);
    }
  }

  const suites = exists(path.join(modDir, 'tests')) ? fs.readdirSync(path.join(modDir, 'tests')).filter((n) => n.endsWith('.suite.json')) : [];
  if (!suites.length) problems.push('no tests/*.suite.json - a mod without a scenario cannot be regressed');
  record('static', problems.length === 0, problems.join('; ') || `${contentOnly ? 'content-only, ' : csprojs.length + ' csproj, '}${suites.length} suite(s)`);
  if (problems.length) finish();
}


// 1b. doc rot (only in --all mode: one check for the whole docs tree)
if (args.includes('--all') && !skipTests) {
  const res = spawnSync(process.execPath, [path.join(REPO, 'tools', 'doccheck.mjs')], { stdio: 'pipe', encoding: 'utf8' });
  const out = ((res.stdout || '') + (res.stderr || '')).trim().split('\n').pop();
  record('docs', res.status === 0, res.status === 0 ? 'DOCCHECK OK' : out);
}

// 2. build
if (contentOnly) {
  record('build', true, 'skipped (content-only package: no src/)');
} else if (!skipBuild) {
  const res = spawnSync(process.execPath, [path.join(REPO, 'tools', 'build-mod.mjs'), modDir], { stdio: 'pipe', encoding: 'utf8' });
  const out = ((res.stdout || '') + (res.stderr || '')).trim().split('\n').slice(-3).join(' | ');
  record('build', res.status === 0, res.status === 0 ? 'dll up to date' : out);
  if (res.status !== 0) finish();
} else {
  record('build', true, 'skipped');
}

// 2b. localization must ship inside dist: the runner's deploy step copies dist only, so a
// Localization folder left at the mod root shows raw keys in game (tag-weave / mixed-pack both hit it).
if (!contentOnly) {
  const locDir = path.join(modDir, 'Localization');
  const srcJson = exists(locDir) ? fs.readdirSync(locDir).filter((n) => n.endsWith('.json')) : [];
  if (srcJson.length) {
    const distLoc = path.join(modDir, 'dist', 'Localization');
    const distJson = exists(distLoc) ? fs.readdirSync(distLoc).filter((n) => n.endsWith('.json')) : [];
    const missing = srcJson.filter((n) => !distJson.includes(n));
    record('localization', missing.length === 0, missing.length
      ? `dist/Localization missing ${missing.join(', ')} - add <None Include="..\\Localization\\**\\*.json" CopyToOutputDirectory="PreserveNewest" /> to the csproj, then delete dist/ and rebuild`
      : `${srcJson.length} json shipped in dist`);
    if (missing.length) finish();
  }
}

// 3. package + validate the artifact people will actually upload
//    Mods without workshop.json are guards/fixtures - they are testable but not publishable.
if (!fs.existsSync(path.join(modDir, 'workshop.json'))) {
  record('package', true, 'skipped (no workshop.json - not a publishable package)');
} else {
  const res = spawnSync(process.execPath, [path.join(REPO, 'tools', 'pack-workshop.mjs'), modDir], { stdio: 'pipe', encoding: 'utf8' });
  if (res.status !== 0) {
    record('package', false, ((res.stdout || '') + (res.stderr || '')).trim().split('\n').slice(-3).join(' | '));
    finish();
  }
  const meta = readJson(path.join(modDir, 'metadata.json'));
  const ws = readJson(path.join(modDir, 'workshop.json'));
  const modId = ws.modId || `com.mostima.${String(meta.modName || '').toLowerCase()}`;
  const zip = path.join(REPO, 'dist', 'workshop', `${modId}-${meta.modVersion}.zip`);
  const chk = spawnSync(process.execPath, [path.join(REPO, 'tools', 'check-package.mjs'), zip], { stdio: 'pipe', encoding: 'utf8' });
  const chkOut = ((chk.stdout || '') + (chk.stderr || '')).trim().split('\n').slice(-1)[0];
  record('package', chk.status === 0, chkOut || `zip ${fs.statSync(zip).size} B`);
  if (chk.status !== 0) finish();
}

// 4. scenario suites
if (!skipTests) {
  const all = discoverSuites({ dirs: [modDir], tier: 'full' });
  const suites = discoverSuites({ dirs: [modDir], tier });
  const skippedByTier = all.filter((s) => tierRank(s.tier) > tierRank(tier));
  for (const s of skippedByTier) skip('suite:' + s.name, `tier ${s.tier} > ${tier}`);
  if (!asJson && all.length) console.log(`  (${describeTier(tier)})`);
  for (const suite of suites) {
    const suitePath = suite.path;
    const suiteName = suite.name;
    // A game-driven suite can lose a restart race under load (the old process is still dying when the
    // new one boots). One retry turns that noise into a pass without hiding a real failure.
    const suiteArgs = [path.join(REPO, 'tools', 'runner', 'run-suite.mjs'), suitePath, '--no-watchdog', '--retries', String(suiteRetries), ...(reuse ? ['--reuse'] : [])];
    let res = spawnSync(process.execPath, suiteArgs, { stdio: 'pipe', encoding: 'utf8' });
    if (res.status !== 0 && suiteRetries > 0) {
      console.log('  retrying once after a failed attempt (flaky restart race)');
      res = spawnSync(process.execPath, suiteArgs, { stdio: 'pipe', encoding: 'utf8' });
    }
    const text = ((res.stdout || '') + (res.stderr || '')).trim();
    const last = text.split('\n').filter((l) => /PASS|FAIL|FAILED/.test(l)).slice(-1)[0] || text.split('\n').slice(-1)[0];
    record('suite:' + suiteName, res.status === 0, last);
  }
  if (!all.length) skip('suite', 'no .suite.json in tests/');
} else {
  skip('suite', '--skip-tests');
}

finish();

function finish() {
  const failed = stages.filter((s) => !s.ok);
  if (asJson) console.log(JSON.stringify({ mod: path.basename(modDir), ok: failed.length === 0, skipped, stages }, null, 2));
  else {
    if (skipped.length) console.log(`\n${skipped.length} stage(s) skipped: ${skipped.join(', ')}`);
    console.log(failed.length === 0 ? '\nPREFLIGHT OK' : `\nPREFLIGHT FAILED (${failed.length} stage(s))`);
  }
  process.exit(failed.length === 0 ? 0 : 1);
}
