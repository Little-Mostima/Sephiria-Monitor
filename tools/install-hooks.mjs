#!/usr/bin/env node
// Install the commit gate into both repos. Hooks live in .githooks/ (versioned) and are activated
// with core.hooksPath - .git/hooks is not versioned, so a hook placed there silently disappears on
// the next clone and nobody notices until the tree has already drifted.
//
//   node tools/install-hooks.mjs          # install / refresh
//   node tools/install-hooks.mjs --check  # exit 1 when a repo is not gated
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

import { REPO } from './config.mjs';

const OUTER = path.resolve(REPO, '..', '..');
const check = process.argv.includes('--check');

const hook = (rel) => `#!/bin/sh
# Commit gate - installed by tools/install-hooks.mjs (core.hooksPath=.githooks).
# Do not edit .git/hooks: it is not versioned and rots. Bypassing with --no-verify skips the only
# check that keeps the tree from drifting, so treat a failing gate as work to do, not an obstacle.
exec node "$(git rev-parse --show-toplevel)/${rel}" gate
`;

const repos = [
  { dir: OUTER, rel: '_work/repo/tools/do.mjs' },
  { dir: REPO, rel: 'tools/do.mjs' },
];

let bad = 0;
for (const r of repos) {
  const dir = path.join(r.dir, '.githooks');
  const file = path.join(dir, 'pre-commit');
  const configured = (spawnSync('git', ['-C', r.dir, 'config', 'core.hooksPath'], { encoding: 'utf8' }).stdout || '').trim();
  const installed = configured === '.githooks' && fs.existsSync(file);
  if (check) {
    if (!installed) {
      bad++;
      console.log('missing  ' + r.dir + '  (hooksPath=' + (configured || 'unset') + ') - run node tools/install-hooks.mjs');
    } else console.log('ok       ' + r.dir);
    continue;
  }
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(file, hook(r.rel), { mode: 0o755 });
  spawnSync('git', ['-C', r.dir, 'config', 'core.hooksPath', '.githooks'], { stdio: 'inherit' });
  console.log('installed ' + path.join(r.dir, '.githooks', 'pre-commit') + ' -> ' + r.rel + ' gate');
}
process.exit(bad ? 1 : 0);
