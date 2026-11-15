# ADR 0003：ExtractionCoordinator 按职责抽步骤 Handler（第 13 块：递归器构建 / 逐层回收 / 递归上限）

- 状态：接受（进行中，类型总行数未达 800）
- 日期：2026-11-15
- 关联：ADR 0002 / M5（`ExtractionCoordinator` 拆分）、结构基线 `docs/开发/结构基线.json`
- 本批范围：只动 `ViewModels/ExtractionCoordinator.RecursiveLayerReclaimStep.cs`（538 行）

## 背景

`ExtractionCoordinator` 是 ViewModel 层的单例，代码全部散在 `ExtractionCoordinator.*.cs` 的 partial 分片里。
P4 已经做到**每个 partial ≤800 行**（37 个分片），但"整个类型的总行数"没有门槛：本批开工时实测
37 个分片共 **13,393 行**（该数字含同期其它并行抽取子代理的在飞改动），其中单文件最深的一档是
`ServiceForwarding.cs` 774 行、`GroupOrderTrialStep.cs` 728 行、`VolumeStepStep.cs` 687 行。

单文件 ≤800 只解决了"文件太大"，没有解决"一个类型装了整条解压流水线"。本批验证一种可复制的做法：
**按职责整块抽协作类，partial 只留转发与访问器**，看单块抽取能把类型总行数压到什么程度。

## 决策

1. 每一块抽成 `internal sealed class XxxHandler`，文件放 `ViewModels/Extraction/Steps/`，namespace 保持扁平
   `ArchiveFixer.ViewModels`（与 `RecursionHandler` / `PostProcessSuccessHandler` 同一套）。
2. Handler **只持有宿主** `ExtractionCoordinator _host`，不持有第二份状态、不复制判据、不用反射。
3. 宿主那一分片只留三类成员：
   - **同签名薄转发**（只给本类仍有调用方的成员：本批 3 个 —— `CreateRecursiveExtractor` /
     `ResolveCandidateSourceForLog` / `BuildRecursionLimits`，其中后两个被单测直接调用）；
   - **最小 `internal` 访问器**（只给定义在别的 partial 里的 private 成员：本批 7 个）；
   - 原本就属于本分片、且别的 partial 直接读写的字段（`_taskRecursiveExtractors` / `_layerReclaimThisBatch`）。
   ⛔ 不给"搬走后没人调用"的内部方法留空转发（ADR 0002 已禁止用一行转发冒充拆分）。
4. ⛔ 不按行数切方法、不把属性压成一行转发、不改调用点、不改测试、不改 XAML。

## 本批执行与证据

| 项 | 数值 |
|---|---|
| 被搬分片 | `ExtractionCoordinator.RecursiveLayerReclaimStep.cs` 538 行 → **119 行** |
| 新协作类 | `ViewModels/Extraction/Steps/RecursiveLayerReclaimHandler.cs` **504 行**（1 个顶层类型，file-scoped） |
| 类型总行数 | 本批贡献 **−419 行**（538 → 119；新类是另一个类型，不计入） |
| 等价证据 | 旧分片（HEAD 版）↔ 新类 `unified=0` diff：**86 删 / 52 增**，全部改动只有 ① 文件/类头与 `_host` 构造 ② 成员引用加 `_host.`（含 7 个访问器名）③ 一处 `<see cref>` 改 `<c>` ④ 一处续行括号并到上一行；**没有一行判据被改写** |
| 守卫口径 | 新文件 504 行 ≤800、1 个顶层类型、file-scoped namespace；旧分片 119 行 ≤800 |

搬出的一块包含：递归核心的构建与全部注入（密码候选、日志、第 0 层源快照校验、逐层回收钩子、
引擎调用 trace、停止信号、可疑条目开关）、逐层回收的执行口、回收用的一次性任务对象、候选来源映射、
递归硬上限映射。删除判据与执行体**一条都没搬**，仍然只有 `PurgeLayerSourcePackage`
（→ `SourceCleanupService`）那一个出口。

## 判定：类型总行数仍 > 800

搬完实测 `ExtractionCoordinator` 全类型 **9,695 行 / 37 个分片**（本批收尾快照；开工时 13,393 行，
差额大部分来自同期并行子代理抽别的分片，只有 −419 是本批的）。**仍远大于 800 ⇒ 按本批任务约定
停在这里，不再继续动其他 partial**。所以本 ADR 只登记"做法成立、单块收益、剩余债务"，不宣布
800 行已达成。

## 剩余债务与后续退出条件

- 剩余 36 个分片、约 9,700 行（实测快照，随并行批次继续下降）：当前最大几个是
  `ServiceForwarding.cs` 774、`ExtractionCoordinator.cs` 487、`ContentGroupFinalizerStep.cs` 482、
  `LogCaptureStep.cs` 478、`SourceProtectionStep.cs` 453、`PasswordPromptStep.cs` 451；
  精确以 `docs/开发/结构基线.json` 与实测为准。
- 退出条件：类型总行数 ≤800 且每个 partial ≤800，结构基线条目只降不升。
- 每批铁律：一个 partial + 一个新 handler；build 0 错 0 警 → `dotnet format --verify-no-changes`
  → 定向测试（本批相关：`RecursionGateAndHintFixesTests` / `RecursionLogAndProbePathFixesTests` /
  `CodeStructureGuardTests`）→ 合并后再跑全量。
- ⛔ 不许为了到 800：删测试、放宽能力断言、把普通流程改回 SingleLayer/SingleChain、按测试名加分支。

## 后果与风险

- 逐层回收是**不可逆路径**（永久删过程物）：本批没有改任何判据，只改了代码位置；调用顺序、日志顺序、
  `reclaimRootSource: false` 等门槛原样。风险集中在"少映一个上限 / 少接一个注入点"这类搬迁遗漏，
  故把 diff 限度（86 删 / 52 增且全部是限定符）作为等价证据写在这里。
- 诊断日志归属：handler 里两处 `AppendLog` 的 `CallerFilePath/CallerLineNumber/CallerMemberName`
  从 partial 文件变成 handler 文件（与 `RecursionHandler` / `PostProcessSuccessHandler` 迁移同口径）。
  只影响诊断里的"调用点"字段，不改级别、文案与 errorCode。
- `ExtractionCoordinator.Source.cs` 里 `<see cref="OnRecursiveLayerReclaim"/>` 现在指向已搬走的私有方法，
  成为悬空 doc 引用（本仓库不生成 XML 文档 ⇒ 不影响构建）。下次动那个文件时改成 `<c>…</c>` 或指向
  `RecursiveLayerReclaimHandler`。
- 本批按任务约定**未跑** build/test/format；`0 错 0 警`与全量绿由主仓集成者在合并后统一验收。
