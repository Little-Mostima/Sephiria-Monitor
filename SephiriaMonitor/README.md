# SephiriaMonitor 0.8.0

Sephiria（Steam AppID 2436940，Unity 6000.3.21f1，Mono）的 **BepInEx 纯监听插件**：在游戏进程内被动采集运行状态、战斗事件、楼层结构与静态数据库，通过本地 HTTP 桥（默认 `127.0.0.1:47901`，只读 GET）交给外部（MCP / 脚本 / AI agent）。游戏界面里不画任何东西。

- 设计目标：**只监听，不改游戏**。唯一能改游戏状态的是 `/enter`（从标题界面进存档）。
- 不提供对外 C# API。
- 不修改游戏表现（不改音量、不屏蔽反馈、不画 UI）。

## 安装

1. 装好 BepInEx 5.4.x（游戏根目录出现 `BepInEx/` 与 `winhttp.dll`）。
2. 把 `BepInEx/plugins/SephiriaMonitor/SephiriaMonitor.dll` 放到游戏根目录同路径下。
3. 启动游戏。日志出现 `BOOT|ready|bridge=47901|session=...` 与 `HARMONY|summary|patched=16|...` 即成功。
4. `GET http://127.0.0.1:47901/health` 应返回 `{"ok":true,"version":"0.8.0",...}`。

升级插件前**必须先关游戏**：dll 被进程映射时覆盖会失败（Windows 报 user-mapped section open）。

启动到桥可用通常约 14 秒（Steam 初始化慢时见过 2 分钟）。到标题后默认 **0.65s 自动进入 SLOT1**。

## 配置（`BepInEx/config/SephiriaMonitor.cfg`）

| 段/键 | 默认 | 说明 |
|---|---|---|
| bridge/Port | 47901 | HTTP 桥端口 |
| title/EnableTitleControl | true | 允许从标题界面进存档（/title /saves /enter） |
| title/AutoEnter title/AutoEnterSlot | true / 1 | 到标题自动进存档 |
| compat/TitleHarmonyHooks compat/GameEventHooks | true | Harmony 补丁开关 |
| data/DatabaseExport | true | 进对局后导出数据库 CSV |
| data/Snapshots | true | 存档键快照 |
| data/ProbeEvents | false | 是否把"自己打自己"的探针命中写进事件流（默认只计数） |

## HTTP 路由（32 条，除 `/enter` 外全部只读）

- 实时状态：`/` `/health` `/run`（轻量，单位数 1s 缓存）`/player`（全量 + nearby 24 个）`/world` `/state` `/statefull` `/scene` `/progress`（只 20 个关键键）`/inventory`（实时反射，非快照）`/inventory_snapshot`（旧快照口径）`/combos`（连击/套装）`/statuses`（buff/debuff/状态实例）`/logs` `/session`
- 事件流：`/events?n=&after=&kind=`（根对象带 `cursor`，单调递增、`after` 排他；`kind` 过滤）、`/damage?n=`、`/path?n=`（帧内位移轨迹）
- 楼层：`/floor`、`/map?r=`（瓦片 ASCII）、`/rooms`、`/mappath?x=&y=`（BFS 路线）、`/mapscan?r=`（重扫）、`/probe?x=&y=`（该点碰撞体明细）
- 写/交互：`/enter?slot=1..3`、`/enterstatus`、`/screenshot`、`/snapshot`
- 标题/内省：`/title` `/saves` `/objects?type=` `/object?type=&index=`

## 事件类型

`damage`（带 `victimInst`/`attackerInst`，非致命标 `lethal`）、`death`（`killerInst`/`byPlayer`）、`exp`、`money`、`loot`（`GridInventory.NotifyAddItem`）、`item`、`evade`、`floor`、`scene`、`gamelog`（只留"获得/升级/解锁/神器/遗物/掉落"等关键词）、`save`、`title`。

只进内存环形缓冲、不落盘的：`hb`（5s 心跳）、`spawn`/`despawn`（改由 `spawnCount`/`despawnCount` 计数）。

去重与噪声：Harmony 同一命中重复进入 `PostApplyDamage` 时，按 (victim, damage 实例, 0.5s 时间窗) 丢弃，计数见 `/health` 的 `deduped`/`lateDuplicates`；"自己打自己"的探针命中只计数（`probes`），默认不写事件流。

## 落盘（`BepInEx/plugins/SephiriaMonitor/out/session-<时间戳>/`）

| 文件 | 内容 |
|---|---|
| `state.json` | 1Hz 玩家/世界快照（level/exp/expRatio/statPoints/kills/rooms/move） |
| `state_full.json` | 90 个关键存档键（白名单） |
| `events.jsonl` | 事件流（HB/spawn/despawn 已排除） |
| `logs.log` | Unity 日志镜像（过滤 discord-ipc 噪声） |
| `floors/floor-NNNN-<层名>.json` | 每层一份：整层瓦片 ASCII + 房间表（id/网格/矩形/接口/难度/刷怪/单位）+ 门/楼梯/传送门 |
| `db/*.csv` | 49 张静态表（UTF-8 **带 BOM**），含 `item_all`/`weapon_all`/`weapon_skill_all`/`active_skill_all` |
| `snapshots/` | `/snapshot` 产出的存档快照 |

## 构建与部署

```powershell
& .\_tools\dotnet\dotnet.exe build .\SephiriaMonitor\SephiriaMonitor.csproj -c Release
Stop-Process -Name Sephiria -Force
Copy-Item .\SephiriaMonitor\bin\Release\SephiriaMonitor.dll `
  '<SEPHIRIA_DIR>\BepInEx\plugins\SephiriaMonitor\' -Force
Start-Process "steam://rungameid/2436940"
```

产物在 `bin\Release\SephiriaMonitor.dll`（**没有** `netstandard2.1` 子目录）。

## 已知边界 / 坑

- 游戏用 Input System package：插件里**不要**用 `UnityEngine.Input`，否则每帧抛 `InvalidOperationException` 并中断 `MonitorHost.Tick()`（表现为心跳停止、state.json 不再更新）。键盘读 `Keyboard.current[key].wasPressedThisFrame`。
- 类里不要定义名叫 `Json` 的成员，会遮蔽 `Json` 静态类（CS0119）。
- `UnityEngine.AudioListener` 在 `UnityEngine.AudioModule`，本项目未引用。
- Buff/Debuff 目前只有名字（`StatusEffectController.activated` 的 key），没有剩余秒数。
- `out/session-*/events.jsonl`、`logs.log` 的 `Length` 在外部看可能是 0（StreamWriter 缓冲），内容可直接读。
- 玩家单位始终在屏幕中心，「单位注册表」通过 `UnitAvatar.Awake/OnDestroy` 钩子维护，避免每秒 `Resources.FindObjectsOfTypeAll` 全场景扫描。
