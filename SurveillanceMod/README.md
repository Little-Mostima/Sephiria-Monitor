# Sephiria Monitor — MCP 部署说明

把运行中的 Sephiria（Steam appid 2436940）的游戏状态，以 **MCP（stdio）** 形式交给 AI agent，替代「截图 → 肉眼判断」。整条链路默认只读；唯一的状态变更类工具是 `sephiria_enter`（让游戏自己加载一个存档槽）。

主界面（Title 场景）也能用：插件在 BepInEx 的 `Awake()` 里就 `BridgeServer.Start()`，所以 47901 从进程启动即在线，可以在**还没进存档**时列出三个存档槽，并由 agent 直接进入指定槽位（不点鼠标、不截图）。

```
agent ──MCP(stdio/JSON-RPC)──> server.mjs ──HTTP GET 127.0.0.1:47901──> BepInEx 插件 SephiriaMonitor
                                                                            │
                                                        out/session-<时间>/ {db/*.csv, logs.log, events.jsonl, snapshots/*.json}
```

## 文件

| 路径 | 作用 |
| --- | --- |
| `SurveillanceMod\server.mjs` | MCP 服务器本体。ESM，零依赖，只用内置 `fetch`/`fs`。 |
| `SurveillanceMod\selfcheck.mjs` | 独立自检：像 DSH 一样起子进程，跑 initialize / tools/list / 6 个工具 / 异常路径，输出 PASS/FAIL 与退出码。 |
| `SephiriaMonitor\` | 插件源码（C# / BepInEx 5.4，netstandard2.1）。 |
| `<SEPHIRIA_DIR>\BepInEx\plugins\SephiriaMonitor\` | 部署目录：`SephiriaMonitor.dll` + `out\session-*\`。 |

## 环境变量

| 变量 | 默认 | 说明 |
| --- | --- | --- |
| `SEPHIRIA_BRIDGE` | `http://127.0.0.1:47901` | 游戏内 HTTP 桥地址；改端口时两边都要改。 |
| `SEPHIRIA_BRIDGE_TIMEOUT_MS` | `10000` | 单次 GET 超时；`/snapshot`、`/screenshot` 固定用 ≥45s（要等 Unity 主线程）。 |

## DSH 侧注册（已生效，勿重复添加）

`<DSH_HOME>\profiles\desktop\cordis.patch.yml` 末尾的 insert 条目：

```yaml
- insert:
    - id: sephiria-agent-mcp
      name: '@deepseek-ai/dsh-mcp-client'
      config:
        serverName: sephiria
        transport: stdio
        command: '<DSH_HOME>\dsh-runtimes\dsh-primary-runtime\dependencies\node\bin\node.exe'
        args:
          - 'SurveillanceMod\server.mjs'
        env:
          SEPHIRIA_BRIDGE: http://127.0.0.1:47901
          SEPHIRIA_BRIDGE_TIMEOUT_MS: '10000'
        failOnStartupError: false
```

要点：

- `dsh-mcp-client` 是 Desktop 内建包，**不需要 npm install**。
- 改 `cordis.patch.yml` 会热加载：约 8 秒后 `Get-CimInstance Win32_Process` 能看到新 PID 的 `node.exe ...\server.mjs`，**不用重启 DSH**。
- 只改 `server.mjs` 不会自动重启子进程 —— 想加载新代码，就动一下 `cordis.patch.yml`（例如加一行注释）。

## 22 个工具

| 工具 | 桥路由 | 说明 |
| --- | --- | --- |
| `sephiria_health` | `/health` + 本地文件 | 部署自检：延迟、mod/Unity 版本、session 目录、CSV 数量与行数、dll 时间戳、最近 error 行、verdict。**任何工具异常时先跑它**。 |
| `sephiria_status` | `/state` | 场景、hp/mp/金钱、附近单位、计数。首选，替代截图。 |
| `sephiria_player` | `/player` | 位置、等级/经验%、属性点、击杀、房间进度、附近 24 个单位（含阵营/是否敌对）。 |
| `sephiria_world` | `/world` | 世界里全部单位（名字/类/坐标/hp）。 |
| `sephiria_logs` | `/logs?n=` | 进程内 Unity 日志环（已滤 discord-ipc 噪声），可 `type=Error` 过滤。全量在 `<session>/logs.log`。 |
| `sephiria_events` | `/events?n=&after=&kind=` | 结构化事件流（damage/death/exp/money/loot/evade/floor/scene/gamelog/save/title/hb），根对象带 `cursor`，`after` 为排他游标。 |
| `sephiria_damage` | `/damage?n=` | 伤害事件（攻方/受方/原始值/实际值/元素/暴击/结果）。 |
| `sephiria_snapshot` | `/snapshot?reason=` | 抓全量快照并与上一份 diff（added/removed/changed），落盘 `snapshots/snapshot-<reason>-HHmmss.json`。 |
| `sephiria_inventory` | `/inventory` | 背包/石板/仓库/属性，**请求时实时反射**（旧快照口径保留在 `/inventory_snapshot`）。 |
| `sephiria_combos` | `/combos` | 连击/套装：流派、当前件数、已生效档位、每档授予的 status 列表。 |
| `sephiria_statuses` | `/statuses` | buff / debuff / 孤立状态实例：层数、剩余时长比例、施加者。 |
| `sephiria_combat` | `/combat?top=` | 本局聚合：命中率、失败原因分布、按来源/元素/受击者 top、玩家受击、掉落/金钱计数、60s 爆发曲线。**代替翻 /damage**。 |
| `sephiria_runs` | `/runs` | 对局边界：inRun、seed、raceId、stages、generatedFloors、timeInWorld、sessionSerial、当前层；起止也会作为 `kind="run"` 事件出现。 |
| `sephiria_progress` | `/progress` | 白名单 20 键：`run.floor.*` / `run.hard.*` / `run.seed` / `run.env.*`。 |
| `sephiria_objects` | `/objects?type=&limit=` | 按类型列存活对象（如 ItemChest、MoneyChest、EPFG）。 |
| `sephiria_object` | `/object?type=&index=&depth=` | 反射 dump 单个对象（depth 可到 3）。 |
| `sephiria_map` | `/map?r=&x=&y=` | 当前楼层的瓦片 ASCII 图（`#` 墙 `.` 地 `~` 水，`P` 玩家），可带目标坐标；`path:true` 返回 BFS 路线。 |
| `sephiria_databases` | `/session` + 本地 | CSV 库：不给 `table` 只列清单与行数（49 张）；给 `table`/`q`/`limit`/`offset` 直接返回行。 |
| `sephiria_screenshot` | `/screenshot` | 兜底：数值不够用时才截图（返回 PNG）。 |
| `sephiria_title` | `/title` | Title 场景就绪度：lobby/面板是否存在、选中槽、能否进入、桥端口、三槽摘要。主界面首选。 |
| `sephiria_saves` | `/saves` | 三槽存档列表：是否存在、玩家名、最后保存时间、死亡次数、序章标记、文件大小/mtime。 |
| `sephiria_enter` | `/enter?slot=&create=` | **唯一会改变游戏状态的工具**：设 SelectedProfile 并调用游戏自己的 `UI_TitleLobby.NetworkHost()`（必要时重试到渐隐结束），随后轮询到 MainWorld 并附一次 `/state`。 |

## 主界面免截图进存档（v0.3.0）

```powershell
(Invoke-WebRequest -UseBasicParsing http://127.0.0.1:47901/title).Content   # {lobby:true, canEnter:true, slots:[...]}
(Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:47901/enter?slot=1').Content
(Invoke-WebRequest -UseBasicParsing http://127.0.0.1:47901/enterstatus).Content  # pending.state: running -> ok
```

实现要点：直接调 `NetworkHost()` 而不先打开存档面板，因为 `UI_SaveProfilePanel.OnOpened()` 才会设置 `hasPrologueClearSave`，绕开面板就不会弹「跳过序章」二选一对话框；`NetworkHost()` 在 `ScreenFader.IsFading` 时会提前 return，所以 `TitleControl.Tick()` 每 1 秒重试一次，直到 `NetworkServer.active` 或场景离开 Title（60 秒超时）。

实测（2026-11-01 04:36，A 级）：Title → `sephiria_enter{slot:"1"}` 11 秒后 `scene=MainWorld`，`hp=95/95`、`money=400`、`units=22`、`playerCount=1`；日志留痕 `TITLE|enter|request`、`TITLE|networkhost|profile=SLOT1`、`TITLE|enter|ok|SLOT1|attempts=1`。

工具结果统一 `{content:[{type:'text',text}]}`，失败带 `isError:true`；文本按工具截断（logs 9000、events 12000、object 12000、snapshot/inventory/progress 8000 字符）。

## 日常验证

```powershell
# 1) 桥本身是否活着（不经过 MCP）
(Invoke-WebRequest -UseBasicParsing http://127.0.0.1:47901/health).Content

# 2) 端到端自检（9 项 PASS 才算部署完好）
& '<DSH_HOME>\dsh-runtimes\dsh-primary-runtime\dependencies\node\bin\node.exe' SurveillanceMod\selfcheck.mjs
```

自检覆盖：initialize、tools/list（22 个工具齐全）、health、status、logs、databases、saves、snapshot 往返、未知工具被干净拒绝、**桥不可用时的明确提示**（会让第二个 server 指向死端口验证）。

## 故障排查

| 现象 | 原因与处理 |
| --- | --- |
| 工具返回 `refused the connection` | 游戏没运行 / 还在加载 / mod 没加载。看 `BepInEx\LogOutput.log` 有没有 `[monitor]` 启动行、`plugins\SephiriaMonitor\SephiriaMonitor.dll` 是否存在。 |
| 工具超时 | 桥把每次读取 marshal 到 Unity 主线程；加载、快照、重场景时可能超时。重试或先 `sephiria_health`。 |
| DSH 里工具消失/仍是老版本 | `cordis.patch.yml` 没动过 → 子进程没重启。改一下 patch 文件触发重载。 |
| `sephiria_databases` 为空 | 还没进存档（MainWorld）。库导出发生在主世界。 |
| `The requested operation cannot be performed on a file with a user-mapped section open` | 游戏运行时无法覆盖插件 dll。必须先 `Stop-Process -Name Sephiria -Force`，再拷贝，再用 `Get-FileHash` 比对 SHA256。 |

## 重新部署 dll（改插件代码时）

```powershell
Stop-Process -Name Sephiria -Force; Start-Sleep -Seconds 4
Copy-Item SephiriaMonitor\bin\Release\netstandard2.1\SephiriaMonitor.dll `
          '<SEPHIRIA_DIR>\BepInEx\plugins\SephiriaMonitor\' -Force
Get-FileHash $src, $dst          # 必须一致，否则白跑一轮验证
Start-Process 'steam://rungameid/2436940'
# ── 约 22-25s 到 Title；然后不要点鼠标，用 /title → /enter?slot=1（或 MCP sephiria_enter）进存档，约 11-16s 到村庄
```

进存档后 `/health` 的 `scene` 会变成 `MainWorld`，同时开始导出 CSV。

## 边界

- 只读：除 `sephiria_enter` 外所有工具只发 GET；`/snapshot` 只让 mod 在自己的 out 目录写文件。
- `sephiria_enter` 是唯一会改游戏状态的调用：它等价于玩家在主界面点存档卡（设 SelectedProfile → NetworkHost → 开主机）。不会被 mod 自动调用。
- 不改游戏内存、不注入逻辑判断、不发游戏内网络包。
- 端口固定 47901（TCP，仅 127.0.0.1 监听）。
