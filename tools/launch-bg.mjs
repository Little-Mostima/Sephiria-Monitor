#!/usr/bin/env node
// Start Sephiria in the background.
//
//   node tools/launch-bg.mjs            # launch if needed, minimize, wait for the bridge
//   node tools/launch-bg.mjs --world    # also auto-enter slot 1 and wait for MainWorld
//   node tools/launch-bg.mjs --kill     # stop the game (restores the BepInEx console window)\n//   node tools/launch-bg.mjs --console  # keep the BepInEx console window visible
//
// Both the game window and the BepInEx console are minimized every few seconds while the
// game boots, so working in another window is not interrupted.
import { isGameRunning, killGame, ensureRunning, waitWorld, minimizeGameNow, setConsoleWindow, sleep } from './runner/lib.mjs';

const args = process.argv.slice(2);
const log = (s) => console.log(s);

if (args.includes('--kill')) {
  killGame();
  setConsoleWindow(true); // 人工游玩恢复日志窗口
  log('game stopped, BepInEx console window restored');
  process.exit(0);
}

if (!isGameRunning()) log('launching Sephiria (minimized)...');
const h = await ensureRunning({ log, consoleWindow: args.includes('--console') });
log(`bridge ok ${h.version} scene=${h.scene}`);

if (args.includes('--world')) {
  const w = await waitWorld({ log });
  log(`world ok scene=${w.scene} inRun=${w.inRun}`);
}

for (let i = 0; i < 3; i++) { minimizeGameNow(); await sleep(700); }
log('windows minimized');
process.exit(0);