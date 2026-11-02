# Sephiria Monitor

**一个插件，既是只读监控，也是 MCP 服务器。**

装上它、把游戏跑起来，`http://127.0.0.1:47901` 就是一个本地只读数据源。任何 AI 客户端（Claude Desktop / Code、DSH、自写的 agent）都可以通过 **MCP** 或 **普通 GET** 读这里的游戏状态：当前楼层、角色属性、护符连击、伤害统计、事件流、数据库 CSV、存档槽、mod 挂点目录。

项目目标不是"再做一个内存修改器"，而是**给 mod 作者和 agent 提供一份可信的运行时事实**：数值从游戏对象里现读，不是从缓存快照里翻旧值。

- 游戏：Sephiria 1.1.1（Unity 6000.3.21f1，Mono）
- 依赖：BepInEx 5.4.x (x64)
- 版本：插件 **0.20.0**，协议 **MCP 2025-06-18**

---

## 1. 仓库结构

```
SephiriaMonitor/          # BepInEx 插件（C#，全部源码）
  MonitorPlugin.cs        # 插件入口，版本号
  BridgeServer.cs         # 本地 HTTP 桥（GET 路由 + POST /mcp 入口）
  McpServer.cs            # 内嵌 MCP 服务器（JSON-RPC，16 个工具）
  MerchantOps.cs          # 商人/楼层操作：列表、传送、开店、货架抽样
  Live.cs                 # 实时读取（inventory / combos / statuses）
  Combat.cs               # 战斗事件在线聚合
  RunTracker.cs           # 对局边界（run start/end）
  EventLog.cs / ...       # 事件、快照、楼层、数据库导出
  hooks.json              # mod 挂点目录（79 条，随 dll 一起部署）
tools/
  mcp-stdio.mjs           # 可选：stdio → HTTP 代理（给只支持 stdio 的客户端）
  selfcheck.mjs           # 端到端自检（23 项）
  preflight.mjs           # 一个 mod 的上线门禁：静态 → 编译 → 打包 → 跑套件
  pack-workshop.mjs       # 把 work/<mod> 打成工坊包（嵌套 metadata + zip）
  runner/
    lib.mjs               # 桥客户端 / 进程生命周期 / 快照与存档备份
    run-suite.mjs         # JSON 套件执行器（expect/baseline/restart/files…）
    examples/             # hello.suite.json：最小冒烟套件
install.ps1               # 一键安装/升级
```

`SurveillanceMod/`（原来的 Node MCP 服务器）**已经并入插件**，不再需要单独进程。0.10.0 起：

- 插件启动即监听 `47901`，同时提供 GET 路由和 `POST /mcp`；
- MCP 的 16 个工具在 C# 里实现，内部直接复用同一套路由函数；
- Node 只在**客户端只会说 stdio** 时用得上（`tools/mcp-stdio.mjs`，20 行逻辑，无依赖）。

---

## 2. 能监听到什么

| 类别 | 内容 |
| --- | --- |
| 状态 | 场景、玩家 hp/mp/money/等级/坐标、单位数量、事件/日志计数、桥版本 |
| 角色细节 | 64 项 `ECustomStat`（属性**实时**读取）、背包格子、石板与刻印、护符连击档位 |
| 战斗 | 每次 `DamageInstance`（类型/元素/来源/暴击/结果）、击杀、死亡、掉落、拾金、经验；`/combat` 直接给命中率、失败分布、分来源聚合 |
| 世界 | 当前楼层与 `FloorData`、楼层生成器内部结构（程序化房间拼图）、地图碰撞、全量单位 |
| 运行时对象 | 任意类型按名枚举，或单个对象按 `depth` 反射展开（`/objects`、`/object`） |
| 对局 | run 开始/结束边界、seed、raceId、stage 列表、每层进度 |
| 数据 | 27 个数据库导出的 CSV（`out/session-*/db/*.csv`），按表/关键词查询 |
| 存档 | 三个槽位的存在性、大小、修改时间、玩家名、死亡数（只读） |
| mod 支持 | `hooks.json` 79 条挂点（签名+行号+真实调用点），`sephiria_hooks` 可按关键词/区域过滤 |

---

## 3. 安装

### 3.1 前置条件

| 需要 | 说明 |
| --- | --- |
| Sephiria 1.1.1（Steam 版） | 游戏根目录下应有 `Sephiria_Data\Managed\Assembly-CSharp.dll` |
| BepInEx 5.4.x **x64** | 必须已经装好；判断标准：`<游戏>\BepInEx\core\BepInEx.dll` 存在 |
| .NET SDK 8（仅在需要编译时） | 只有 runtime 的机器编译不了，`dotnet --list-sdks` 必须能列出 8.x |

游戏路径默认按 `D:\steam\steamapps\common\Sephiria` → `C:\Program Files (x86)\Steam\...` → `D:\Steam\...` 顺序猜，猜不到就用 `-GameDir` 指定。

### 3.2 安装（推荐：一条命令）

```powershell
git clone https://github.com/Little-Mostima/Sephiria-Monitor.git
cd Sephiria-Monitor
.\install.ps1 -Build -StopGame
```

`install.ps1` 会依次做四件事：

1. 找到游戏目录（找不到就报错，不会乱装）；
2. 用带 SDK 的 dotnet 编译 `SephiriaMonitor\`（Release）；
3. 停掉运行中的 `Sephiria` 进程（dll 被占用时才需要）；
4. 把 `SephiriaMonitor.dll` + `hooks.json` 复制到 `<游戏>\BepInEx\plugins\SephiriaMonitor\`，并打印文件大小与 sha256 前 16 位。

| 参数 | 作用 |
| --- | --- |
| `-Build` | 先从源码编译；不加则用仓库里的 `dist\` 或 `bin\Release\` 预制 dll |
| `-StopGame` | 替换前先停游戏（不加时如果 dll 被占用会失败） |
| `-GameDir <path>` | 指定游戏根目录 |
| `-Dotnet <path>` | 指定带 SDK 的 dotnet.exe |

### 3.3 安装（手动，三步）

```powershell
# 1) 编译（可跳过，用 release 附件里的 dll）
$env:SEPHIRIA_GAME_DIR = "D:\steam\steamapps\common\Sephiria"
dotnet build SephiriaMonitor -c Release

# 2) 复制两个文件（hooks.json 必须和 dll 放一起）
$dst = "$env:SEPHIRIA_GAME_DIR\BepInEx\plugins\SephiriaMonitor"
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item SephiriaMonitor\bin\Release\SephiriaMonitor.dll $dst -Force
Copy-Item SephiriaMonitor\hooks.json $dst -Force

# 3) 启动游戏并读档
```

### 3.4 验证装好了

启动游戏 → **读档进入 MainWorld**（标题界面不加载数据库，桥也不会起）→ 然后：

```powershell
Invoke-RestMethod http://127.0.0.1:47901/health     # 期望 version = 0.20.0
node tools\selfcheck.mjs                            # 期望 23 passed, 0 failed
```

BepInEx 控制台里应能看到 `[Info :SephiriaMonitor] bridge listening on http://127.0.0.1:47901/`。

### 3.5 升级 / 卸载

- **升级**：`git pull` 后重跑 `.\install.ps1 -Build -StopGame`（会覆盖同名 dll，配置与存档不动）。
- **卸载**：删掉 `<游戏>\BepInEx\plugins\SephiriaMonitor\` 整个目录即可，游戏与存档不受影响。

### 3.6 安装失败对照

| 报错 | 原因 | 处理 |
| --- | --- | --- |
| `Game dir not found` | 游戏不在默认路径 | `-GameDir "E:\Steam\steamapps\common\Sephiria"` |
| `No dotnet SDK found` | 只装了 runtime | 装 .NET SDK 8，或 `-Dotnet "C:\Program Files\dotnet\dotnet.exe"` |
| `找不到 Sephiria 的游戏程序集` | `GameDir` 指错（少一层/多一层） | 指向**含 `Sephiria_Data`** 的那层 |
| 复制 dll 失败（文件占用） | 游戏在跑 | 加 `-StopGame` 或手动退出游戏 |
| 装完 `/health` 连不上 | 还停在标题界面 | 读档进 MainWorld |
## 4. MCP 接入

### 4.1 直接走 HTTP（推荐）

端点：`POST http://127.0.0.1:47901/mcp`，Content-Type `application/json`，返回 JSON（不是 SSE）。协议版本 `2025-06-18`。

```bash
curl -s -X POST http://127.0.0.1:47901/mcp \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

### 4.2 只支持 stdio 的客户端

让它启动 `node tools/mcp-stdio.mjs`，由它把 stdio 转发到 `47901/mcp`：

```json
{
  "mcpServers": {
    "sephiria": {
      "command": "node",
      "args": ["D:\\AscensionMod\\_work\\repo\\tools\\mcp-stdio.mjs"]
    }
  }
}
```

DSH 的 `cordis.patch.yml` 同理，`args` 指向 `mcp-stdio.mjs` 即可。环境变量 `SEPHIRIA_BRIDGE` 可改桥地址（默认 `http://127.0.0.1:47901`）。

---

## 5. 十六个工具

| 工具 | 作用 |
| --- | --- |
| `sephiria_health` | 桥版本、场景、会话目录、计数器 |
| `sephiria_brief` | **一次拿全**：health + runs + player + progress + combat 聚合 + combos + statuses（`top` 控制战斗行数） |
| `sephiria_status` | 场景/血量/金钱/单位数；`detail:true` 追加 progress |
| `sephiria_inventory` | **实时**属性（64 项）、背包、石板、刻印；`live:true` |
| `sephiria_world` | 无参=全量单位；`type`=按类型枚举；`type`+`index`(+`depth`)=单对象反射；`map/x/y/r/rooms/path/probe`=地图 |
| `sephiria_events` | 增量事件流（`after` 游标、`kind` 过滤）；damage/death/loot/floor/run/spawn… |
| `sephiria_logs` | 游戏内日志（`type=` 过滤；`after=<seq>` 增量读，只回新行） |
| `sephiria_db` | 数据库 CSV：无参列表，`table/q/limit/offset` 读行 |
| `sephiria_hooks` | mod 挂点目录（`q/area/kind/side/limit`），含真实调用点 |
| `sephiria_save` | `action=slots`（存档槽）/`title`（标题状态）/`enter`（进档，轮询到 MainWorld）/`key`（读或写单个存档键：`key=` `scope=meta\|run` `set=` `type=string\|int\|float\|bool`）/`dump`（`prefix=` 列键） |
| `sephiria_merchant` | **写操作**：`action=merchants` 列出商人+货架；`floors` 列出本局楼层及计划商人类型；`goto` 跳到指定楼层（`floor=` / `to=vendor`）；`tp` 传送到商人旁；`shop` 打开商店面板；`sample` 用真实配货算法抽样（不改游戏状态） |
| `sephiria_travel` | **写操作**：`action=spots` 列出当前层的交互点（NPC/传送门/祭坛/奇迹选择器/床/任务板，含 type/name/pos/distance）；`use` 传送到该点并触发它的交互（与玩家按 F 同一条事件链 `Interactable.Interactive`）；`rooms` 列出程序化楼层的房间实例（边界/中心/传送点）；`jump` 传送到指定房间（`room=` 名字或 `index=`） |
| `sephiria_mods` | **测 mod 主力**：`action=list` 已加载的 BepInEx / AddOns / 工坊包（来源、状态、dll 路径）；`patches` 按 owner 列 Harmony 补丁（`q=` 方法名、`owner=` modId）；`data` 你的 mod 注册了哪些行（id ≥ 900000）；`l10n` 解析 `key=` 或审计 `prefix=`；**`options`** 读所有 mod 设置页，或 `set=<key>&value=<v>&mod=<modId>` **写**（走游戏自己的 option 对象，会触发回调并落盘 `DeviceOptions.xml`） |
| `sephiria_ui` | UI 探针：`action=list` 打开的控件栈、`widgets` 注册的 UI 元素名、`open`/`close` 按名字或类型开/关面板（写操作；面板懒加载，会自动经 `UIManager.GetElement` 解析） |
| `sephiria_player` | **写操作**：`action=god`（`on=1` 只回血、可被索敌；`invuln=1` 真无敌但 AI 会跳过你）、`boss`（Boss 血量/位置/当前动作/目标）、`heal/money/level/stat`、`config`（`npcDamage=0` 不记录 NPC 互殴） |
| `sephiria_screenshot` | 兜底：游戏窗口截图 |

同一个端口还提供 56 条 GET 路由（`/health /state /player /world /inventory /combat /runs /events /objects /object /db /map /saves …`），MCP 工具只是它们的薄封装——想省 token 就直接 GET。

---

## 6. 模组工坊：做、测、发

这套插件同时是一个**模组制作与回归测试台**：

```text
work/<mod>/              本地工作区（不进 git：mod.json / metadata.json / src / dist / tests / workshop.json）
  tests/*.suite.json     JSON 场景套件：真游戏、真存档、真断言
tools/runner/            node run-suite.mjs <suite>  → 起游戏 → 自动进档 → 逐步断言 → 还原
tools/preflight.mjs      node preflight.mjs work/<mod> → 静态 → 编译 → 打包校验 → 全部套件
tools/pack-workshop.mjs  node pack-workshop.mjs work/<mod> → dist/workshop/<modId>/ + zip
tools/new-mod.mjs         node new-mod.mjs <Name> → work/<name>/ 骨架（可编译、可跑套件）
tools/doctor.mjs          node doctor.mjs → 环境自检（游戏 / dotnet / 插件版本 / 桥 / 存档 / 模组通道）
tools/check-package.mjs   node check-package.mjs <zip> → 按工坊真实规则校验产物（preflight 已内置）
```

隔离与安全：跑套件前给 `AddOns\` + `BepInEx\plugins\` 做快照（结束后还原），存档整目录备份（`SLOT*.sav`+`SLOT*TMP.sav`），只在标记文件存在时自动进档（2 小时过期、每进程一次）。

第一个产物 **RunSaveGuard**（`work/runsave-guard`）：每 30 秒把 `SLOTxTMP.sav` 复制到 `Saved Games\Sephiria\runsave-guard\`，保留最近 12 份——游戏只在 TMP 里存当前运行状态，死亡/崩溃会直接删掉整局。实机套件 `PASS 6/6`，`preflight` 全绿；打包产物按工坊形态装进 `WorkshopLocal\\` 后 `workshop-install` 套件 `PASS 7/7`（日志出现 loaded、40 s 内产出备份）。

`/engravings`（0.20.0）是配套的观测点：固定刻印存在 `GridInventory.fixedEngravingsOnServer` 里，不是场景对象，`/objects` 看不到——这类“非组件状态”必须由服务端读点暴露，否则很容易把“看不见”写成“丢了”。

## 7. 版本记录

| 版本 | 变更 |
| --- | --- |
| **0.20.0** | **观测点补齐**：新增 `/engravings`（服务端读 `fixedEngravingsOnServer`，`/objects` 看不到非组件类状态）；版本号与文档同步。配套新增 mod 工坊工具链：`tools/runner`（JSON 套件驱动实机回归）、`tools/preflight.mjs`（一键门禁）、`tools/pack-workshop.mjs`（工坊打包） |
| **0.19.0** | **自动进档改成双钥匙、一次性**：配置 `[title] AutoEnter` 默认 `false`（总开关），且必须存在标记文件 `BepInEx\plugins\SephiriaMonitor\autoenter.on`（内含 2 小时过期时间戳）才会进档；进档后插件自己删除标记，且**每个进程只触发一次**——退回主界面不会再被拽进存档。新增 `/arm?minutes=` `/disarm` |
| **0.18.0** | 测 mod 三件能力补齐：`/savekey` `/savedump`（读写存档键）、`/modoptions?set=`（写 mod 设置）、`/logs?after=`（增量日志）；`/combat.playerTaken` 改为按 `dealt` 统计（另给 `rawTotal`） |
| **0.17.1** | 新增商人工具与写操作：`/merchants` `/floors` `/goto` `/tp` `/shop` `/sample`（传送到商人、开商店、配货抽样），12 个工具 |
| **0.12.0** | 新增「到达任何地方」工具：`/spots` `/use` `/gridrooms` `/jump`（交互点枚举与触发、程序化房间枚举与跳转），13 个工具 |
| **0.10.0** | **MCP 服务器并入插件**（`POST /mcp`，11 工具），删除独立 Node 服务器；`hooks.json` 随插件部署，79 条挂点 |
| 0.9.2 | 工具层精简 23 → 11 个复合工具（brief/world/save/db 合并），README 合并为单份 |
| 0.9.1 | `sephiria_hooks` + `hooks.json`；`content-item` 模板；`smoke.ps1` |
| 0.9.0 | `/combat` 在线聚合、`/runs` 对局边界、会话只留 6 个、events.jsonl 32 MB 封顶 |
| 0.8.0 | `/inventory` 改实时、新增 `/combos` `/statuses` `/inventory_snapshot` |
| `/merchants` `/floors` `/stage` `/goto` `/tp` `/shop` `/sample` | 商人列表 / 楼层列表 / **推进舞台**（`stage`，会自动生成该关楼层）/ 跳楼（`hidden=1` 允许隐藏层）/ 传送（`index`/`name`/`to` 或绝对 `x,y`）/ 开店 / 配货抽样（后四个是写操作或重计算） |
| `/spots` `/use` `/gridrooms` `/jump` | 交互点枚举 / 触发交互（`index=`/`name=`/`type=`，`tp=1` 先传送）/ 程序化楼层房间表 / 传送到房间中心 |
| 0.7.x | 初版：桥、事件流、数据库导出、MCP server（Node） |

---

## 8. 从源码编译

- 目标框架 `netstandard2.1`，`LangVersion 9.0`，需要 .NET SDK 8 构建（运行时不够）。
- 引用游戏的程序集（`Sephiria_Data\Managed`）与 BepInEx、0Harmony、`Newtonsoft.Json`，全部 `Private=false`（不复制到输出）。
- 游戏目录解析顺序：`SEPHIRIA_GAME_DIR` → `-p:GameDir=...` → 常见 Steam 路径；找不到会在构建时直接报错。

```powershell
$env:SEPHIRIA_GAME_DIR = "D:\steam\steamapps\common\Sephiria"
dotnet build SephiriaMonitor -c Release
```

---

## 9. 游戏内设置页（玩家手动调试）

插件会把调试开关注册进游戏自己的 **设置 → 游戏性 → 模组设置 → Sephiria Monitor**（`ModOptions` 是 `internal`，走全反射注册；值存在 `DeviceOptions.xml` 的 `ModOptions:com.surveillance.monitor:<key>`）：

| 行 | 作用 |
|---|---|
| 上帝模式（敌人能锁定你，但伤害为 0） | 保持 `invulnerableCount = 0`（敌人照常索敌、Boss 照常进阶段），并用 Harmony 前缀把 `UnitAvatar.ApplyDefense` 对玩家的结果改成 0 |
| 一击必杀（所有伤害 ×1000） | 写 `ECustomStat.AllDamageBonus = 100000`，关掉即回 0 |

NPC 互殴伤害属于后台噪声控制，不放在玩家可见的设置里（默认不记录，需要时 `POST /config?npcDamage=1`）。

查看当前值与所有 mod 页：`GET /modoptions`，或 MCP `sephiria_mods` 的 `action=options`。

## 10. 排错

| 现象 | 原因 / 处理 |
| --- | --- |
| `47901` 连不上 | 游戏没跑，或没进 MainWorld（标题界面不启桥） |
| `/health` 有响应但 DB 为空 | 还没进过对局；数据库在进入 MainWorld 时才加载导出 |
| `tools/list` 掉连接 | 0.10.0 之前没有 `/mcp`；确认 dll 版本与 `/health` 的 `version` |
| 部署 dll 失败 | 游戏占用文件 → `install.ps1 -StopGame` |
| 属性看着不对 | 用 `sephiria_inventory`（实时）；旧快照在 `/inventory_snapshot` |
| 模板/mod 不加载 | AddOns 与 Workshop 模组只在 **MainWorld** 加载，改完要重新进档 |

---

## 11. 边界

- 只读。写操作只有 `/enter`（调用游戏自己的 `UI_TitleLobby.NetworkHost()` 进档）和 `/arm`（写自动进档标记），都需要显式调用。
- **自动进档默认完全关闭**：`[title] AutoEnter=false`；即使打开，也必须存在 2 小时有效期的标记文件，且每进程只生效一次，正常游玩不会被打断。
- 只监听 `127.0.0.1`，不对外网开放；请勿把它暴露到公网或打包进工坊内容。
- 不含任何绕过、注入或修改游戏逻辑的代码；Harmony 只用于读取（`Postfix` 观测）。

MIT License.
## 10. 一句话让 AI 部署

把下面**这一句**发给任意有终端权限的 AI（Claude Code / Codex / DSH 等），它会自己完成克隆、编译、部署、启动与自检：

> 把 https://github.com/Little-Mostima/Sephiria-Monitor 部署到我的 Sephiria：用 .NET SDK 8 编译 Release 版插件，把 `SephiriaMonitor.dll` 和 `hooks.json` 复制到 `<游戏根目录>\BepInEx\plugins\SephiriaMonitor\`，启动游戏并读档到 MainWorld，然后请求 `http://127.0.0.1:47901/health` 确认版本是 0.19.0、运行 `node tools\selfcheck.mjs` 确认 23 项全过，最后把版本号、自检结果和游戏目录告诉我；遇到问题先报告再动手，不要修改游戏本体文件。

如果 AI 需要更明确的边界，可以再补一句：

> 只允许写 `BepInEx\plugins\SephiriaMonitor\` 目录；不要动存档、不要动 `Sephiria_Data`，也不要改 BepInEx 配置；没有 .NET SDK 就先停下问我。

### 10.1 AI 的执行清单（对应上面那句话）

1. `git clone` 仓库到任意临时目录（或用户指定目录）。
2. 找 SDK：`dotnet --list-sdks` 有 8.x 才继续；否则停下报告。
3. 找游戏：存在 `Sephiria.exe` 且同级有 `Sephiria_Data\Managed\Assembly-CSharp.dll`。
4. `$env:SEPHIRIA_GAME_DIR=<游戏目录>; dotnet build SephiriaMonitor -c Release`。
5. 停游戏进程（若在跑）→ 建 `BepInEx\plugins\SephiriaMonitor` → 复制 `SephiriaMonitor.dll` + `hooks.json`。
6. 启动游戏，等到 `GET /health` 返回 `scene":"MainWorld"`（标题界面不算成功）。
7. `node tools\selfcheck.mjs`，把 `N passed, M failed` 原样报给用户。
8. 汇报：版本号、dll 的 sha256 前 16 位、自检结果、MCP 端点 `http://127.0.0.1:47901/mcp`。

### 10.2 部署完成后 AI 该先调什么

```
sephiria_health                     # 桥是否活着、版本
sephiria_brief                      # 一次拿全：对局/玩家/战斗/连击/状态
sephiria_hooks(q="ApplyDamage")     # 要找挂点时先查这里，别猜签名
```
