#!/usr/bin/env node
// Build one mod: node tools/build-mod.mjs <mod|path> [--deploy] [--quiet]
// 单一构建入口：公共配置在 work/Directory.Build.props，版本号只来自 metadata.json。
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { REPO, WORK, GAME, ADDONS } from './runner/lib.mjs';

const args = process.argv.slice(2);
const target = args.find((a) => !a.startsWith('--'));
const deploy = args.includes('--deploy');
const quiet = args.includes('--quiet');
if (!target) {
  console.error('usage: node tools/build-mod.mjs <mod|path> [--deploy] [--quiet]');
  process.exit(2);
}
const modDir = path.isAbsolute(target) ? target : path.join(WORK, target);
if (!fs.existsSync(path.join(modDir, 'metadata.json'))) {
  console.error(`not a mod dir (no metadata.json): ${modDir}`);
  process.exit(2);
}
const meta = JSON.parse(fs.readFileSync(path.join(modDir, 'metadata.json'), 'utf8').replace(/^\uFEFF/, ''));
const srcDir = path.join(modDir, 'src');
const csprojs = fs.existsSync(srcDir) ? fs.readdirSync(srcDir).filter((f) => f.endsWith('.csproj')) : [];
if (csprojs.length !== 1) {
  console.error(`expected exactly one .csproj in ${srcDir}, found ${csprojs.length}`);
  process.exit(2);
}
const dotnet = process.env.SEPHIRIA_DOTNET || 'D:\\AscensionMod\\_work\\dotnet\\dotnet.exe';
const res = spawnSync(dotnet, ['build', path.join(srcDir, csprojs[0]), '-c', 'Release', '-v', 'q', '--nologo', '-p:GenerateDependencyFile=false'], {
  stdio: quiet ? 'pipe' : 'inherit',
  encoding: 'utf8',
  env: { ...process.env, SEPHIRIA_GAME_DIR: GAME },
});
if (res.status !== 0) {
  const out = ((res.stdout || '') + (res.stderr || '')).trim().split('\n').slice(-6).join('\n');
  console.error(out || 'build failed');
  process.exit(1);
}
const dist = path.join(modDir, 'dist');
fs.mkdirSync(dist, { recursive: true });
fs.copyFileSync(path.join(modDir, 'metadata.json'), path.join(dist, 'metadata.json'));
const dll = path.join(dist, meta.dllFile);
if (!fs.existsSync(dll)) {
  console.error(`build produced no ${meta.dllFile} in ${dist}`);
  process.exit(1);
}
let deployed = null;
if (deploy) {
  const modJsonPath = path.join(modDir, 'mod.json');
  const modJson = fs.existsSync(modJsonPath) ? JSON.parse(fs.readFileSync(modJsonPath, 'utf8')) : {};
  const entry = (modJson.deploy && modJson.deploy.addons && modJson.deploy.addons[0]) || {};
  const folder = entry.folder || meta.modName.replace(/[^A-Za-z0-9]/g, '');
  deployed = path.join(ADDONS, folder);
  fs.mkdirSync(deployed, { recursive: true });
  for (const f of fs.readdirSync(dist)) fs.copyFileSync(path.join(dist, f), path.join(deployed, f));
}
const size = fs.statSync(dll).size;
console.log(`BUILD OK  ${path.basename(modDir)}  ${meta.modName} v${meta.modVersion}  ${meta.dllFile} ${size} B${deployed ? `  -> ${deployed}` : ''}`);