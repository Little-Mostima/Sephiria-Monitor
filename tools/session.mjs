// The one way into the world.
//
// Why this file exists: "get me into MainWorld" used to be written six times (launch-bg --world,
// do world, do session, run-suite's enter step, run-suite's newWorld step, compat) with three
// different ideas of what success means - one of them (server||client) reported success before the
// scene had even loaded. The plugin now owns the sequence (TitleControl's state machine) and
// reports a phase plus a failure reason; this module is the only client of it.
//
// Callers ask for a world, not for a sequence of HTTP calls:
//   await enterWorld({ slot: 2, fresh: true })   // clean profile, then stand in MainWorld
//   await enterWorld({ slot: 1 })                // load the existing profile
//   await requestEnter({ slot: 1 })              // fire and forget (a suite drives the rest)
import fs from 'node:fs';
import path from 'node:path';
import { SAVES, STATE } from './config.mjs';
import { getJson, tryJson, health, sleep, minimizeGameNow } from './runner/lib.mjs';

/** Phases the plugin reports through /enterstatus. Kept here so callers never string-match. */
export const PHASES = ['idle', 'preparing', 'waiting_gate', 'hosting', 'loading', 'ready', 'failed'];

/** Ask the plugin to enter a slot. Returns the plugin's acknowledgement; does not wait. */
export async function requestEnter({ slot = 0, name = '', skip = true, create = true } = {}) {
  const q = new URLSearchParams({ slot: String(slot || 1), create: create ? '1' : '0', skip: skip ? '1' : '0' });
  if (name) q.set('name', name);
  const res = await tryJson('/enter?' + q.toString(), 60000);
  if (!res) throw new Error('bridge did not answer /enter');
  if (res.error) throw new Error('enter rejected: ' + res.error);
  return res;
}

/** Current state machine snapshot, or null when the bridge is down. */
export async function enterStatus() {
  return tryJson('/enterstatus');
}

/** Poll until the game is in MainWorld, following the plugin's phase and failing fast on 'failed'. */
export async function awaitWorld({ timeoutMs = 180000, log = () => {} } = {}) {
  const deadline = Date.now() + timeoutMs;
  let lastPhase = '';
  while (Date.now() < deadline) {
    minimizeGameNow();
    const cur = await health();
    if (cur && cur.scene === 'MainWorld') return cur;
    const st = await enterStatus();
    const p = st && st.pending;
    if (p) {
      if (p.phase !== lastPhase) {
        log('enter: ' + p.phase + (p.detail ? ' - ' + p.detail : ''));
        lastPhase = p.phase;
      }
      if (p.phase === 'failed') {
        throw new Error('enter failed: ' + (p.error || 'unknown') + (p.detail ? ' (' + p.detail + ')' : ''));
      }
    }
    await sleep(1000);
  }
  throw new Error('timeout waiting for MainWorld after ' + timeoutMs + ' ms (last phase: ' + (lastPhase || 'unknown') + ')');
}

/** Wait for the title screen (a cold start lands there before anything else). */
export async function awaitTitle({ timeoutMs = 180000, log = () => {} } = {}) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const h = await tryJson('/health');
    if (h && h.scene === 'Title') return h;
    await sleep(1000);
  }
  throw new Error('timeout waiting for the title screen after ' + timeoutMs + ' ms');
}

/**
 * Write a clean profile straight into a save slot (name + prologue cleared) so the title screen
 * never has to show its name prompt. Returns 'prepnew' on success, 'create' when the bridge has no
 * /prepnew and the profile has to be created through the game's own UI.
 */
export async function prepareFreshSlot({ slot = 2, name = 'Tester', log = () => {} } = {}) {
  const s = Number(slot) || 2;
  try {
    const prep = await getJson('/prepnew?' + new URLSearchParams({ slot: 'SLOT' + s, name, fresh: '1' }).toString(), 60000);
    if (prep && prep.ok === true) {
      log('prepnew: slot ' + s + ' prepared for ' + JSON.stringify(name));
      return 'prepnew';
    }
    log('prepnew returned ' + JSON.stringify(prep).slice(0, 200));
  } catch (ex) {
    log('prepnew unavailable (' + ex.message + ')');
  }
  log('falling back to creating the profile through the title screen');
  return 'create';
}

/**
 * The whole job: make sure the game is in MainWorld on the requested slot.
 * Returns { scene, via, health } - 'via' records which path actually worked.
 */
export async function enterWorld({ slot = 0, name = '', skip = true, fresh = false, timeoutMs = 180000, log = () => {} } = {}) {
  const h = await health();
  if (h && h.scene === 'MainWorld') {
    log('already in MainWorld');
    return { scene: h.scene, via: 'already', health: h };
  }
  let via = 'enter';
  if (fresh) {
    via = await prepareFreshSlot({ slot: slot || 2, name: name || 'Tester', log });
    if (via === 'create') await requestEnter({ slot: slot || 2, name: name || 'Tester', skip, create: true });
    else await requestEnter({ slot: slot || 2, name, skip, create: false });
  } else {
    await requestEnter({ slot, name, skip, create: true });
  }
  const world = await awaitWorld({ timeoutMs, log });
  return { scene: world.scene, via, health: world };
}

// ---------- the old one-shot gate ----------
// /arm predates the state machine: it made the plugin enter the world on its own once the title
// screen appeared. It is kept only for suites that need "start the game and end up in the world
// without driving it", and it is no longer part of enterWorld's path.

export async function arm(minutes = 120, slot = 0) {
  return getJson('/arm?minutes=' + minutes + (slot ? '&slot=' + slot : ''));
}

export async function disarm() {
  return getJson('/disarm');
}

// ---------- a dedicated test slot ----------
// Clone SLOT1.sav to SLOTn.sav before a suite and remove it afterwards, so a test run never writes
// the slot the player actually uses.

export function prepareTestSlot(n) {
  if (!n || n === 1) return null;
  const src = path.join(SAVES, 'SLOT1.sav');
  const dst = path.join(SAVES, 'SLOT' + n + '.sav');
  if (!fs.existsSync(src)) throw new Error('no SLOT1.sav to clone for the test slot');
  // remember what already belongs to this slot so cleanup only removes what the test created
  const before = fs.existsSync(SAVES) ? fs.readdirSync(SAVES).filter((f) => f.startsWith('SLOT' + n)) : [];
  fs.mkdirSync(STATE, { recursive: true });
  fs.writeFileSync(path.join(STATE, 'slot' + n + '-before.json'), JSON.stringify(before));
  if (fs.existsSync(dst)) fs.copyFileSync(dst, dst + '.bak');
  fs.copyFileSync(src, dst);
  return dst;
}

export function cleanupTestSlot(n) {
  if (!n || n === 1) return;
  const dst = path.join(SAVES, 'SLOT' + n + '.sav');
  const bak = dst + '.bak';
  const manifest = path.join(STATE, 'slot' + n + '-before.json');
  let before = [];
  try { before = JSON.parse(fs.readFileSync(manifest, 'utf8')); } catch { /* first run */ }
  const keep = new Set(before);
  try {
    // remove everything the test created for this slot (save, TMP, the game's own backups)
    for (const f of fs.readdirSync(SAVES)) {
      if (!f.startsWith('SLOT' + n) || keep.has(f) || f.endsWith('.bak')) continue;
      fs.rmSync(path.join(SAVES, f), { force: true });
    }
    if (fs.existsSync(bak)) { fs.copyFileSync(bak, dst); fs.rmSync(bak); }
    fs.rmSync(manifest, { force: true });
  } catch { /* best effort */ }
}
