#!/usr/bin/env node
// Sephiria game-state MCP server (stdio). Bridges MCP tool calls to the in-game
// HTTP bridge started by the BepInEx plugin SephiriaMonitor (default 127.0.0.1:47901).
// Read-only by default: every tool maps to a GET on the bridge. The single exception is
// sephiria_enter (load a save slot) and sephiria_mute (mute the game own audio + feedback).

import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const CFG = (() => {
  try { return JSON.parse(fs.readFileSync(path.join(HERE, 'config.json'), 'utf8')); } catch { return {}; }
})();

const BRIDGE = (process.env.SEPHIRIA_BRIDGE || CFG.bridge || 'http://127.0.0.1:47901').replace(/\/$/, '');
const TIMEOUT = Math.max(1000, Number(process.env.SEPHIRIA_BRIDGE_TIMEOUT_MS || CFG.timeoutMs) || 10000);
const SLOW_TIMEOUT = Math.max(TIMEOUT, Number(CFG.slowTimeoutMs) || 45000); // /snapshot waits on the Unity main thread
const HTTP_PORT = Math.max(1024, Number(process.env.SURVEILLANCEMOD_HTTP_PORT || CFG.httpPort) || 47902);
const VERSION = '0.9.1';
const PROTOCOL = '2024-11-05';

// Machine-readable hook catalogue shipped next to this server (SurveillanceMod/hooks.json).
let HOOKS_CACHE = null;
function loadHooks() {
  if (HOOKS_CACHE) return HOOKS_CACHE;
  const candidates = [
    path.join(HERE, 'hooks.json'),
    path.join(HERE, '..', '_re', 'modding', 'hooks.json'),
    'D:\\AscensionMod\\_re\\modding\\hooks.json'
  ];
  for (const file of candidates) {
    try { HOOKS_CACHE = JSON.parse(fs.readFileSync(file, 'utf8')); return HOOKS_CACHE; } catch { }
  }
  throw new Error('hooks.json not found next to server.mjs; reinstall with install.ps1 (it copies SurveillanceMod files).');
}


function send(msg) {
  process.stdout.write(JSON.stringify(msg) + '\n');
}

// One bridge GET with timeout and a human-readable failure diagnosis.
async function get(pathName, timeoutMs) {
  const res = await fetch(BRIDGE + pathName, {
    headers: { accept: 'application/json' },
    signal: AbortSignal.timeout(timeoutMs || TIMEOUT)
  });
  const text = await res.text();
  if (!res.ok) throw new Error('bridge HTTP ' + res.status + ' on ' + pathName + ': ' + text.slice(0, 200));
  return text;
}

async function getJson(pathName, timeoutMs) {
  return JSON.parse(await get(pathName, timeoutMs));
}

function hintFor(err, pathName) {
  const cause = (err && err.cause) || {};
  const code = cause.code || (err && err.code) || '';
  const timedOut = (err && (err.name === 'TimeoutError' || err.name === 'AbortError')) || code === 'UND_ERR_ABORTED';
  if (code === 'ECONNREFUSED') {
    return [
      'The in-game bridge at ' + BRIDGE + ' refused the connection.',
      'Likely causes: the game is not running, it is still on the launcher/loading screen, or the mod failed to load.',
      'Check: 1) Sephiria process alive; 2) BepInEx\\LogOutput.log has a "[monitor]" boot line; 3) BepInEx\\plugins\\SephiriaMonitor\\SephiriaMonitor.dll exists.',
      'Note: restarting the game restarts the bridge on the same port, so no re-registration is needed.'
    ].join('\n');
  }
  if (timedOut) {
    return [
      'No answer from ' + BRIDGE + pathName + ' within the timeout (main thread busy).',
      'The bridge marshals every read onto the Unity main thread; a long load, a snapshot or a heavy scene can exceed it.',
      'Try sephiria_health, or retry sephiria_status after a few seconds.'
    ].join('\n');
  }
  if (code === 'ECONNRESET' || code === 'UND_ERR_SOCKET') {
    return 'The connection to the bridge was reset. The game may be alt-tabbed/suspended, or the process just exited. Retry sephiria_health.';
  }
  return 'Bridge request failed (' + (code || 'unknown') + ').';
}

function compact(value, maxLen) {
  const text = typeof value === 'string' ? value : JSON.stringify(value);
  if (text.length <= maxLen) return text;
  return text.slice(0, maxLen) + '... [' + (text.length - maxLen) + ' more chars]';
}

function textResult(text) {
  return { content: [{ type: 'text', text }] };
}

// CSV row count = non-empty lines minus the header row (no trailing-newline assumption).
async function countCsvRows(file) {
  try {
    const body = await (await import('node:fs/promises')).readFile(file, 'utf8');
    if (!body) return 0;
    const lines = body.split(/\r?\n/).filter((l) => l.length > 0);
    return Math.max(0, lines.length - 1);
  } catch {
    return 0;
  }
}

// Minimal RFC4180 reader: quoted cells, "" escapes and embedded newlines are all handled, and a
// UTF-8 BOM (written by the plugin so Excel decodes Chinese/Korean correctly) is stripped.
function parseCsv(body) {
  const text = body.charCodeAt(0) === 0xfeff ? body.slice(1) : body;
  const rows = [];
  let row = [];
  let cell = '';
  let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"') {
        if (text[i + 1] === '"') { cell += '"'; i++; } else { quoted = false; }
      } else {
        cell += c;
      }
      continue;
    }
    if (c === '"') { quoted = true; continue; }
    if (c === ',') { row.push(cell); cell = ''; continue; }
    if (c === '\n') { row.push(cell); rows.push(row); row = []; cell = ''; continue; }
    if (c === '\r') continue;
    cell += c;
  }
  if (cell.length > 0 || row.length > 0) { row.push(cell); rows.push(row); }
  return rows;
}

// Table rows as objects keyed by the header, optionally substring-filtered across every cell.
function csvRows(body, { limit, offset, q }) {
  const rows = parseCsv(body);
  if (rows.length === 0) return { columns: [], matched: 0, rows: [] };
  const columns = rows[0];
  const needle = q === undefined || q === null ? '' : String(q).toLowerCase();
  const data = [];
  for (let i = 1; i < rows.length; i++) {
    const r = rows[i];
    if (r.length === 1 && r[0] === '') continue;
    if (needle && !r.some((v) => String(v).toLowerCase().includes(needle))) continue;
    const obj = {};
    for (let c = 0; c < columns.length; c++) obj[columns[c]] = r[c] === undefined ? '' : r[c];
    data.push(obj);
  }
  const start = Math.max(0, Number(offset) || 0);
  return { columns, matched: data.length, rows: data.slice(start, start + limit) };
}

const tools = [
  {
    name: 'sephiria_health',
    description: 'Deployment self-check: bridge reachability and latency, mod/Unity version, current session directory, exported database count and rows, plugin dll timestamp, recent error lines. Run this first when any other tool fails.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    run: async () => {
      const started = Date.now();
      const report = { server: VERSION, node: process.version, bridge: BRIDGE, ok: false };
      const health = await getJson('/health');
      report.latencyMs = Date.now() - started;
      report.ok = health.ok === true;
      report.mod = health.mod + ' ' + health.version;
      report.unity = health.unity;
      report.scene = health.scene;
      report.session = health.session;
      report.events = health.events;
      report.requests = health.requests;

      if (String(health.scene || '') === 'Title') {
        try {
          const title = JSON.parse(await get('/title'));
          report.title = {
            lobby: title.lobby,
            panelOpen: title.panelOpen,
            selected: title.selected,
            canEnter: title.canEnter,
            slots: (title.slots || []).map((s) => ({ slot: s.name, exists: s.exists, player: s.playerName || null, saved: s.saveDate || null }))
          };
        } catch (err) {
          report.title = { error: String((err && err.message) || err) };
        }
      }

      const fsMod = await import('node:fs/promises');
      const session = String(health.session || '');
      const dbDir = path.join(session, 'db');
      try {
        const files = (await fsMod.readdir(dbDir)).filter((f) => f.endsWith('.csv'));
        let rows = 0;
        for (const file of files) {
          rows += await countCsvRows(path.join(dbDir, file));
        }
        report.databases = { files: files.length, rows };
      } catch (err) {
        report.databases = { files: 0, error: String((err && err.message) || err) };
      }
      try {
        const dll = session.replace(/[\\/]out[\\/]session-[^\\/]+$/, '') + '\\SephiriaMonitor.dll';
        const stat = await fsMod.stat(dll);
        report.plugin = { dll, sizeBytes: stat.size, modified: new Date(stat.mtimeMs).toISOString() };
      } catch (err) {
        report.plugin = { error: String((err && err.message) || err) };
      }
      try {
        const logs = JSON.parse(await get('/logs?n=300'));
        report.log = { lines: logs.count, errors: logs.errors, warnings: logs.warnings, noiseDropped: logs.noise };
        report.recentErrors = (logs.lines || [])
          .filter((l) => l.type === 'Error' || l.type === 'Exception' || l.type === 'Assert')
          .slice(-5)
          .map((l) => ({ t: Number(l.t).toFixed(1), type: l.type, msg: String(l.msg).slice(0, 220) }));
      } catch (err) {
        report.log = { error: String((err && err.message) || err) };
      }
      report.verdict = report.ok && report.scene ? 'healthy' : 'degraded';
      report.tools = tools.length;
      return compact(report, 6000);
    }
  },
  {
    name: 'sephiria_status',
    description: 'One-shot game status: scene, player hp/mp/money/level, unit count, nearby units, damage/log counters, database export state. Use this first instead of a screenshot.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    run: async () => compact(await getJson('/state'), 4000)
  },
  {
    name: 'sephiria_player',
    description: 'Player summary: position, hp/mp, money, level/exp, weapon, nearby units, event counters.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    run: async () => compact(await getJson('/player'), 4000)
  },
  {
    name: 'sephiria_world',
    description: 'Every unit currently in the world (name, class, position, hp), plus player position. Use for "what is around me" questions.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    run: async () => compact(await getJson('/world'), 6000)
  },
  {
    name: 'sephiria_logs',
    description: 'Recent in-game Unity log lines captured inside the process (discord-ipc noise filtered out). Returns counts plus line objects; full log is on disk at <session>/logs.log.',
    inputSchema: {
      type: 'object',
      properties: {
        n: { type: 'number', description: 'how many lines, default 60, max 500' },
        type: { type: 'string', description: 'optional filter: Error, Warning, Log, Exception, Assert' }
      },
      additionalProperties: false
    },
    run: async (args) => {
      const n = Math.min(500, Math.max(1, Number(args.n) || 60));
      const data = await getJson('/logs?n=' + n);
      const wanted = String(args.type || '').toLowerCase();
      if (wanted) {
        data.lines = (data.lines || []).filter((l) => String(l.type).toLowerCase().indexOf(wanted) >= 0);
        data.filtered = data.lines.length;
      }
      return compact(data, 9000);
    }
  },
  {
    name: 'sephiria_events',
    description: 'Structured event stream (damage, spawn, despawn, floor, save, snapshot, scene, heartbeat, in-game log). Newest last; use after= to poll incrementally.',
    inputSchema: {
      type: 'object',
      properties: {
        n: { type: 'number', description: 'how many events, default 80, max 500' },
        after: { type: 'number', description: 'only events with seq > after (incremental poll)' }
      },
      additionalProperties: false
    },
    run: async (args) => {
      const n = Math.min(500, Math.max(1, Number(args.n) || 80));
      const after = Math.max(0, Number(args.after) || 0);
      return compact(await getJson('/events?n=' + n + '&after=' + after), 12000);
    }
  },
  {
    name: 'sephiria_damage',
    description: 'Damage events only: attacker, target, raw damage, dealt damage, type/element, crit, result.',
    inputSchema: {
      type: 'object',
      properties: { n: { type: 'number', description: 'default 40, max 300' } },
      additionalProperties: false
    },
    run: async (args) => compact(await getJson('/damage?n=' + Math.min(300, Math.max(1, Number(args.n) || 40))), 8000)
  },
  {
    name: 'sephiria_snapshot',
    description: 'Capture a full state snapshot (players, stats, inventory, run/floor progress, save data) and get the diff against the previous capture.',
    inputSchema: {
      type: 'object',
      properties: { reason: { type: 'string', description: 'label stored with the snapshot, e.g. before_boss' } },
      additionalProperties: false
    },
    run: async (args) => compact(await getJson('/snapshot?reason=' + encodeURIComponent(String(args.reason || 'agent')), SLOW_TIMEOUT), 8000)
  },
  {
    name: 'sephiria_inventory',
    description: 'Player inventory, tablets, storage and character stats computed live at call time (no snapshot needed).',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    run: async () => compact(await getJson('/inventory'), 8000)
  },
  {
    name: 'sephiria_combos',
    description: 'Live combo/set effects on the player: category, current count, applied threshold and the statuses each tier grants.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    run: async () => compact(await getJson('/combos'), 8000)
  },
  {
    name: 'sephiria_statuses',
    description: 'Live buffs, debuffs and orphaned status instances of every player (stack, remaining ratio, attacker).',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    run: async () => compact(await getJson('/statuses'), 8000)
  },
  {
    name: 'sephiria_combat',
    description: 'In-run combat aggregation kept by the plugin: hit rate, fail reasons, damage by source/element/victim, player deaths, loot and money counters, 60s burst curve. Cheaper than scanning /damage.',
    inputSchema: {
      type: 'object',
      properties: { top: { type: 'number', description: 'rows per breakdown, default 12, max 40' } },
      additionalProperties: false
    },
    run: async (args) => compact(await getJson('/combat?top=' + Math.min(40, Math.max(1, Number(args.top) || 12))), 9000)
  },
  {
    name: 'sephiria_runs',
    description: 'Current run boundary: inRun, destiny seed, raceId, stage list, floor counts, session serial, players, plus combat counters. Run start/end also appear as kind="run" events.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    run: async () => compact(await getJson('/runs'), 8000)
  },
  {
    name: 'sephiria_hooks',
    description: 'Query the game hook catalogue (62 entries: class.member, signature, file:line, server/client side, api/event/harmony, what it is for, known pitfall). Use this BEFORE reading decompiled sources.',
    inputSchema: {
      type: 'object',
      properties: {
        q: { type: 'string', description: 'substring across name/signature/use/pitfall, e.g. damage, shop, floor, charm' },
        area: { type: 'string', description: 'lifecycle | data | combat | world | ui | save | net | antipattern' },
        kind: { type: 'string', description: 'api | event | harmony | avoid' },
        side: { type: 'string', description: 'server | client | both | -' },
        limit: { type: 'number', description: 'default 20, max 62' }
      },
      additionalProperties: false
    },
    run: async (args) => {
      const doc = loadHooks();
      const q = String(args.q || '').toLowerCase();
      const lim = Math.min(62, Math.max(1, Number(args.limit) || 20));
      const rows = doc.hooks.filter((h) => {
        if (args.area && String(h.area) !== String(args.area)) return false;
        if (args.kind && String(h.kind) !== String(args.kind)) return false;
        if (args.side && String(h.side) !== String(args.side)) return false;
        if (!q) return true;
        const hay = [h.id, h.name, h.signature, h.use, h.pitfall, h.source].join(' ').toLowerCase();
        return hay.includes(q);
      });
      const out = rows.slice(0, lim).map((h) => ({
        id: h.id, area: h.area, name: h.name, signature: h.signature, source: h.source,
        side: h.side, kind: h.kind, use: h.use, pitfall: h.pitfall,
        callerCount: h.callerCount || 0,
        callers: (h.callers || []).slice(0, 3).map((c) => c.file + ':' + c.line)
      }));
      return compact(JSON.stringify({ total: rows.length, shown: out.length, note: rows.length > lim ? 'raise limit or narrow q' : undefined, hooks: out }), 12000);
    }
  },
  {
    name: 'sephiria_progress',
    description: 'Run progress: destiny seed, chapter, hard-mode modifiers, current floor (name, seed, threat, main event, rooms), save keys.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    run: async () => compact(await getJson('/progress'), 8000)
  },
  {
    name: 'sephiria_objects',
    description: 'List live objects of a given game type (e.g. ItemChest, MoneyChest, EnhancedProceduralFloorGenerator, NetworkCutScenePlayer) with position and basic state.',
    inputSchema: {
      type: 'object',
      properties: {
        type: { type: 'string', description: 'game type short name' },
        limit: { type: 'number', description: 'default 20, max 100' }
      },
      required: ['type'],
      additionalProperties: false
    },
    run: async (args) => compact(await getJson('/objects?type=' + encodeURIComponent(String(args.type)) + '&limit=' + Math.min(100, Math.max(1, Number(args.limit) || 20))), 8000)
  },
  {
    name: 'sephiria_object',
    description: 'Dump one object of a game type with reflected fields (use index to pick, depth for nesting).',
    inputSchema: {
      type: 'object',
      properties: {
        type: { type: 'string', description: 'game type short name' },
        index: { type: 'number', description: 'default 0' },
        depth: { type: 'number', description: 'default 1, max 3' }
      },
      required: ['type'],
      additionalProperties: false
    },
    run: async (args) => compact(await getJson('/object?type=' + encodeURIComponent(String(args.type)) + '&index=' + Math.max(0, Number(args.index) || 0) + '&depth=' + Math.min(3, Math.max(1, Number(args.depth) || 1))), 12000)
  },
  {
    name: 'sephiria_databases',
    description: 'Exported game databases. Without args: list every CSV in the current session with row counts. With table: return the table rows themselves (item, item_all, weapon, weapon_all, weapon_skill_all, active_skill_all, prop, race, tree_shop, ...), optionally filtered by a substring (q) and paged by limit/offset.',
    inputSchema: {
      type: 'object',
      properties: {
        table: { type: 'string', description: 'table name without .csv, e.g. item' },
        q: { type: 'string', description: 'case-insensitive substring match across all cells' },
        limit: { type: 'number', description: 'rows to return, default 50, max 500' },
        offset: { type: 'number', description: 'row offset for paging, default 0' }
      },
      additionalProperties: false
    },
    run: async (args) => {
      const session = await getJson('/session');
      const dir = path.join(String(session.root || ''), 'db');
      const fsMod = await import('node:fs/promises');
      const limit = Math.min(500, Math.max(1, Number(args.limit) || 50));
      if (args.table) {
        const table = String(args.table).replace(/\.csv$/i, '').replace(/[^A-Za-z0-9_\-]/g, '');
        try {
          const body = await fsMod.readFile(path.join(dir, table + '.csv'), 'utf8');
          const page = csvRows(body, { limit, offset: args.offset, q: args.q });
          return compact({ session: session.root, table, columns: page.columns, matched: page.matched, returned: page.rows.length, rows: page.rows }, 14000);
        } catch (err) {
          const files = (await fsMod.readdir(dir).catch(() => [])).filter((f) => f.endsWith('.csv')).map((f) => f.replace(/\.csv$/, '')).sort();
          return textResult('no table "' + table + '": ' + String((err && err.message) || err) + '\navailable: ' + files.join(', '));
        }
      }
      const out = { session: session.root, databases: [] };
      try {
        const files = (await fsMod.readdir(dir)).filter((f) => f.endsWith('.csv')).sort();
        for (const file of files) {
          const rows = await countCsvRows(path.join(dir, file));
          out.databases.push({ name: file.replace(/\.csv$/, ''), rows });
        }
        out.total = out.databases.reduce((a, b) => a + b.rows, 0);
        out.hint = 'Pass table (e.g. "item") to read rows, plus q/limit/offset to filter and page.';
      } catch (err) {
        out.error = String((err && err.message) || err);
        out.hint = 'Enter a save (MainWorld) so the plugin can export databases.';
      }
      return compact(out, 6000);
    }
  },
  {
    name: 'sephiria_title',
    description: 'Title-screen readiness: is the lobby loaded, is the save-profile panel open, which profile is selected, bridge port, plus the three save slots. Use at the main menu instead of a screenshot.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    run: async () => compact(await getJson('/title'), 5000)
  },
  {
    name: 'sephiria_saves',
    description: 'List the three save slots (existence, player name, last saved, death count, prologue flag, file size/mtime). Read-only; works on the title screen.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    run: async () => compact(await getJson('/saves'), 5000)
  },
  {
    name: 'sephiria_enter',
    description: 'Start a save slot from the title screen without mouse coordinates: sets SelectedProfile and calls the game own UI_TitleLobby.NetworkHost(), retrying while the fader blocks it, then waits for the world to load. This is the only tool that changes game state (it starts the run you asked for).',
    inputSchema: {
      type: 'object',
      properties: {
        slot: { type: 'string', description: '1..3 or SLOT1..SLOT3, default 1' },
        create: { type: 'boolean', description: 'create a new save when the slot is empty (default false)' },
        wait: { type: 'boolean', description: 'wait for the world to load (default true, up to 75s)' }
      },
      additionalProperties: false
    },
    run: async (args) => {
      const slot = String(args.slot === undefined || args.slot === null ? '1' : args.slot);
      const create = args.create ? '1' : '0';
      const out = { request: JSON.parse(await get('/enter?slot=' + encodeURIComponent(slot) + '&create=' + create)) };
      if (out.request.ok !== true || args.wait === false) return compact(out, 5000);
      const deadline = Date.now() + 75000;
      let last = null;
      while (Date.now() < deadline) {
        await new Promise((r) => setTimeout(r, 1000));
        last = JSON.parse(await get('/enterstatus'));
        const state = (last.pending || {}).state;
        if (state === 'ok' || state === 'timeout' || state === 'idle') break;
      }
      out.result = last;
      try { out.status = JSON.parse(await get('/state')); } catch (err) { out.statusError = String((err && err.message) || err); }
      return compact(out, 7000);
    }
  },
  {
    name: 'sephiria_map',
    description: 'Collision map of the current floor. The plugin probes a 0.5-unit grid of solid colliders after every floor change, so this returns an ASCII window around the player (# = solid rock/wall, . = free floor, P = you, @ = target, ? = not scanned yet) plus scan progress. Pass x,y world coordinates to mark a target, and path:true to get a walkable BFS route to it. Use it before walking: it shows which side is blocked.',
    inputSchema: {
      type: 'object',
      properties: {
        r: { type: 'number', description: 'half size of the ASCII window in world units (default 18)' },
        x: { type: 'number', description: 'target world x (optional)' },
        y: { type: 'number', description: 'target world y (optional)' },
        path: { type: 'boolean', description: 'also return a walkable route to x,y' },
        probe: { type: 'boolean', description: 'list the colliders at x,y that make it solid' },
        rooms: { type: 'boolean', description: 'also dump the room table: room grid pos/size/world rect/type/spawnMonster/units, doors, stairs and a coarse room layout' }
      },
      additionalProperties: false
    },
    run: async (args) => {
      const out = {};
      const r = Number.isFinite(args.r) ? args.r : 18;
      const q = new URLSearchParams({ r: String(r) });
      if (Number.isFinite(args.x)) q.set('tx', String(args.x));
      if (Number.isFinite(args.y)) q.set('ty', String(args.y));
      try { out.map = JSON.parse(await get('/map?' + q.toString())); } catch (err) { out.mapError = String((err && err.message) || err); }
      if (args.probe && Number.isFinite(args.x) && Number.isFinite(args.y)) {
        try { out.probe = JSON.parse(await get('/probe?x=' + args.x + '&y=' + args.y)); } catch (err) { out.probeError = String((err && err.message) || err); }
      }
      if (args.rooms) {
        try { out.rooms = JSON.parse(await get('/rooms')); } catch (err) { out.roomsError = String((err && err.message) || err); }
      }
      if (args.path && Number.isFinite(args.x) && Number.isFinite(args.y)) {
        try { out.route = JSON.parse(await get('/mappath?x=' + args.x + '&y=' + args.y)); } catch (err) { out.routeError = String((err && err.message) || err); }
      }
      return compact(out, 12000);
    }
  },
  {
    name: 'sephiria_screenshot',
    description: 'Fallback only: capture a screenshot of the game window when numeric state is not enough.',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false },
    run: async () => {
      const shot = await getJson('/screenshot', SLOW_TIMEOUT);
      const fsMod = await import('node:fs/promises');
      const data = await fsMod.readFile(shot.path);
      return {
        content: [
          { type: 'text', text: 'screenshot ' + shot.width + 'x' + shot.height + ' -> ' + shot.path },
          { type: 'image', data: data.toString('base64'), mimeType: 'image/png' }
        ]
      };
    }
  }
];

async function handleCall(params) {
  const name = params && params.name;
  const tool = tools.find((t) => t.name === name);
  if (!tool) return { content: [{ type: 'text', text: 'unknown tool: ' + name }], isError: true };
  try {
    const result = await tool.run(params.arguments || {});
    return typeof result === 'string' ? textResult(result) : result;
  } catch (err) {
    const head = 'sephiria bridge error in ' + name + ': ' + String((err && err.message) || err);
    const pathName = { sephiria_status: '/state', sephiria_player: '/player', sephiria_snapshot: '/snapshot', sephiria_enter: '/enter', sephiria_saves: '/saves', sephiria_title: '/title' }[name] || '/health';
    return { content: [{ type: 'text', text: head + '\n\n' + hintFor(err, pathName) }], isError: true };
  }
}

function startStdio() {
  let buffer = '';
  process.stdin.setEncoding('utf8');
  process.stdin.on('data', (chunk) => {
  buffer += chunk;
  let idx;
  while ((idx = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, idx).trim();
    buffer = buffer.slice(idx + 1);
    if (!line) continue;
    let msg;
    try { msg = JSON.parse(line); } catch { continue; }
    void dispatch(msg);
  }
  });
}

async function dispatch(msg) {
  if (msg.id === undefined) return; // notification
  const id = msg.id;
  try {
    if (msg.method === 'initialize') {
      send({
        jsonrpc: '2.0', id,
        result: {
          protocolVersion: PROTOCOL,
          capabilities: { tools: { listChanged: false } },
          serverInfo: { name: 'sephiria', version: VERSION },
          instructions: 'Sephiria game state bridge (read-only, live in-game state). Call sephiria_health once when tools misbehave; otherwise start with sephiria_status and avoid screenshots.'
        }
      });
      return;
    }
    if (msg.method === 'ping') { send({ jsonrpc: '2.0', id, result: {} }); return; }
    if (msg.method === 'tools/list') {
      send({ jsonrpc: '2.0', id, result: { tools: tools.map((t) => ({ name: t.name, description: t.description, inputSchema: t.inputSchema })) } });
      return;
    }
    if (msg.method === 'tools/call') {
      send({ jsonrpc: '2.0', id, result: await handleCall(msg.params) });
      return;
    }
    send({ jsonrpc: '2.0', id, error: { code: -32601, message: 'method not found: ' + msg.method } });
  } catch (err) {
    send({ jsonrpc: '2.0', id, error: { code: -32603, message: String((err && err.message) || err) } });
  }
}

function printHelp() {
  process.stdout.write([
    'SurveillanceMod ' + VERSION + ' - Sephiria game-state bridge for AI agents',
    '',
    'Usage:',
    '  node server.mjs                MCP server on stdio (this is what an MCP client launches)',
    '  node server.mjs status         one-shot call, prints JSON (any tool name also works short: status, saves, enter...)',
    '  node server.mjs logs n=20      one-shot call with key=value arguments',
    '  node server.mjs --call sephiria_status \'{"n":20}\'',
    '  node server.mjs --http         local HTTP API on http://127.0.0.1:' + HTTP_PORT + '/ (no MCP client needed)',
    '  node server.mjs --list         list tools',
    '  node server.mjs --version      print version',
    '',
    'Bridge: ' + BRIDGE,
    'Config: ' + path.join(HERE, 'config.json') + '  (bridge, timeoutMs, slowTimeoutMs, httpPort)',
    ''
  ].join('\n'));
}

function toolByName(name) {
  const raw = String(name || '');
  if (!raw) return null;
  const full = raw.indexOf('sephiria_') === 0 ? raw : 'sephiria_' + raw;
  return tools.find((t) => t.name === full) || tools.find((t) => t.name === raw) || null;
}

function parseArgs(list) {
  const out = {};
  for (const item of list) {
    const eq = item.indexOf('=');
    if (eq <= 0) continue;
    const key = item.slice(0, eq);
    const raw = item.slice(eq + 1);
    if (raw === 'true') out[key] = true;
    else if (raw === 'false') out[key] = false;
    else if (/^-?[0-9]+(\.[0-9]+)?$/.test(raw)) out[key] = Number(raw);
    else out[key] = raw;
  }
  return out;
}

// One tool call, transport independent: used by stdio, HTTP and CLI modes.
async function runTool(name, args) {
  const tool = toolByName(name);
  if (!tool) return { tool: String(name), text: 'unknown tool: ' + name + ' (try --list)', isError: true };
  const result = await handleCall({ name: tool.name, arguments: args || {} });
  const text = (result.content || []).filter((c) => c.type === 'text').map((c) => c.text).join('\n');
  return { tool: tool.name, text, isError: !!result.isError, result };
}

async function runCli(name, rest) {
  let args = {};
  if (rest.length === 1 && rest[0].trim().charAt(0) === '{') {
    try { args = JSON.parse(rest[0]); } catch (err) {
      process.stderr.write('bad JSON arguments: ' + err.message + '\n');
      process.exitCode = 2;
      return;
    }
  } else {
    args = parseArgs(rest);
  }
  try {
    const res = await runTool(name, args);
    const payload = { tool: res.tool, ok: !res.isError };
    try { payload.result = JSON.parse(res.text); } catch { payload.result = res.text; }
    process.stdout.write(JSON.stringify(payload, null, 2) + '\n');
    process.exitCode = res.isError ? 1 : 0;
  } catch (err) {
    process.stderr.write('call failed: ' + String((err && err.message) || err) + '\n');
    process.exitCode = 1;
  }
}

function startHttp() {
  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url || '/', 'http://127.0.0.1');
    const sendJson = (code, obj) => {
      const body = JSON.stringify(obj, null, 2);
      res.writeHead(code, { 'content-type': 'application/json; charset=utf-8' });
      res.end(body);
    };
    try {
      if (req.method === 'POST' && url.pathname === '/call') {
        let raw = '';
        for await (const chunk of req) raw += chunk;
        let body;
        try { body = JSON.parse(raw || '{}'); } catch { return sendJson(400, { ok: false, error: 'invalid JSON body' }); }
        const out = await runTool(body.name, body.arguments || body.args || {});
        return sendJson(out.isError ? 400 : 200, { ok: !out.isError, tool: out.tool, text: out.text });
      }
      if (url.pathname === '/' || url.pathname === '/info') {
        return sendJson(200, {
          server: 'SurveillanceMod', version: VERSION, bridge: BRIDGE, httpPort: HTTP_PORT, tools: tools.length,
          usage: 'GET /tools | GET /tool/<name>?k=v | POST /call {"name":"sephiria_status","arguments":{}}'
        });
      }
      if (url.pathname === '/tools') {
        return sendJson(200, { ok: true, tools: tools.map((t) => ({ name: t.name, description: t.description, inputSchema: t.inputSchema })) });
      }
      if (url.pathname.indexOf('/tool/') === 0) {
        const name = decodeURIComponent(url.pathname.slice(6));
        const args = {};
        for (const pair of url.searchParams) args[pair[0]] = /^-?[0-9]+$/.test(pair[1]) ? Number(pair[1]) : pair[1];
        const out = await runTool(name, args);
        return sendJson(out.isError ? 400 : 200, { ok: !out.isError, tool: out.tool, text: out.text });
      }
      return sendJson(404, { ok: false, error: 'unknown path', hint: 'GET /tools, GET /tool/<name>, POST /call' });
    } catch (err) {
      return sendJson(500, { ok: false, error: String((err && err.message) || err) });
    }
  });
  server.on('error', (err) => {
    process.stderr.write('[surveillancemod] http listen failed on ' + HTTP_PORT + ': ' + err.message + '\n');
    process.exitCode = 1;
  });
  server.listen(HTTP_PORT, '127.0.0.1', () => {
    process.stderr.write('[surveillancemod] v' + VERSION + ' http=http://127.0.0.1:' + HTTP_PORT + '/ bridge=' + BRIDGE + ' tools=' + tools.length + '\n');
  });
}

async function main() {
  const argv = process.argv.slice(2);
  const first = argv[0];
  if (!first || first === '--stdio' || first === 'stdio') {
    process.stderr.write('[surveillancemod] v' + VERSION + ' stdio bridge=' + BRIDGE + ' tools=' + tools.length + '\n');
    startStdio();
    return;
  }
  if (first === '--help' || first === '-h' || first === 'help') { printHelp(); return; }
  if (first === '--version' || first === '-v' || first === 'version') { process.stdout.write(VERSION + '\n'); return; }
  if (first === '--list' || first === 'list' || first === 'tools') {
    process.stdout.write(JSON.stringify(tools.map((t) => ({ name: t.name, description: t.description })), null, 2) + '\n');
    return;
  }
  if (first === '--http' || first === 'http' || first === '--serve') { startHttp(); return; }
  if (first === '--cli' || first === '--call' || first === 'call') { await runCli(argv[1], argv.slice(2)); return; }
  if (toolByName(first)) { await runCli(first, argv.slice(1)); return; }
  process.stderr.write('unknown command: ' + first + '\n\n');
  printHelp();
  process.exitCode = 2;
}

main();
