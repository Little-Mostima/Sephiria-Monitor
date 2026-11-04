#!/usr/bin/env node
// Start Sephiria in the background.
//
//   node tools/launch-bg.mjs            # launch if needed, minimize, wait for the bridge
//   node tools/launch-bg.mjs --world [--slot N] [--fresh] [--name X]   # also enter the world and wait
//   node tools/launch-bg.mjs --kill     # stop the game (restores the BepInEx console window)
//   node tools/launch-bg.mjs --console  # keep the BepInEx console window visible
//   node tools/launch-bg.mjs --visible  # launch on the desktop (debugging: you can see it)
//
// The game is created on a private desktop by default: its windows never appear on your desktop,
// so nothing flashes and the foreground window never changes (--visible for a normal launch).
import { isGameRunning, killGame, ensureRunning, minimizeGameNow, setConsoleWindow, sleep, getJson } from './runner/lib.mjs';
import { enterWorld } from './session.mjs';

const args = process.argv.slice(2);
const log = (s) => console.log(s);

if (args.includes('--kill')) {
  killGame();
  setConsoleWindow(true); // 人工游玩恢复日志窗口
  log('game stopped, BepInEx console window restored');
  process.exit(0);
}

if (!isGameRunning()) log('launching Sephiria (minimized)...');
const h = args.includes('--visible')
  ? await ensureRunning({ log, consoleWindow: args.includes('--console'), hidden: false })
  : await ensureRunning({ log, consoleWindow: args.includes('--console') });
log(`bridge ok ${h.version} scene=${h.scene}`);

if (args.includes('--world')) {
  const arg = (name, fallback = null) => {
    const i = args.indexOf(name);
    return i >= 0 && args[i + 1] && !args[i + 1].startsWith('--') ? args[i + 1] : fallback;
  };
  const w = await enterWorld({
    log,
    slot: Number(arg('--slot', '0')) || 0,
    name: arg('--name', ''),
    skip: !args.includes('--no-skip'),
    fresh: args.includes('--fresh'),
  });
  const runs = await getJson('/runs').catch(() => ({}));
  log(`world ok scene=${w.scene} inRun=${runs.inRun}`);
}

for (let i = 0; i < 3; i++) { minimizeGameNow(); await sleep(700); }
log(args.includes('--visible') ? 'windows minimized' : 'hidden desktop (nothing to minimise)');
process.exit(0);