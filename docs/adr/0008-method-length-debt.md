# ADR 0008：单方法 >150 行债务（105 个）与退出条件

- 状态：接受（M5 未闭环，登记债务）
- 日期：2026-11-15
- 关联：M5 / ADR 0004

## 事实

用 SDK Roslyn 对 `src/ArchiveFixer` 全量解析（`MethodDeclarationSyntax` 行跨度）：

- 方法总数 4,674；**>150 行 105 个**。
- 最大：`LayerExecutor.ExecuteAsync` 756、`ExtractTaskMainHandler.RunAsync` 694、`PostProcessSuccessHandler.PostProcessSuccessAsync` 632、`ExtractionDeferredVolumeRechecker.RecheckDeferredVolumeDeficitsAsync` 629、`ExtractionVolumeCandidateCollector.CollectVolumeCandidatePaths` 626、`ExtractEntryHandler.StartExtractCoreAsync` 625。
- 这些方法都是生命周期/失败模型强耦合的批处理主体；按行数切会跨 async 状态机、取消、日志与不可逆顺序，风险高于收益。

## 决策

- **不在本轮按行数切这些方法**；M5 的单方法 0 目标未达成，登记为本 ADR 的未结项。
- 下一步只允许按**职责边界**拆（例如“直读/抠取”“预检”“密码候选循环”“逐层回收”），每拆一块必须先建对应的行为守门用例；禁止按行数切、禁止 partial 搬家、禁止改公开 API。
- 结构守卫后续新增方法长度基线时，只允许从此清单减少，不允许新增；红检必须能指出一个具体方法。

## 后果

- M5 不能宣称全绿；`src >800` 仅剩 ArchiveTask（ADR 0002），但单方法债务 105 个仍开。
- 任何“方法长度达标”的汇报必须引用本 ADR 与实测数字，不能口头豁免。
- 退出条件：105 → 0（或逐条 ADR 例外），并跑全量 + build 0/0 + format。
