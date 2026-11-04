// Single source of truth for every path, port and tool location the scripts use.
//
// Why this file exists: the game path used to be resolved in four places, the bridge port in
// twelve, and dotnet in three - so moving the game or the SDK meant finding all of them and
// usually missing one. Nothing outside this file may hardcode a path or a port.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const WORK = path.join(REPO, 'work');
export const STATE = path.join(WORK, '_state');
export const RUNS = path.join(WORK, '_runs');
export const DOCS = path.resolve(REPO, '..', '_re');

export const GAME = process.env.SEPHIRIA_GAME_DIR || 'D:\\steam\\steamapps\\common\\Sephiria';
export const PORT = Number(process.env.SEPHIRIA_BRIDGE_PORT || 47901);
export const BASE = `http://127.0.0.1:${PORT}`;
// Documents may be redirected (OneDrive / known-folder move), so resolve it the way Unity does.
export const SAVES = process.env.SEPHIRIA_SAVES_DIR || path.join(os.homedir(), 'Documents', 'Saved Games', 'Sephiria');
export const ADDONS = path.join(GAME, 'AddOns');
export const PLUGINS = path.join(GAME, 'BepInEx', 'plugins');
export const WORKSHOP_LOCAL = path.join(GAME, 'WorkshopLocal');
export const STEAM_URL = 'steam://rungameid/2436940';

// The bundled SDK lives in _work/dotnet; fall back to PATH so a machine without it still works.
export const DOTNET = (() => {
  if (process.env.SEPHIRIA_DOTNET) return process.env.SEPHIRIA_DOTNET;
  const exe = process.platform === 'win32' ? 'dotnet.exe' : 'dotnet';
  const bundled = path.join(REPO, '..', 'dotnet', exe);
  return fs.existsSync(bundled) ? bundled : 'dotnet';
})();
