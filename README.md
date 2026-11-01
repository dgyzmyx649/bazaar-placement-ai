# Bazaar Placement AI

《The Bazaar》（大巴扎）LookingIN 排布搜索的 AI 加速插件。

LookingIN 的原算法遇到复杂阵容时，要跑满 15 秒才能给出推荐，而且只能覆盖一小部分可能的摆法。本插件用一个排序模型先挑出最有希望的候选，再交给 LookingIN 自己的计算核心做短搜索和独立复核，所以评分口径和原算法完全一致。

## 效果

同一局、同样 15 件物品、8 秒评分点的实测对比：

| 方法 | 耗时 | 评分 |
|---|---|---|
| LookingIN 原算法 | 15.23 秒 | 294.1 |
| Bazaar Placement AI | 4.77 秒（模型已预热；首次启动约 8.4 秒） | 300 |

- 评分是 50 次采样的估计值，不同局面结果会不同。
- 在 24 套独立的真实背包上，得分平均是原算法的 98.8%，最低约 90.2%。完整数据见 `BepInEx/plugins/BazaarPlacementAI/reports/acceptance-report.json`。
- 这是近似搜索，不保证每一局都和长时间的原算法搜索同分。

## 下载

点仓库页面右上角的 **Code → Download ZIP**，或者直接下载 [`release/BazaarPlacementAI-0.1.7.zip`](release/BazaarPlacementAI-0.1.7.zip)。

## 安装

本插件依赖 LookingIN，必须**先装好 LookingIN，再装本插件**。LookingIN 的文件不在本仓库里，请从 LookingIN 官方发布页下载。

### 一行命令安装（推荐）

先装好 LookingIN（见下面第 1 步），**关闭游戏**，然后打开 PowerShell（开始菜单搜索 "PowerShell"），粘贴这一行回车：

```powershell
irm https://raw.githubusercontent.com/dgyzmyx649/bazaar-placement-ai/main/install.ps1 | iex
```

脚本会自动：

1. 从 Steam 找到游戏目录（找不到会让你手动输入）；
2. 检查 LookingIN 是否已安装，并显示它的版本；
3. 下载本插件，再从 LookingIN 官方仓库下载两个 SDK 文件并校验哈希；
4. 有旧版本时先备份到 `BepInEx/BazaarPlacementAI-backups/`，再安装。

不会改动 LookingIN 或游戏本身的任何文件。脚本源码就是仓库里的 [`install.ps1`](install.ps1)，可以先打开看一眼。

游戏不在默认位置，或者想卸载：

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/dgyzmyx649/bazaar-placement-ai/main/install.ps1))) -GameDir "D:\SteamLibrary\steamapps\common\The Bazaar"
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/dgyzmyx649/bazaar-placement-ai/main/install.ps1))) -Uninstall
```

### 手动安装

**第 1 步：安装 LookingIN 插件**

到 LookingIN 官方发布页下载最新版，按它的说明装好，进游戏确认 LookingIN 能正常用，再关闭游戏：
<https://github.com/LookingIN97/LookingIN-Distribution/releases/latest>

已实测验证：LookingIN **0.247.6** 和 **0.248.0**。插件按结构识别 LookingIN 的内部接口，LookingIN 正常更新后通常也能继续用；万一接不上，会在 `BepInEx/LogOutput.log` 里写明原因，并自动退回 LookingIN 原搜索，不影响游戏。

**第 2 步：安装本插件**

1. 下载本仓库（**Code → Download ZIP**），或者只下载 [`release/BazaarPlacementAI-0.1.7.zip`](release/BazaarPlacementAI-0.1.7.zip)。
2. 把压缩包里的 `BepInEx` 文件夹合并到游戏目录。
3. 到 LookingIN 官方仓库的 `sdk/` 目录下载这两个 SDK 文件，放进 `BepInEx/plugins/BazaarPlacementAI/`：
   - [`LookingIN.Compute.Client.dll`](https://github.com/LookingIN97/LookingIN-Distribution/raw/v0.248.0/sdk/LookingIN.Compute.Client.dll)
   - [`LookingIN.Compute.Contracts.dll`](https://github.com/LookingIN97/LookingIN-Distribution/raw/v0.248.0/sdk/LookingIN.Compute.Contracts.dll)

   这两个文件是 LookingIN97 公开发布的 SDK（MIT 许可），本插件用它们和 LookingIN 的计算核心通信。上面链接是经过测试的版本。

**装好之后的目录结构应该是这样：**

```
The Bazaar/
├─ TheBazaar.exe
└─ BepInEx/
   └─ plugins/
      ├─ LookingIN/                          ← 第 1 步，LookingIN 官方插件
      │  ├─ LookingIN.dll
      │  ├─ LookingIN.Compute.Worker.exe
      │  └─ ...
      └─ BazaarPlacementAI/                  ← 第 2 步，本插件
         ├─ BazaarPlacementAI.dll
         ├─ LookingIN.Compute.Client.dll     ← 从 LookingIN 官方 sdk/ 下载
         ├─ LookingIN.Compute.Contracts.dll  ← 从 LookingIN 官方 sdk/ 下载
         ├─ model/
         └─ ...
```

启动游戏，按 **G** 就能用。不需要 Python、GPU、服务器或账号，推理全部在本机 CPU 上运行。

## 快捷键

| 按键 | 作用 |
|---|---|
| **G** | 智能搜索全部背包：复杂阵容自动用 AI，简单阵容（候选不超过 10 件且没有特殊格位）沿用原算法 |
| **Alt + G** | 强制用原算法搜索全部背包（用于对比） |
| **Ctrl + G** | 强制用 AI 模型搜索全部背包（用于对比） |
| 鼠标悬停卡牌按 **中键** | LookingIN 原来的入口 |

- 有 PIN 时自动走原算法。
- 搜索只生成推荐，不会自动换位；推荐、曲线、应用、撤销都沿用 LookingIN 原面板。

## 设置

配置文件在 `BepInEx/config/local.ai.bazaar-placement.cfg`：

- `Search/Milliseconds`：模型辅助搜索的发现预算，默认 3500 毫秒。
- `Search/MaximumEvaluations`：模型侧最多评估的排布数，默认 512。
- `Search/WholeStashKey`：全背包搜索快捷键，默认 G。
- `Search/BackgroundWarmup`：游戏启动后在后台预热模型和计算核心，默认开启。

日志在 `BepInEx/LogOutput.log` 和 `BepInEx/logs/BazaarPlacementAI/`。

## 卸载

关闭游戏，删除 `BepInEx/plugins/BazaarPlacementAI` 文件夹即可。插件不会改写 LookingIN 的任何文件，LookingIN 照常可用。

## 包含什么、不包含什么

- 包含：插件 DLL、模型权重（含一份从游戏数据整理的物品数值表 `model/metadata.json`）、ONNX Runtime 及其许可。
- 不包含：游戏本体、游戏数据库，以及 LookingIN 的任何文件（插件本体、计算核心 Worker、SDK DLL），都需要从 LookingIN 官方渠道安装。

## 源码

插件源码在 `source/`。构建前把上面两个 LookingIN SDK DLL 放进 `source/dependencies/LookingIN/`，再用 `-p:GameDir=<你的游戏目录>` 指定游戏路径。

## 致谢

搜索和评分都建立在 LookingIN 的计算核心之上，感谢 LookingIN 大佬。

## 许可

插件源码和模型权重都使用 [MIT License](LICENSE)。第三方组件保留各自的许可，见 `BepInEx/plugins/BazaarPlacementAI/licenses`。
