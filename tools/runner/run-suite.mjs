#!/usr/bin/env node
// Scenario runner for the Sephiria mod workbench.
//
//   node tools/runner/run-suite.mjs work/<mod>/tests/<suite>.json
//   node tools/runner/run-suite.mjs <suite> --baseline      # record a baseline
//   node tools/runner/run-suite.mjs <suite> --json          # machine-readable result
//   node tools/runner/run-suite.mjs <suite> --keep          # keep snapshot/saves/artifacts
//   node tools/runner/run-suite.mjs <suite> --no-watchdog   # never restart the game
//
// Exit code 0 = every assertion and baseline comparison passed.

import fs from 'node:fs';
import path from 'node:path';
import {
  REPO, WORK, GAME, SAVES, health, get, getJson, tryJson, waitFor, sleep,
  ensureRunning, waitWorld, arm, disarm, killGame, launchGame, waitBridge,
  snapshot, restore, backupSaves, restoreSaves, deployMod, undeployMod,
  readManifest, modDir, pick, compare, numDiff, artifactsDir, captureArtifacts,
  isGameRunning, copyDir, installPackage, removePackage,
} from './lib.mjs';

const argv = process.argv.slice(2);
const opts = {
  baseline: argv.includes('--baseline'),
  json: argv.includes('--json'),
  keep: argv.includes('--keep'),
  watchdog: !argv.includes('--no-watchdog'),
  retries: 1,
  verbose: argv.includes('--verbose'),
};
for (let i = 0; i < argv.length; i++) {
  if (argv[i] === '--retries' && argv[i + 1]) opts.retries = Number(argv[++i]);
}
const suitePath = argv.find((a) => !a.startsWith('--'));
if (!suitePath) {
  console.error('usage: node tools/runner/run-suite.mjs <suite.json> [--baseline] [--json] [--keep] [--no-watchdog] [--retries N]');
  process.exit(2);
}

const suiteFile = path.resolve(process.cwd(), suitePath);
if (!fs.existsSync(suiteFile)) {
  console.error(`suite not found: ${suiteFile}`);
  process.exit(2);
}
const suite = JSON.parse(fs.readFileSync(suiteFile, 'utf8'));
const suiteName = suite.name || path.basename(suiteFile, '.suite.json');
const suiteDir = path.dirname(suiteFile);
const baselineFile = path.join(suiteDir, '..', 'baseline', path.basename(suiteFile));
const timeoutSec = Number(suite.timeoutSec || 240);

const log = (msg) => {
  const line = `${new Date().toISOString().slice(11, 19)} ${msg}`;
  if (!opts.json) console.log(line);
  else process.stderr.write(line + '\n');
};

const results = { suite: suiteName, startedAt: new Date().toISOString(), steps: [], baseline: null, meta: null, ok: true, error: null };

function fail(message) {
  const err = new Error(message);
  err.assertion = true;
  throw err;
}

async function readHealthOrFail() {
  const h = await health();
  if (!h || !h.ok) fail('bridge not reachable (is the game running with SephiriaMonitor installed?)');
  return h;
}

function checkVersion(h) {
  const requireGame = suite.game && suite.game.requireVersion;
  if (requireGame && h.gameVersion && h.gameVersion !== requireGame) {
    fail(`game version ${h.gameVersion} != suite requirement ${requireGame}`);
  }
}

async function readPath(step) {
  const p = typeof step === 'string' ? step : step.path;
  const body = await getJson(p, Number(step.timeoutMs || 15000));
  return { body, value: pick(body, typeof step === 'string' ? undefined : step.field) };
}

async function pollExpect(step) {
  const timeoutMs = Number(step.timeoutMs || (step.op === 'exists' ? 15000 : 8000));
  return waitFor(async () => {
    const { body, value } = await readPath(step);
    return compare(step.op || '==', value, step.value) ? { body, value } : null;
  }, { timeoutMs, intervalMs: Number(step.intervalMs || 700), label: `expect ${step.path}${step.field ? '.' + step.field : ''} ${step.op || '=='} ${JSON.stringify(step.value)}` });
}

async function runStep(step, ctx, index) {
  const kind = Object.keys(step)[0];
  const body = step[kind];
  const started = Date.now();
  try {
    switch (kind) {
      case 'call': {
        const text = await get(typeof body === 'string' ? body : body.path, Number((body && body.timeoutMs) || 20000));
        ctx.last = safeJson(text);
        results.steps.push({ index, kind, target: typeof body === 'string' ? body : body.path, ok: true, ms: Date.now() - started });
        return;
      }
      case 'expect': {
        const { body: raw, value } = await pollExpect(body);
        ctx.last = raw;
        results.steps.push({ index, kind, target: body.path, field: body.field, actual: value, expected: body.value, ok: true, ms: Date.now() - started });
        return;
      }
      case 'baseline': {
        const { value } = await readPath(body);
        const key = body.key || `${body.path}${body.field ? '.' + body.field : ''}`;
        if (opts.baseline) {
          ctx.baseline[key] = value;
          log(`baseline ${key} = ${JSON.stringify(value)}`);
        } else {
          if (!(key in ctx.baseline.values)) fail(`baseline has no key ${key} — rerun with --baseline`);
          const expected = ctx.baseline.values[key];
          const tol = Number(body.tolerance || 0);
          const ok = typeof expected === 'number' && typeof value === 'number' ? numDiff(expected, value, tol) : compare('==', value, expected);
          if (!ok) fail(`baseline ${key}: ${JSON.stringify(value)} != ${JSON.stringify(expected)} (tolerance ${tol})`);
          log(`baseline ${key} ok (${JSON.stringify(value)})`);
        }
        results.steps.push({ index, kind, key, ok: true, ms: Date.now() - started });
        return;
      }
      case 'wait': {
        await sleep(Number(body) * 1000);
        results.steps.push({ index, kind, seconds: body, ok: true, ms: Date.now() - started });
        return;
      }
      case 'arm': {
        const res = await arm(Number((body && body.minutes) || 120));
        results.steps.push({ index, kind, ok: true, detail: res, ms: Date.now() - started });
        return;
      }
      case 'disarm': {
        await disarm();
        results.steps.push({ index, kind, ok: true, ms: Date.now() - started });
        return;
      }
      case 'launch': {
        const h = await health();
        if (!h || !h.ok) {
          launchGame();
          await waitBridge();
        }
        results.steps.push({ index, kind, ok: true, ms: Date.now() - started });
        return;
      }
      case 'restart': {
        log('restarting game (watchdog step)');
        killGame();
        await sleep(4000);
        launchGame();
        await waitBridge(120000);
        await waitWorld({ timeoutMs: 180000, log });
        results.steps.push({ index, kind, ok: true, ms: Date.now() - started });
        return;
      }
      case 'snapshot': {
        snapshot(String(body));
        results.steps.push({ index, kind, name: String(body), ok: true, ms: Date.now() - started });
        return;
      }
      case 'restore': {
        restore(String(body));
        results.steps.push({ index, kind, name: String(body), ok: true, ms: Date.now() - started });
        return;
      }
      case 'deploy': {
        deployMod(String(body), log);
        results.steps.push({ index, kind, name: String(body), ok: true, ms: Date.now() - started });
        return;
      }
      case 'undeploy': {
        undeployMod(String(body), log);
        results.steps.push({ index, kind, name: String(body), ok: true, ms: Date.now() - started });
        return;
      }
      case 'installPackage': {
        const r = installPackage(String(body));
        log(`installed workshop package ${r.id} -> ${r.dir}`);
        results.steps.push({ index, kind, name: r.id, ok: true, ms: Date.now() - started });
        return;
      }
      case 'removePackage': {
        removePackage(String(body));
        results.steps.push({ index, kind, name: String(body), ok: true, ms: Date.now() - started });
        return;
      }
      case 'backupSaves': {
        const r = backupSaves(String(body));
        results.steps.push({ index, kind, files: r.files, ok: true, ms: Date.now() - started });
        return;
      }
      case 'restoreSaves': {
        restoreSaves(String(body));
        results.steps.push({ index, kind, ok: true, ms: Date.now() - started });
        return;
      }
      case 'enter': {
        const res = await getJson(`/enter?slot=${encodeURIComponent(String(body || '1'))}`, 30000);
        results.steps.push({ index, kind, ok: true, detail: res, ms: Date.now() - started });
        return;
      }
      case 'files': {
        const dir = expandEnv(String(body.dir));
        const pattern = String(body.pattern || '*');
        const esc = (ch) => (ch === '*' ? '.*' : ch === '?' ? '.' : /[.*+?^${}()|[\]\\]/.test(ch) ? '\\' + ch : ch);
        const regex = new RegExp('^' + pattern.split('').map(esc).join('') + '$');
        const min = Number(body.min == null ? 1 : body.min);
        const names = fs.existsSync(dir) ? fs.readdirSync(dir).filter((n) => regex.test(n)) : [];
        if (names.length < min) fail(`expected at least ${min} file(s) matching ${pattern} in ${dir}, found ${names.length}`);
        results.steps.push({ index, kind, dir, files: names.length, ok: true, ms: Date.now() - started });
        return;
      }
      case 'log': {
        const line = await get(`/logs?n=${Number((body && body.n) || 200)}`);
        const parsed = safeJson(line);
        const needle = String((body && body.contains) || '');
        const found = needle ? JSON.stringify(parsed).includes(needle) : true;
        if (!found) fail(`log does not contain "${needle}"`);
        results.steps.push({ index, kind, contains: needle, ok: true, ms: Date.now() - started });
        return;
      }
      default:
        fail(`unknown step kind "${kind}"`);
    }
  } catch (ex) {
    results.steps.push({ index, kind, target: typeof body === 'string' ? body : body && body.path, ok: false, error: ex.message, ms: Date.now() - started });
    throw ex;
  }
}

function expandEnv(value) {
  return String(value).replace(/\$\{([A-Za-z_][A-Za-z0-9_]*)\}/g, (m, name) => process.env[name] ?? m);
}

function safeJson(text) {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

async function runSuite(ctx) {
  let stepIndex = 0;
  const deadline = Date.now() + timeoutSec * 1000;
  for (const step of suite.steps) {
    if (Date.now() > deadline) fail(`suite timeout after ${timeoutSec}s`);
    stepIndex++;
    log(`step ${stepIndex}: ${Object.keys(step)[0]}`);
    await runStep(step, ctx, stepIndex);
  }
}

async function main() {
  const h = await ensureRunning({ log });
  checkVersion(h);
  results.meta = { gameVersion: h.gameVersion, pluginVersion: h.version, scene: h.scene };

  if (suite.mods && suite.mods.length && !(opts.baseline && suite.baselineSkipsDeploy)) {
    log(`mods: ${suite.mods.join(', ')}`);
  }

  const snap = `pre-${suiteName.replace(/[^a-z0-9_-]+/gi, '-')}`;
  snapshot(snap);
  const saves = backupSaves('pre');
  log(`isolated: snapshot ${snap}, ${saves.files.length} save file(s) backed up`);

  let ctx = { last: null, baseline: { values: {} } };
  const needsBaseline = suite.steps.some((st) => st && typeof st === "object" && "baseline" in st);
  if (!opts.baseline && needsBaseline) {
    if (!fs.existsSync(baselineFile)) fail(`no baseline at ${baselineFile} — run with --baseline first`);
    ctx.baseline = JSON.parse(fs.readFileSync(baselineFile, 'utf8'));
    for (const key of ['gameVersion', 'pluginVersion']) {
      if (ctx.baseline.meta && ctx.baseline.meta[key] !== results.meta[key]) {
        fail(`baseline was recorded with ${key}=${ctx.baseline.meta[key]}, now ${results.meta[key]} — rerun with --baseline`);
      }
    }
  }

  let attempt = 0;
  for (;;) {
    try {
      await waitWorld({ log });
      for (const name of suite.mods || []) {
        const manifest = readManifest(name);
        if (manifest.autoDeploy) deployMod(name, log);
      }
      if (suite.steps.some((s) => 'restart' in s)) {
        // a restart step resets the session; nothing to pre-arm here
      }
      await runSuite(ctx);
      break;
    } catch (ex) {
      attempt++;
      results.error = ex.message;
      const dir = artifactsDir(suiteName);
      await captureArtifacts(dir);
      log(`FAILED: ${ex.message}`);
      log(`artifacts: ${dir}`);
      if (!opts.watchdog || attempt > opts.retries) {
        results.ok = false;
        break;
      }
      log(`watchdog: restart and retry once (${attempt}/${opts.retries})`);
      killGame();
      await sleep(4000);
      launchGame();
      await waitBridge(120000);
      await arm();
      await waitWorld({ timeoutMs: 180000, log });
    }
  }

  if (opts.baseline) {
    fs.mkdirSync(path.dirname(baselineFile), { recursive: true });
    const payload = {
      meta: { ...results.meta, suite: suiteName, recordedAt: new Date().toISOString() },
      values: ctx.baseline.values,
    };
    fs.writeFileSync(baselineFile, JSON.stringify(payload, null, 2));
    log(`baseline written: ${baselineFile}`);
    results.baseline = baselineFile;
  }

  if (!opts.keep) {
    try {
      restore(snap);
      restoreSaves('pre');
      log('isolation restored (AddOns/plugins + saves)');
    } catch (ex) {
      log(`restore warning: ${ex.message}`);
    }
  } else {
    log(`--keep: snapshot left at work/_state/${snap}, saves at work/_state/saves-pre`);
  }

  results.finishedAt = new Date().toISOString();
  results.ok = results.ok && results.steps.every((s) => s.ok);
  if (opts.json) {
    console.log(JSON.stringify(results, null, 2));
  } else {
    const passed = results.steps.filter((s) => s.ok).length;
    console.log(`\n${results.ok ? 'PASS' : 'FAIL'}  ${suiteName}  (${passed}/${results.steps.length} steps)`);
  }
  process.exit(results.ok ? 0 : 1);
}

main().catch(async (ex) => {
  results.ok = false;
  results.error = ex.message;
  let dir = null;
  try { dir = artifactsDir(suiteName); await captureArtifacts(dir); } catch {}
  console.error(`FAILED: ${ex.message}`);
  if (opts.json) console.log(JSON.stringify(results, null, 2));
  process.exit(1);
});
