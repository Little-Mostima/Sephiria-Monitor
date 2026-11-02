#!/usr/bin/env node
// Doc rot check: node tools/doccheck.mjs [--json]
// 拿源码/索引里的真实数字去校验文档里写死的版本号与计数。
import fs from 'node:fs';
import path from 'node:path';
import { REPO } from './runner/lib.mjs';

const DOCS_REPO = path.resolve(REPO, '..', '..');   // D:\AscensionMod
const JSON_OUT = process.argv.includes('--json');

function walk(dir, out = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (e.name === 'index' && dir.endsWith('_re')) continue;
    if (e.name.startsWith('.')) continue;
    const p = path.join(dir, e.name);
    if (e.isDirectory()) { if (e.name !== 'node_modules') walk(p, out); }
    else if (e.name.endsWith('.md')) out.push(p);
  }
  return out;
}

// ---- truth ----
const pluginSrc = fs.readFileSync(path.join(REPO, 'SephiriaMonitor', 'MonitorPlugin.cs'), 'utf8');
const version = (pluginSrc.match(/Version\s*=\s*"(\d+\.\d+\.\d+)"/) || [])[1] || '?';
const catalog = path.join(DOCS_REPO, '_re', 'CATALOG.tsv');
const catalogCount = fs.existsSync(catalog) ? fs.readFileSync(catalog, 'utf8').trim().split('\n').length - 1 : 0;
const suites = [];
for (const name of fs.readdirSync(path.join(REPO, 'work'))) {
  const t = path.join(REPO, 'work', name, 'tests');
  if (name.startsWith('_') || !fs.existsSync(t)) continue;
  for (const f of fs.readdirSync(t)) if (f.endsWith('.suite.json')) suites.push(`${name}/${f}`);
}
const routes = fs.readFileSync(path.join(DOCS_REPO, '_re', 'index', 'plugin-routes.tsv'), 'utf8').trim().split('\n').length - 1;
const mods = fs.readdirSync(path.join(REPO, 'work')).filter((n) => !n.startsWith('_') && fs.existsSync(path.join(REPO, 'work', n, 'metadata.json'))).length;

const truth = { version, catalog: catalogCount, suites: suites.length, routes, mods };

// ---- rules ----
const RULES = [
  { id: 'plugin-version', re: /(?:SephiriaMonitor|插件)\s*`?v?(\d+\.\d+\.\d+)`?|v=(\d+\.\d+\.\d+)/g, expect: version, label: 'plugin version',
    skip: (line) => /2026-\d|当时|起|以前的|历史|版本记录/.test(line) },
  { id: 'catalog-count', re: /(\d+)\s*篇/g, expect: catalogCount, label: 'CATALOG 篇数', only: /CATALOG|资产库|文档库|沉淀/,
    skip: (line) => !/CATALOG|总目录|资产库|沉淀/.test(line) || /官方|playbooks|testing\\\\|decisions|memory/.test(line) },
  { id: 'suite-count', re: /(\d+)\s*个?\s*套件/g, expect: suites.length, label: '套件数' },
  { id: 'route-count', re: /(\d+)\s*条?\s*路由/g, expect: routes, label: '路由数' },
  { id: 'mod-count', re: /(\d+)\s*个\s*(?:成品)?mod/gi, expect: mods, label: 'mod 数' },
];

const SKIP_FILES = /(VALIDATION|MODTEST|GAME-BASELINE)\.md$/;
const files = [...walk(path.join(DOCS_REPO, '_re')), path.join(DOCS_REPO, 'START_HERE.md')].filter((f) => fs.existsSync(f) && !SKIP_FILES.test(f));
const problems = [];
for (const file of files) {
  const text = fs.readFileSync(file, 'utf8');
  const lines = text.split('\n');
  for (const rule of RULES) {
    if (rule.only && !rule.only.test(text)) continue;
    lines.forEach((line, i) => {
      if (!/[\u4e00-\u9fa5]/.test(line) && rule.id !== 'plugin-version') return;
      if (rule.skip && rule.skip(line)) return;
      for (const m of line.matchAll(rule.re)) {
        const found = m[1] || m[2];
        if (rule.id === 'plugin-version' && !/^\d+\.\d+\.\d+$/.test(found)) continue;
        if (String(found) !== String(rule.expect)) {
          problems.push({ file: path.relative(DOCS_REPO, file), line: i + 1, rule: rule.label, found, expect: rule.expect, text: line.trim().slice(0, 110) });
        }
      }
    });
  }
}

if (JSON_OUT) console.log(JSON.stringify({ truth, problems }, null, 2));
else {
  console.log(`truth  version=${version} catalog=${catalogCount} suites=${suites.length} routes=${routes} mods=${mods}`);
  if (!problems.length) console.log('DOCCHECK OK — no stale version/count in docs');
  else {
    console.log(`DOCCHECK  ${problems.length} stale value(s):`);
    for (const p of problems) console.log(`  ${p.file}:${p.line}  ${p.rule}: found ${p.found}, truth ${p.expect}\n      ${p.text}`);
  }
}
process.exit(problems.length ? 1 : 0);