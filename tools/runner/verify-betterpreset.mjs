// v_g4: verifies the Better Preset workshop mod's own UI end to end.
//   * the preset slot limit really moved past vanilla 15 (creates 16 more slots)
//   * the search box filters the slot list (and clearing it restores the list)
//   * the favourite button writes the mod's own save key and the favourites-only filter hides the rest
// Usage: node tools/runner/verify-betterpreset.mjs [--add 16] [--keep] [--no-restart]
// Evidence: work/_runs/betterpreset-verify/<stamp>/evidence.json + screenshots.
import fs from 'node:fs';
import path from 'node:path';
import { RUNS, getJson, tryJson, sleep, waitFor, waitBridge, ensureRunning, backupSaves, restoreSaves } from './lib.mjs';

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
  return (r.panels || []).filter((p) => p.name === 'PresetPanel').flatMap((p) => p.texts || []);
};
const countText = async () => (await panelTexts()).find((t) => /^预设 \d+\/\d+$/.test(t)) || null;
const activeSlots = async () => {
  const r = await ui('active', 'PresetGroup');
  const root = (r.hits || []).find((h) => h.name === 'PresetGroup Scroll View');
  return (root?.children || []).filter((c) => c.name === 'SlotOrigin(Clone)' && String(c.active).toLowerCase() === 'true').length;
};

try {
  backupSaves('bp');
  if (!args.includes('--no-restart')) {
    await tryJson('/disarm');
    await ensureRunning({ restart: true, log: console.log });
    await waitBridge(180000);
    await sleep(4000);
  }
  // The panel is a lobby panel but UIBase.Open works in any scene, so no title juggling needed.
  let opened = null;
  for (let i = 0; i < 40 && !opened?.opened; i++) {
    opened = await ui('open', 'PresetPanel');
    if (!opened?.opened) await sleep(1000);
  }
  note('panel opens', !!opened?.opened, { matched: opened?.matched });
  await sleep(1200);
  await shot('01-panel');

  const before = await countText();
  const beforeSlots = await activeSlots();
  note('count text before', /预设 \d+\/100/.test(before || ''), { before, beforeSlots });

  for (let i = 0; i < addCount; i++) {
    await ui('click', 'AddSlot');
    await sleep(300);
  }
  await sleep(800);
  const after = await countText();
  const afterSlots = await activeSlots();
  const n = Number((after || '').match(/预设 (\d+)\/100/)?.[1] || 0);
  note('slot limit past vanilla 15', n > 15 && afterSlots > 15 && afterSlots >= beforeSlots + addCount - 1,
    { before, after, beforeSlots, afterSlots });
  await shot('02-slots-created');

  await ui('settext', 'BetterPresetSearch', String(n));
  await sleep(900);
  const filtered = await activeSlots();
  note('search narrows the list', filtered < afterSlots && filtered >= 1, { query: String(n), filtered, afterSlots });
  await shot('03-search');
  await ui('settext', 'BetterPresetSearch', '');
  await sleep(900);
  const cleared = await activeSlots();
  note('clearing the search restores the list', cleared === afterSlots, { cleared, afterSlots });

  const badgesBefore = (await ui('active', 'FavoriteBadge')).matched;
  await ui('click', 'BetterPresetFavoriteButton');
  await sleep(900);
  const badges = await ui('active', 'FavoriteBadge');
  const badge = (badges.hits || [])[0]?.name || null;
  const slotIndex = Number((badge || '').match(/_(\d+)$/)?.[1] ?? -1);
  const key = slotIndex >= 0 ? `BetterPreset_${slotIndex}_Favorite` : null;
  const saved = key ? await tryJson(`/savekey?key=${encodeURIComponent(key)}`) : null;
  note('favourite button writes the mod key', badges.matched > badgesBefore && saved?.exists === true && saved?.value === '1',
    { badgesBefore, badge, key, saved });
  await shot('04-favourite');

  await ui('click', 'BetterPresetFavoriteFilter');
  await sleep(900);
  const onlyFav = await activeSlots();
  note('favourites-only hides the rest', onlyFav === Math.max(badges.matched, 1) && onlyFav < cleared,
    { onlyFav, badges: badges.matched, cleared });
  await shot('05-favourites-only');
  await ui('click', 'BetterPresetFavoriteFilter');
  await sleep(900);
  const unfiltered = await activeSlots();
  note('filter off restores the list', unfiltered === cleared, { unfiltered, cleared });

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
