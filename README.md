# Raft 本地分屏双人 Mod (Local Split-Screen Co-op)

为生存游戏 **Raft** 增加**本地分屏双人**玩法的 mod。P1 用键鼠（左半屏），P2 用手柄（右半屏），在同一台电脑上一起玩。

> A local split-screen co-op mod for **Raft**. Player 1 plays with keyboard/mouse on the left half of the screen; Player 2 joins with a gamepad on the right half — all on one machine.

## 特性

- **本地分屏**：左右分屏（也支持双显示器模式），各自独立相机 / HUD / 输入。
- **P2 = 手柄玩家**：P2 是 `Network_Player` 克隆（`isLocalPlayer=false`），与 P1 共享单例系统，但拥有独立的背包、手持栏、第一/第三人称相机与交互。
- **完整交互覆盖**：采集、建造、制造、箱子/存储、床与座椅、雪橇车、滑索、钢琴、望远镜、搬运动物、钓鱼、交易站等均已适配 P2。
- **交易站**：P2 使用原版手柄面板在自己的半屏交易，买卖走 P2 背包，与 P1 共享声誉等级。
- **存档**：P2 背包/装备随世界持久化。

## 目录结构

| 目录 | 说明 |
|------|------|
| `LocalCoop/` | RaftModLoader (RML) 版本源码（当前活跃） |
| `SplitScreen/` | UnityModManager (UMM) 版本源码（镜像，入口不同） |
| `docs/` | 设计与排查文档 |
| `tools/` | 辅助脚本 |

> 说明：反编译的 Raft 原版源码（`SourceCode/`）仅在开发时作本地只读参考，**不属于本 mod、也未包含在本仓库中**，其版权归 Redbeet Interactive 所有。

## 构建与安装

本 mod 支持两种加载器，源码树互为镜像（命名空间均为 `SplitScreen`）：

- **RaftModLoader (RML)** — `LocalCoop/`，打包为 `.rmod`（源码包，游戏加载时现编译）。
- **UnityModManager (UMM)** — `SplitScreen/`，编译为 `SplitScreen.dll`。

编译校验（仅验 C# 编译）：

```bash
dotnet build -c Release
```

安装后需**重启 Raft** 才生效（mod 在游戏启动时加载）。游戏内按 **F9** 生成 P2。

## 兼容性说明

- 建议为 Raft **关闭 Steam Input**（虚拟手柄会占用旧输入后端，导致手柄无法正确分离给 P2）。
- 部分第三方 mod 会与分屏重初始化冲突，若遇崩溃可先排查 mod 冲突。

## 许可证

本 mod 代码以 [MIT](LICENSE) 许可证发布。Raft 及其原版资源、代码归 Redbeet Interactive 所有，本项目与其无隶属关系。
