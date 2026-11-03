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
  REPO, WORK, GAME, SAVES, ADDONS, STATE, health, get, getJson, tryJson, waitFor, sleep,
  ensureRunning, waitWorld, arm, disarm, killGame, killGameAndWait, launchGame, waitBridge,
  snapshot, restore, backupSaves, restoreSaves, verifySaves, hashFiles, deployMod, undeployMod,
  prepareTestSlot, cleanupTestSlot,
  readManifest, modDir, pick, compare, numDiff, artifactsDir, captureArtifacts,
  isGameRunning, copyDir, installPackage, removePackage, rescan,
} from './lib.mjs';

const argv = process.argv.slice(2);
const opts = {
  baseline: argv.includes('--baseline'),
  json: argv.includes('--json'),
  keep: argv.includes('--keep'),
  watchdog: !argv.includes('--no-watchdog'),
  retries: 1,
  tier: (() => { const i = argv.indexOf('--tier'); return i >= 0 ? argv[i + 1] : null; })(),
  slot: (() => { const i = argv.indexOf('--slot'); return i >= 0 ? parseInt(argv[i + 1], 10) || 0 : 0; })(),
  reuse: argv.includes('--reuse'),
  verbose: argv.includes('--verbose'),
};
for (let i = 0; i < argv.length; i++) {
  if (argv[i] === '--retries' && argv[i + 1]) opts.retries = Number(argv[++i]);
}
const suitePath = argv.find((a) => !a.startsWith('--'));
if (!suitePath) {
  console.error('usage: node tools/runner/run-suite.mjs <suite.json> [--baseline] [--json] [--keep] [--no-watchdog] [--retries N] [--tier smoke|fast]');
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

function fail(message, extra) {
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

// Finds the first object inside any array of the response whose `where.field` equals `where.equals`.
// Lets a suite assert "the CURSE combo entry has applied == 6" without knowing its array index.
function findByWhere(body, where) {
  const want = where.field;
  const val = where.equals;
  const arrays = [];
  const walk = (node) => {
    if (Array.isArray(node)) {
      arrays.push(node);
      for (const el of node) walk(el);
    } else if (node && typeof node === 'object') {
      for (const k of Object.keys(node)) walk(node[k]);
    }
  };
  walk(body);
  for (const arr of arrays) {
    const hit = arr.find((el) => el && typeof el === 'object' && String(el[want]) === String(val));
    if (hit) return hit;
  }
  return undefined;
}

async function readPath(step) {
  const p = typeof step === 'string' ? step : step.path;
  const body = await getJson(p, Number(step.timeoutMs || 15000));
  if (typeof step !== 'string' && step.where) {
    const hit = findByWhere(body, step.where);
    return { body, value: hit === undefined ? undefined : pick(hit, step.field) };
  }
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
          ctx.baseline.values[key] = value;
          log(`baseline ${key} = ${JSON.stringify(value)}`);
        } else {
          if (!(key in ctx.baseline.values)) fail(`baseline has no key ${key} - rerun with --baseline`);
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
        const res = await arm(Number((body && body.minutes) || 120), opts.slot);
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
      case 'title': {
        // Cold start and STAY on the title screen (no /arm), for panels that only exist there
        // (UI_SteamWorkshopPanel, UI_CloudSaveComparer).
        log('restarting game to the title screen');
        killGameAndWait();
        launchGame();
        try {
          await waitBridge(150000);
        } catch (e) {
          log(`bridge did not come up (${e.message}); relaunching once`);
          killGameAndWait();
          launchGame();
          await waitBridge(150000);
        }
        log('bridge up, staying at title');
        break;
      }
      case 'restart': {
        log('restarting game (watchdog step)');
        killGameAndWait();
        launchGame();
        try {
          await waitBridge(150000);
        } catch (e) {
          log(`bridge did not come up (${e.message}); relaunching once`);
          killGameAndWait();
          launchGame();
          await waitBridge(150000);
        }
        await waitWorld({ timeoutMs: 180000, log, slot: opts.slot });
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
      case 'isolateAddons': {
        // Move every AddOns folder except the named one aside, so a suite that measures absolute
        // player stats sees only its own pack. The end-of-suite snapshot restore puts them back.
        // renameSync is only same-drive: fall back to copy+remove when AddOns and work/ are on
        // different volumes (GAME on C:, repo on D:).
        const keep = String(body);
        const aside = path.join(STATE, `addons-aside-${Date.now()}`);
        fs.mkdirSync(aside, { recursive: true });
        const moved = [];
        for (const name of fs.existsSync(ADDONS) ? fs.readdirSync(ADDONS) : []) {
          if (name.toLowerCase() === keep.toLowerCase()) continue;
          const src = path.join(ADDONS, name);
          const dst = path.join(aside, name);
          try {
            fs.renameSync(src, dst);
          } catch (ex) {
            if (ex.code !== 'EXDEV' && ex.code !== 'EPERM' && ex.code !== 'EACCES' && ex.code !== 'EBUSY') throw ex;
            fs.cpSync(src, dst, { recursive: true });
            fs.rmSync(src, { recursive: true, force: true });
          }
          moved.push(name);
        }
        globalThis.__isolatedAside = aside;
        log(`isolateAddons: kept ${keep}, moved aside ${moved.length} (${moved.join(', ') || 'none'})`);
        results.steps.push({ index, kind, keep, moved, ok: true, ms: Date.now() - started });
        return;
      }
      case 'undeploy': {
        undeployMod(String(body), log);
        results.steps.push({ index, kind, name: String(body), ok: true, ms: Date.now() - started });
        return;
      }
      case 'rescan': {
        const r = await rescan();
        step.extra = r;
        if (r && r.ok === false) throw new Error(`rescan failed: ${r.error || 'unknown'}`);
        break;
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
        const slot = String(body && body.slot != null ? body.slot : body || '1');
        const create = !!(body && body.create);
        const name = body && body.name ? String(body.name) : '';
        const res = await getJson(
          `/enter?slot=${encodeURIComponent(slot)}` + (create ? '&create=1' : '') + (name ? `&name=${encodeURIComponent(name)}` : ''),
          30000,
        );
        results.steps.push({ index, kind, ok: true, slot, create, name: name || undefined, detail: res, ms: Date.now() - started });
        return;
      }
      case 'newWorld': {
        // Fresh save slot without touching the title-screen UI: /prepnew writes a profile
        // (name + prologue cleared) and /enter loads it. Needed whenever a suite measures
        // absolute player stats: a long-lived save still carries relics from earlier packs.
        // If /prepnew is unavailable the bridge falls back to answering the game's own
        // "enter your name" prompt (create=1&name=...) - that path is slower but equivalent.
        const slot = Number(body && body.slot != null ? body.slot : 2);
        const name = String((body && body.name) || 'Tester');
        killGameAndWait();
        launchGame();
        await waitBridge(150000);
        let title = false;
        for (let i = 0; i < 180; i++) {
          const h = await tryJson('/health');
          if (h && h.scene === 'Title') { title = true; break; }
          await sleep(1000);
        }
        if (!title) fail('newWorld: title screen did not appear');
        let via = 'prepnew';
        let prep = null;
        try {
          prep = await getJson(`/prepnew?slot=SLOT${slot}&name=${encodeURIComponent(name)}&fresh=1`, 60000);
          log(`prepnew: ${JSON.stringify(prep).slice(0, 200)}`);
        } catch (ex) {
          log(`prepnew error: ${ex.message}`);
        }
        if (!prep || prep.ok !== true) {
          via = 'create';
          log(`newWorld: prepnew unavailable, creating the profile through the game instead`);
          const res = await getJson(`/enter?slot=SLOT${slot}&create=1&name=${encodeURIComponent(name)}`, 60000);
          log(`enter(create): ${JSON.stringify(res).slice(0, 200)}`);
          if (!res || res.ok === false) fail(`newWorld: create+enter failed: ${JSON.stringify(res).slice(0, 200)}`);
        } else {
          const res = await getJson(`/enter?slot=SLOT${slot}`, 60000);
          log(`enter: ${JSON.stringify(res).slice(0, 160)}`);
        }
        let world = false;
        for (let i = 0; i < 180; i++) {
          const st = await tryJson('/state');
          if (st && st.scene === 'MainWorld') { world = true; break; }
          await sleep(1000);
        }
        if (!world) fail('newWorld: MainWorld did not load');
        // Absolute check: the slot we are standing on must be the clean profile we asked for.
        await sleep(1200);
        const saves = await tryJson('/saves');
        const row = saves && Array.isArray(saves.slots) ? saves.slots.find((s) => s && Number(s.slot) === slot) : null;
        if (!row || !row.exists) fail(`newWorld: slot ${slot} missing from /saves after entering`);
        if (row.playerName !== name) fail(`newWorld: slot ${slot} playerName=${JSON.stringify(row.playerName)} != ${JSON.stringify(name)}`);
        results.steps.push({ index, kind, slot, name, via, deathCount: row.deathCount, ok: true, ms: Date.now() - started });
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
        const needle = String((body && body.contains) || '');
        const until = Date.now() + Number((body && body.untilMs) || 0);
        let found = false;
        let parsed = null;
        do {
          const line = await get(`/logs?n=${Number((body && body.n) || 400)}`);
          parsed = safeJson(line);
          found = !needle || JSON.stringify(parsed).includes(needle);
          if (!found && Date.now() < until) await sleep(1500);
        } while (!found && Date.now() < until);
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

// --reuse: join a game that is already running instead of cold-starting. Suites that install a
// package and assert activation (installPackage + rescan) then cost seconds, not a restart.
async function reuseSession() {
  const h0 = await health();
  if (!h0 || !h0.ok) {
    throw new Error('--reuse: the bridge is down — start the game first (node tools/launch-bg.mjs --world)');
  }
  if (h0.scene !== 'MainWorld') {
    log('--reuse: at title, arming and waiting for the world');
    await arm(120, opts.slot || 0);
    await waitWorld({ log, slot: opts.slot || 0 });
  }
  return health();
}

async function main() {
  const h = opts.reuse ? await reuseSession() : await ensureRunning({ log });
  checkVersion(h);
  results.meta = { gameVersion: h.gameVersion, pluginVersion: h.version, scene: h.scene };

  if (suite.mods && suite.mods.length && !(opts.baseline && suite.baselineSkipsDeploy)) {
    log(`mods: ${suite.mods.join(', ')}`);
  }

  const snap = `pre-${suiteName.replace(/[^a-z0-9_-]+/gi, '-')}`;
  if (opts.slot) {
    prepareTestSlot(opts.slot);
    log(`test slot: SLOT${opts.slot}.sav cloned from SLOT1.sav (removed afterwards)`);
  }
  snapshot(snap);
  const saves = backupSaves('pre');
  globalThis.__isolation = { snap, saves: true, keep: opts.keep };
  log(`isolated: snapshot ${snap}, ${saves.files.length} save file(s) backed up (sha256 recorded)`);

  let ctx = { last: null, baseline: { values: {} } };
  const needsBaseline = suite.steps.some((st) => st && typeof st === "object" && "baseline" in st);
  if (!opts.baseline && needsBaseline) {
    if (!fs.existsSync(baselineFile)) fail(`no baseline at ${baselineFile} - run with --baseline first`);
    ctx.baseline = JSON.parse(fs.readFileSync(baselineFile, 'utf8'));
    for (const key of ['gameVersion', 'pluginVersion']) {
      if (ctx.baseline.meta && ctx.baseline.meta[key] !== results.meta[key]) {
        fail(`baseline was recorded with ${key}=${ctx.baseline.meta[key]}, now ${results.meta[key]} - rerun with --baseline`);
      }
    }
  }

  let attempt = 0;
  for (;;) {
    try {
      await waitWorld({ log, slot: opts.slot });
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
      await arm(120, opts.slot);
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
      const v = verifySaves('pre', opts.slot ? [`SLOT${opts.slot}.sav`] : []);
      cleanupTestSlot(opts.slot);
      if (globalThis.__isolatedAside) {
        fs.rmSync(globalThis.__isolatedAside, { recursive: true, force: true });
        globalThis.__isolatedAside = null;
      }
      results.isolation = { snapshot: snap, saveFiles: v.files, restored: v.ok, mismatched: v.bad };
      if (v.ok) log(`isolation restored (AddOns/plugins + ${v.files} save file(s), sha256 verified)`);
      else {
        results.ok = false;
        log(`ISOLATION BROKEN: ${v.bad.join(', ')} differ from the backup - stop and inspect work/_state/saves-pre`);
      }
    } catch (ex) {
      results.ok = false;
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
  const iso = globalThis.__isolation;
  if (iso && iso.saves && !iso.keep) {
    try {
      restore(iso.snap);
      restoreSaves('pre');
      const v = verifySaves('pre', opts.slot ? [`SLOT${opts.slot}.sav`] : []);
      cleanupTestSlot(opts.slot);
      if (globalThis.__isolatedAside) {
        fs.rmSync(globalThis.__isolatedAside, { recursive: true, force: true });
        globalThis.__isolatedAside = null;
      }
      results.isolation = { snapshot: iso.snap, saveFiles: v.files, restored: v.ok, mismatched: v.bad, recovered: true };
      console.error(v.ok ? 'isolation restored after failure' : `ISOLATION BROKEN after failure: ${v.bad.join(', ')}`);
    } catch (e2) { console.error(`isolation restore failed: ${e2.message}`); }
  }
  let dir = null;
  try { dir = artifactsDir(suiteName); await captureArtifacts(dir); } catch {}
  console.error(`FAILED: ${ex.message}`);
  if (opts.json) console.log(JSON.stringify(results, null, 2));
  process.exit(1);
});
