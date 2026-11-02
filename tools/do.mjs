#!/usr/bin/env node
// One entry point for the whole workflow.
//   node tools/do.mjs build <mod> [--deploy]   build one mod (shared props, version from metadata.json)
//   node tools/do.mjs test [<mod>|--all] [--skip-tests|--skip-build]   preflight
//   node tools/do.mjs fast                     preflight fast tier only (no cold starts)
//   node tools/do.mjs pack <mod>               package + validate the zip
//   node tools/do.mjs release <mod> [--stage]  release checklist (+ stage into WorkshopLocal)
//   node tools/do.mjs compat [--static|--pairs|--all]   mod-to-mod compatibility
//   node tools/do.mjs docs                     doc rot check
//   node tools/do.mjs doctor                   environment check
//   node tools/do.mjs new <name> [--kind content --base-id 930000]
//   node tools/do.mjs clean                    drop test runs / snapshots / build leftovers
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { REPO, WORK } from './runner/lib.mjs';

const [, , task, ...rest] = process.argv;
const run = (script, args) => {
  const res = spawnSync(process.execPath, [path.join(REPO, 'tools', script), ...args], { stdio: 'inherit' });
  process.exit(res.status ?? 1);
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