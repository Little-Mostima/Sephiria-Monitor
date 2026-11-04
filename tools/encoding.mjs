#!/usr/bin/env node
// Encoding guard: every text file in the project is UTF-8 without BOM.
//
//   node tools/encoding.mjs                 check (exit 1 when something is wrong)
//   node tools/encoding.mjs --fix           convert GBK / UTF-16 -> UTF-8, strip BOM
//   node tools/encoding.mjs --read <file>   print one file decoded with the detected encoding
//   node tools/encoding.mjs --json          machine-readable result
//
// Why this exists: a GBK or BOM file makes read, grep, JSON.parse, PowerShell and MSBuild
// disagree about the same bytes. Every disagreement costs a re-read and a guess, and the guess
// is sometimes wrong (a GBK decode of UTF-8 bytes silently produces mojibake that then gets
// written back). One rule, one tool, one guard check: UTF-8, no BOM, everywhere.
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { REPO, DOCS } from './config.mjs';
const ROOT = path.resolve(DOCS, '..');           // D:\AscensionMod
const TEXT_EXT = new Set([
  '.cs', '.csproj', '.sln', '.props', '.targets', '.mjs', '.js', '.cjs', '.py', '.md', '.json',
  '.ps1', '.psm1', '.txt', '.yml', '.yaml', '.xml', '.tsv', '.csv', '.cfg', '.ini', '.sh',
  '.bat', '.cmd', '.editorconfig', '.gitignore', '.gitattributes', '.gitmodules',
]);
const SKIP_DIRS = new Set([
  '.git', 'bin', 'obj', 'dist', 'node_modules', 'art', 'catalog', 'rooms', 'textassets',
  'localization', 'official', '_dump', 'peers', 'dotnet', 'AddOns', 'BepInEx',
]);
const MAX_BYTES = 8 * 1024 * 1024;

const utf8 = new TextDecoder('utf-8', { fatal: true });
const gbk = new TextDecoder('gbk');

function isTextFile(p) {
  const base = path.basename(p).toLowerCase();
  return TEXT_EXT.has(path.extname(base)) || TEXT_EXT.has(base);
}

function walk(dir, out, depth = 0) {
  if (depth > 10) return out;
  let ents;
  try { ents = fs.readdirSync(dir, { withFileTypes: true }); } catch { return out; }
  for (const e of ents) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) { if (!SKIP_DIRS.has(e.name)) walk(p, out, depth + 1); continue; }
    if (isTextFile(p)) out.push(p);
  }
  return out;
}

// A multi-byte character whose tail was replaced by '?' during a lossy conversion:
// lead + continuation(s) + 0x3F. Only 3- and 4-byte leads are matched, so "é?" stays clean.
const TRUNCATED = /[\xe3-\xef][\x80-\xbf]\x3f|[\xf0-\xf4][\x80-\xbf][\x80-\xbf]\x3f/g;

/** Inspect one file. Returns null when it is clean. */
export function inspect(file) {
  let buf;
  try { buf = fs.readFileSync(file); } catch (e) { return { file, kind: 'unreadable', detail: e.message }; }
  if (buf.length > MAX_BYTES) return null;
  if (buf.length >= 2 && ((buf[0] === 0xff && buf[1] === 0xfe) || (buf[0] === 0xfe && buf[1] === 0xff))) {
    return { file, kind: 'utf16', detail: buf[0] === 0xff ? 'UTF-16 LE' : 'UTF-16 BE' };
  }
  let body = buf;
  let bom = false;
  if (buf.length >= 3 && buf[0] === 0xef && buf[1] === 0xbb && buf[2] === 0xbf) { body = buf.subarray(3); bom = true; }
  let text;
  try { text = utf8.decode(body); }
  catch {
    // Not UTF-8. On this machine that is almost always GBK (Chinese Windows default).
    return { file, kind: 'gbk', detail: 'not valid UTF-8 (decodes as GBK)' };
  }
  const hits = [];
  const latin = body.toString('latin1');
  TRUNCATED.lastIndex = 0;
  let m;
  while ((m = TRUNCATED.exec(latin)) !== null) {
    const line = latin.slice(0, m.index).split('\n').length;
    const ctx = text.slice(Math.max(0, m.index - 12), m.index + 4).replace(/\r?\n/g, ' ');
    hits.push({ offset: m.index, line, ctx });
  }
  if (bom) return { file, kind: 'bom', detail: 'UTF-8 with BOM', text };
  if (hits.length) return { file, kind: 'truncated', detail: hits.length + ' truncated char(s)', hits, text };
  return null;
}

/** Scan the whole project. */
export function scan({ roots = [REPO, DOCS, path.join(ROOT, 'START_HERE.md'), path.join(ROOT, 'AGENTS.md')] } = {}) {
  const files = [];
  for (const r of roots) {
    let st;
    try { st = fs.statSync(r); } catch { continue; }
    if (st.isFile()) { if (isTextFile(r)) files.push(r); } else walk(r, files);
  }
  const problems = [];
  for (const f of files) {
    const p = inspect(f);
    if (p) problems.push(p);
  }
  return { files, problems };
}

/** Repair what can be repaired without guessing. Returns the list of files written. */
export function fix(problems) {
  const written = [];
  for (const p of problems) {
    if (p.kind === 'bom') {
      fs.writeFileSync(p.file, p.text, 'utf8');
      written.push(p.file);
    } else if (p.kind === 'gbk') {
      const text = gbk.decode(fs.readFileSync(p.file));
      fs.writeFileSync(p.file, text, 'utf8');
      written.push(p.file);
    } else if (p.kind === 'utf16') {
      const buf = fs.readFileSync(p.file);
      const enc = buf[0] === 0xff ? 'utf-16le' : 'utf-16be';
      const text = new TextDecoder(enc).decode(buf.subarray(2));
      fs.writeFileSync(p.file, text, 'utf8');
      written.push(p.file);
    }
  }
  return written;
}

function rel(p) { return path.relative(ROOT, p) || p; }

function main() {
  const argv = process.argv.slice(2);
  const readAt = argv.indexOf('--read');
  if (readAt >= 0) {
    const file = path.resolve(argv[readAt + 1] || '');
    const buf = fs.readFileSync(file);
    let enc = 'utf-8';
    let body = buf;
    if (buf.length >= 2 && buf[0] === 0xff && buf[1] === 0xfe) { enc = 'utf-16le'; body = buf.subarray(2); }
    else if (buf.length >= 2 && buf[0] === 0xfe && buf[1] === 0xff) { enc = 'utf-16be'; body = buf.subarray(2); }
    else if (buf.length >= 3 && buf[0] === 0xef && buf[1] === 0xbb && buf[2] === 0xbf) { enc = 'utf-8 (BOM)'; body = buf.subarray(3); }
    else {
      try { utf8.decode(body); } catch { enc = 'gbk'; }
    }
    const text = enc === 'gbk' ? gbk.decode(body) : new TextDecoder(enc.replace(' (BOM)', '')).decode(body);
    process.stdout.write('--- ' + rel(file) + ' [' + enc + '] ---\n' + text);
    return 0;
  }

  const { files, problems } = scan();
  if (argv.includes('--fix')) {
    const written = fix(problems);
    const left = problems.filter((p) => p.kind === 'truncated' || p.kind === 'unreadable');
    for (const f of written) console.log('fixed  ' + rel(f));
    for (const p of left) console.log('manual ' + rel(p.file) + '  ' + p.detail);
    console.log('encoding: ' + written.length + ' fixed, ' + left.length + ' need a human');
    return left.length ? 1 : 0;
  }
  if (argv.includes('--json')) {
    console.log(JSON.stringify({ scanned: files.length, problems: problems.map((p) => ({ file: rel(p.file), kind: p.kind, detail: p.detail, hits: p.hits })) }, null, 2));
    return problems.length ? 1 : 0;
  }
  for (const p of problems) {
    console.log(p.kind.padEnd(9) + ' ' + rel(p.file) + '  ' + p.detail);
    for (const h of p.hits || []) console.log('          line ' + h.line + ': ...' + h.ctx + '...');
  }
  console.log('encoding: ' + files.length + ' text files, ' + problems.length + ' problem(s)');
  return problems.length ? 1 : 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  process.exitCode = main();
}
