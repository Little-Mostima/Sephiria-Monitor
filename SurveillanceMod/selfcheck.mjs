#!/usr/bin/env node
// Standalone self-check for the Sephiria MCP server: spawns server.mjs exactly the way
// DSH does (stdio, newline-delimited JSON-RPC), then exercises initialize, tools/list and
// a few read-only tools against the live game bridge.
//
//   node selfcheck.mjs                 # uses http://127.0.0.1:47901
//   SEPHIRIA_BRIDGE=http://127.0.0.1:47901 node selfcheck.mjs
//
// Exit code 0 = every check passed, 1 = at least one check failed.

import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const serverPath = path.join(here, 'server.mjs');
const node = process.execPath;

const child = spawn(node, [serverPath], {
  stdio: ['pipe', 'pipe', 'pipe'],
  env: { ...process.env }
});

let stderr = '';
child.stderr.setEncoding('utf8');
child.stderr.on('data', (d) => { stderr += d; });

let buf = '';
const pending = new Map();
child.stdout.setEncoding('utf8');
child.stdout.on('data', (chunk) => {
  buf += chunk;
  let idx;
  while ((idx = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, idx).trim();
    buf = buf.slice(idx + 1);
    if (!line) continue;
    let msg;
    try { msg = JSON.parse(line); } catch { continue; }
    const resolver = pending.get(msg.id);
    if (resolver) { pending.delete(msg.id); resolver(msg); }
  }
});

let nextId = 1;
function rpc(method, params, timeoutMs = 60000) {
  const id = nextId++;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { pending.delete(id); reject(new Error('timeout ' + method)); }, timeoutMs);
    pending.set(id, (msg) => { clearTimeout(timer); resolve(msg); });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
  });
}

const results = [];
function record(name, ok, detail) {
  results.push({ name, ok, detail });
  console.log((ok ? 'PASS ' : 'FAIL ') + name + (detail ? '  ' + detail.replace(/\s+/g, ' ').slice(0, 300) : ''));
}

function textOf(msg) {
  const content = msg && msg.result && msg.result.content;
  if (!Array.isArray(content)) return '';
  return content.filter((c) => c.type === 'text').map((c) => c.text).join('\n');
}

const tools = ['sephiria_health', 'sephiria_status', 'sephiria_player', 'sephiria_world', 'sephiria_logs', 'sephiria_events', 'sephiria_damage', 'sephiria_snapshot', 'sephiria_inventory', 'sephiria_progress', 'sephiria_objects', 'sephiria_object', 'sephiria_databases', 'sephiria_title', 'sephiria_saves', 'sephiria_enter', 'sephiria_map', 'sephiria_screenshot', 'sephiria_combos', 'sephiria_statuses', 'sephiria_combat', 'sephiria_runs', 'sephiria_hooks'];

async function main() {
  const init = await rpc('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'selfcheck', version: '1' } });
  const info = init.result && init.result.serverInfo;
  record('initialize', Boolean(info), info ? info.name + ' ' + info.version : JSON.stringify(init.error || init));

  const list = await rpc('tools/list', {});
  const declared = ((list.result && list.result.tools) || []).map((t) => t.name);
  const missing = tools.filter((t) => !declared.includes(t));
  record('tools/list', declared.length === tools.length && missing.length === 0, declared.length + ' tools' + (missing.length ? '; missing ' + missing.join(',') : ''));

  async function call(name, args) {
    const msg = await rpc('tools/call', { name, arguments: args || {} });
    const res = msg.result || {};
    const text = textOf(msg);
    return { ok: !res.isError && Boolean(text), text, error: msg.error };
  }

  // 1. health
  const health = await call('sephiria_health');
  let healthJson = null;
  try { healthJson = JSON.parse(health.text); } catch { /* not json */ }
  record('sephiria_health', health.ok && healthJson && healthJson.ok === true,
    healthJson ? 'scene=' + healthJson.scene + ' latency=' + healthJson.latencyMs + 'ms dbs=' + (healthJson.databases && healthJson.databases.files) + ' verdict=' + healthJson.verdict : health.text);

  // 2. status
  const status = await call('sephiria_status');
  let statusJson = null;
  try { statusJson = JSON.parse(status.text); } catch { /* not json */ }
  record('sephiria_status', status.ok && statusJson && typeof statusJson.scene === 'string',
    statusJson ? 'scene=' + statusJson.scene + ' hp=' + statusJson.hp + '/' + statusJson.maxHp + ' money=' + statusJson.money + ' units=' + statusJson.unitCount : status.text);

  // 3. logs (shape + counters)
  const logs = await call('sephiria_logs', { n: 20 });
  let logsJson = null;
  try { logsJson = JSON.parse(logs.text); } catch { /* not json */ }
  record('sephiria_logs', logs.ok && logsJson && Array.isArray(logsJson.lines),
    logsJson ? 'lines=' + logsJson.count + ' errors=' + logsJson.errors + ' returned=' + logsJson.lines.length : logs.text);

  // 4. databases (disk)
  const dbs = await call('sephiria_databases');
  let dbsJson = null;
  try { dbsJson = JSON.parse(dbs.text); } catch { /* not json */ }
  record('sephiria_databases', dbs.ok && dbsJson && Array.isArray(dbsJson.databases) && dbsJson.databases.length > 0,
    dbsJson ? dbsJson.databases.length + ' csv, ' + dbsJson.total + ' rows' + (dbsJson.error ? ' error=' + dbsJson.error : '') : dbs.text);

  // 5. snapshot round trip (slow path through the main thread)
  const saves = await call('sephiria_saves');
let savesJson = null;
try { savesJson = JSON.parse(saves.text); } catch { /* not json */ }
record('sephiria_saves', saves.ok && savesJson && Array.isArray(savesJson.slots) && savesJson.slots.length === 3,
  savesJson && savesJson.slots ? savesJson.slots.map((s) => s.name + (s.exists ? '(' + (s.playerName || '?') + ')' : '(empty)')).join(' ') : 'no slots');

const snap = await call('sephiria_snapshot', { reason: 'selfcheck' });
  let snapJson = null;
  try { snapJson = JSON.parse(snap.text); } catch { /* not json */ }
  record('sephiria_snapshot', snap.ok && snapJson && snapJson.keys > 0,
    snapJson ? 'keys=' + snapJson.keys + ' added=' + snapJson.added + ' changed=' + snapJson.changed : snap.text);

  // 6. unknown tool must be a clean error, not a crash
  const bogus = await rpc('tools/call', { name: 'no_such_tool', arguments: {} });
  record('unknown tool rejected', Boolean(bogus.result && bogus.result.isError), textOf(bogus).slice(0, 80));

  // 7. bridge-down hint path: point a second server at a dead port
  const dead = spawn(node, [serverPath], { stdio: ['pipe', 'pipe', 'pipe'], env: { ...process.env, SEPHIRIA_BRIDGE: 'http://127.0.0.1:49999' } });
  let deadBuf = '';
  const deadReply = await new Promise((resolve) => {
    const timer = setTimeout(() => resolve(null), 30000);
    dead.stdout.setEncoding('utf8');
    dead.stdout.on('data', (d) => {
      deadBuf += d;
      let i;
      while ((i = deadBuf.indexOf('\n')) >= 0) {
        const line = deadBuf.slice(0, i).trim();
        deadBuf = deadBuf.slice(i + 1);
        if (!line) continue;
        let msg;
        try { msg = JSON.parse(line); } catch { continue; }
        if (msg.id === 2) { clearTimeout(timer); resolve(msg); }
      }
    });
    dead.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: {} }) + '\n');
    dead.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: 'sephiria_status', arguments: {} } }) + '\n');
  });
  dead.kill();
  const deadText = textOf(deadReply);
  record('bridge-down hint', Boolean(deadReply && deadReply.result && deadReply.result.isError) && /refused the connection/i.test(deadText),
    deadText.split('\n')[0] || 'no reply');

  const failed = results.filter((r) => !r.ok);
  child.kill();
  console.log('\n' + (results.length - failed.length) + '/' + results.length + ' checks passed' + (stderr ? ' | server stderr: ' + stderr.trim().split('\n')[0] : ''));
  process.exit(failed.length === 0 ? 0 : 1);
}

main().catch((err) => {
  console.log('FAIL selfcheck crashed: ' + String((err && err.stack) || err));
  child.kill();
  process.exit(1);
});
