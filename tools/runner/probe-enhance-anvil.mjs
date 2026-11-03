// 铁砧必出探针：重启游戏 → 进档 → 逐层找「Anvil 0_Moleland」→ 触发铁砧 → 断言 localWeaponList 含 981000。
// 用法：node tools/runner/probe-enhance-anvil.mjs [--keep] [--no-restart]
import fs from 'node:fs';
import path from 'node:path';
import { getJson, tryJson, sleep, RUNS, ensureRunning, waitBridge, backupSaves, restoreSaves } from './lib.mjs';

const KEEP = process.argv.includes('--keep');
const NO_RESTART = process.argv.includes('--no-restart');
const stamp = new Date().toISOString().replace(/[:.]/g, '-');
const outDir = path.join(RUNS, `enhance-anvil-${stamp}`);
fs.mkdirSync(outDir, { recursive: true });
const log = (...a) => { const s = a.join(' '); console.log(s); fs.appendFileSync(path.join(outDir, 'probe.log'), s + '\n'); };

const gj = (p, t = 20000) => getJson(p, t).catch(e => ({ error: String(e.message || e) }));

async function waitScene(scene, timeoutMs = 180000) {
  const t0 = Date.now();
  while (Date.now() - t0 < timeoutMs) {
    const h = await tryJson('/health', 4000);
    if (h && h.scene === scene) return true;
    await sleep(1000);
  }
  return false;
}

async function gotoFloor(name) {
  const r = await gj(`/goto?floor=${encodeURIComponent(name)}`, 30000);
  // 等场景真的换过去：/progress 的 run.floor.name 与请求一致，且 /spots 已就绪
  for (let i = 0; i < 60; i++) {
    const prog = await tryJson('/progress', 6000);
    const cur = String(prog?.['run.floor.name'] || '');
    if (cur.startsWith(name)) {
      const spots = await tryJson('/spots', 6000);
      if (spots && Number(spots.total) > 0) {
        const rooms = await tryJson('/rooms', 6000);
        return { r, rooms };
      }
    }
    await sleep(250);
  }
  return { r, rooms: null };
}

const evidence = { startedAt: new Date().toISOString(), floorsTried: [], hit: null, ok: false };
let backup = null;

try {
  if (!NO_RESTART) {
    backup = backupSaves('enhance-anvil');
    await ensureRunning({ restart: true, log: m => log('run', m) });
    await waitBridge(150000);
    if (!(await waitScene('Title'))) throw new Error('no Title scene');
    await sleep(1500);
    const enter = await gj('/enter?slot=SLOT2', 60000);
    log('enter', JSON.stringify(enter));
    if (!(await waitScene('MainWorld'))) throw new Error('no MainWorld');
    await sleep(4000);
  }

  const equip = await gj('/equip?weapon=20', 20000);
  log('equip', JSON.stringify(equip));

  const floors = (await gj('/floors', 20000))?.floors || [];
  log('floors', String(floors.length));

  for (const f of floors) {
    const name = f.name;
    if (!name || /town|rabbittown|training|house|shrine|salon|tree|zone/i.test(name)) continue;
    const { r, rooms } = await gotoFloor(name);
    const room = rooms && Array.isArray(rooms.rooms) ? rooms.rooms.find(x => /anvil/i.test(String(x.name || ''))) : null;
    evidence.floorsTried.push({ floor: name, moved: Boolean(r && r.moved), actual: rooms?.floor ?? null, anvilRoom: room ? room.name : null });
    log('try', name, 'moved=' + Boolean(r && r.moved), 'actual=' + (rooms?.floor ?? '?'), 'anvil=' + (room ? room.name : '-'));
    if (!room) continue;

    // 命中：先传送到房间中心（/spots 只列半径内的交互点），再交互
    const rect = room.rect || [];
    const cx = rect.length === 4 ? (rect[0] + rect[2]) / 2 : null;
    const cy = rect.length === 4 ? (rect[1] + rect[3]) / 2 : null;
    if (cx != null) {
      const tp = await gj(`/tp?x=${cx}&y=${cy}`, 15000);
      log('tp', JSON.stringify(tp));
      // 等交互点注册（换层后 Interactable 要等 floor finalized 才出现）
      for (let i = 0; i < 40; i++) {
        const spots = await tryJson('/spots', 6000);
        if (spots && Number(spots.total) > 0) break;
        await sleep(250);
      }
    }
    const use = await gj(`/use?name=${encodeURIComponent(room.name)}&tp=1`, 20000);
    log('use', JSON.stringify(use));
    await sleep(1200);
    const obj = await gj('/object?type=Anvil&depth=2', 15000);
    const list = obj?.fields?.localWeaponList;
    const flat = Array.isArray(list) ? JSON.stringify(list) : String(list || '');
    const logs = await gj('/logs?limit=300', 15000);
    const hitLog = /anvil hit: inBattle=(\w+)/.exec(JSON.stringify(logs));
    evidence.hit = { floor: rooms.floor, room: room.name, localWeaponList: list ?? null, inBattle: hitLog ? hitLog[1] : null };
    log('list', flat);
    log('inBattle', hitLog ? hitLog[1] : '?');
    evidence.ok = flat.includes('981000');
    if (evidence.ok) break;
    log('retry-next-floor');
  }
} catch (e) {
  evidence.error = String(e.message || e);
  log('ERROR', evidence.error);
} finally {
  evidence.finishedAt = new Date().toISOString();
  fs.writeFileSync(path.join(outDir, 'evidence.json'), JSON.stringify(evidence, null, 2));
  log('evidence', path.join(outDir, 'evidence.json'));
  if (backup && !KEEP) { try { restoreSaves('enhance-anvil'); log('saves restored'); } catch (e) { log('restore failed', String(e.message || e)); } }
  process.exit(evidence.ok ? 0 : 3);
}
