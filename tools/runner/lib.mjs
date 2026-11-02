// Shared helpers for the Sephiria mod workbench: bridge client, game lifecycle,
// per-mod isolation (snapshot/restore), save backups and scenario step evaluation.
//
// Design rules (see _re/MODDEV/testing/protocol.md):
//  * the game is only ever driven through the loopback bridge (127.0.0.1), no screenshots;
//  * auto-enter is always explicit here: launch -> wait bridge -> /arm -> wait MainWorld
//    (the plugin also accepts /arm at the title screen, which re-opens its one-shot gate);
//  * every suite runs with a snapshot of AddOns/ and BepInEx/plugins/ and a backup of the
//    save slots, and both are restored afterwards unless --keep is passed;
//  * artifacts of a failed run (health/logs/events/state) are written to work/_runs/<id>/.

import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { startMinimizer, minimizeGameNow, setConsoleWindow, launchHidden } from './focus.mjs';
export { minimizeGameNow, setConsoleWindow, startMinimizer } from './focus.mjs';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

export const RUNNER_DIR = path.dirname(fileURLToPath(import.meta.url));
export const REPO = path.resolve(RUNNER_DIR, '..', '..');
export const WORK = path.join(REPO, 'work');
export const STATE = path.join(WORK, '_state');
export const RUNS = path.join(WORK, '_runs');
export const GAME = process.env.SEPHIRIA_GAME_DIR || 'D:\\steam\\steamapps\\common\\Sephiria';
export const PORT = Number(process.env.SEPHIRIA_BRIDGE_PORT || 47901);
export const BASE = `http://127.0.0.1:${PORT}`;
// Documents may be redirected (OneDrive/known-folder move), so resolve it the way Unity does.
export const SAVES = process.env.SEPHIRIA_SAVES_DIR || path.join(os.homedir(), 'Documents', 'Saved Games', 'Sephiria');
export const ADDONS = path.join(GAME, 'AddOns');
export const PLUGINS = path.join(GAME, 'BepInEx', 'plugins');
export const STEAM_URL = 'steam://rungameid/2436940';

let requestSerial = 0;

function ensureDir(p) {
  fs.mkdirSync(p, { recursive: true });
}

export async function get(pathOrUrl, timeoutMs = 15000) {
  requestSerial++;
  const url = pathOrUrl.startsWith('http') ? pathOrUrl : BASE + (pathOrUrl.startsWith('/') ? '' : '/') + pathOrUrl;
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), timeoutMs);
  try {
    const res = await fetch(url, { signal: ctl.signal });
    const text = await res.text();
    if (!res.ok) throw new Error(`HTTP ${res.status} ${url}: ${text.slice(0, 300)}`);
    return text;
  } finally {
    clearTimeout(timer);
  }
}

export async function getJson(pathOrUrl, timeoutMs = 15000) {
  return JSON.parse(await get(pathOrUrl, timeoutMs));
}

export async function tryJson(pathOrUrl, timeoutMs = 5000) {
  try {
    return await getJson(pathOrUrl, timeoutMs);
  } catch {
    return null;
  }
}

export async function health(timeoutMs = 4000) {
  return tryJson('/health', timeoutMs);
}

export async function waitFor(check, { timeoutMs = 60000, intervalMs = 1000, label = 'condition' } = {}) {
  const started = Date.now();
  let lastError = null;
  while (Date.now() - started < timeoutMs) {
    try {
      const value = await check();
      if (value) return value;
    } catch (ex) {
      lastError = ex;
    }
    await sleep(intervalMs);
  }
  throw new Error(`timeout waiting for ${label} after ${timeoutMs} ms` + (lastError ? ` (last: ${lastError.message})` : ''));
}

export function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

export function isGameRunning() {
  const out = spawnSync('tasklist.exe', ['/FI', 'IMAGENAME eq Sephiria.exe', '/NH'], { encoding: 'utf8' });
  return /Sephiria\.exe/i.test(out.stdout || '');
}

/** Poll the bridge to liveness, minimizing the game window on the way (background launches). */
async function waitBridgeMinimizing(timeoutMs = 150000) {
  const deadline = Date.now() + timeoutMs;
  let lastMin = 0;
  while (Date.now() < deadline) {
    const h = await health(2500);
    if (h && h.ok) {
      // the window appears around the same time as the bridge: settle for a few seconds
      for (let i = 0; i < 4; i++) { minimizeGameNow(); await sleep(1200); }
      return h;
    }
    if (Date.now() - lastMin > 4000 && isGameRunning()) {
      minimizeGameNow();
      lastMin = Date.now();
    }
    await sleep(1000);
  }
  throw new Error('timeout waiting for bridge /health after ' + timeoutMs + ' ms');
}

export async function waitBridge(timeoutMs = 150000) {
  return waitFor(() => health(), { timeoutMs, intervalMs: 1500, label: 'bridge /health' });
}

const syncSleep = (ms) => { try { Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms); } catch { /* ignore */ } };

const sleepMs = (ms) => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);

// Kill the game and wait until the process is really gone: launching a new copy while the
// old one is still unloading produces "bridge never came up" failures (the new instance
// loses the startup race and exits, or Unity refuses a second instance).
export function killGameAndWait({ timeoutMs = 30000 } = {}) {
  killGame();
  const started = Date.now();
  while (Date.now() - started < timeoutMs) {
    if (!isGameRunning()) return true;
    sleepMs(400);
  }
  try { spawnSync('taskkill', ['/IM', 'Sephiria.exe', '/F', '/T'], { stdio: 'ignore' }); } catch { /* ignore */ }
  sleepMs(800);
  return !isGameRunning();
}

export function killGame() {
  if (!isGameRunning()) return true;
  spawnSync('taskkill.exe', ['/IM', 'Sephiria.exe', '/F'], { stdio: 'ignore' });
  // wait for the process to actually disappear: relaunching while the old one is still
  // tearing down makes Steam hand back a hung process (Player.log stops after the Mono path lines)
  const deadline = Date.now() + 25000;
  while (Date.now() < deadline) {
    if (!isGameRunning()) return true;
    syncSleep(500);
  }
  spawnSync('taskkill.exe', ['/IM', 'Sephiria.exe', '/F', '/T'], { stdio: 'ignore' });
  syncSleep(1000);
  return !isGameRunning();
}

// Launch and wait for the bridge. If nothing appears and the log never advanced, the launcher
// probably lost the race against Steam (or a crashed instance still held the lock): kill and retry once.
export async function launchAndWait({ timeoutMs = 180000, log = () => {}, consoleWindow = false, hidden } = {}) {
  const playerLog = path.join(os.homedir(), 'AppData', 'LocalLow', 'TEAMHORAY', 'Sephiria', 'Player.log');
  const before = fs.existsSync(playerLog) ? fs.statSync(playerLog).mtimeMs : 0;
  launchGame({ consoleWindow, hidden });
  try {
    return await waitBridgeMinimizing(timeoutMs);
  } catch (e) {
    const advanced = fs.existsSync(playerLog) && fs.statSync(playerLog).mtimeMs > before;
    if (advanced && isGameRunning()) throw e;
    log('bridge never appeared; killing and relaunching once');
    killGame();
    await sleep(2000);
    launchGame({ consoleWindow, hidden });
    return waitBridgeMinimizing(timeoutMs);
  }
}

export function launchGame({ consoleWindow = false, hidden = !process.env.SEPHIRIA_VISIBLE } = {}) {
  // Three layers of "do not disturb", strongest first:
  //   1. hidden=true (default): the game is created on a private window station desktop, so its
  //      windows exist but are not on the interactive desktop at all - no flash, no focus steal,
  //      and no race with the minimise loop. SEPHIRIA_VISIBLE=1 opts out.
  //   2. launch the exe directly, not steam://rungameid: Steam pops its own "starting / syncing
  //      cloud" dialog and that steals focus too. Steam itself must still be running.
  //   3. the BepInEx console window is switched off (logs stay in LogOutput.log and /logs), and a
  //      minimise loop runs as a safety net for the visible-launch path.
  if (!consoleWindow) setConsoleWindow(false);
  const exe = path.join(GAME, 'Sephiria.exe');
  if (hidden && fs.existsSync(exe)) {
    try {
      launchHidden({ exe, workdir: GAME });
      return;
    } catch {
      // fall through to the visible-but-minimised path
    }
  }
  startMinimizer(240);
  if (fs.existsSync(exe)) {
    // spawnSync + `start` returns right away and leaves no pending libuv handle (that dangling
    // handle is what caused `Assertion failed: !(handle->flags & UV_HANDLE_CLOSING)` on exit).
    spawnSync('cmd.exe', ['/c', 'start', '', '/min', exe], { cwd: GAME, stdio: 'ignore', windowsHide: true, timeout: 15000 });
    return;
  }
  spawnSync('cmd.exe', ['/c', 'start', '', STEAM_URL], { stdio: 'ignore', windowsHide: true, timeout: 15000 });
}

export async function ensureRunning({ restart = false, log = () => {}, consoleWindow = false, hidden } = {}) {
  if (restart) {
    log('restarting game');
    killGame();
    await sleep(4000);
  }
  let h = await health();
  if (!h || !h.ok) {
    log('launching game');
    h = await launchAndWait({ log, consoleWindow, hidden });
  }
  log(`bridge ok: plugin ${h.version} · ${h.scene}`);
  return h;
}

/** Re-scan WorkshopLocal in-world: newly installed packages activate without a restart (0.23.0). */
export async function rescan(timeoutMs = 30000) {
  const r = await getJson("/mods?action=rescan", timeoutMs);
  return r;
}

export async function arm(minutes = 120, slot = 0) {
  return getJson(`/arm?minutes=${minutes}` + (slot ? `&slot=${slot}` : ''));
}

// A dedicated test slot: clone SLOT1.sav to SLOTn.sav before a suite and remove it afterwards,
// so a test run never writes the slot the player actually uses.
export function prepareTestSlot(n) {
  if (!n || n === 1) return null;
  const src = path.join(SAVES, 'SLOT1.sav');
  const dst = path.join(SAVES, `SLOT${n}.sav`);
  if (!fs.existsSync(src)) throw new Error('no SLOT1.sav to clone for the test slot');
  // remember what already belongs to this slot so cleanup only removes what the test created
  const before = fs.existsSync(SAVES) ? fs.readdirSync(SAVES).filter((f) => f.startsWith(`SLOT${n}`)) : [];
  fs.writeFileSync(path.join(STATE, `slot${n}-before.json`), JSON.stringify(before));
  if (fs.existsSync(dst)) fs.copyFileSync(dst, dst + '.bak');
  fs.copyFileSync(src, dst);
  return dst;
}

export function cleanupTestSlot(n) {
  if (!n || n === 1) return;
  const dst = path.join(SAVES, `SLOT${n}.sav`);
  const bak = dst + '.bak';
  const manifest = path.join(STATE, `slot${n}-before.json`);
  let before = [];
  try { before = JSON.parse(fs.readFileSync(manifest, 'utf8')); } catch {}
  const keep = new Set(before);
  try {
    // remove everything the test created for this slot (save, TMP, the game's own backups)
    for (const f of fs.readdirSync(SAVES)) {
      if (!f.startsWith(`SLOT${n}`) || keep.has(f) || f.endsWith('.bak')) continue;
      fs.rmSync(path.join(SAVES, f), { force: true });
    }
    if (fs.existsSync(bak)) { fs.copyFileSync(bak, dst); fs.rmSync(bak); }
    fs.rmSync(manifest, { force: true });
  } catch {}
}

export async function disarm() {
  return getJson('/disarm');
}

/** Wait until the game is in MainWorld; arms the gate first if it is still on the title screen. */
export async function waitWorld({ timeoutMs = 180000, log = () => {}, slot = 0 } = {}) {
  const h = await health();
  if (h && h.scene === 'MainWorld') return h;
  if (h && h.scene === 'Title') {
    log('title screen reached, arming auto-enter');
    await arm(120, slot);
  } else {
    // still booting: arm right away, the plugin reads the marker when the title appears
    await tryJson('/arm?minutes=120' + (slot ? `&slot=${slot}` : '')).catch(() => null);
  }
  return waitFor(async () => {
    minimizeGameNow();
    const cur = await health();
    return cur && cur.scene === 'MainWorld' ? cur : null;
  }, { timeoutMs, intervalMs: 2000, label: 'MainWorld' });
}

// ---------- isolation ----------

export function copyDir(src, dst) {
  ensureDir(dst);
  fs.cpSync(src, dst, { recursive: true, force: true });
}

export function snapshot(name) {
  const dir = path.join(STATE, name);
  fs.rmSync(dir, { recursive: true, force: true });
  ensureDir(dir);
  if (fs.existsSync(ADDONS)) copyDir(ADDONS, path.join(dir, 'AddOns'));
  if (fs.existsSync(PLUGINS)) {
    // the monitor's own folder holds live session dumps and the running dll: never snapshot it
    ensureDir(path.join(dir, 'plugins'));
    for (const name of fs.readdirSync(PLUGINS)) {
      if (name.toLowerCase() === 'sephiriamonitor') continue;
      copyDir(path.join(PLUGINS, name), path.join(dir, 'plugins', name));
    }
  }
  fs.writeFileSync(path.join(dir, 'manifest.json'), JSON.stringify({
    name,
    createdAt: new Date().toISOString(),
    addons: fs.existsSync(ADDONS) ? fs.readdirSync(ADDONS) : [],
    plugins: fs.existsSync(PLUGINS) ? fs.readdirSync(PLUGINS) : [],
  }, null, 2));
  return dir;
}

export function restore(name) {
  const dir = path.join(STATE, name);
  if (!fs.existsSync(dir)) throw new Error(`no snapshot named ${name}`);
  const wipe = (target) => {
    try {
      fs.rmSync(target, { recursive: true, force: true });
    } catch (ex) {
      if (ex && (ex.code === 'EPERM' || ex.code === 'EBUSY')) {
        const err = new Error(`${target} is locked${isGameRunning() ? ' (the game is running - stop it first, or run with --keep)' : ''}`);
        err.locked = true;
        throw err;
      }
      throw ex;
    }
  };
  wipe(ADDONS);
  // Wiping BepInEx\plugins wholesale also deleted the monitor's out\session-* dumps (and the
  // dll that is running); keep its folder and only reset the other plugins.
  if (fs.existsSync(PLUGINS)) {
    for (const name of fs.readdirSync(PLUGINS)) {
      if (name.toLowerCase() === 'sephiriamonitor') continue;
      wipe(path.join(PLUGINS, name));
    }
  }
  if (fs.existsSync(path.join(dir, 'AddOns'))) copyDir(path.join(dir, 'AddOns'), ADDONS);
  const snapPlugins = path.join(dir, 'plugins');
  if (fs.existsSync(snapPlugins)) {
    for (const name of fs.readdirSync(snapPlugins)) copyDir(path.join(snapPlugins, name), path.join(PLUGINS, name));
  }
  return dir;
}

export function backupSaves(tag) {
  const dir = path.join(STATE, `saves-${tag}`);
  fs.rmSync(dir, { recursive: true, force: true });
  ensureDir(dir);
  const saved = [];
  if (fs.existsSync(SAVES)) {
    for (const f of fs.readdirSync(SAVES)) {
      if (/\.sav$/i.test(f)) {
        fs.copyFileSync(path.join(SAVES, f), path.join(dir, f));
        saved.push(f);
      }
    }
  }
  return { dir, files: saved };
}

export function hashFiles(dir) {
  const out = {};
  if (!fs.existsSync(dir)) return out;
  for (const f of fs.readdirSync(dir)) {
    if (!/\.sav$/i.test(f)) continue;
    out[f] = crypto.createHash('sha256').update(fs.readFileSync(path.join(dir, f))).digest('hex');
  }
  return out;
}

export function verifySaves(tag, ignore = []) {
  const dir = path.join(STATE, `saves-${tag}`);
  const want = hashFiles(dir);
  const bad = [];
  const skip = new Set(ignore);
  for (const [f, h] of Object.entries(want)) {
    if (skip.has(f)) continue;
    const cur = path.join(SAVES, f);
    const got = fs.existsSync(cur) ? crypto.createHash('sha256').update(fs.readFileSync(cur)).digest('hex') : null;
    if (got !== h) bad.push(f);
  }
  return { ok: bad.length === 0, files: Object.keys(want).length, bad };
}

export function restoreSaves(tag) {
  const dir = path.join(STATE, `saves-${tag}`);
  if (!fs.existsSync(dir)) throw new Error(`no save backup named ${tag}`);
  for (const f of fs.readdirSync(dir)) {
    if (/\.sav$/i.test(f)) fs.copyFileSync(path.join(dir, f), path.join(SAVES, f));
  }
  return dir;
}

// ---------- mods ----------

export function modDir(name) {
  return path.join(WORK, name);
}

export function readManifest(name) {
  const p = path.join(modDir(name), 'mod.json');
  if (!fs.existsSync(p)) throw new Error(`missing ${p}`);
  return JSON.parse(fs.readFileSync(p, 'utf8'));
}

/** Copies the manifest's deploy entries into the game (AddOns/<folder> or BepInEx/plugins/<folder>). */
export function deployMod(name, log = () => {}) {
  const manifest = readManifest(name);
  const base = modDir(name);
  const deploy = manifest.deploy || {};
  const placed = [];
  for (const entry of deploy.addons || []) {
    const from = path.join(base, entry.src || 'dist');
    const to = path.join(ADDONS, entry.folder || name);
    fs.rmSync(to, { recursive: true, force: true });
    copyDir(from, to);
    placed.push(to);
  }
  for (const entry of deploy.plugins || []) {
    const from = path.join(base, entry.src || 'dist');
    const to = path.join(PLUGINS, entry.folder || name);
    fs.rmSync(to, { recursive: true, force: true });
    copyDir(from, to);
    placed.push(to);
  }
  log(`deployed ${name}: ${placed.length} target(s)`);
  return placed;
}

// Workshop channel: put a built package where the game's workshop pipeline looks for local
// packages (WorkshopLocal\<modId>\metadata.json). This is how the packaged artifact gets tested -
// AddOns has its own loader and would hide a broken nested manifest.
export function installPackage(zipOrDir) {
  const src = path.resolve(zipOrDir);
  if (!fs.existsSync(src)) throw new Error('package not found: ' + src);
  const wsl = path.join(GAME, 'WorkshopLocal');
  fs.mkdirSync(wsl, { recursive: true });
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'sephiria-pkg-'));
  if (fs.statSync(src).isDirectory()) {
    copyDir(src, tmp);
  } else {
    const res = spawnSync('powershell.exe', ['-NoProfile', '-Command',
      `Expand-Archive -LiteralPath '${src}' -DestinationPath '${tmp}' -Force`], { stdio: 'pipe' });
    if (res.status !== 0) throw new Error('cannot expand ' + src);
  }
  const metaPath = path.join(tmp, 'metadata.json');
  if (!fs.existsSync(metaPath)) throw new Error('package has no metadata.json at its root: ' + src);
  const meta = JSON.parse(fs.readFileSync(metaPath, 'utf8').replace(/^\uFEFF/, ''));
  const id = (meta.mod && meta.mod.modId) || path.basename(src).replace(/-\d+\.\d+\.\d+(\.zip)?$/i, '').replace(/\.zip$/i, '');
  const dst = path.join(wsl, id);
  fs.rmSync(dst, { recursive: true, force: true });
  copyDir(tmp, dst);
  fs.rmSync(tmp, { recursive: true, force: true });
  return { id, dir: dst };
}

export function removePackage(id) {
  const dst = path.join(GAME, 'WorkshopLocal', String(id));
  fs.rmSync(dst, { recursive: true, force: true });
  return dst;
}

export function undeployMod(name, log = () => {}) {
  const manifest = readManifest(name);
  const deploy = manifest.deploy || {};
  for (const entry of deploy.addons || []) {
    fs.rmSync(path.join(ADDONS, entry.folder || name), { recursive: true, force: true });
  }
  for (const entry of deploy.plugins || []) {
    fs.rmSync(path.join(PLUGINS, entry.folder || name), { recursive: true, force: true });
  }
  log(`undeployed ${name}`);
}

// ---------- assertions ----------

export function pick(obj, fieldPath) {
  if (!fieldPath) return obj;
  // payload keys may contain dots themselves ("p0.name"), so try the literal key first
  if (obj && typeof obj === 'object' && Object.prototype.hasOwnProperty.call(obj, fieldPath)) return obj[fieldPath];
  let cur = obj;
  for (const part of String(fieldPath).split('.')) {
    if (cur == null) return undefined;
    cur = cur[part];
  }
  return cur;
}

export function compare(op, actual, expected) {
  switch (op) {
    case '==': return deepEqual(actual, expected);
    case '!=': return !deepEqual(actual, expected);
    case '>': return Number(actual) > Number(expected);
    case '>=': return Number(actual) >= Number(expected);
    case '<': return Number(actual) < Number(expected);
    case '<=': return Number(actual) <= Number(expected);
    case 'contains':
    // arrays of objects: match on the stringified element, so a suite can assert
    // /mods {source:"workshop"} without adding new endpoints
    if (Array.isArray(actual)) return actual.some((v) => JSON.stringify(v).includes(String(expected)));
    return String(actual ?? '').includes(String(expected));
    case 'matches': return new RegExp(String(expected)).test(String(actual ?? ''));
    case 'exists': return actual !== undefined && actual !== null;
    default: throw new Error(`unknown op ${op}`);
  }
}

function deepEqual(a, b) {
  if (typeof a === 'number' && typeof b === 'number') return Math.abs(a - b) < 1e-9;
  if (a && b && typeof a === 'object' && typeof b === 'object') return JSON.stringify(a) === JSON.stringify(b);
  return a === b;
}

export function numDiff(a, b, tolerance) {
  const d = Math.abs(Number(a) - Number(b));
  return d <= tolerance;
}

// ---------- artifacts ----------

export function artifactsDir(suiteName) {
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const dir = path.join(RUNS, `${stamp}-${suiteName.replace(/[^a-z0-9_-]+/gi, '-')}`);
  ensureDir(dir);
  return dir;
}

export async function captureArtifacts(dir) {
  ensureDir(dir);
  const grab = async (name, p) => {
    try {
      const text = await get(p, 8000);
      fs.writeFileSync(path.join(dir, name), text);
    } catch (ex) {
      fs.writeFileSync(path.join(dir, name + '.error.txt'), String(ex.message || ex));
    }
  };
  await grab('health.json', '/health');
  await grab('state.json', '/state');
  await grab('logs.json', '/logs?n=300');
  await grab('events.json', '/events?n=200');
  await grab('runs.json', '/runs');
  return dir;
}

export { requestSerial };
