# Sephiria Monitor

**一个插件，既是只读监控，也是 MCP 服务器。**

装上它、把游戏跑起来，`http://127.0.0.1:47901` 就是一个本地只读数据源。任何 AI 客户端（Claude Desktop / Code、DSH、自写的 agent）都可以通过 **MCP** 或 **普通 GET** 读这里的游戏状态：当前楼层、角色属性、护符连击、伤害统计、事件流、数据库 CSV、存档槽、mod 挂点目录。

项目目标不是"再做一个内存修改器"，而是**给 mod 作者和 agent 提供一份可信的运行时事实**：数值从游戏对象里现读，不是从缓存快照里翻旧值。

- 游戏：Sephiria 1.1.1（Unity 6000.3.21f1，Mono）
- 依赖：BepInEx 5.4.x (x64)
- 版本：插件 **0.10.0**，协议 **MCP 2025-06-18**

---

## 1. 仓库结构

```
SephiriaMonitor/          # BepInEx 插件（C#，全部源码）
  MonitorPlugin.cs        # 插件入口，版本号
  BridgeServer.cs         # 本地 HTTP 桥（GET 路由 + POST /mcp 入口）
  McpServer.cs            # 内嵌 MCP 服务器（JSON-RPC，11 个工具）
  Live.cs                 # 实时读取（inventory / combos / statuses）
  Combat.cs               # 战斗事件在线聚合
  RunTracker.cs           # 对局边界（run start/end）
  EventLog.cs / ...       # 事件、快照、楼层、数据库导出
  hooks.json              # mod 挂点目录（79 条，随 dll 一起部署）
tools/
  mcp-stdio.mjs           # 可选：stdio → HTTP 代理（给只支持 stdio 的客户端）
  selfcheck.mjs           # 端到端自检（14 项）
install.ps1               # 一键安装/升级
```

`SurveillanceMod/`（原来的 Node MCP 服务器）**已经并入插件**，不再需要单独进程。0.10.0 起：

- 插件启动即监听 `47901`，同时提供 GET 路由和 `POST /mcp`；
- MCP 的 11 个工具在 C# 里实现，内部直接复用同一套路由函数；
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

```powershell
git clone https://github.com/Little-Mostima/Sephiria-Monitor.git
cd Sephiria-Monitor

# 从源码编译并安装（需要 .NET SDK 8）
.\install.ps1 -Build -StopGame

# 或者用仓库里已编译的 dll
.\install.ps1 -StopGame
```

| 参数 | 作用 |
| --- | --- |
| `-Build` | 先 `dotnet build SephiriaMonitor -c Release` |
| `-StopGame` | 替换 dll 前先停掉运行中的游戏（dll 被占用会失败） |
| `-GameDir <path>` | 游戏根目录；缺省依次尝试注册表外的常见 Steam 路径 |
| `-Dotnet <path>` | 指定带 SDK 的 dotnet（运行时-only 的装不了） |

装完启动游戏、**读到 MainWorld**（标题界面不加载数据库），桥就会起来。验证：

```powershell
Invoke-RestMethod http://127.0.0.1:47901/health
node tools\selfcheck.mjs          # 14 项端到端断言
```

---

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

## 5. 十一个工具

| 工具 | 作用 |
| --- | --- |
| `sephiria_health` | 桥版本、场景、会话目录、计数器 |
| `sephiria_brief` | **一次拿全**：health + runs + player + progress + combat 聚合 + combos + statuses（`top` 控制战斗行数） |
| `sephiria_status` | 场景/血量/金钱/单位数；`detail:true` 追加 progress |
| `sephiria_inventory` | **实时**属性（64 项）、背包、石板、刻印；`live:true` |
| `sephiria_world` | 无参=全量单位；`type`=按类型枚举；`type`+`index`(+`depth`)=单对象反射；`map/x/y/r/rooms/path/probe`=地图 |
| `sephiria_events` | 增量事件流（`after` 游标、`kind` 过滤）；damage/death/loot/floor/run/spawn… |
| `sephiria_logs` | 游戏内日志（可按类型过滤） |
| `sephiria_db` | 数据库 CSV：无参列表，`table/q/limit/offset` 读行 |
| `sephiria_hooks` | mod 挂点目录（`q/area/kind/side/limit`），含真实调用点 |
| `sephiria_save` | `action=slots`（存档槽）/`title`（标题状态）/`enter`（进档，轮询到 MainWorld） |
| `sephiria_screenshot` | 兜底：游戏窗口截图 |

同一个端口还提供 34 条只读 GET 路由（`/health /state /player /world /inventory /combat /runs /events /objects /object /db /map /saves …`），MCP 工具只是它们的薄封装——想省 token 就直接 GET。

---

## 6. 版本记录

| 版本 | 变更 |
| --- | --- |
| **0.10.0** | **MCP 服务器并入插件**（`POST /mcp`，11 工具），删除独立 Node 服务器；`hooks.json` 随插件部署，79 条挂点 |
| 0.9.2 | 工具层精简 23 → 11 个复合工具（brief/world/save/db 合并），README 合并为单份 |
| 0.9.1 | `sephiria_hooks` + `hooks.json`；`content-item` 模板；`smoke.ps1` |
| 0.9.0 | `/combat` 在线聚合、`/runs` 对局边界、会话只留 6 个、events.jsonl 32 MB 封顶 |
| 0.8.0 | `/inventory` 改实时、新增 `/combos` `/statuses` `/inventory_snapshot` |
| 0.7.x | 初版：桥、事件流、数据库导出、MCP server（Node） |

---

## 7. 从源码编译

- 目标框架 `netstandard2.1`，`LangVersion 9.0`，需要 .NET SDK 8 构建（运行时不够）。
- 引用游戏的程序集（`Sephiria_Data\Managed`）与 BepInEx、0Harmony、`Newtonsoft.Json`，全部 `Private=false`（不复制到输出）。
- 游戏目录解析顺序：`SEPHIRIA_GAME_DIR` → `-p:GameDir=...` → 常见 Steam 路径；找不到会在构建时直接报错。

```powershell
$env:SEPHIRIA_GAME_DIR = "D:\steam\steamapps\common\Sephiria"
dotnet build SephiriaMonitor -c Release
```

---

## 8. 排错

| 现象 | 原因 / 处理 |
| --- | --- |
| `47901` 连不上 | 游戏没跑，或没进 MainWorld（标题界面不启桥） |
| `/health` 有响应但 DB 为空 | 还没进过对局；数据库在进入 MainWorld 时才加载导出 |
| `tools/list` 掉连接 | 0.10.0 之前没有 `/mcp`；确认 dll 版本与 `/health` 的 `version` |
| 部署 dll 失败 | 游戏占用文件 → `install.ps1 -StopGame` |
| 属性看着不对 | 用 `sephiria_inventory`（实时）；旧快照在 `/inventory_snapshot` |
| 模板/mod 不加载 | AddOns 与 Workshop 模组只在 **MainWorld** 加载，改完要重新进档 |

---

## 9. 边界

- 只读。唯一的写操作是 `/enter`（调用游戏自己的 `UI_TitleLobby.NetworkHost()` 进档），需要你明确要求才用。
- 只监听 `127.0.0.1`，不对外网开放；请勿把它暴露到公网或打包进工坊内容。
- 不含任何绕过、注入或修改游戏逻辑的代码；Harmony 只用于读取（`Postfix` 观测）。

MIT License.
