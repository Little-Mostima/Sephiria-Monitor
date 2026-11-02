#!/usr/bin/env node
// What happened to a workshop upload? Combines the live log ring (upload + ticket lines) with
// the durable on-disk state (ModGuardWorkshop cache, submissions zip), so the answer still works
// after a restart - the log ring is in-memory only.
//
//   node tools/review-status.mjs                  # auto: match our dll hash against the cache
//   node tools/review-status.mjs --item 3832391740
//   node tools/review-status.mjs --json
//
// Durable sources on disk (%LocalLow%\TEAMHORAY\Sephiria):
//   ModGuardWorkshop\<fileId>.json   {"published_file_id","dll_sha256","app_id","status"}
//   WorkshopSubmissions\<modId>\<idempotencyKey>.zip
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';

const args = process.argv.slice(2);
const bridge = process.env.SEPHIRIA_BRIDGE || 'http://127.0.0.1:47901';
const asJson = args.includes('--json');
const opt = (name) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : null;
};
const LOCAL = path.join(os.homedir(), 'AppData', 'LocalLow', 'TEAMHORAY', 'Sephiria');
const CACHE = path.join(LOCAL, 'ModGuardWorkshop');
const REPO = path.resolve(path.dirname(new URL(import.meta.url).pathname).replace(/^\/([A-Za-z]:)/, '$1'), '..');

const sha = (p) => crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');

// 1. dll hashes we could have uploaded (work/<mod>/dist/**/*.dll)
const ourDllHashes = new Map();
const workDir = path.join(REPO, 'work');
if (fs.existsSync(workDir)) {
  for (const mod of fs.readdirSync(workDir)) {
    const dist = path.join(workDir, mod, 'dist');
    if (!fs.existsSync(dist)) continue;
    const walk = (dir) => {
      for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
        const p = path.join(dir, e.name);
        if (e.isDirectory()) walk(p);
        else if (e.name.endsWith('.dll')) ourDllHashes.set(sha(p), mod);
      }
    };
    walk(dist);
  }
}

// 2. durable cache entries (one per published/reviewed item)
let entries = [];
if (fs.existsSync(CACHE)) {
  for (const f of fs.readdirSync(CACHE).filter((n) => n.endsWith('.json') && !n.endsWith('.dlls.json'))) {
    try {
      const j = JSON.parse(fs.readFileSync(path.join(CACHE, f), 'utf8').replace(/^\uFEFF/, ''));
      if (!j.published_file_id) continue;
      entries.push({ file: f, itemId: String(j.published_file_id), dll: j.dll_sha256, status: j.status, ours: ourDllHashes.get(j.dll_sha256) || null });
    } catch { /* ignore malformed */ }
  }
}

// 2b. workshop.json may pin the item id after the first publish (steamItemId) - that is the
// most reliable anchor, because the uploaded dll can be rebuilt later.
const pinned = new Map();
for (const mod of fs.existsSync(workDir) ? fs.readdirSync(workDir) : []) {
  const wj = path.join(workDir, mod, 'workshop.json');
  if (!fs.existsSync(wj)) continue;
  try {
    const j = JSON.parse(fs.readFileSync(wj, 'utf8').replace(/^\uFEFF/, ''));
    if (j.steamItemId) pinned.set(String(j.steamItemId), mod);
  } catch { /* ignore */ }
}
for (const e of entries) if (pinned.has(e.itemId)) e.ours = pinned.get(e.itemId);

const wantItem = opt('--item');
let mine = entries.filter((e) => e.ours);
if (wantItem) mine = entries.filter((e) => e.itemId === String(wantItem));
if (!mine.length && entries.length) {
  // fall back to the newest cache file when the hash does not match (e.g. dll rebuilt since upload)
  mine = [entries.sort((a, b) => fs.statSync(path.join(CACHE, b.file)).mtimeMs - fs.statSync(path.join(CACHE, a.file)).mtimeMs)[0]];
}

// 3. submissions on disk
const submissions = [];
const subRoot = path.join(LOCAL, 'WorkshopSubmissions');
if (fs.existsSync(subRoot)) {
  for (const mod of fs.readdirSync(subRoot)) {
    for (const f of fs.readdirSync(path.join(subRoot, mod))) {
      const p = path.join(subRoot, mod, f);
      const st = fs.statSync(p);
      submissions.push({ mod, key: f.replace(/\.zip$/, ''), bytes: st.size, mtime: st.mtime.toISOString() });
    }
  }
}

// 4. live log ring (optional; upload lines only exist until the game restarts)
let ring = { upload: null, sameContent: null, submitted: null, tagWarning: null, tickets: [] };
try {
  const res = await fetch(`${bridge}/logs?n=600`, { signal: AbortSignal.timeout(4000) });
  const { lines } = await res.json();
  const msgs = (lines || []).map((l) => String(l.msg || '').replace(/\s+/g, ' '));
  const pick = (re) => msgs.filter((m) => re.test(m)).slice(-1)[0] || null;
  ring.upload = pick(/\[WorkshopUploader\] (작업 완료|시작)/);
  ring.sameContent = pick(/같은 내용이 이미 있음/);
  ring.submitted = pick(/제출 zip/);
  ring.tagWarning = pick(/카테고리 목록에 없음/);
  const re = /\[ModGuardReviewStatus\]\s+(\S+):\s*(.+?)\s*\(([^)]*)\)/g;
  for (const m of msgs) {
    re.lastIndex = 0;
    let h;
    while ((h = re.exec(m))) ring.tickets.push({ ticket: h[1], status: h[2].trim(), detail: h[3].trim() });
  }
} catch { /* bridge down: disk state is enough */ }

const item = mine[0] || null;
const lastTicket = ring.tickets.slice(-1)[0] || null;
const out = {
  itemId: item ? item.itemId : null,
  mod: item ? item.ours : null,
  status: item ? item.status : null,
  dllSha256: item ? item.dll : null,
  ticket: lastTicket ? lastTicket.ticket : null,
  ticketStatus: lastTicket ? `${lastTicket.status} (${lastTicket.detail})` : null,
  submissions,
  ring,
};

if (asJson) {
  console.log(JSON.stringify(out, null, 2));
  process.exit(0);
}
console.log(`item      ${out.itemId || '(not found - no cache entry matches a built dll)'}${out.mod ? '  (' + out.mod + ')' : ''}`);
console.log(`status    ${out.status || '(unknown)'}   ← ModGuardWorkshop/<id>.json（持久）`);
if (out.ticket) console.log(`ticket    ${out.ticket}  ${out.ticketStatus}`);
for (const s of submissions) console.log(`submit    ${s.mod}/${s.key}.zip  ${s.bytes} B  ${s.mtime}`);
if (ring.upload) console.log(`upload    ${ring.upload.slice(0, 170)}`);
if (ring.sameContent) console.log('note      same content already on the item - only the review ticket was re-sent');
if (ring.tagWarning) console.log('warning   a tag is not a workshop category (uploads but is filtered out)');
if (!ring.upload && !out.ticket) console.log('ring      empty (game restarted since the upload) - the durable lines above still apply');
