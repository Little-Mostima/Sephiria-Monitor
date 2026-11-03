// v_g4: verifies the Better Preset workshop mod's own UI end to end.
//   * the preset slot limit really moved past vanilla 15 (creates more slots)
//   * the search box filters the slot list (and clearing it restores the list)
//   * the favourite button writes the mod's own save key and the favourites-only filter hides the rest
// Usage: node tools/runner/verify-betterpreset.mjs [--add 16] [--keep] [--no-restart]
// Evidence: work/_runs/betterpreset-verify/<stamp>/evidence.json + screenshots.
import fs from 'node:fs';
import path from 'node:path';
import { RUNS, tryJson, sleep, waitFor, waitBridge, ensureRunning, backupSaves, restoreSaves } from './lib.mjs';

const args = process.argv.slice(2);
const addCount = Number(args.includes('--add') ? args[args.indexOf('--add') + 1] : 16);
const outDir = path.join(RUNS, 'betterpreset-verify', new Date().toISOString().replace(/[:.]/g, '-'));
fs.mkdirSync(outDir, { recursive: true });

const evidence = { startedAt: new Date().toISOString(), addCount, checks: [], screenshots: [] };
const save = () => fs.writeFileSync(path.join(outDir, 'evidence.json'), JSON.stringify(evidence, null, 2));
const note = (name, pass, detail) => {
  evidence.checks.push({ name, pass, detail });
  console.log(`${pass ? 'PASS' : 'FAIL'}  ${name}  ${JSON.stringify(detail)}`);
};
const ui = (action, name = '', value = '') =>
  tryJson(`/ui?action=${encodeURIComponent(action)}&name=${encodeURIComponent(name)}&value=${encodeURIComponent(value)}`, 25000);
const shot = async (label) => {
  const r = await tryJson(`/screenshot?name=${encodeURIComponent(label)}`, 25000);
  evidence.screenshots.push({ label, path: r?.path });
  return r?.path;
};
const panelTexts = async () => {
  const r = await ui('text');
  return (r?.panels || []).filter((p) => p.name === 'PresetPanel').flatMap((p) => p.texts || []);
};
const countText = async () => (await panelTexts()).find((t) => /^预设 \d+\/\d+$/.test(t)) || null;
const waitCountText = async (timeoutMs = 15000) => {
  const until = Date.now() + timeoutMs;
  for (;;) {
    const t = await countText();
    if (t || Date.now() > until) return t;
    await sleep(500);
  }
};
const activeSlots = async () => {
  const r = await ui('active', 'PresetGroup');
  const root = (r?.hits || []).find((h) => h.name === 'PresetGroup Scroll View');
  return (root?.children || []).filter((c) => c.name === 'SlotOrigin(Clone)' && String(c.active).toLowerCase() === 'true').length;
};
const badgeNames = async () => ((await ui('active', 'FavoriteBadge'))?.hits || []).map((h) => h.name);

try {
  backupSaves('bp');
  if (!args.includes('--no-restart')) {
    await tryJson('/disarm');
    await ensureRunning({ restart: true, log: console.log });
    await waitBridge(180000);
    await sleep(4000);
  }
  // At the title screen a cloud-save conflict dialog can swallow the panel, so make sure we are
  // in a world first (the panel is a lobby panel, but UIBase.Open works in any scene).
  const health = await tryJson('/health');
  if (health?.scene === 'Title') {
    evidence.enter = await tryJson('/enter?slot=SLOT2', 60000);
    await waitFor(async () => (await tryJson('/health'))?.scene === 'MainWorld', { timeoutMs: 90000, label: 'MainWorld' });
    await sleep(1500);
  }

  const isOpen = (r) => !!(r?.hits || []).some((h) => h.opened);
  let opened = null;
  for (let i = 0; i < 40 && !isOpen(opened); i++) {
    opened = await ui('open', 'PresetPanel');
    if (!isOpen(opened)) await sleep(1000);
  }
  note('panel opens', isOpen(opened), { matched: opened?.matched, hits: opened?.hits });
  await sleep(1200);
  await shot('01-panel');

  // Normalise the panel state left over from earlier runs: no search text, favourites-only off.
  await ui('settext', 'BetterPresetSearch', '');
  await sleep(600);
  let total = await activeSlots();
  if (total === 0) {
    await sleep(1500);
    total = await activeSlots();
  }
  const countBefore = await waitCountText();
  if (countBefore && total > 0 && Number(countBefore.match(/预设 (\d+)\//)?.[1]) !== total) {
    // favourites-only is on from a previous run
    await ui('click', 'BetterPresetFavoriteFilter');
    await sleep(900);
    total = await activeSlots();
  }
  note('count text matches the slot list', /^预设 \d+\/100$/.test(countBefore || '') && Number(countBefore.match(/预设 (\d+)\//)[1]) === total,
    { countBefore, total });
  evidence.baseline = { countBefore, total };

  for (let i = 0; i < addCount; i++) {
    await ui('click', 'AddSlot');
    await sleep(300);
  }
  await sleep(800);
  const countAfter = await waitCountText();
  const afterSlots = await activeSlots();
  const n = Number((countAfter || '').match(/预设 (\d+)\/100/)?.[1] || 0);
  note('slot limit past vanilla 15', n > 15 && afterSlots > 15 && afterSlots >= total + addCount - 1,
    { countBefore, countAfter, total, afterSlots });
  await shot('02-slots-created');

  await ui('settext', 'BetterPresetSearch', String(n));
  await sleep(900);
  const filtered = await activeSlots();
  note('search narrows the list', filtered >= 1 && filtered < afterSlots, { query: String(n), filtered, afterSlots });
  await shot('03-search');
  await ui('settext', 'BetterPresetSearch', '');
  await sleep(900);
  const cleared = await activeSlots();
  note('clearing the search restores the list', cleared === afterSlots && afterSlots > 0, { cleared, afterSlots });

  const badgesBefore = await badgeNames();
  await ui('click', 'BetterPresetFavoriteButton');
  await sleep(900);
  const badgesAfter = await badgeNames();
  const badge = badgesAfter.find((b) => !badgesBefore.includes(b)) || badgesAfter[0] || null;
  const slotIndex = Number((badge || '').match(/_(\d+)$/)?.[1] ?? -1);
  const key = slotIndex >= 0 ? `BetterPreset_${slotIndex}_Favorite` : null;
  const saved = key ? await tryJson(`/savekey?key=${encodeURIComponent(key)}`) : null;
  note('favourite button writes the mod key', badgesAfter.length !== badgesBefore.length && saved?.exists === true && saved?.value === '1',
    { badgesBefore, badgesAfter, key, saved });
  await shot('04-favourite');

  // Make sure at least one favourite exists, then the filter must show exactly those.
  if (badgesAfter.length === 0) {
    await ui('click', 'BetterPresetFavoriteButton');
    await sleep(900);
  }
  const favourites = (await badgeNames()).length;
  await ui('click', 'BetterPresetFavoriteFilter');
  await sleep(900);
  const onlyFav = await activeSlots();
  note('favourites-only hides the rest', favourites >= 1 && onlyFav === favourites && onlyFav < cleared,
    { onlyFav, favourites, cleared });
  await shot('05-favourites-only');
  await ui('click', 'BetterPresetFavoriteFilter');
  await sleep(900);
  const unfiltered = await activeSlots();
  note('filter off restores the list', unfiltered === cleared && cleared > 0, { unfiltered, cleared });

  const failed = evidence.checks.filter((c) => !c.pass);
  evidence.passed = failed.length === 0;
  evidence.finishedAt = new Date().toISOString();
  save();
  console.log(failed.length ? `\n${failed.length} check(s) failed` : '\nall checks passed');
  if (failed.length) process.exitCode = 1;
} catch (e) {
  evidence.error = String((e && e.stack) || e);
  save();
  console.error(e);
  process.exitCode = 1;
} finally {
  if (!args.includes('--keep')) {
    try { await ui('close', 'PresetPanel'); } catch {}
    try { restoreSaves('bp'); } catch {}
  }
  console.log(`evidence: ${path.join(outDir, 'evidence.json')}`);
}
