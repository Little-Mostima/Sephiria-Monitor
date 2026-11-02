#!/usr/bin/env node
// What happened to a workshop upload? Reads the plugin's live log ring and reports the last
// upload / submission / review lines, so you don't have to guess from the panel.
//
//   node tools/review-status.mjs                 # last upload + review line
//   node tools/review-status.mjs --ticket nfPMl6ZeLDU2
//   node tools/review-status.mjs --json
//
// The review state machine lives in WorkshopReviewState (approved -> proof -> ReadyToPublish ->
// 게시); the game polls the ticket roughly every 30 s while the workshop panel is open.
const args = process.argv.slice(2);
const bridge = process.env.SEPHIRIA_BRIDGE || 'http://127.0.0.1:47901';
const wantTicket = (() => {
  const i = args.indexOf('--ticket');
  return i >= 0 ? args[i + 1] : null;
})();
const asJson = args.includes('--json');

const res = await fetch(`${bridge}/logs?n=600`);
if (!res.ok) {
  console.error(`bridge ${bridge} returned ${res.status}`);
  process.exit(2);
}
const { lines } = await res.json();
const msgs = (lines || []).map((l) => String(l.msg || '').replace(/\s+/g, ' '));

const pick = (re) => msgs.filter((m) => re.test(m)).slice(-1)[0] || null;
const ticketRe = /\[ModGuardReviewStatus\]\s+(\S+):\s*(.+?)\s*\(([^)]*)\)/g;

let reviews = [];
for (const m of msgs) {
  ticketRe.lastIndex = 0;
  let hit;
  while ((hit = ticketRe.exec(m))) {
    reviews.push({ ticket: hit[1], status: hit[2].trim(), detail: hit[3].trim() });
  }
}
if (wantTicket) reviews = reviews.filter((r) => r.ticket === wantTicket);

const upload = pick(/\[WorkshopUploader\] (작업 완료|시작)/);
const sameContent = pick(/같은 내용이 이미 있음/);
const submitted = pick(/제출 zip/);
const tagWarn = pick(/카테고리 목록에 없음/);
const itemId = (() => {
  const m = msgs.join('\n').match(/#(\d{6,})\s*갱신/);
  return m ? m[1] : null;
})();

const last = reviews.slice(-1)[0] || null;
const out = {
  itemId,
  ticket: last ? last.ticket : null,
  status: last ? last.status : null,
  detail: last ? last.detail : null,
  history: reviews.slice(-5),
  upload,
  sameContent,
  submitted,
  tagWarning: tagWarn,
};

if (asJson) {
  console.log(JSON.stringify(out, null, 2));
  process.exit(0);
}
console.log(`item     ${itemId || '(unknown — no upload line in the ring)'}`);
console.log(`ticket   ${out.ticket || '(none)'}`);
console.log(`status   ${out.status || '(none)'}${out.detail ? '  (' + out.detail + ')' : ''}`);
if (out.history.length > 1) {
  console.log('earlier  ' + out.history.slice(0, -1).map((h) => `${h.status}/${h.detail}`).join(' → '));
}
if (upload) console.log(`upload   ${upload.slice(0, 160)}`);
if (sameContent) console.log('note     same content already on the item — only the review ticket was re-sent');
if (tagWarn) console.log('warning  a tag is not a workshop category (uploads but is filtered out)');
