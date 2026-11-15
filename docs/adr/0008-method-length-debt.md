# ADR 0008：单方法 >150 行债务（105 个）与退出条件

- 状态：接受（按用户 2026-11-15 口径：>150 是报告信号，不是硬性 0；不为行数硬拆）
- 日期：2026-11-15
- 关联：M5 / ADR 0004

## 事实

用 SDK Roslyn 对 `src/ArchiveFixer` 全量解析（`MethodDeclarationSyntax` 行跨度）：

- 方法总数 4,674；**>150 行 105 个**。
- 最大：`LayerExecutor.ExecuteAsync` 756、`ExtractTaskMainHandler.RunAsync` 694、`PostProcessSuccessHandler.PostProcessSuccessAsync` 632、`ExtractionDeferredVolumeRechecker.RecheckDeferredVolumeDeficitsAsync` 629、`ExtractionVolumeCandidateCollector.CollectVolumeCandidatePaths` 626、`ExtractEntryHandler.StartExtractCoreAsync` 625。
- 这些方法都是生命周期/失败模型强耦合的批处理主体；按行数切会跨 async 状态机、取消、日志与不可逆顺序，风险高于收益。

## 决策

- **不在本轮按行数切这些方法**；用户 2026-11-15 明确：方法 <150 不是死要求，先判断是否有必要搬，禁止为行数硬拆。本 ADR 把 105 条登记为**报告项/例外**，不是未闭环。
- 下一步只允许按**职责边界**拆（例如“直读/抠取”“预检”“密码候选循环”“逐层回收”），每拆一块必须先建对应的行为守门用例；禁止按行数切、禁止 partial 搬家、禁止改公开 API。
- 结构守卫后续新增方法长度基线时，只允许从此清单减少，不允许新增；红检必须能指出一个具体方法。

## 后果

- M5 的“单文件 >800”已闭环；单方法 >150 为报告项，按本 ADR 例外处理，不再作为硬性 0 验收。
- 任何“方法长度达标”的汇报必须引用本 ADR 与实测数字，不能口头豁免。
- 退出条件：新代码不得新增 >150 且无职责理由的方法；存量 105 条按 owner/原因逐条复核，必要拆分另行立项。
