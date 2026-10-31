Bazaar Placement AI 0.1.6 预览版

适配 LookingIN 0.247.6 / 计算核心 5.5.7，Windows x64。

使用：沿用 LookingIN 原面板；G 搜索全部背包；中键沿用原来的候选选择。
对比快捷键：Alt+G 使用原算法全背包搜索；Ctrl+G 使用模型辅助全背包搜索。两者都只生成推荐，不自动换位。
启动后默认在后台预热模型和计算核心，同一次游戏内反复复用，空闲不做搜索，退出游戏一起释放。Search.BackgroundWarmup=false 可关闭预热。若搜索时尚未预热完成，只等待同一个初始化任务，不重复创建进程。
无 PIN 时自动分流：候选不超过 10 件且没有特殊格位时沿用快速原搜索；更复杂的情况使用原核心真实评分训练的排序模型、全局候选和局部细化。PIN 搜索继续走原方法。

支持最多 20 件库存候选，保留节奏格、冰格、高温格、留空格、成长和附魔的原核心复核。不增加独立 AI 面板。
默认模型辅助搜索的发现预算为 3.5 秒，模型侧最多 512 个排布；属性还原和独立复核另计。小规模原搜索保留 LookingIN 的原设置。

测试：保存的原对局达到 192 分；独立真实背包 24 项对照的平均得分为原方法的 98.8%，最低约 90.2%。属于近似搜索，不保证每局与长时间原搜索等分。完整报告见 reports/acceptance-report.json。

安装：关闭游戏后将 BepInEx 文件夹合并到游戏目录。已有版本请先备份。训练和运行都不需要把个人历史上传到第三方服务；游戏内推理使用本机 CPU。
本包不包含游戏本体、数据库或 LookingIN 的专有计算核心，需要另外安装匹配版本。

公开发布前请查看 MODEL-TERMS.txt 并确定模型许可；本包为本机验证的预览版本。

需要另外安装：LookingIN 0.247.6（https://github.com/LookingIN97/LookingIN-Distribution/releases/tag/v0.247.6），
并从 LookingIN 官方仓库 sdk/ 目录下载 LookingIN.Compute.Client.dll 和 LookingIN.Compute.Contracts.dll 放进本文件夹。详见仓库 README.md。
