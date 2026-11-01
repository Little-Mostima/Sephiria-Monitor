# Sephiria Monitor

给 [Sephiria](https://store.steampowered.com/app/2436940/)（Steam AppID `2436940`）做的**只读数据监听套件**： BepInEx 插件在游戏进程里采集状态与事件，零依赖的 MCP 服务器把数据交给 AI agent。

**作为辅助Mods开发的Mods**：可以使你进行的操作是「从标题界面加载一个存档」（`/enter`，可关）。

```
游戏进程 ── BepInEx 插件 ──HTTP GET 127.0.0.1:47901──> MCP 服务器(stdio) ──> AI / 脚本
                                    └─ 落盘：state.json / events.jsonl / floors/*.json / db/*.csv
```

## 能监听到什么

| 类别 | 内容 |
|---|---|
| 即时状态 | 场景、楼层、坐标、速度/峰值、本层与累计行走、生命/法力/金币、等级/经验%/属性点、击杀数、单位数、敌对单位数、最近 24 个单位（名字/类型/坐标/hp/阵营） |
| 事件流 | 伤害（攻方/受方实例、是否致命）、死亡（杀手、是否玩家击杀）、经验、金钱、拾取、闪避、换层、场景切换、存档、值得注意的游戏提示（获得/购买/升级/神器…） |
| 楼层 | 进层自动转储：整层瓦片 ASCII（墙/地/水/悬崖）+ 房间表（编号/网格/尺寸/接口/是否刷怪/房内单位）+ 楼梯/门/传送门坐标 |
| 数据库 | 游戏 49 张数据表导出为 CSV（物品/武器/技能全表，含禁用条目，UTF-8 BOM） |
| 存档 | 三个槽位是否存在、玩家名、最后保存时间、死亡次数 |

## 目录结构

```
SephiriaMonitor/     BepInEx 插件（C# / netstandard2.1，19 个 .cs）
  ├─ MonitorPlugin.cs      入口，BepInPlugin(com.surveillance.sephiriamonitor)
  ├─ BridgeServer.cs       本地 HTTP 桥（29 条只读路由）
  ├─ Events.cs             事件环形缓冲 + events.jsonl
  ├─ PlayerState.cs        1 Hz 状态快照
  ├─ FloorDump.cs          楼层/房间/瓦片转储、ASCII 地图、BFS 寻路
  ├─ DataExport.cs         游戏数据库 → CSV
  ├─ MonitorPatches.cs     Harmony 补丁（伤害/经验/金钱/拾取/单位注册表）
  ├─ TitleControl.cs       标题界面进存档
  └─ README.md             插件自身的技术说明
SurveillanceMod/     MCP 服务器（Node ≥ 18，ESM，零依赖，18 个工具）
  ├─ server.mjs            stdio JSON-RPC，转发到 127.0.0.1:47901
  ├─ selfcheck.mjs         自检 10 项，输出 PASS/FAIL
  └─ README.md             MCP 侧说明与工具清单
```

## 安装

### 前置条件

- Windows，Sephiria 已安装（Steam AppID `2436940`）
- **BepInEx 5.4.x x64**（本仓库不附带，请自行装到游戏根目录）
- MCP 侧需要 **Node.js ≥ 18**（只用标准库，无需 `npm install`）

### 方式 A：用 Releases 的预编译包

1. 下载 `SephiriaMonitor-0.7.2.zip`，解压到**游戏根目录**（Steam → 右键游戏 → 管理 → 浏览本地文件），得到
   `BepInEx\plugins\SephiriaMonitor\SephiriaMonitor.dll`。
2. 下载 `SurveillanceMod-0.7.2.zip` 解压到任意目录（如 `D:\tools\SurveillanceMod\`）。
3. 启动游戏（标题界面就绪后会自动进入存档），验证桥：
   ```powershell
   (Invoke-WebRequest -UseBasicParsing http://127.0.0.1:47901/health).Content
   ```
   看到 `"mod":"SephiriaMonitor","version":"0.7.2"` 即成功。

### 方式 B：从源码编译

```powershell
# 只需要 .NET SDK 8。游戏目录按顺序解析：SEPHIRIA_GAME_DIR 环境变量 → 常见 Steam 位置自动探测
$env:SEPHIRIA_GAME_DIR = "D:\steam\steamapps\common\Sephiria"   # 自动探测不到时才需要
dotnet build .\SephiriaMonitor -c Release
Copy-Item .\SephiriaMonitor\bin\Release\SephiriaMonitor.dll `
  "$env:SEPHIRIA_GAME_DIR\BepInEx\plugins\SephiriaMonitor\" -Force
```

Steam 库不在 C/D 盘默认位置时，构建会直接报 `找不到 Sephiria 的游戏程序集...请设置环境变量 SEPHIRIA_GAME_DIR`，按提示设一下即可，不会给出难懂的 `CS0246`。

编译时游戏**不能**在运行（dll 被映射），先 `Stop-Process -Name Sephiria -Force`。

### 注册 MCP 服务器

任意支持 MCP 的客户端都行，本质是「用 node 起一个 stdio 进程」：

```json
{
  "mcpServers": {
    "sephiria": {
      "command": "node",
      "args": ["D:/tools/SurveillanceMod/server.mjs"],
      "env": { "SEPHIRIA_BRIDGE": "http://127.0.0.1:47901" }
    }
  }
}
```

Claude Desktop 放进 `claude_desktop_config.json`；改完**重启客户端**才会加载。验证服务器本身（不需要游戏）：

```powershell
node .\SurveillanceMod\selfcheck.mjs     # 游戏在跑时期望 10/10；没开游戏只有 4/10（桥相关 6 项必然 FAIL）
```

## 给 AI 的一句话部署指令

把下面这段直接发给你的 AI（Claude Code / Cursor / DSH 等），它就能自己装完：

> 克隆这个仓库，把 `SephiriaMonitor/` 用 `dotnet build -c Release` 编译出的 `SephiriaMonitor.dll` 复制到我的 Sephiria 安装目录（Steam AppID 2436940，已装 BepInEx 5.4 x64）的 `BepInEx/plugins/SephiriaMonitor/` 下，再把 `SurveillanceMod/server.mjs` 以 stdio 方式注册成本机 MCP 服务器（命令 `node`，参数是该文件的绝对路径，环境变量 `SEPHIRIA_BRIDGE=http://127.0.0.1:47901`），然后启动游戏、用 `curl http://127.0.0.1:47901/health` 确认返回 `"version":"0.7.2"`，并跑 `node SurveillanceMod/selfcheck.mjs` 确认 10/10 通过。

英文版：

> Clone this repo, build `SephiriaMonitor/` with `dotnet build -c Release`, copy `SephiriaMonitor.dll` into my Sephiria install (Steam appid 2436940, BepInEx 5.4 x64 already installed) under `BepInEx/plugins/SephiriaMonitor/`, register `SurveillanceMod/server.mjs` as a stdio MCP server (`node`, absolute path, `SEPHIRIA_BRIDGE=http://127.0.0.1:47901`), then launch the game and verify `curl http://127.0.0.1:47901/health` returns `"version":"0.7.2"` and `node SurveillanceMod/selfcheck.mjs` passes 10/10.

## 配置

首次启动生成 `BepInEx\config\com.surveillance.sephiriamonitor.cfg`：

| 键 | 默认 | 说明 |
|---|---|---|
| `bridge.Port` | `47901` | 本地桥端口（只监听 127.0.0.1） |
| `title.EnableTitleControl` | `true` | 允许标题界面轮询与 `/enter` |
| `title.AutoEnter` | `true` | 标题界面就绪后自动加载存档 |
| `title.AutoEnterSlot` | `1` | 自动加载的槽位 |
| `compat.TitleHarmonyHooks` | `true` | 标题界面 Harmony 补丁（与其他 mod 冲突时可关） |
| `compat.GameEventHooks` | `true` | 订阅游戏存档/楼层/单位事件 |
| `data.Snapshots` | `true` | 存档/换层事件时抓快照 |
| `data.DatabaseExport` | `true` | 导出数据库 CSV |
| `data.ProbeEvents` | `false` | 记录「自己打自己」的命中测试条目（默认只计数） |

## 桥路由速查（29 条，除 `/enter` 外全部只读）

`/` `/health` `/run` `/player` `/world` `/state` `/statefull` `/scene` `/objects` `/object` `/logs` `/events` `/damage` `/path` `/floor` `/map` `/rooms` `/mappath` `/mapscan` `/probe` `/inventory` `/progress` `/screenshot` `/snapshot` `/session` `/title` `/saves` `/enter` `/enterstatus`

MCP 工具 18 个：`sephiria_health` `sephiria_status` `sephiria_player` `sephiria_world` `sephiria_logs` `sephiria_events` `sephiria_damage` `sephiria_snapshot` `sephiria_inventory` `sephiria_progress` `sephiria_objects` `sephiria_object` `sephiria_databases` `sephiria_title` `sephiria_saves` `sephiria_enter` `sephiria_map` `sephiria_screenshot`

## 数据落盘

`BepInEx\plugins\SephiriaMonitor\out\session-<时间戳>\`：

- `state.json`（1 Hz）、`state_full.json`（白名单 90 键）
- `events.jsonl`（心跳/生成/消失不落盘，其余结构化事件）
- `logs.log`（进程内 Unity 日志环）
- `floors\floor-NNNN-<层名>.json`（每层一份）
- `db\*.csv`（49 张，UTF-8 带 BOM，Excel 可直接打开）

## 常见问题

| 现象 | 处理 |
|---|---|
| `/health` 连不上 | 游戏没启动 / BepInEx 没装 / 插件没加载（看 `BepInEx\LogOutput.log` 里 `SephiriaMonitor` 行） |
| 覆盖 dll 报「无法对文件执行操作」 | 游戏在运行，先 `Stop-Process -Name Sephiria -Force` |
| 端口被占 | 改 `bridge.Port`，同时改 MCP 的 `SEPHIRIA_BRIDGE` |
| `selfcheck` 报 bridge-down / 只有 4/10 | 游戏没开或还没进存档；游戏运行时才是 10/10。其中 `bridge-down hint` 是**故意**在没有桥时验证提示文案 |

## 许可

MIT，见 [LICENSE](LICENSE)。与游戏开发商无关联，仅用于单机数据读取与内容开发辅助。
