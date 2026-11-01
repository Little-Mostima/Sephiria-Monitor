# Sephiria Monitor

给 [Sephiria](https://store.steampowered.com/app/2436940/)（Steam AppID `2436940`）做的**只读数据监听套件**：BepInEx 插件在游戏进程里采集状态与事件，零依赖的 MCP 服务器把数据交给 AI agent。

**作为辅助 Mods 开发的 Mods**：除「从标题界面加载一个存档」（`sephiria_save` 的 `enter` / `/enter`）外不改动游戏状态。

```
游戏进程 ── BepInEx 插件 ──HTTP GET 127.0.0.1:47901──> MCP 服务器(stdio) ──> AI / 脚本
                                    └─ 落盘：state.json / events.jsonl / floors/*.json / db/*.csv
```

> 版本：插件 **0.9.2** / MCP **0.9.2**（游戏 1.1.1，Unity 6000.3.21f1，BepInEx 5.4.23 x64）
> 只监听回环地址；MCP 暴露 **11 个复合工具**，桥暴露 **33 条 HTTP 路由**。

## 能监听到什么

| 类别 | 内容 |
|---|---|
| 即时状态 | 场景、楼层、坐标、速度/峰值、本层与累计行走、生命/法力/金币、等级/经验%/属性点、击杀数、单位数、敌对单位数、最近 24 个单位（名字/类型/坐标/hp/阵营） |
| 人物细节 | 64 项自定义属性（攻击/元素/暴击/闪避/谈判/掉落…）、背包格子（id/数量/等级/预制体/旋转）、药水槽、石板与刻印、连击档位与生效状态、buff/debuff/孤立状态 |
| 事件流 | 伤害（攻方/受方实例、是否致命）、死亡（杀手、是否玩家击杀）、经验、金钱、拾取、换层、场景切换、存档、对局开始/结束、值得注意的游戏提示 |
| 战斗聚合 | 命中率、失败原因分布（绝对失败/闪避/格挡）、按伤害来源/元素/受击者聚合、玩家承伤、60 秒爆发曲线 —— 不必自己扫事件 |
| 楼层 | 进层自动转储：整层瓦片 ASCII（墙/地/水/悬崖）+ 房间表（编号/网格/尺寸/接口/是否刷怪/房内单位）+ 楼梯/门/传送门坐标 |
| 数据库 | 游戏 49 张数据表导出为 CSV（物品/武器/技能全表，含禁用条目，UTF-8 BOM） |
| 挂点目录 | 62 条可用于 mod 的方法/事件：签名、`文件:行`、服务端/客户端、用途、已知坑、真实调用点 |
| 存档 | 三个槽位是否存在、玩家名、最后保存时间、死亡次数 |

## 仓库结构

| 路径 | 是什么 |
|---|---|
| `SephiriaMonitor/` | C# 插件（`netstandard2.1` / C# 9）。Harmony 补丁采集事件、导出 49 张数据库 CSV、起本地 HTTP 桥 |
| `SurveillanceMod/` | 零依赖 Node ESM MCP 服务器（stdio）。把桥的端点映射成 11 个工具；`hooks.json` 是 62 条挂点目录 |
| `install.ps1` | 一条命令：编译 → 停游戏 → 部署 dll → 校验哈希 → 拷 MCP 文件 |
| `SurveillanceMod/selfcheck.mjs` | 端到端自检：拉起 MCP、`tools/list`、逐个调工具断言形状（12 项） |

两个子目录原先各有一份 `README.md`，已合并进本文件，子目录不再单独维护文档。

## 安装

前置：游戏本体、**BepInEx 5.4.23 x64**（即 `Sephiria\winhttp.dll` 与 `Sephiria\BepInEx\core\BepInEx.dll` 存在）、Node 18+；只有从源码编译时才需要 .NET 8 SDK。

```powershell
git clone https://github.com/Little-Mostima/Sephiria-Monitor.git
cd Sephiria-Monitor

# 编译 + 部署（自动找 SDK；找不到会明确报错）
.\install.ps1 -Build -StopGame -McpDir "$env:USERPROFILE\sephiria-mcp"
```

| 参数 | 作用 |
|---|---|
| `-Build` | 先 `dotnet build -c Release` |
| `-StopGame` | 部署前 `Stop-Process Sephiria`（dll 被映射锁住时必须） |
| `-GameDir` | 游戏目录，默认 `D:\steam\steamapps\common\Sephiria` |
| `-Dotnet` | 指定 `dotnet.exe`；否则依次试 `SEPHIRIA_DOTNET`、本仓库旁的 `_work\dotnet`、`%USERPROFILE%\.dotnet`、系统 |
| `-McpDir` | 顺便把 `server.mjs` / `selfcheck.mjs` / `hooks.json` 拷到该目录 |

手动安装等价于：把 `SephiriaMonitor\bin\Release\SephiriaMonitor.dll` 放到
`<游戏>\BepInEx\plugins\SephiriaMonitor\SephiriaMonitor.dll`，然后**启动游戏并进入存档**——插件只在 `MainWorld` 场景加载，标题画面不加载。

## MCP 接入

任意 MCP 客户端（Claude Desktop、DSH、自写 stdio 客户端）：

```json
{
  "mcpServers": {
    "sephiria": {
      "command": "node",
      "args": ["D:\\path\\to\\SurveillanceMod\\server.mjs"],
      "env": { "SEPHIRIA_BRIDGE": "http://127.0.0.1:47901" }
    }
  }
}
```

桥在游戏进程起来 15–20 秒后可用；进档后 `/health` 的 `scene` 变成 `MainWorld`。

## 工具（11 个）

v0.9.2 把原先 23 个细粒度工具收敛成 11 个复合工具，减少「选错工具」的成本：

| 工具 | 用途 | 底层 |
|---|---|---|
| `sephiria_brief` | **平常用这个**：健康 + 对局边界 + 玩家 + 进度 + 战斗聚合 + 连击 + 状态，一次给全 | `/health` `/runs` `/player` `/progress` `/combat` `/combos` `/statuses` |
| `sephiria_status` | 场景 / 血量 / 金钱 / 单位数 / 计数器；`detail:true` 追加对局进度 | `/state` `/progress` |
| `sephiria_inventory` | 实时背包：64 项属性、格子、药水槽、石板（0.8.0 起不再读缓存快照） | `/inventory` |
| `sephiria_world` | 单位列表；`type` 列同类对象；`type`+`index` 反射字段；`map` 出碰撞图（仅地牢） | `/world` `/objects` `/object` `/map` `/rooms` `/probe` `/mappath` |
| `sephiria_events` | 结构化事件流，`after` 增量、`kind` 过滤 | `/events` |
| `sephiria_logs` | 进程内 Unity 日志（已滤掉 discord-ipc 噪音） | `/logs` |
| `sephiria_db` | 49 张数据库 CSV：列表、子串过滤、分页 | 会话目录 `db\*.csv` |
| `sephiria_hooks` | 62 条挂点目录：签名、`文件:行`、侧、用途、坑、调用点 | 本地 `hooks.json` |
| `sephiria_save` | `slots` 存档槽 / `title` 标题屏 / `enter` 进档并等世界加载 | `/saves` `/title` `/enter` |
| `sephiria_screenshot` | 兜底截图（数值不够用时才用） | `/screenshot` |
| `sephiria_health` | 桥健康（版本、场景、会话目录、事件计数） | `/health` |

## HTTP 接口（33 条，脚本化时用）

| 分组 | 端点 |
|---|---|
| 状态 | `/health` `/state` `/statefull` `/player` `/world` `/scene` `/run` `/progress` `/floor` |
| 数据 | `/inventory` `/inventory_snapshot` `/combos` `/statuses` `/objects` `/object` `/events` `/damage` `/combat` `/runs` `/logs` `/session` `/snapshot` |
| 地图 | `/map` `/mappath` `/path` `/probe` `/rooms` `/mapscan` |
| 存档 | `/saves` `/title` `/enter` `/enterstatus` |
| 其他 | `/screenshot` |

```powershell
Invoke-RestMethod http://127.0.0.1:47901/health
Invoke-RestMethod "http://127.0.0.1:47901/combat?top=12"   # 本局聚合：命中率/失败原因/按来源·元素·受击者/60s 曲线
Invoke-RestMethod http://127.0.0.1:47901/runs              # 对局边界：seed/stages/raceId/timeInWorld
```

## 版本记录

| 版本 | 变更 |
|---|---|
| **0.9.2** | MCP 工具 23 → **11** 个复合工具；`selfcheck.mjs` 重写为 12 项；`install.ps1` 支持 `-McpDir` |
| 0.9.1 | `sephiria_hooks` + `hooks.json`（62 条挂点，含真实调用点） |
| 0.9.0 | `/combat` 在线聚合、`/runs` 对局边界（`kind="run"` 事件）、会话只留 6 个、`events.jsonl` 32 MB 封顶 |
| 0.8.0 | `/inventory` 改为实时计算、新增 `/combos` `/statuses` |
| 0.7.x | 初版 |

## 从源码编译

```powershell
$env:SEPHIRIA_GAME_DIR = "D:\steam\steamapps\common\Sephiria"
dotnet build .\SephiriaMonitor -c Release
```

csproj 硬约束（与游戏程序集对齐）：`netstandard2.1`、`LangVersion=9.0`、`GenerateAssemblyInfo=false`、`DebugType=none`、`Deterministic=true`、`Nullable=disable`、`ImplicitUsings=disable`；引用游戏 `Managed` 下的 `Assembly-CSharp`、`UnityEngine(+CoreModule/IMGUIModule)`、`Unity.InputSystem`、`Mirror`、`BepInEx\core\0Harmony.dll`，全部 `<Private>false</Private>`。

## 自检与排错

```powershell
node .\SurveillanceMod\selfcheck.mjs        # 12 项全 PASS 才算出货；需要游戏进 MainWorld
```

| 现象 | 原因 / 处理 |
|---|---|
| 桥拒绝连接 | 游戏没进主线世界，或插件没部署；先看 `/health`，再看 `%USERPROFILE%\AppData\LocalLow\TEAMHORAY\Sephiria\Player.log` |
| 部署后行为没变 | dll 被映射锁住，`Copy-Item` 静默失败；先 `Stop-Process Sephiria`，部署后比对 `Get-FileHash` |
| `/status` 404 | 该路径不存在，健康检查用 `/health` |
| `/objects` 数量比预期少 | 按**精确类名**匹配，子类不计入（`Unit_Soldier` 与 `UnitAvatar` 分开） |
| 地图是空的 | `/map` 只对地牢楼层有效，城镇没有 floor grid |
| 数值过期 | 用 `/inventory`（0.8.0 起实时）；旧口径在 `/inventory_snapshot` |

## 边界与安全

- 只监听回环地址、无鉴权——本机任何进程都能读，不要在有不可信进程的机器上长期开着。
- 除 `/enter` 外不改游戏状态；`/enter` 只是调用游戏自己的 `UI_TitleLobby.NetworkHost()`。
- **不要把这个插件发到 Steam 创意工坊**：官方 ModGuard 审核会反编译 DLL，本地 HTTP 端点会被判风险；工坊只适合纯内容/本地化 mod，插件走 BepInEx 通道分发。

## 许可

私有仓库，仅供自用与授权测试。Sephiria 及其资产版权归 TEAMHORAY 所有。
