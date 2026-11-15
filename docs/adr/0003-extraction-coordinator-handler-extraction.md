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

## 后续批次记录：内容组最终判决（`ContentGroupFinalizerStep.cs`）

- 状态：本块完成；**类型总行数仍 > 800**，按约定停在这里，不再动其他 partial。
- 被搬分片：`ViewModels/ExtractionCoordinator.ContentGroupFinalizerStep.cs` 482 行 → **48 行**。
- 新协作类：`ViewModels/Extraction/Steps/ContentGroupFinalizerHandler.cs` **497 行**
  （1 个顶层类型、file-scoped namespace、≤800；无 partial、无第二份状态、不用反射）。
- 本批贡献 **−434 行**：按 HEAD 快照口径 9,695 → 9,261；工作树实测快照合计 8,023 行（差额来自同期并行子代理的在飞改动，不计入本批）。
- 薄转发 2 个（签名、可见性、参数名一字不改）：`RecordFinalGroupFailure`（调用方 =
  `VolumeStepHandler`、`OneClickSecondStepEntryLinker`）/ `FinalizeContentGroups`（调用方 =
  `OneClickRoundRunner`、`ExtractionContentGraphTests`）。
- 最小 `internal` 访问器 1 个：`PreserveCurrentProcessArtifactsForContentGroupFinalizer` →
  转调 `ServiceForwarding` 里的 private `PreserveCurrentProcessArtifacts`（唯一实现仍在原处）。
  另 3 个成员本来就是 `internal`，直接复用：`_extractionContentGraph`（VolumeHandoffStep）、
  `IsUserImportedSourcePath`（PieceAdoptionStep）、`AppendLog`（LogCaptureStep）。
- 等价证据：旧分片（HEAD）↔ 新类 `unified=0` diff = **12 删 / 27 增**，全部落在
  ① 类头 + `_host` 字段/构造 ② 3 处 `_host.` 限定符 ③ 2 处 `PreserveCurrentProcessArtifacts*` 调用因限定符变长换行
  ④ 尾部空行；去注释/空白后的 token 流 **9,048 字符逐字相同**；Roslyn 语法解析两个文件均 0 错；
  结构守卫口径自查通过（48 / 497 行、各 1 个顶层类型、file-scoped namespace、VM 禁 `Window`/`Application.Current` 命中 0）。
- 未跑：build / 定向 / 全量 / `dotnet format`（按本批约定由集成者统一验收）；未提交。
- 风险：① 该分片唯一一处 `AppendLog` 的 `CallerFilePath/CallerLineNumber` 归属从 partial 变成 handler
  （与 `RecursionHandler` / `VolumeLedgerHandler` 迁移同口径；级别、文案、errorCode 不变）；
  ② 搬迁遗漏（少限定一个成员 / 少接一个访问器）—— 由 token 等价与语法解析兜底；
  ③ 未编译验证，集成者重编时应以 0 错 0 警为门禁。

## 后续批次记录：递归结论落地（`RecursionOutcomeStep.cs`）

- 状态：本块完成；**类型总行数仍 > 800**，按约定停在这里，不再动其他 partial。
- 被搬分片：`ViewModels/ExtractionCoordinator.RecursionOutcomeStep.cs` 410 行 → **45 行**。
- 新协作类：`ViewModels/Extraction/Steps/RecursionOutcomeHandler.cs` **421 行**
  （1 个顶层类型、file-scoped namespace、≤800；无 partial、无第二份状态、不用反射）。
- 本批贡献 **−365 行**（本分片 410 → 45）。类型总行数实测口径 = HEAD（`7fad9599`）37 个分片
  逐文件计数（含空行）= **9,695 行 → 9,330 行**（与 ADR 0003 尾部的 9,695 同一口径；这是"只有本块"的算法，
  与并行批次各自从同一 HEAD 起算的写法一致）。
- 薄转发 3 个（签名、可见性、参数名一字不改）：`ApplyRecursionResult`（调用方 =
  `ExtractionCoordinator.Recursion.cs` 的 `ApplyRecursionResultForRecursion`）、
  `MoveDirectoryContent` / `HasNoFiles`（调用方 = `RecursionHandler`，仍经
  `MoveDirectoryContentForRecursion` / `HasNoFilesForRecursion` 访问器，那条 partial 一个字未动）。
- 最小 `internal` 访问器 1 个：`ApplyLandingViolationNoteForRecursionOutcome` →
  转调 `OutcomeFinalizeStep` 里的 private `ApplyLandingViolationNote`（唯一实现仍在原处）。
  另 4 个成员本来就是 `internal`，直接复用：`_vm`（ExtractionCoordinator.cs）、
  `AppendLog` / `CriterionLog`（LogCaptureStep）、`_consumedVolumeSources`（VolumeLedgerStep）。
- 等价证据：新类方法体 **381 行**做反向归一（去 `_host.` 限定、恢复 3 处 visibility）后
  与 HEAD 旧分片原块**逐字节相同**（脚本断言 `EQUIVALENT (byte-identical): True`，0 行差异）；
  全部差异只有 ① 类头 + `_host` 字段/构造 ② 5 类 `_host.` 限定共 13 处
  （`_vm` 1 / `AppendLog` 8 / `CriterionLog` 1 / `ApplyLandingViolationNote` 2 / `_consumedVolumeSources` 1）
  ③ handler 侧 3 处 `private`→`internal`（partial 侧签名仍是 private）④ 分片尾部空行；
  **判据、日志顺序、调用顺序一行未改写**。结构守卫口径自查：45 / 421 行、各 1 个顶层类型、
  file-scoped namespace、VM 禁 `Window`/`Application.Current` 命中 0、结构基线未改。
- 未跑：build / 定向 / 全量 / `dotnet format`（按本批约定由集成者统一验收）；未提交。
- 风险：① 该分片 10 处日志调用（`AppendLog` 8 / `CriterionLog` 1 / `Logger.Exception` 1）的
  `CallerFilePath/CallerLineNumber` 归属从 partial 变成 handler（`CallerMemberName` 仍是
  `ApplyRecursionResult`；级别、文案、errorCode、component 一个不变）；
  ② 搬迁遗漏（少限定一个成员 / 少接一个访问器）—— 由上述逐字节等价兜底；
  ③ 未编译验证，集成者重编时应以 0 错 0 警为门禁。

## 后续批次记录：任务级日志捕获 / 摘要行 / 批清账（`LogCaptureStep.cs`）

- 状态：本块完成；**类型总行数仍 > 800**，按约定停在这里，不再动其他 partial。
- 被搬分片：`ViewModels/ExtractionCoordinator.LogCaptureStep.cs` 478 行 → **211 行**。
- 新协作类：`ViewModels/Extraction/Steps/LogCaptureHandler.cs` **423 行**（1 个顶层类型 + 1 个私有嵌套
  `TaskLogCapture`、file-scoped namespace、≤800；无第二份判据、不用反射）。
- 本批贡献 **−267 行**（本分片 478 → 211）。类型总行数工作树实测：改前 7,327 → 改后 7,061；两者之差
  −266，比本分片自身的 −267 多 1 行，差额来自同期并行子代理的在飞改动（不计入本批）。
- 薄转发 13 个（签名、可见性、参数名一字不改）：`AppendTaskRowSnapshot` / `BeginTaskLogCapture` /
  `EndTaskLogCapture` / `NoteBatchPurge` / `LogStopRequestOnce` / `ReadBatchPurge` / `AppendLog` ×3 重载 /
  `AppendTaskLog` ×2 / `AppendLogPair` / `AppendLogWithCode`；另 1 个私有 `LogCapture` 属性做 handler 取的
  唯一入口（**加锁**懒建，理由见「风险」②）。
- 最小 `internal` 访问器 4 个（都是"把 private 字段放宽到 internal"，没有新增属性）：
  `_batchPurgedTaskCount` / `_batchPurgedBytes` / `_batchPurgedPermanently` / `_stopNoticeLogged` ——
  它们由 handler 在锁里读写，而批首清零仍在 `ExtractionCoordinator.ExtractEntryStep.cs`
  （`ResetBatchPurgeCounters`）里直接赋 0，那一处**一个字未动**。
  另 4 个成员本来就是 `internal`，直接复用：`Settings` / `Tasks` / `_vm` / `KeepTaskDetailInLog`。
- 留在分片的 4 个成员（不搬）：`CriterionLog` / `UpdateSummary` 是转给 `MainViewModel` 唯一收口的
  **一行适配器**（没有实现可搬，搬走只多一跳）；`KeepTaskDetailInLog` / `PostProcessStartedForTests`
  是测试与别的 Handler 直接读写的状态（`ExtractionPipelineFixTests` 写后者、`SourceHandler` 读后者）。
- 等价证据：把 HEAD 旧分片里被搬走的 19 个成员块（363 行）与新类方法体（386 行）做去 `_host.` 归一后的
  `unified=0` diff = **8 个 hunk**，全部落在 ① 1 处 `SnapshotTaskTable(Tasks)` →
  `ExtractionCoordinator.SnapshotTaskTable(...)`（跨类静态调用加类型前缀）② 6 处 `private`→`internal`
  （handler 侧；partial 侧签名仍 private）③ 1 处 doc `<see cref="FinalizeDeferredVolumeDeficits"/>` → `<c>…</c>`
  ④ `NoteBatchPurge` 新增一段 doc（原来的记账说明仍在宿主字段上，逐字未改）；
  **判据、表达式、日志顺序、调用顺序一行未改写**。Roslyn 语法解析两文件各 **0 诊断 / 0 错**。
  结构守卫口径自查：423 / 211 行（≤800）、各 1 个顶层类型、file-scoped namespace、
  ViewModels 禁 `new *Window` / `Application.Current` / `MessageBox.` 命中 0、结构基线未改、无 BOM。
- 搬走的一块包含：任务级捕获缓冲（`AsyncLocal`）+ 开始/收尾、详细档判定、收尾摘要行（成功时唯一一行）、
  任务收尾诊断（span / 最近失败事件 / 终态兜底码 / `DiagnosticLog.WriteTaskEnd`）、①页逐格行快照、
  批清理记账与「停止后续只写一次」、`AppendLog` 四个入口与三个适配口。
- 未跑：build / 定向 / 全量 / `dotnet format`（按本批约定由集成者统一验收）；未提交。
- 风险：① 诊断日志归属：**外部调用点零变化**（宿主的重载照旧带
  `CallerFilePath/CallerLineNumber/CallerMemberName`，取值后把三个值原样传给 handler —— 这比前几批更强一档）；
  只有 handler 内部 7 处自调（`AppendTaskRowSnapshot` / `LogStopRequestOnce` / `AppendTaskLog` /
  `AppendLogPair` / `AppendLogWithCode` 各 1 处 `AppendLog`，加 `EndTaskLogCapture` / `FlushTaskLogBuffer`
  的 `_vm.AppendLog`）的 `sourceFile` 从 `…LogCaptureStep.cs` 变成 `…LogCaptureHandler.cs`；成员名、级别、
  文案、errorCode、component 全不变。② 懒建用锁而不是 `??=`：搬走的是**实例状态**（`AsyncLocal` 缓冲 +
  批记账锁），而任务线程本来就会并发调 `BeginTaskLogCapture` / `AppendLog`；无锁懒建可能造出第二个实例，
  让"开始捕获"和"写日志"落到两个不同缓冲（默认档下成功任务的 INFO 细节会漏上屏幕，正好踩既有红线）。
  这与同批兄弟的 `??=` 写法不同，理由写在宿主字段的 doc 里。③ 两处死代码随实现**原样搬走**
  （`IsProgressNoise` 0 调用、`MaxSummaryPathLength` 0 使用，HEAD 时即如此）：本次只搬不改，
  避免把清理混进搬迁。④ 未编译验证：`AppendLog` 三个重载在新类内的重载决议与今天宿主里那三个同签名重载
  是同一组（`(level, message)` 命中 5 参版、`(level, message, errorCode, component)` 命中 7 参版），
  `ReadBatchPurge` 的命名元组返回值逐字未改；0 错 0 警仍以集成者重编为准。
