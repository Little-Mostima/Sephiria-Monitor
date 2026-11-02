#!/usr/bin/env node
// Start Sephiria in the background.
//
//   node tools/launch-bg.mjs            # launch if needed, minimize, wait for the bridge
//   node tools/launch-bg.mjs --world    # also auto-enter slot 1 and wait for MainWorld
//   node tools/launch-bg.mjs --kill     # only stop the game
//
// Both the game window and the BepInEx console are minimized every few seconds while the
// game boots, so working in another window is not interrupted.
import { isGameRunning, killGame, ensureRunning, waitWorld, minimizeGameNow, sleep } from './runner/lib.mjs';

const args = process.argv.slice(2);
const log = (s) => console.log(s);

if (args.includes('--kill')) {
  killGame();
  log('game stopped');
  process.exit(0);
}

if (!isGameRunning()) log('launching Sephiria (minimized)...');
const h = await ensureRunning({ log });
log(`bridge ok ${h.version} scene=${h.scene}`);

if (args.includes('--world')) {
  const w = await waitWorld({ log });
  log(`world ok scene=${w.scene} inRun=${w.inRun}`);
}

for (let i = 0; i < 3; i++) { minimizeGameNow(); await sleep(700); }
log('windows minimized');
process.exit(0);