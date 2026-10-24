# 第二大步残留（停用暂存区）

用户 2026-10-24 拍板：**第一大步只许对分卷做「信息登记」，不许做任何操作。**
于是把「第二大步该干的活」从第一大步路径上整段停用，并集中到这个目录；重建第二大步时
从这里挑还能用的，第二大步完工后整个目录删掉。

## 已搬进来的文件

| 文件 | 原职责 |
|---|---|
| `GroupAnalysis.cs` | 全树组分析（基名 + 大小分布 + 交接单事实） |
| `VolumeGroupEntryLayer.cs` | 搬片进入口层 / 成功收片 |
| `VolumeStepExecutor.cs` | 组入口任务的执行体（旧接线，原本就无人调用） |
| `SyntheticGroupEntryQueue.cs` | 合成组入口队列（旧接线，原本就无人调用） |
| `ExtractionCoordinator.BatchVolumeEntryStep.cs` | 批首组分析 + 点名入口任务 + 搬/链接片 + 产出方落点推演 |
| `ExtractionCoordinator.DeferredVolumeDeficitStep.cs` | 缺卷延期账 / 批末补判 / 链尾终判 / 收卷 |
| `ExtractionCoordinator.VolumeStepPreflightStep.cs` | 缺卷不开工 / 收片账 / 成功收片、失败留片 |
| `ExtractionCoordinator.VolumeHandoffStep.cs` | 交接账消费 / 组结论回写 / 批末交接汇总 / 疑似片升级 / 接住 |
| `ExtractionCoordinator.VolumeGatherStep.cs` | 收片、单片刻接、账本 |
| `ExtractionCoordinator.VolumeGatherCollectStep.cs` | 收拢 / 接片（`TryGatherGroupPiecesInto` → `TryAdoptUnresolvedVolumePiece`） |
| `ExtractionCoordinator.PieceAdoptionStep.cs` | 接片辅助（用户源红线、暂存目录判据） |
| `ExtractionCoordinator.GroupOrderTrialStep.cs` | 组内顺序试开 |
| `VolumeGroupingService.cs` | 扫描期按目录归组（一组卷 = 一个任务） |
| `SecondStepResidueState.cs` | 总开关 `Parked`（true = 残留区所有执行体直接返回） |

## 已从第一大步路径断开的调用

- `ExtractEntryStep`：批首 `ApplyVolumeGroupingFromDirectory` + `MarkVolumeStepEntriesForBatch`；
  批末 `RecheckDeferredVolumeDeficitsAsync` + `UpgradeUnrecognizedMaybePieceVerdicts`
- `ExtractTaskMainStep`：第二大步入口放行；`TryStopVolumeStepBeforeStartAsync`；
  `TryPurgeSolvedVolumeStepPieces`；`LogVolumeGroupFirstVolumeLanding`
- `OneClickCoordinator`：轮边界组分析/收卷；`TryMarkHandoffGroupEntryTasks`；
  `RememberVolumeStepTaskForRound`；批末 `HarvestRememberedGroupVolumesForBatch` +
  `AppendVolumeHandoffBatchSummary`；链尾 `FinalizeDeferredVolumeDeficitsAsync`
- 扫描期：`FileScanService` 不再调 `VolumeGroupingService.ApplyVolumeGrouping`（一份文件一个任务）
- 续解加任务：`OneClickCoordinator` 不再按目录补跑 `ApplyVolumeGroupingFromDirectory`
- 递归层：`RecursiveExtractor` 不再跨层收卷（`TryGatherCrossLayerVolumes` 直接放行）、
  不再还原伪装卷名（`RestoreDisguisedInnerPackageNames` 跳过）
- 残留区执行体自身：`TryAdoptUnresolvedVolumePiece` / `TryGatherGroupPiecesInto` /
  `PurgeGroupLandedPieces` 开头都先问 `SecondStepResidueState.Parked`，为 true 直接返回

## 仍然留在第一大步的

允许保留（只是登记，不是操作）：

- 内容卡读取（`FileNature.Inspect`）
- `RegisterVolumeHandoff` 登记交接事实 + 闸门跳过 + `ClearVolumeHandoffLedger`
- 手动「只解压」的一行范围 WARN（只写日志）

还没搬完（深层，下一轮处理）：

- **删除/其余物里的分卷闸门**：`PurgeLayerSourcePackage`（源包/层回收删除前的完整性判据）、
  `RestVolumeCompletenessGate`、`RestItemPurger`、`ProcessArtifactLayout`（整组搬源包）。
  这些是**只读安全闸门**或删除路径，不是"解分卷"，本轮保留（搬走会误删/漏删）。
- **轮判定**：`RoundProductClassifier` 读 `GroupVolumeDirectory` 判"这一轮解出来的是不是分卷组"——
  读事实、不动盘；属于轮次模型，等重建第二大步时一并处理。
- **手动功能**：`修复分卷名`（`VolumeRepairStep` / `VolumeNameNormalizeStep` / `MainViewModel`）
  是用户点按钮触发的独立功能，不属于第一大步管线。
- **只读事实**：`ArchiveDetectService` 的加密判定、`PathService` 的落点、`TaskSummaryService`
  的"分卷：N 卷"——归组停了之后它们的 `VolumePaths` 恒空，实际已自然失效，无需再改。

## 重建第二大步时的纪律

1. 事实只从交接单（`VolumeHandoffEntry`）读，⛔ 不重读盘识别。
2. 只解入口那一层；用户源片只链接、不搬；成功收片、失败留片。
3. 每接回一块，都要在造包器 + 真机 30 份上各验一次。
4. 第二大步完工 ⇒ 删除本目录（不再留死代码）。
