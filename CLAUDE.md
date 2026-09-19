# RaftMod — Project Instructions

Raft 本地分屏双人 mod。P1=键鼠(左半屏)，P2=手柄(右半屏)。P2 是 `Network_Player` 克隆(`isLocalPlayer=false`)，与 P1 共享单例系统(PlayerInventory/相机/输入/玩家模型)。反编译的 vanilla 源码在 `SourceCode/`，mod 在 `SplitScreen/`。

## 回复语言

**始终用中文回复。** 代码、命令、commit message、代码注释按项目既有约定(注释为中文)。

## Build / Deploy / Test 流程

每次改动后：
```bash
rtk dotnet build -c Release    # 在 SplitScreen/ 目录
# 拷贝 DLL 到游戏 mods 目录：
cp bin/Release/SplitScreen.dll "C:/Program Files (x86)/Steam/steamapps/common/Raft/mods/SplitScreen/SplitScreen.dll"
```
- mod 在游戏**启动时**加载 DLL → 改动后用户需**重启 Raft** 才生效。
- 诊断/日志读 `C:/Users/Nero/AppData/LocalLow/Redbeet Interactive/Raft/Player.log`。
- 所有 shell 命令前缀 `rtk`(含 `&&` 链中的每条)。
- 每个改动单独 commit，`feat/fix/refactor(P2): ...`，注释和正文用中文。
- 优先复用 vanilla 函数(直接调用/复刻)，而非重写。
- `SplitScreen.csproj` 用**显式 `<Compile Include>` 列表** → 新增 .cs 文件必须手动加进去。

## 工具/环境避坑(本机特定 — 务必遵守)

> 2026-06-22 一次长会话踩坑总结。本机若干工具不可靠,直接用会反复"改了不生效/输出假象"。按下列走可靠通道。

### 不可靠工具(避免使用)
- **Edit 工具**:会"返回成功但不落盘",改动丢失。→ 用 PowerShell `[IO.File]::WriteAllText`,改完**同进程 `ReadAllText` 重读 `Contains` 验证**,不信 Edit 的成功返回。
- **Bash 工具**:每次调用过安全分类器,分类器(claude-opus-4-8)间歇宕机 → Bash 失败/输出污染(假 commit、重复行、混入 retry 文本)。→ 一律改用 PowerShell。
- **裸 `git`**:本机 `C:\WINDOWS\system32\git` 是 0 字节空文件挡在真 git.exe 前,PowerShell 管道报 "Cannot run a document in the middle of a pipeline"。→ 用 `git.exe`(删掉那个空文件可恢复裸 git)。
- **rtk 包装**:本会话输出污染严重(`ok`/`ek` 前缀错乱、重复行)。→ 直接用原命令(`dotnet build` / `git.exe`)。
- **PowerShell `@`+单引号 here-string**:工具传参时结束符不顶格被误解析,Replace 用错内容。→ 用单引号普通字符串 + 显式换行,或数组 `-join`。

### Read 工具
- **有缓存**,可能返回旧内容(系统会警告 do not assume latest)。验证改动用 `[IO.File]::ReadAllText().Contains()`,或写小报告文件再 Read。
- 中转报告文件名**带时间戳/递增**,避免命中旧缓存。
- **别给大文件大 limit**:本仓库 .cs 注释密集,Read 把注释显示成空行,大 limit 一次烧上万行上下文。用小 offset/limit 或 PowerShell 输出指定行。

### Build / 部署物理约束
- **build/部署前必须先退出 Raft**:游戏运行锁死 `bin/Release/SplitScreen.dll` 与 mods 目录 DLL,新文件写不进 → 哈希永不变(stale DLL 假象)。脚本先 `Get-Process *Raft*` 检测,在跑就提示退出。
- 判断是否真重编译看新旧哈希是否不同,别只看 BuildOK;部署后比对 src/dst `Get-FileHash`。

### 与 codex 并行的铁律(用户常开 codex 一起改本项目)
- **一个文件同一时刻只能一个 writer**。两 agent 改同文件 → 互相覆盖/还原(本会话最大元凶:改了被 codex 还原 → 哈希不变 → 误判没改对 → 反复折腾)。
- 开工前 `Get-Process node` / `git status` 看 codex 是否在动同批文件;约定文件/模块分工,或用 `git worktree` 物理隔离。
- 改完立即 `git.exe` commit,缩小被还原窗口。

### 用 UnityExplorer(UE)辅助开发(能用就用)
> 本项目改一次要"编译→打包.rmod→退Raft→部署→重启→游戏内复现→读Player.log",诊断周期极慢。UE(已装,见 memory `reference_unityexplorer_setup`)能实时查运行时状态,大幅缩短定位循环。

- **运行时状态/结构疑问**(某组件哪个实例、字段运行时值、GameObject 层级与 active、哪个单例指谁)→ **先给用户 UE C# Console REPL 片段或 Object Search+Inspector 步骤**让其现场查、回报值/截图,而不是先加日志重编译。(Claude 看不到画面,须用户操作 UE 回报。)
- UE **不擅长**的仍走日志:**逐帧时序/连续量**(如相机抖动的帧间 delta、更新顺序)——值变太快肉眼读不了。判据:静态快照用 UE,时间序列用日志。
- 修复后优先**用 UE 验证运行时确已变**,再走正式 build。
- REPL:打印用 `UnityExplorer.ExplorerCore.Log(...)`;枚举全部实例 `Resources.FindObjectsOfTypeAll<T>()`;层级路径手动上溯 parent(`GetHierarchyPath` 在 REPL 不在作用域)。

### 可靠改文件标准动作(本会话验证有效)
```powershell
$enc=New-Object System.Text.UTF8Encoding($true)
$t=[IO.File]::ReadAllText($path)
$t=$t.Replace("旧串","新串")   # 锚点用单行唯一片段,不用 here-string
[IO.File]::WriteAllText($path,$t,$enc)
# 同进程重读验证: ([IO.File]::ReadAllText($path)).Contains("新串")
```

## P2 架构要点

- **`P2FrameContext`(让 P2 跑 vanilla 逻辑的统一入口/约定)**：组合 `P2OriginalScope`(强制本地+相机+输入) + 可选 `P2InventoryScope`(背包路由)。
  - 物品进出 P2 背包(采集/扣减/放置/返还) → `P2FrameContext.Tool(routeInventory: true)`；纯门控/瞄准 → `P2FrameContext.Tool()`。也有 `.Build()/.Interaction()`。
  - **新功能默认用它**，不要再手写 `Scope = P2OriginalScope.Tool(), Inv = new P2InventoryScope()` 组合。
- `P2OriginalScope`(Tool/Build/Menu/Interaction/FillWater 等模式)：P2FrameContext 的底层 —— 临时强制 P2 `isLocalPlayer=true` + `PlayerContext=P2` + `P2Mode`(驱动 AimRay 相机/输入映射)，让门控本地玩家的 vanilla 逻辑以 P2 身份跑。
- `P2InventoryScope`：换入 P2 背包(`P2InventoryStore`)+ 拾取路由。两者底层仍可单独用(只门控 或 已在 scope 内只换背包)。
- 设备交互的额外特化(选中槽注入 / 设备 localPlayer override / IsBusy save-restore)在 `InteractionRouter.RunDeviceRayAsP2`，设备专属、未并入通用 context。
- P2 手持栏独立：`_p2Hotbar`(非共享选中槽)。手持模型由 `EquipP2` 装备，每次先清空 `allConnections` 再 `StartUsing` 激活当前(杜绝克隆体预挂模型残留)。
- **克隆 vanilla UI/库存必走 `Main.SanitizeClone(Inventory)`**：`Object.Instantiate` 活实例会让 `allSlots` 翻倍(序列化复制+克隆 Awake 再 AddRange)。新增任何"克隆 vanilla Inventory 给 P2 显示"的系统,Instantiate 后**立即调用 `SanitizeClone`**(背包/箱子已走)。注意它只治 A 类翻倍;B 类(克隆 Awake 主动碰全局:`recipe.Learned` 清零 / `FindObjectOfType<Hotbar>` 抓 P1 / 抢单例 `Value`)须自己在 Instantiate 前后 快照→还原 守护。
- 详细背景见 memory：`~/.claude/projects/C--Users-Nero-Desktop-RaftMod/memory/`(MEMORY.md 为索引)。

<!-- CODEGRAPH_START -->
## CodeGraph

This project has a CodeGraph MCP server (`codegraph_*` tools) configured. CodeGraph is a tree-sitter-parsed knowledge graph of every symbol, edge, and file. Reads are sub-millisecond and return structural information grep cannot.

### When to prefer codegraph over native search

Use codegraph for **structural** questions — what calls what, what would break, where is X defined, what is X's signature. Use native grep/read only for **literal text** queries (string contents, comments, log messages) or after you already have a specific file open.

| Question | Tool |
|---|---|
| "Where is X defined?" / "Find symbol named X" | `codegraph_search` |
| "What calls function Y?" | `codegraph_callers` |
| "What does Y call?" | `codegraph_callees` |
| "How does X reach/become Y? / trace the flow from X to Y" | `codegraph_trace` (one call = the whole path, incl. callback/React/JSX dynamic hops) |
| "What would break if I changed Z?" | `codegraph_impact` |
| "Show me Y's signature / source / docstring" | `codegraph_node` |
| "Give me focused context for a task/area" | `codegraph_context` |
| "See several related symbols' source at once" | `codegraph_explore` |
| "What files exist under path/" | `codegraph_files` |
| "Is the index healthy?" | `codegraph_status` |

### Rules of thumb

- **Answer directly — don't delegate exploration.** For "how does X work" / architecture questions, answer with 2-3 codegraph calls: `codegraph_context` first, then ONE `codegraph_explore` for the source of the symbols it surfaces. For a specific **flow** ("how does X reach Y") start with `codegraph_trace` from→to — one call returns the whole path with dynamic hops bridged — then ONE `codegraph_explore` for the bodies; don't rebuild the path with `codegraph_search` + `codegraph_callers`. Codegraph IS the pre-built index, so spawning a separate file-reading sub-task/agent — or running a grep + read loop — repeats work codegraph already did and costs more for the same answer.
- **Trust codegraph results.** They come from a full AST parse. Do NOT re-verify them with grep — that's slower, less accurate, and wastes context.
- **Don't grep first** when looking up a symbol by name. `codegraph_search` is faster and returns kind + location + signature in one call.
- **Don't chain `codegraph_search` + `codegraph_node`** when you just want context — `codegraph_context` is one call.
- **Don't loop `codegraph_node` over many symbols** — one `codegraph_explore` call returns several symbols' source grouped in a single capped call, while each separate node/Read call re-reads the whole context and costs far more.
- **Index lag — check the staleness banner, don't guess a wait.** When a codegraph response starts with "⚠️ Some files referenced below were edited since the last index sync…", the listed files are pending re-index — Read those specific files for accurate content. Files NOT in that banner are fresh and codegraph is authoritative for them. `codegraph_status` also lists pending files under "Pending sync".

### If `.codegraph/` doesn't exist

The MCP server returns "not initialized." Ask the user: *"I notice this project doesn't have CodeGraph initialized. Want me to run `codegraph init -i` to build the index?"*
<!-- CODEGRAPH_END -->
