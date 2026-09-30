---
name: archivefixer-parallel-agents
description: 在 ArchiveFixer 上用多个代理并行干活的标准流程——git worktree 分工、每个代理不许碰哪些文件、报告格式、合并顺序、冲突热点、合并后由单一集成者跑全量验收再写文档。任务能切成互不重叠的几块、要开多个子代理并行、或要把并行分支合回 main 时用它。触发词：并行、worktree、子代理、分支、合并、merge、冲突、集成、收尾、主 checkout。
---

# 多代理并行：分工 → 合并 → 集成验收

## 1. 开工（每个代理一份独立 worktree）

```powershell
cd E:\DeepSeekProjects\ArchiveFixer
git worktree add E:\DeepSeekProjects\_tmp\ArchiveFixer\wt-<名字> -b feat/<名字> HEAD
```

- worktree 一律放 `E:\DeepSeekProjects\_tmp\ArchiveFixer\wt-*`（2.1 约定）；分支名 `feat/<主题>`。
- 主 checkout 留给**协调者**：只做合并、验收、写文档，⛔ 不在主 checkout 上改业务代码（否则和并行分支打架）。
- 每个代理在自己的 worktree 里构建/测试 —— 不同目录不抢锁；**同一个 checkout 里永远只跑一个构建/测试**。

## 2. 分工纪律（写进每个代理的 prompt）

- ⛔ **每个代理不许改**：`AGENTS.md`、`修改日志.md`、`docs\需求变更.md`、`docs\真机事故复盘.md` —— 这四份由**协调者**在合并后统一写，避免五个分支各写一版互相冲突。
- 代理可以改自己那块的代码 + 自己那份契约文档 + 自己的用例；**改公共契约要说明**。
- 报告格式固定（≤20 行）：改了哪些文件 / 每件事一句话 / 新增用例名与条数 / **红检现场** / 构建+format+全量的**真实数字** / 分支与 commit 短哈希 / **仍没做、没验的如实列出**。
- 要求代理**每条修复都做红检**（见 `archivefixer-verify`），没有红检现场的修复按"未验证"处理。

## 2.1 并发上限与"防猝死"（2026-09-30 实测教训）

四个代理同开、每个一口气改 20~80 个文件的后果：**四个在差不多同一时刻被运行时掐断**（不是机器死机、不是程序在跑，是代理自身的回合被中止），死因拿不到日志，只能按最可能的原因防：

- **一次最多放 2 个**代理（机器与运行时的余量都要留）；剩下的排队，前两个报完再放后两个。
- **每个代理必须"每绿一步就 `git add -A && git commit`"**（`WIP:` 前缀也行）—— 代理猝死时**未提交的现场就是真丢**；有提交就能原样接手。
- **任务切小**：一个代理别指望干完"四件事 + 全量测试"，宁可分成两轮接手；上下文炸掉 = 白干一轮。
- **workers 只跑定向测试，全量留给集成者跑一次**（4 份全量并发跑既慢又是把机器压满的元凶）。
- 接手协议：新代理先 `git log --oneline -3` + `git show --stat HEAD` 看前任落到哪，⛔ 不重做已完成的部分；报告里必须写"前任声称做过、我核实后成立 / 不成立"。
- 撞见残留的 `dotnet.exe` 时先看命令行：`/nodemode:1 /nodeReuse:true` = MSBuild 常驻节点，**正常，别杀**。

## 3. 合并（一次一个，合完立刻验）

- 冲突热点（几乎必碰）：`ViewModels\ExtractionCoordinator.cs`、`Extraction\RecursiveExtractor.cs`、`Extraction\ResultFinalizer.cs`、`Models\StatusText.cs`、`ViewModels\MainViewModel.cs`、`Detection\` 下的判据文件。
- 合并时**判据类冲突按语义合**：同一件事只能留**一个出口**（§9.5），⛔ 不许"两边都留着，各判一遍"。
- 每合一个分支后：`git merge --no-ff`，然后在**主 checkout** 跑一次构建；全部合完再跑**一次全量**（不是每个分支各跑一遍全量）。
- 合并后必须 `git worktree remove <路径> --force`（有 `.git` 残留文件就 `--force`），分支可留可删，⛔ 不留半成品 worktree。

## 4. 集成验收（最后的把关代理或协调者本人）

1. `dotnet build ArchiveFixer.slnx --nologo -v q` = **0 错 0 警**（⛔ 警告也算不通过）；
2. `dotnet format ArchiveFixer.slnx --verify-no-changes` = exit 0；
3. **全量**测试，数字与基线（`AGENTS.md` §11.2）对照，如实报；
4. 逐条**抽查每个分支声称做过的事**（关键用例真在、红检真能变红、唯一出口真只有一个）—— 代理的自述**不等于**已验证；
5. 刷新绿色目录（见 `archivefixer-delivery`，⛔ 程序在跑就跳过并说明）；
6. 写 `修改日志.md` 一行一条 + `docs\需求变更.md` 追加 + `AGENTS.md` §11 结论同步；
7. commit → push（走代理，见全局 `AGENTS.md` §0），报告里给 commit 短哈希与远程同步状态。
