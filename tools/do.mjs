#!/usr/bin/env node
// One entry point for the whole workflow. Every task is a thin wrapper: the logic lives in the
// script it calls, so there is exactly one implementation of each thing.
//
//   node tools/do.mjs build <mod> [--deploy]   build one mod (shared props, version from metadata.json)
//   node tools/do.mjs test [<mod>|--all] [--skip-tests|--skip-build]   preflight
//   node tools/do.mjs fast                     project guards + preflight fast tier (no cold starts)
//   node tools/do.mjs pack <mod>               package + validate the zip
//   node tools/do.mjs release <mod> [--stage]  release checklist (+ stage into WorkshopLocal)
//   node tools/do.mjs compat [--static|--pairs|--all]   mod-to-mod compatibility
//   node tools/do.mjs docs                     doc rot check
//   node tools/do.mjs guard [--world]          structural guards (dead paths, links, hooks, encoding, plugin build, syntax; --world adds the enter-world contract)
//   node tools/do.mjs plugin [--no-deploy]     build the monitor plugin (warnings are errors) and deploy it
//   node tools/do.mjs encoding [--fix]         check (or repair) text file encodings: UTF-8, no BOM
//   node tools/do.mjs q <api|sig|grep|item|hook|doc|table|src|cat|live> ...   offline index query (q.py, UTF-8 output)
//   node tools/do.mjs gate                     the commit gate (guard + doccheck); the pre-commit hook runs this
//   node tools/do.mjs commit -m "msg"          gate, then stage everything and commit
//   node tools/do.mjs push [--dry-run] [--proxy URL]   gate, show what would leave, then push origin main
//   node tools/do.mjs fix                      mechanical repairs (encoding --fix, catalog, links)
//   node tools/do.mjs format [--fix]           C# formatting check (dotnet format is the definition)
//   node tools/do.mjs hooks [--check]          install / verify the pre-commit gate in both repos
//   node tools/do.mjs doctor                   environment check
//   node tools/do.mjs new <name> [--kind content --base-id 930000]
//   node tools/do.mjs qc [<mod>|--all]         content quality checks
//   node tools/do.mjs selfcheck                plugin self-check (routes, hooks, schema)
//   node tools/do.mjs review [--json]          review status of the working tree
//   node tools/do.mjs dev [--watch]            dev loop (build + deploy on change)
//   node tools/do.mjs world [--slot N] [--fresh] [--visible]   launch and enter the world (quick channel)
//   node tools/do.mjs session [suite...] [--tier fast|full]  one cold start, then run suites with --reuse
//   node tools/do.mjs ship <mod> [--stage]     build -> full preflight -> pack -> release (one publish path)
//   node tools/do.mjs clean                    drop test runs / snapshots / build leftovers
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { REPO, WORK, DOCS, killGameAndWait } from './runner/lib.mjs';
import { PYTHON } from './config.mjs';
import { discoverSuites, describeTier, DEFAULT_TIER } from './suites.mjs';

const [, , task, ...rest] = process.argv;
const run = (script, args) => {
  const res = spawnSync(process.execPath, [path.join(REPO, 'tools', script), ...args], { stdio: 'inherit' });
  process.exit(res.status ?? 1);
};
const flag = (a, name, fallback = null) => {
  const i = a.indexOf(name);
  return i >= 0 && a[i + 1] && !a[i + 1].startsWith('--') ? a[i + 1] : fallback;
};

const TASKS = {
  build: (a) => run('build-mod.mjs', a),
  test: (a) => run('preflight.mjs', a),
  // 'fast' used to be preflight alone, which prints "PREFLIGHT OK - 0 mod(s)" when work/ is empty -
  // a green light for a check that ran nothing. Project guards run first, then the mod gate.
  fast: (a) => {
    const g = spawnSync(process.execPath, [path.join(REPO, 'tools', 'guard.mjs')], { stdio: 'inherit' });
    const p = spawnSync(process.execPath, [path.join(REPO, 'tools', 'preflight.mjs'), '--all', '--tier', 'fast', ...a], { stdio: 'inherit' });
    const ok = (g.status ?? 1) === 0 && (p.status ?? 1) === 0;
    console.log(ok ? '\nFAST OK' : '\nFAST FAILED');
    process.exit(ok ? 0 : 1);
  },
  pack: (a) => run('pack-workshop.mjs', a),
  release: (a) => run('release.mjs', a),
  compat: (a) => run('compat.mjs', a),
  docs: (a) => run('doccheck.mjs', a),
  guard: (a) => run('guard.mjs', a),
  plugin: (a) => run('plugin.mjs', a),
  encoding: (a) => run('encoding.mjs', a),
  format: (a) => run('format.mjs', a),
  hooks: (a) => run('install-hooks.mjs', a),
  // The commit gate: everything that must hold before a commit lands. Same code the pre-commit
  // hook runs, so "it passed locally" and "it passed the hook" cannot diverge.
  // guard already runs doccheck (its "numbers" item), so the gate is guard alone - one run, one verdict.
  gate: (a) => {
    const g = spawnSync(process.execPath, [path.join(REPO, 'tools', 'guard.mjs'), ...a], { stdio: 'inherit' });
    const ok = (g.status ?? 1) === 0;
    console.log(ok ? '\nGATE OK' : '\nGATE FAILED');
    process.exit(ok ? 0 : 1);
  },
  // One command to commit: gate first, then stage and commit. An agent that forgets the gate cannot
  // forget this one, and the pre-commit hook catches the ones that call git directly.
  commit: (a) => {
    const msg = flag(a, '-m') || flag(a, '--message');
    if (!msg) {
      console.error('usage: node tools/do.mjs commit -m "message"');
      process.exit(2);
    }
    const g = spawnSync(process.execPath, [path.join(REPO, 'tools', 'guard.mjs')], { stdio: 'inherit' });
    if ((g.status ?? 1) !== 0) {
      console.error('\ncommit: gate failed, nothing committed');
      process.exit(1);
    }
    spawnSync('git', ['-C', REPO, 'add', '-A'], { stdio: 'inherit' });
    const c = spawnSync('git', ['-C', REPO, 'commit', '-m', msg], { stdio: 'inherit' });
    process.exit(c.status ?? 1);
  },
  // Pushing is the one step that is hard to take back, so it never happens as a side effect of
  // something else: the task runs the gate, prints exactly what would leave the machine, then pushes.
  push: (a) => {
    const dry = a.includes('--dry-run');
    const proxy = flag(a, '--proxy') || 'http://127.0.0.1:7890';
    const g = spawnSync(process.execPath, [path.join(REPO, 'tools', 'guard.mjs')], { stdio: 'inherit' });
    if ((g.status ?? 1) !== 0) {
      console.error('\npush: gate failed, nothing pushed');
      process.exit(1);
    }
    const git = (args) => spawnSync('git', ['-C', REPO, ...args], { encoding: 'utf8' });
    const ahead = (git(['log', '--oneline', 'origin/main..HEAD']).stdout || '').trim();
    const stat = (git(['diff', '--stat', 'origin/main..HEAD']).stdout || '').trim().split('\n').pop() || '';
    const n = ahead ? ahead.split('\n').length : 0;
    console.log(`\nPUSH  ${n} commit(s) ahead${stat ? ', ' + stat.trim() : ''}`);
    if (n) console.log(ahead);
    if (!n) {
      console.log('nothing to push');
      process.exit(0);
    }
    if (dry) {
      console.log('(dry run, nothing pushed)');
      process.exit(0);
    }
    const res = spawnSync(
      'git',
      ['-C', REPO, '-c', `http.proxy=${proxy}`, '-c', `https.proxy=${proxy}`, 'push', 'origin', 'main'],
      { stdio: 'inherit' },
    );
    process.exit(res.status ?? 1);
  },
  // Mechanical repairs only. Anything that needs a decision stays a failure for a human to read.
  fix: () => {
    const enc = spawnSync(process.execPath, [path.join(REPO, 'tools', 'encoding.mjs'), '--fix'], { stdio: 'inherit' });
    const cat = spawnSync(PYTHON, [path.join(DOCS, 'index', 'build-catalog.py')], { stdio: 'inherit' });
    const ok = [enc, cat].every((r) => (r.status ?? 1) === 0);
    console.log(ok ? '\nFIX OK' : '\nFIX: something still needs a human');
    process.exit(ok ? 0 : 1);
  },
  // q.py prints Chinese; without PYTHONIOENCODING the pipe decodes as GBK and every answer
  // arrives as mojibake. One entry point, correct encoding, no per-call incantation.
  q: (a) => {
    const res = spawnSync(PYTHON, [path.join(DOCS, 'index', 'q.py'), ...a], {
      stdio: 'inherit',
      env: { ...process.env, PYTHONIOENCODING: 'utf-8' },
    });
    process.exit(res.status ?? 1);
  },
  doctor: (a) => run('doctor.mjs', a),
  new: (a) => run('new-mod.mjs', a),
  qc: (a) => run('qc-check.mjs', a),
  selfcheck: (a) => run('selfcheck.mjs', a),
  review: (a) => run('review-status.mjs', a),
  dev: (a) => run('dev.mjs', a),

  // The quick channel: one command from "nothing running" to "standing in the world".
  world: (a) => {
    const args = ['--world'];
    const slot = flag(a, '--slot');
    if (slot) args.push('--slot', slot);
    if (a.includes('--fresh')) args.push('--fresh');
    if (a.includes('--visible')) args.push('--visible');
    if (a.includes('--console')) args.push('--console');
    run('launch-bg.mjs', args);
  },

  session: (a) => {
    const tier = flag(a, '--tier', 'fast');
    const explicit = a.filter((x) => !x.startsWith('--') && x !== tier);
    const list = explicit.length ? explicit : discoverSuites({ tier }).map((s) => s.path);
    console.log(`session: ${list.length} suite(s) against one running world (${describeTier(tier)})`);
    if (!list.length) { console.error('session: no suite matched - nothing to run'); process.exit(1); }
    const boot = spawnSync(process.execPath, [path.join(REPO, 'tools', 'launch-bg.mjs'), '--world'], { stdio: 'inherit' });
    if ((boot.status ?? 1) !== 0) { console.error('SESSION FAILED: could not start the game'); process.exit(1); }
    const failed = [];
    for (const f of list) {
      console.log(`\n=== ${path.basename(f)}`);
      const res = spawnSync(process.execPath, [path.join(REPO, 'tools', 'runner', 'run-suite.mjs'), f, '--reuse', '--no-watchdog'], { stdio: 'inherit' });
      if ((res.status ?? 1) !== 0) failed.push(path.basename(f));
    }
    try { killGameAndWait(); } catch {}
    console.log(failed.length ? `\nSESSION FAILED: ${failed.join(', ')}` : `\nSESSION OK - ${list.length} suite(s)`);
    process.exit(failed.length ? 1 : 0);
  },

  ship: (a) => {
    const arg = a.find((x) => !x.startsWith('--'));
    if (!arg) { console.error('usage: do.mjs ship <mod> [--stage]'); process.exit(2); }
    const mod = fs.existsSync(path.join(WORK, arg)) ? path.join(WORK, arg) : arg;
    const stage = a.includes('--stage') ? ['--stage'] : [];
    const steps = [
      ['build-mod.mjs', [mod]],
      ['preflight.mjs', [mod]],
      ['pack-workshop.mjs', [mod]],
      ['release.mjs', [mod, ...stage]],
    ];
    for (const [script, args] of steps) {
      console.log(`\n=== ${script} ${args.join(' ')}`);
      const res = spawnSync(process.execPath, [path.join(REPO, 'tools', script), ...args], { stdio: 'inherit' });
      if ((res.status ?? 1) !== 0) { console.error(`SHIP FAILED at ${script}`); process.exit(res.status ?? 1); }
    }
    console.log(`\nSHIP OK  ${mod}`);
  },

  clean: () => {
    const drop = ['_runs', '_state', '_compat'].map((d) => path.join(WORK, d));
    let bytes = 0;
    for (const d of drop) {
      if (!fs.existsSync(d)) continue;
      const size = (function walk(p) { let n = 0; for (const e of fs.readdirSync(p, { withFileTypes: true })) { const q = path.join(p, e.name); n += e.isDirectory() ? walk(q) : fs.statSync(q).size; } return n; })(d);
      bytes += size;
      fs.rmSync(d, { recursive: true, force: true });
      console.log(`removed ${path.relative(REPO, d)}  (${(size / 1048576).toFixed(1)} MB)`);
    }
    for (const name of fs.readdirSync(WORK)) {
      for (const sub of ['obj', 'bin']) {
        const p = path.join(WORK, name, 'src', sub);
        if (fs.existsSync(p)) { fs.rmSync(p, { recursive: true, force: true }); console.log(`removed ${path.relative(REPO, p)}`); }
      }
      const dist = path.join(WORK, name, 'dist');
      if (fs.existsSync(dist)) {
        for (const f of fs.readdirSync(dist)) if (f.endsWith('.deps.json') || f.endsWith('.pdb')) fs.rmSync(path.join(dist, f));
      }
    }
    console.log(`CLEAN OK  ${(bytes / 1048576).toFixed(1)} MB freed`);
  },
};

if (!task || !TASKS[task]) {
  console.log(Object.keys(TASKS).map((k) => `  node tools/do.mjs ${k}`).join('\n'));
  process.exit(task ? 2 : 0);
}
TASKS[task](rest);
