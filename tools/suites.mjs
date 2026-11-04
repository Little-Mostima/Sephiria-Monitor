// Single suite discovery.
//
// Why this file exists: do.mjs, preflight.mjs and run-suite.mjs each had their own idea of what a
// tier means. do.mjs only accepted tier === 'fast' (a suite without a tier was silently dropped),
// preflight defaulted a missing tier to 'full' and treated tier === 'smoke' as "skip every suite
// and record ok" - and because `do watch` defaulted to smoke, watch never ran a suite at all.
// One rule now: a suite runs when its tier is at or below the requested tier; a missing tier means
// 'full' (the strictest), so a new suite is never silently skipped.
import fs from 'node:fs';
import path from 'node:path';
import { WORK } from './config.mjs';

export const TIERS = ['smoke', 'fast', 'full'];
export const DEFAULT_TIER = 'full';

export function tierRank(tier) {
  const i = TIERS.indexOf(String(tier || '').toLowerCase());
  return i < 0 ? TIERS.length - 1 : i;
}

export function readSuite(file) {
  let meta = {};
  let parseError = null;
  try {
    meta = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
  } catch (e) {
    parseError = String((e && e.message) || e);
  }
  return {
    path: file,
    name: path.basename(file).replace(/\.suite\.json$/, ''),
    mod: path.basename(path.dirname(path.dirname(file))),
    tier: meta.tier || DEFAULT_TIER,
    meta,
    parseError,
  };
}

// dirs: explicit mod directories (absolute). Defaults to every directory under work/.
export function discoverSuites({ dirs = null, root = WORK, tier = DEFAULT_TIER } = {}) {
  const modDirs = dirs || (fs.existsSync(root) ? fs.readdirSync(root).map((n) => path.join(root, n)) : []);
  const out = [];
  for (const dir of modDirs) {
    const tests = path.join(dir, 'tests');
    if (!fs.existsSync(tests)) continue;
    for (const f of fs.readdirSync(tests)) {
      if (!f.endsWith('.suite.json')) continue;
      const suite = readSuite(path.join(tests, f));
      if (tierRank(suite.tier) <= tierRank(tier)) out.push(suite);
    }
  }
  return out.sort((a, b) => a.path.localeCompare(b.path));
}

export function describeTier(tier) {
  const t = String(tier || DEFAULT_TIER).toLowerCase();
  if (t === 'smoke') return 'smoke: static checks only, no game-driven suite';
  if (t === 'fast') return 'fast: static checks + suites marked smoke/fast';
  return 'full: everything, including suites that cold-start the game';
}
