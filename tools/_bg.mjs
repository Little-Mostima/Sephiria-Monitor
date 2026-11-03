// Move the game to the background: kill the visible instance and relaunch it on the
// private window station (hidden=true), then arm it so it stays in MainWorld.
import { killGameAndWait, launchGame, waitBridge, arm, health } from './runner/lib.mjs';

const log = (m) => console.log(new Date().toISOString().slice(11, 19), m);
killGameAndWait();
log('visible instance stopped');
launchGame({ hidden: true });
log('launched hidden');
const h = await waitBridge(180000);
log('bridge ok: ' + (h && h.ok ? (h.scene || 'title') : 'no health'));
const h2 = await health(4000);
log('scene: ' + (h2 && h2.scene));
await arm(600);
log('armed 600 minutes');
