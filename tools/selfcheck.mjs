#!/usr/bin/env node
// End-to-end check of the plugin's bridge + embedded MCP server.
// Requires the game running with the plugin installed.
//
//   node tools/selfcheck.mjs
//   node tools/selfcheck.mjs --bridge http://127.0.0.1:47901

const args = process.argv.slice(2);
const bridgeArg = args.indexOf('--bridge');
const BRIDGE = (bridgeArg >= 0 ? args[bridgeArg + 1] : process.env.SEPHIRIA_BRIDGE || 'http://127.0.0.1:47901').replace(/\/$/, '');

let pass = 0;
let fail = 0;
const failures = [];

function ok(name, detail) {
  pass++;
  console.log(`  PASS  ${name}${detail ? ' — ' + detail : ''}`);
}
function bad(name, detail) {
  fail++;
  failures.push(name);
  console.log(`  FAIL  ${name}${detail ? ' — ' + detail : ''}`);
}
function check(name, cond, detail) {
  if (cond) ok(name, detail);
  else bad(name, detail);
}

async function getJson(path) {
  const res = await fetch(BRIDGE + path, { signal: AbortSignal.timeout(10000) });
  return res.json();
}

let id = 0;
async function rpc(method, params) {
  id++;
  const res = await fetch(BRIDGE + '/mcp', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' },
    body: JSON.stringify({ jsonrpc: '2.0', id, method, params }),
    signal: AbortSignal.timeout(20000),
  });
  const text = await res.text();
  return text.trim() ? JSON.parse(text) : null;
}

async function tool(name, args = {}) {
  const res = await rpc('tools/call', { name, arguments: args });
  if (!res || res.error) throw new Error(res ? JSON.stringify(res.error) : 'empty response');
  const text = res.result.content[0].text;
  try {
    return JSON.parse(text);
  } catch {
    return { raw: text };
  }
}

(async () => {
  console.log(`Sephiria Monitor self-check @ ${BRIDGE}`);

  let health = null;
  try {
    health = await getJson('/health');
  } catch (e) {
    bad('bridge reachable', e.message);
    console.log(`\n${pass} passed, ${fail} failed`);
    console.log('Start Sephiria (the plugin listens on port 47901 while the game runs), then retry.');
    process.exit(2);
  }
  check('bridge reachable', health && health.ok === true, `v${health.version} scene=${health.scene}`);

  const init = await rpc('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: {},
    clientInfo: { name: 'selfcheck', version: '1' },
  });
  check('mcp initialize', init && init.result && init.result.serverInfo, init && init.result && init.result.serverInfo.name);
  check('mcp protocol version', init && init.result.protocolVersion === '2025-06-18', init && init.result.protocolVersion);

  const list = await rpc('tools/list', {});
  const tools = (list && list.result && list.result.tools) || [];
  check('tools/list', tools.length === 16, `${tools.length} tools`);
  check('tool names', tools.map((t) => t.name).includes('sephiria_brief'), tools.map((t) => t.name).join(','));
  check('tool schemas', tools.every((t) => t.inputSchema && t.inputSchema.type === 'object'), 'all object-rooted');

  try {
    const brief = await tool('sephiria_brief', { top: 3 });
    check('brief', brief.health && brief.runs && brief.combat, `scene=${brief.health.scene} inRun=${brief.runs.inRun}`);
    check('brief combos', Array.isArray(brief.combos.combos), `${brief.combos.combos.length} combo effects`);
  } catch (e) {
    bad('brief', e.message);
  }

  try {
    const inv = await tool('sephiria_inventory', {});
    const stats = Object.keys(inv).filter((k) => /^p\d+\.stat\./.test(k));
    check('inventory live', inv.live === true, `${stats.length} stat keys`);
  } catch (e) {
    bad('inventory', e.message);
  }

  try {
    const hooks = await tool('sephiria_hooks', { q: 'applydamage', limit: 2 });
    check('hooks catalogue', hooks.total > 50 && hooks.hooks.length > 0, `${hooks.total} hooks, matched ${hooks.matched}`);
    check('hooks callers', hooks.hooks.some((h) => (h.callers || []).length > 0), 'call sites present');
  } catch (e) {
    bad('hooks', e.message);
  }

  try {
    const db = await tool('sephiria_db', { table: 'item', limit: 1 });
    check('db csv', Array.isArray(db.rows) && db.rows.length === 1, `${db.table} rows=${db.total ?? '?'}`);
  } catch (e) {
    bad('db', e.message);
  }

  try {
    const save = await tool('sephiria_save', { action: 'slots' });
    check('save slots', Array.isArray(save.slots) && save.slots.length === 3, save.slots.map((s) => s.name).join(','));

  const key = await tool('sephiria_save', { action: 'key', key: 'PlayerName' });
  check('save key read', key.exists === true && typeof key.value === 'string', `${key.key}=${key.value}`);

  const log1 = await tool('sephiria_logs', { n: 3 });
  const lastSeq = (log1.lines || []).reduce((m, l) => Math.max(m, l.seq || 0), 0);
  const log2 = await tool('sephiria_logs', { n: 200, after: lastSeq });
  check('logs incremental', Array.isArray(log2.lines) && log2.lines.every((l) => (l.seq || 0) > lastSeq),
    `after=${lastSeq} -> ${(log2.lines || []).length} new`);

  const opt = await tool('sephiria_mods', { action: 'options' });
  const mine = (opt.pages || []).find((p) => p.modId === 'com.surveillance.monitor');
  check('mod options page', !!mine && (mine.items || []).length >= 2,
    mine ? `${mine.items.length} items` : 'page missing');
  } catch (e) {
    bad('save', e.message);
  }

  try {
    const tr = await tool('sephiria_travel', { action: 'spots', max: 5 });
    check('travel spots', Array.isArray(tr.points) && typeof tr.total === 'number', `${tr.total} interactables on ${tr.floor}`);
  } catch (e) {
    bad('travel', e.message);
  }

  try {
    const m = await tool('sephiria_merchant', { action: 'merchants' });
    check('merchants', Array.isArray(m.merchants), `${m.count} merchants`);
  } catch (e) {
    bad('merchants', e.message);
  }

  try {
    const mods = await tool('sephiria_mods', { action: 'list' });
    check('mods list', Array.isArray(mods.mods) && mods.counts && mods.counts.total > 0,
      `${mods.counts.total} mods (bepinex ${mods.counts.bepinex}, addons ${mods.counts.addons}, workshop ${mods.counts.workshop})`);

    const patches = await tool('sephiria_mods', { action: 'patches', limit: 5 });
    check('mods patches', typeof patches.totalPatchedMethods === 'number',
      `${patches.totalPatchedMethods} patched methods, ${patches.matched} matched`);

    const l10n = await tool('sephiria_mods', { action: 'l10n', key: 'Item_Name_1' });
    check('mods l10n', Array.isArray(l10n.languages), `${(l10n.languages || []).length} languages`);

    const ui = await tool('sephiria_ui', { action: 'list' });
    check('ui stack', typeof ui.depth === 'number', `depth=${ui.depth}, open=${(ui.open || []).length}`);
  } catch (e) {
    bad('mods', e.message);
  }

  try {
    const unknown = await rpc('tools/call', { name: 'sephiria_nope', arguments: {} });
    check('unknown tool rejected', unknown && unknown.error, unknown && unknown.error && unknown.error.message);
  } catch (e) {
    bad('unknown tool rejected', e.message);
  }

  console.log(`\n${pass} passed, ${fail} failed`);
  if (fail) console.log('failed: ' + failures.join(', '));
  process.exit(fail ? 1 : 0);
})().catch((e) => {
  console.error('self-check crashed:', e.message);
  process.exit(2);
});
