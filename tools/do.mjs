#!/usr/bin/env node
// One entry point for the whole workflow. Every task is a thin wrapper: the logic lives in the
// script it calls, so there is exactly one implementation of each thing.
//
//   node tools/do.mjs build <mod> [--deploy]   build one mod (shared props, version from metadata.json)
//   node tools/do.mjs test [<mod>|--all] [--skip-tests|--skip-build]   preflight
//   node tools/do.mjs fast                     preflight fast tier only (no cold starts)
//   node tools/do.mjs pack <mod>               package + validate the zip
//   node tools/do.mjs release <mod> [--stage]  release checklist (+ stage into WorkshopLocal)
//   node tools/do.mjs compat [--static|--pairs|--all]   mod-to-mod compatibility
//   node tools/do.mjs docs                     doc rot check
//   node tools/do.mjs doctor                   environment check
//   node tools/do.mjs new <name> [--kind content --base-id 930000]
//   node tools/do.mjs qc [<mod>|--all]         content quality checks
//   node tools/do.mjs selfcheck                plugin self-check (routes, hooks, schema)
//   node tools/do.mjs review [--json]          review status of the working tree
//   node tools/do.mjs dev [--watch]            dev loop (build + deploy on change)
//   node tools/do.mjs world [--slot N] [--fresh] [--visible]   launch and enter the world (quick channel)
//   node tools/do.mjs watch [--tier smoke|fast|full]  re-run checks whenever a mod or doc changes
//   node tools/do.mjs session [suite...] [--tier fast|full]  one cold start, then run suites with --reuse
//   node tools/do.mjs ship <mod> [--stage]     build -> full preflight -> pack -> release (one publish path)
//   node tools/do.mjs clean                    drop test runs / snapshots / build leftovers
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { REPO, WORK, DOCS, killGameAndWait } from './runner/lib.mjs';
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
  fast: (a) => run('preflight.mjs', ['--all', '--tier', 'fast', ...a]),
  pack: (a) => run('pack-workshop.mjs', a),
  release: (a) => run('release.mjs', a),
  compat: (a) => run('compat.mjs', a),
  docs: (a) => run('doccheck.mjs', a),
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

  watch: (a) => {
    // Default 'fast', not 'smoke': smoke means "static checks only", so the old default made the
    // watcher report green while never running a single suite.
    const tier = flag(a, '--tier', 'fast');
    const roots = [];
    for (const name of fs.readdirSync(WORK)) {
      const dir = path.join(WORK, name);
      if (!fs.existsSync(path.join(dir, 'metadata.json'))) continue;
      for (const sub of ['src', 'Localization', 'data', 'costume']) {
        const p = path.join(dir, sub);
        if (fs.existsSync(p)) roots.push(p);
      }
    }
    if (fs.existsSync(DOCS)) roots.push(DOCS);
    const stamp = () => {
      let n = 0;
      const walk = (p) => {
        for (const e of fs.readdirSync(p, { withFileTypes: true })) {
          const q = path.join(p, e.name);
          if (e.isDirectory()) { if (e.name !== 'obj' && e.name !== 'bin' && e.name !== 'node_modules') walk(q); }
          else { const st = fs.statSync(q); n += st.size + Math.floor(st.mtimeMs / 1000); }
        }
      };
      for (const r of roots) walk(r);
      return n;
    };
    console.log(`watching ${roots.length} roots - ${describeTier(tier)} - Ctrl+C to stop`);
    let last = stamp();
    let running = false;
    setInterval(() => {
      if (running) return;
      const now = stamp();
      if (now === last) return;
      last = now;
      running = true;
      console.log(`\n[${new Date().toLocaleTimeString()}] change detected -> preflight --all --tier ${tier}`);
      const res = spawnSync(process.execPath, [path.join(REPO, 'tools', 'preflight.mjs'), '--all', '--tier', tier], { stdio: 'inherit' });
      console.log(res.status === 0 ? 'WATCH OK' : 'WATCH FAILED');
      running = false;
      last = stamp();
    }, 1500);
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
