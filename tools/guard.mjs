#!/usr/bin/env node
// Structural guards: the invariants whose absence made the same bug get re-fixed in a different
// place every time. Static by default (no game, ~2 s); --world adds the live enter-world contract.
//
//   node tools/guard.mjs            # static guards
//   node tools/guard.mjs --world    # + launch the game and assert the enter-world contract
//   node tools/guard.mjs --json
//
// Exit code 1 when any guard fails. Warnings do not fail.
import fs from 'node:fs';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';

import { REPO, DOCS, PORT, PYTHON, GAME } from './config.mjs';
import { scan as scanEncoding } from './encoding.mjs';
import { killGameAndWait } from './runner/lib.mjs';

const asJson = process.argv.includes('--json');
const withWorld = process.argv.includes('--world');
const rows = [];
const add = (name, state, detail) => rows.push({ name, state, detail });
const rel = (p) => path.relative(REPO, p).replace(/\\/g, '/');
const brief = (list, n) => list.slice(0, n).join('; ') + (list.length > n ? ' (+' + (list.length - n) + ')' : '');

function walk(dir, filter, out = []) {
  if (!fs.existsSync(dir)) return out;
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (e.name === 'node_modules' || e.name === '.git' || e.name === 'obj' || e.name === 'bin') continue;
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p, filter, out);
    else if (filter(p)) out.push(p);
  }
  return out;
}

// ---------- 1. no script may point at a path that no longer exists ----------
// The 2026-11-04 restructure moved _re/assets -> _re/{volumes,dump}. A script that keeps the old
// literal still looks fine until someone runs it, which is how a fix lands in the wrong file.
{
  const dead = ['_re\\assets', '_re/assets', '_re\\modding', '_re/modding', 'monitor-live.ps1', '_work\\survey', '_work/survey', '_work\\_dump'];
  const scripts = [
    ...walk(path.join(DOCS, 'index'), (p) => /\.(py|mjs|ps1)$/.test(p)),
    ...walk(path.join(DOCS, 'MODDEV', 'tools'), (p) => /\.(py|mjs|ps1)$/.test(p)),
    ...walk(path.join(REPO, 'tools'), (p) => /\.(mjs|ps1)$/.test(p)),
  ];
  const hits = [];
  for (const f of scripts) {
    if (path.basename(f) === 'guard.mjs') continue; // this file lists the dead paths on purpose
    const text = fs.readFileSync(f, 'utf8');
    for (const needle of dead) if (text.includes(needle)) hits.push(rel(f) + ' -> ' + needle);
  }
  add('paths', hits.length ? 'fail' : 'ok', hits.length ? brief(hits, 8) : scripts.length + ' script(s), no dead path');
}

// ---------- 2. no doc may reference a file that no longer exists ----------
{
  const dead = ['HANDOFF.md', 'monitor-live.ps1', '_re/assets', '_re\\assets', '_re/modding', '_re\\modding',
    'build-customstats.py', 'build-floor-report.py', 'build-input-bindings.py', 'build-miniscript.py',
    'build-panels.py', 'build-quests.py', 'build-savekeys.py', 'build-scene-inventory.py', 'build-statuses.py',
    'build-systems.py', 'build-xml-schema.py', 'gen-calc-volume.py', 'gen-enemy-volume.py', 'gen-map-volume.py',
    'gen-relic-volume.py', 'gen-status-volume.py', 'gen-systems-volumes.py', 'gen-weapon-volume.py',
    'merge-indices.py', 'passage-check.py', 'room-purpose.py', 'analyze-survey.py', 'survey.py'];
  const docs = [
    ...walk(DOCS, (p) => p.endsWith('.md')),
    path.join(REPO, '..', 'START_HERE.md'),
    path.join(REPO, 'README.md'),
  ].filter((p) => fs.existsSync(p) && !p.includes(path.join(DOCS, 'research')));
  // A line that says the file is gone ("已删除"/"已退役"/"deleted") is a record, not a stale pointer.
  const gone = /删除|退役|deleted|retired/i;
  const hits = [];
  for (const f of docs) {
    const lines = fs.readFileSync(f, 'utf8').split('\n');
    for (let i = 0; i < lines.length; i++) {
      if (gone.test(lines[i])) continue;
      for (const needle of dead) if (lines[i].includes(needle)) hits.push(rel(f) + ':' + (i + 1) + ' -> ' + needle);
    }
  }
  add('docs-dead', hits.length ? 'fail' : 'ok', hits.length ? brief(hits, 8) : docs.length + ' doc(s), no dead reference');
}

// ---------- 3. every relative markdown link resolves ----------
{
  // official/ is a verbatim copy of the game's upstream docs (its relative links point into that repo),
  // research/ holds raw notes - neither is ours to keep link-clean.
  const docs = walk(DOCS, (p) => p.endsWith('.md'))
    .filter((p) => !p.includes(path.join(DOCS, 'official')) && !p.includes(path.join(DOCS, 'research')))
    .concat([path.join(REPO, '..', '..', 'START_HERE.md')]).filter((p) => fs.existsSync(p));
  const bad = [];
  for (const f of docs) {
    const text = fs.readFileSync(f, 'utf8');
    for (const m of text.matchAll(/\[[^\]]*\]\(([^)#]+?)\)/g)) {
      const t = m[1].trim();
      if (!t || t.startsWith('http') || t.startsWith('mailto:') || t.startsWith('#')) continue;
      const target = path.resolve(path.dirname(f), decodeURIComponent(t.replace(/%20/g, ' ')));
      if (!fs.existsSync(target)) bad.push(rel(f) + ' -> ' + t);
    }
  }
  add('links', bad.length ? 'fail' : 'ok', bad.length ? brief(bad, 8) : docs.length + ' doc(s), all links resolve');
}

// ---------- 4. hooks.json is one generated artifact, not two hand-edited copies ----------
{
  const a = path.join(DOCS, 'MODDEV', 'hooks.json');
  const b = path.join(REPO, 'SephiriaMonitor', 'hooks.json');
  if (!fs.existsSync(a) || !fs.existsSync(b)) add('hooks', 'fail', 'hooks.json missing in _re or repo');
  else {
    const same = fs.readFileSync(a).equals(fs.readFileSync(b));
    add('hooks', same ? 'ok' : 'fail', same ? 'both copies byte-identical' : 'copies differ - run _re/index/gen-hooks.py (single source)');
  }
}

// ---------- 5. no stale version/count in docs ----------
{
  const res = spawnSync(process.execPath, [path.join(REPO, 'tools', 'doccheck.mjs')], { encoding: 'utf8' });
  const out = ((res.stdout || '') + (res.stderr || '')).trim();
  add('numbers', res.status === 0 ? 'ok' : 'fail', res.status === 0 ? 'no stale version/count' : out.split('\n').slice(-6).join(' | '));
}

// ---------- 5c. the task board is intact ----------
// The board is the one file every session edits by hand or by CLI; a duplicated id or a done item
// with no note means the next reader cannot tell what was actually finished.
{
  const script = path.join(REPO, 'tools', 'board.mjs');
  if (!fs.existsSync(script)) add('board', 'fail', 'tools/board.mjs missing');
  else {
    const res = spawnSync(process.execPath, [script, 'check'], { encoding: 'utf8' });
    const out = ((res.stdout || '') + (res.stderr || '')).trim();
    add('board', res.status === 0 ? 'ok' : 'fail', res.status === 0 ? out.replace(/^BOARD OK - /, '') : out.split('\n').slice(-4).join(' | '));
  }
}

// ---------- 5b. the knowledge base still describes the installed game ----------
// A game patch silently invalidates every line number and every count in _re/. One hash check
// turns "why is this wrong" into "the game moved".
{
  const script = path.join(DOCS, 'index', 'check-baseline.py');
  if (!fs.existsSync(script)) add('baseline', 'warn', 'check-baseline.py missing');
  else {
    const res = spawnSync(PYTHON, [script], { encoding: 'utf8' });
    if (res.status === 0) add('baseline', 'ok', 'installed game matches _re/GAME-BASELINE.json');
    else if (res.status === 1) add('baseline', 'fail', 'game binaries changed - re-decompile and re-verify _re/ (check-baseline.py --write after)');
    else add('baseline', 'warn', 'baseline missing or game not found (set SEPHIRIA_GAME_DIR)');
  }
}

// ---------- 6. every script still parses ----------
{
  const mjs = walk(path.join(REPO, 'tools'), (p) => p.endsWith('.mjs'));
  // 并行跑 node --check：23 个文件串行 spawn 要 1.2 s，并行约 0.3 s
  const checkOne = (f) => new Promise((resolve) => {
    const c = spawn(process.execPath, ['--check', f], { stdio: 'ignore' });
    c.on('exit', (code) => resolve(code === 0 ? null : rel(f)));
    c.on('error', () => resolve(rel(f)));
  });
  const bad = (await Promise.all(mjs.map(checkOne))).filter(Boolean);
  const py = walk(path.join(DOCS, 'index'), (p) => p.endsWith('.py'));
  // py_compile 一次收全部文件：20 次 spawn 变 1 次
  const pyBad = [];
  if (py.length) {
    const res = spawnSync(PYTHON, ['-m', 'py_compile', ...py], { encoding: 'utf8' });
    if (res.status !== 0) {
      const msg = ((res.stderr || '') + (res.stdout || '')).trim().split(/\r?\n/).filter(Boolean).slice(-2).join(' | ');
      pyBad.push(msg || 'py_compile failed');
    }
  }
  const detail = [];
  if (bad.length) detail.push('mjs: ' + bad.join(', '));
  if (pyBad.length) detail.push('py: ' + pyBad.join(', '));
  add('syntax', detail.length ? 'fail' : 'ok', detail.length ? detail.join('; ') : mjs.length + ' mjs + ' + py.length + ' py parse');
}

// ---------- 6b. every text file is UTF-8 without BOM ----------
// A GBK or BOM file makes read/grep/JSON.parse/PowerShell disagree about the same bytes; the
// disagreement costs a re-read and a wrong guess, and the wrong guess gets written back.
{
  const { files, problems } = scanEncoding();
  const hard = problems.filter((p) => p.kind !== 'truncated');
  const soft = problems.filter((p) => p.kind === 'truncated');
  if (hard.length) {
    add('encoding', 'fail', brief(hard.map((p) => rel(p.file) + ' (' + p.kind + ')'), 5) + ' - run node tools/encoding.mjs --fix');
  } else if (soft.length) {
    add('encoding', 'warn', brief(soft.map((p) => rel(p.file)), 5) + ' - truncated char(s), needs a human');
  } else {
    add('encoding', 'ok', files.length + ' text files, UTF-8 no BOM');
  }
}

// ---------- 6c. the plugin still compiles, warnings are errors ----------
// The plugin build was never part of any gate, so a broken build or a new deprecation warning
// only showed up when the game was launched - the slowest possible place to find it.
{
  if (!fs.existsSync(path.join(GAME, 'Sephiria_Data', 'Managed', 'Assembly-CSharp.dll'))) {
    add('plugin', 'warn', 'game assemblies not found (set SEPHIRIA_GAME_DIR)');
  } else {
    const t0 = Date.now();
    const res = spawnSync(process.execPath, [path.join(REPO, 'tools', 'plugin.mjs'), '--check'], { encoding: 'utf8' });
    const ms = Date.now() - t0;
    if (res.status === 0) add('plugin', 'ok', 'Release build clean in ' + ms + ' ms');
    else add('plugin', 'fail', ((res.stderr || '') + (res.stdout || '')).trim().split(/\r?\n/).slice(-4).join(' | '));
  }
}

// ---------- 6c2. every doc is in the catalog ----------
// A new .md that nobody rebuilt the catalog for is invisible to q.py cat and to the index - it exists
// on disk and nowhere else. Cheap to check, and it is the step agents skip most often.
{
  const tsv = path.join(DOCS, 'CATALOG.tsv');
  if (!fs.existsSync(tsv)) add('catalog', 'fail', 'CATALOG.tsv missing - run _re/index/build-catalog.py');
  else {
    const listed = new Set();
    for (const line of fs.readFileSync(tsv, 'utf8').split('\n').slice(1)) {
      const cols = line.split('\t');
      if (cols.length > 4) listed.add(cols[4].replace(/\\/g, '/'));
    }
    // Same skip list as build-catalog.py: index/ is generated, docs/ and _sources/ are not catalogued.
    const docs = walk(DOCS, (f) => f.endsWith('.md'))
      .filter((f) => !f.includes(path.join(DOCS, 'official')) && !f.includes(path.join(DOCS, 'research')))
      .filter((f) => path.basename(f) !== 'CATALOG.md')
      .filter((f) => {
        const parts = path.relative(DOCS, f).split(path.sep);
        return parts[0] !== 'index' && !parts.includes('docs') && !parts.includes('_sources');
      });
    const missing = docs
      .map((f) => path.relative(path.resolve(DOCS, '..'), f).replace(/\\/g, '/'))
      .filter((rel) => !listed.has(rel));
    add('catalog', missing.length ? 'fail' : 'ok',
      missing.length ? brief(missing, 5) + ' - run _re/index/build-catalog.py' : docs.length + ' doc(s), all in CATALOG.tsv');
  }
}

// ---------- 6d. C# formatting, only when C# changed ----------
// dotnet format takes ~4 s, so it runs when the working tree has .cs edits - which is exactly when a
// commit is about to happen - and is skipped otherwise. --full forces it.
{
  const full = process.argv.includes('--full');
  const dirty = spawnSync('git', ['-C', REPO, 'status', '--porcelain', '--', '*.cs'], { encoding: 'utf8' });
  const changed = ((dirty.stdout || '') + (dirty.stderr || '')).trim().length > 0;
  if (!changed && !full) add('format', 'ok', 'no C# changes, skipped');
  else {
    const res = spawnSync(process.execPath, [path.join(REPO, 'tools', 'format.mjs')], { encoding: 'utf8' });
    const out = ((res.stdout || '') + (res.stderr || '')).trim();
    add('format', res.status === 0 ? 'ok' : 'fail',
      res.status === 0 ? out.split('\n')[0] : out.split('\n').slice(0, 4).join(' | '));
  }
}

// ---------- 6e. the commit gate is actually installed ----------
// A gate nobody runs is documentation. core.hooksPath is per-clone and can silently be unset, so the
// enforcement itself is checked - otherwise the next clone is back to trusting good intentions.
{
  const outer = path.resolve(REPO, '..', '..');
  const bad = [];
  for (const dir of [outer, REPO]) {
    const hp = spawnSync('git', ['-C', dir, 'config', 'core.hooksPath'], { encoding: 'utf8' });
    const val = (hp.stdout || '').trim();
    const file = path.join(dir, '.githooks', 'pre-commit');
    if (val !== '.githooks') bad.push(path.basename(dir) + ': hooksPath=' + (val || 'unset'));
    else if (!fs.existsSync(file)) bad.push(path.basename(dir) + ': .githooks/pre-commit missing');
  }
  add('precommit', bad.length ? 'fail' : 'ok',
    bad.length ? brief(bad, 4) + ' - run node tools/install-hooks.mjs' : 'both repos gated by .githooks/pre-commit');
}

// ---------- 7. the enter-world contract (opt-in: needs the game) ----------
// One NetworkHost call, phase ready, no dialog fallback. If someone reintroduces a retry loop or
// goes back to clicking the name prompt, hostCalls stops being 1 and this fails.
if (withWorld) {
  const slot = '1';
  const boot = spawnSync(process.execPath, [path.join(REPO, 'tools', 'launch-bg.mjs'), '--world', '--slot', slot], { encoding: 'utf8' });
  const log = ((boot.stdout || '') + (boot.stderr || '')).trim();
  let status = null;
  try {
    const res = await fetch('http://127.0.0.1:' + PORT + '/enterstatus', { signal: AbortSignal.timeout(3000) });
    status = await res.json();
  } catch { /* bridge already gone */ }
  const p = status && status.pending;
  const problems = [];
  if (boot.status !== 0) problems.push('launch-bg exited ' + boot.status);
  if (!p) problems.push('no /enterstatus snapshot');
  else {
    if (p.phase !== 'ready') problems.push('phase=' + p.phase);
    if (p.hostCalls !== 1) problems.push('hostCalls=' + p.hostCalls + ' (must be exactly 1)');
    if (p.state !== 'ok') problems.push('state=' + p.state);
  }
  try { killGameAndWait(); } catch { /* best effort */ }
  add('world', problems.length ? 'fail' : 'ok', problems.length ? problems.join('; ') + ' | ' + log.split('\n').slice(-3).join(' | ') : 'slot ' + slot + ': hostCalls=1 phase=ready scene=MainWorld');
}

const failed = rows.filter((r) => r.state === 'fail');
if (asJson) {
  console.log(JSON.stringify({ ok: failed.length === 0, checks: rows }, null, 2));
} else {
  for (const r of rows) console.log((r.state === 'ok' ? 'ok  ' : r.state === 'warn' ? 'warn' : 'FAIL') + '  ' + r.name.padEnd(10) + ' ' + r.detail);
  console.log(failed.length ? '\nGUARD: ' + failed.length + ' broken invariant(s)' : '\nGUARD OK');
}
process.exit(failed.length ? 1 : 0);
