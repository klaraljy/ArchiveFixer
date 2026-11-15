# ADR 0006：RecursiveExtractor 按职责抽步骤 Handler（第 6 块：结论组装 / 收尾）

- 状态：接受（进行中，类型总行数未达 800）
- 日期：2026-11-15
- 关联：ADR 0003 / ADR 0004（`ExtractionCoordinator` 同款做法）、结构基线 `docs/开发/结构基线.json`、M5
- 本批范围：只动 `src/ArchiveFixer/Extraction/Recursion/RecursiveExtractor.Result.cs`（354 行）+ 新建一个协作类

## 背景

`RecursiveExtractor` 的实现散在 12 个 partial 分片里（`Extraction/RecursiveExtractor.cs` +
`Extraction/Recursion/RecursiveExtractor.*.cs`）。HEAD 实测（`git show HEAD:` 逐文件计数，含空行）
合计 **4,486 行**；单看文件最大的是 `Probe.Embedded.cs` 681、`ExtractAsync.cs` 524、`InnerDiscovery.cs` 531
—— **没有一片超过 800**，但「整个类型」远大于 800。

`Result.cs` 是「最后一公里」：组装最终结论（层 / 停因 / 发布结果 / 盘上产物事实）、发布告警落日志、
写恢复报告、兜底删跨盘临时链接、按结论保留最后一层过程物、按结论清工作区。它不参与解压执行，
只读本次运行的账本 + 宿主的几个既有出口 ⇒ 适合整块搬成协作类。

## 决策

1. 抽成 `internal sealed class RecursiveResultHandler`，文件放 `Extraction/Recursion/Steps/`，
   namespace 保持扁平 `ArchiveFixer.Extraction`（与 `RecursiveResultBuilder` / `RecursiveLayerExpansion` 同一套）。
2. Handler **只持有宿主** `RecursiveExtractor _host`（partial 里懒建一次：
   `_resultHandler ??= new RecursiveResultHandler(this)`），不持有第二份状态、不复制判据、不用反射。
3. 宿主那一分片只留三类成员：
   - **同签名薄转发 5 个**：`BuildResult`（`ExtractAsync` 三处调用）、
     `RetainProcessArtifactBeforeConsumption`（`InnerDiscovery` 一处）、`FinalizeRun`（`ExtractAsync` 三处），
     以及 `ResolveRetainedArtifactRoot` / `ResolveWorkspaceOutputRoot`（见第 4 条）；
   - **最小 `internal` 访问器 9 个**（只给定义在别的 partial 里的 private 成员，见下表）；
   - 原本就属于本分片、别的 partial 直接读的 internal 访问器（`Engine` / `Limits` / `PasswordFallbackNote` /
     `MarkExactStopReasonReported` / `AddCrossBoundaryLinks` / `AddConsumedVolumeSources`）**原样保留**。
4. `ResolveRetainedArtifactRoot` / `ResolveWorkspaceOutputRoot` 的实现随本块搬进 handler，但**必须留同签名转发**：
   本批执行期间，`ExtractAsync` 那一分片已经把两个宿主访问器 `ResolveRetainedRoot`（`RecursiveExtractHandler` 用）
   与 `ResolveRunOutputRoot` 建在这两个 private 方法上 ⇒ 实现搬走后若删转发，那两个访问器会编译失败。
   ⛔ 不给「搬走后没人调用」的方法留空转发；这两个是**有调用方**的。
5. ⛔ 不按行数切方法、不改调用点、不改其他 partial / 测试 / XAML、不 commit；
   按本批约定**未跑** build / test / format（由集成者统一验收）。

## 本批执行与证据

| 项 | 数值 |
|---|---|
| 被搬分片 | `RecursiveExtractor.Result.cs` 354 行 → **193 行** |
| 新协作类 | `Steps/RecursiveResultHandler.cs` **312 行**（1 个顶层类型，file-scoped namespace） |
| 类型总行数 | 本块贡献 **−161 行**（354 → 193；新类是另一个类型，不计入） |
| 工作树快照 | 12 个分片合计 **1,951 行**（含同期 7 个并行子代理的在飞改动，不计入本块） |
| HEAD 口径 | 4,486 − 161 = **4,325 行**（只算本块） |
| 等价证据（反向） | 旧块 279 行 ↔ 新类方法体做**反向归一**（去掉 `_host.`、访问器名换回原 private 名、5 处可见性改回 `private`）后 `git diff --no-index` = **0 删 / 0 增**；去空白后的 token 流 **6,475 字符逐字相同** |
| 等价证据（正向） | 旧块 ↔ 新类方法体 `unified=0` diff = **20 个 hunk / 23 删 / 23 增**，全部只有三类：① 5 处 `private`→`internal`（partial 侧签名仍 private）② 18 处宿主引用限定（`_host.` 16 + `RecursiveExtractor.` 2），其中 1 处同时把属性赋值改成 setter 访问器（`RetainedProcessArtifactPath = target;` → `_host.SetRetainedProcessArtifactPath(target);`）③ 6 个访问器改名（与 ② 同处）；**判据 / 文案 / 调用顺序一行未改写** |
| 语法 | 两个文件 Roslyn 8.0.401 语法解析 **0 错 0 警** |
| 守卫口径 | 312 / 193 行（≤800）、各 1 个顶层类型、file-scoped namespace、`Extraction` 无 `System.Windows`；结构基线未改（新文件未超 800，无需登记） |
| 卫生 | 两文件 UTF-8 无 BOM、LF、无行尾空白、末尾单换行 |

搬走的一块包含：最终结论组装（含「发布失败 ⇒ `Completed=false` / `PartiallyCompleted=true`」的降级、
就地替换判据的读取、发布告警逐条落日志）、恢复报告落盘、跨盘临时链接兜底清理、
「盘上这一趟有没有解出过东西」的一次性度量、空间不足模式的最后一层过程物保留、
按结论决定清本次工作区 / 清被本次续跑取代的旧工作区。

## 访问器清单（唯一实现全部仍在原 partial）

| 访问器 | 转调的 private 成员 | 定义在 |
|---|---|---|
| `IsInsideWorkspaceRootForResult` | `IsInsideWorkspaceRoot` | `RecursiveExtractor.Reporting.StopReason.cs` |
| `WriteRecoveryReportForResult` | `WriteRecoveryReport` | `RecursiveExtractor.Reporting.Recovery.cs` |
| `RemoveCrossBoundaryLinksForResult` | `RemoveCrossBoundaryLinks` | `RecursiveExtractor.cs` |
| `ConsumedVolumeSources` | `_consumedVolumeSources` | `RecursiveExtractor.cs` |
| `UnresolvedVolumePieces` | `_unresolvedVolumePieces` | `RecursiveExtractor.cs` |
| `BuildSummaryForResult` | `BuildSummary` | `RecursiveExtractor.Reporting.StopReason.cs` |
| `CleanupWorkspaceForResult` | `CleanupWorkspace` | `RecursiveExtractor.WorkspaceCleanup.cs` |
| `CleanupSupersededWorkspacesForResult` | `CleanupSupersededWorkspaces` | `RecursiveExtractor.WorkspaceCleanup.cs` |
| `SetRetainedProcessArtifactPath` | `RetainedProcessArtifactPath` 的 private set | `RecursiveExtractor.cs` |

本来就 internal / public、直接复用（不另开访问器）：`Log`、`ReclaimedProcessArtifacts`、`Limits`、`Engine`、
`LastRunArchivePath`（`ExtractAsync` 那一分片已露 internal）、`RetainedArtifactDirectory`。

## 判定：类型总行数仍 > 800

本块搬完，工作树 12 个分片合计 **1,951 行**（HEAD 口径 4,325 行）—— **仍远大于 800**
⇒ 按本批任务约定停在这里，不再动其他 partial。所以本 ADR 只登记「做法成立、单块收益、剩余债务与风险」，
不宣布 800 行已达成。

## 风险与集成注意事项

1. **跨批依赖（最重要）**：本类依赖三类「别的 partial 里的成员」继续存在 ——
   ① 上表 9 个 private 成员（访问器的目标）；② `ExtractAsync` 那一分片的 `internal string LastRunArchivePath`；
   ③ 两个薄转发背后的调用方（`ResolveRetainedRoot` / `ResolveRunOutputRoot`）。
   同期有 7 个并行子代理在搬 `ExtractAsync` / `InnerDiscovery` / `LayerRules` / `Probe.*` / `StopReason` /
   `RecursiveExtractor.cs` 等分片：如果它们把某个成员也整块搬进自己的 handler 并**删掉了 partial 里的实现**，
   本类会编译失败。集成时按上表逐个核对；若已搬走，改成调用对应 Handler 的 internal 出口
   （判据仍只有一份，⛔ 不复制）。
2. **重名已消解**：第一版访问器里还有一个 `LastRunArchivePath`，与 `ExtractAsync` 那一分片已露的
   `internal string LastRunArchivePath` 重名（`CS0102`）⇒ 直接复用宿主那个属性、删掉自造的访问器
   （`_host.LastRunArchivePath` 读的仍是同一个 `_lastRunArchivePath`），行为不变。
3. **两个 Resolve 助手是「实现搬走 + 原签名转发」**：它们的转发不是空转发 —— 同期批次已经把它们当宿主出口用
   （`ExtractAsync.ResolveRetainedRoot` → `RecursiveResultHandler` 那个 helper 供 `RecursiveExtractHandler`；
   `ResolveRunOutputRoot` 同理）。转发留着的代价是 2 个方法各 2 行；删掉的代价是那次批次编译失败。
4. **未编译验证**：按本批约定未跑 build / test / format。已做的替代验证：Roslyn 语法解析 0 错、
   跨分片成员重名启发式扫描 0 命中（`BuildLayerReport` 那条是重载误报）、9 个访问器的目标声明逐个核对仍在、
   反向归一后逐字节等价。0 错 0 警与全量绿仍以集成者重编为准。
5. **诊断归属**：搬走的代码里**没有** `[CallerFilePath]` / `[CallerLineNumber]` / `[CallerMemberName]`
   （`Log` 是注入委托，`WriteRecoveryReport` / `BuildSummary` 都是普通调用）⇒ 诊断的 source_file 不受影响
   （与 `ExtractionCoordinator` 那几批不同）。
6. **既有瑕疵原样保留（本批不修）**：`RecursiveExtractor.Result.cs` 里「一次运行的收尾……」那段 XML doc
   在 HEAD 就已经挂在 `RetainProcessArtifactBeforeConsumption` 上（真正对应的是 `FinalizeRun`）。
   本批按「搬迁不改写」原样搬进 handler；修正留给下一次动这个文件时做（ADR 0004 口径：不把清理混进搬迁）。
7. **未提交**：三个文件（一个改、一个新建、本 ADR）留在工作树，由集成者统一提交。
