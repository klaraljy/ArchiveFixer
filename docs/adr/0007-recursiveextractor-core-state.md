# ADR 0007：RecursiveExtractor 主文件抽出 RecursiveCoreState（状态 / 装配）

- 状态：接受（进行中，类型总行数未达 800）
- 日期：2026-11-15
- 关联：ADR 0003 / ADR 0004 / ADR 0006（同批 Handler 抽取）、结构基线 `docs/开发/结构基线.json`、M5
- 本批范围：只动 `src/ArchiveFixer/Extraction/RecursiveExtractor.cs`（497 行）+ 新建一个协作类

## 背景

`RecursiveExtractor` 的实现散在 12 个 partial 分片里。HEAD 实测（`git show HEAD:` 逐文件计数，含空行）
12 片合计 **4,486 行**；主文件 `Extraction/RecursiveExtractor.cs` 497 行，其中约七成是**存放**：
9 个运行账本字段、15 个可配档位属性、6 个注入依赖、5 个常量/静态出口，以及这些成员各自的大段
"为什么"文档。主文件**没有**一次解压执行逻辑（层入口 `ExtractLayerAsync` 只有 22 行；主循环在
`ExtractAsync` 分片里）。

同期有 7 个并行子代理在把其余 partial 搬成 `Extraction/Recursion/Steps/Recursive*Handler.cs`
（它们在各自分片里加宿主访问器，**不改主文件**）。本块只负责主文件：把可迁移的"存放"抽成协作类，
主文件保留公开 API 与最小转发 ⇒ 类型总行数下降，且其他分片/协作类继续按原名编译。

## 决策

1. 抽成 `internal sealed class RecursiveCoreState`，文件放 `Extraction/Recursion/Steps/`，
   namespace 保持扁平 `ArchiveFixer.Extraction`（与 `RecursiveLayerPlanner` / `RecursiveWorkspaceArtifacts`
   同一套）。一个文件一个顶层类型，未用 partial 拆类。
2. 主文件只留：类文档、5 个常量/静态表、`readonly RecursiveCoreState _core`、
   `readonly RecursiveLayerPlanner _layerPlanner`（仍由主文件装配）、公开 API 的**同签名转发**、
   `ExtractLayerAsync`、5 个既有静态出口。
3. **状态唯一存储在 `_core`**：注入依赖（Engine / Prober / PasswordProvider / Limits / Log / SourceCheck
   与三处空值兜底）、12 个运行账本/缓存槽、15 个可配档位、2 个既有转调（`RememberUnresolvedVolumePiece`
   / `RemoveCrossBoundaryLinks`）全部搬进去。主文件对应成员改成**同名转发**：
   - 14 个私有成员（`_engine` … `_lastRunArchivePath`）→ 同名私有属性（`{ get; set; }` 或只读）；
   - `RetainedProcessArtifactPath` / `CurrentWorkspace` → 同名公开属性，`private set` 原样；
   - 15 个公开/内部档位属性 → 同名转发（`get => _core.X; set => _core.X = value;`）；
   - `ReclaimedProcessArtifacts` / `RootSourceCandidates` / `RememberUnresolvedVolumePiece` 保留原访问级别。
   这样 11 个其他分片、7 个协作类与 8 个在飞 Handler 的调用点**一行都不用改**。
4. **本批新增 internal 访问器 = 0**：宿主原有 internal 面（`ReclaimedProcessArtifacts`、`RootSourceCandidates`、
   `RememberUnresolvedVolumePiece`、`StopRequested`、`DangerousEntriesReported`、`ShouldStopAtVolumeGroupMember`、
   两个目录名常量、5 个静态出口）逐个原样保留；并行批在各自分片里加的访问器（如
   `internal string LastRunArchivePath`、`internal bool ExactStopReasonReported`、`RootSourceCheck`、
   `PruneCrossBoundaryLinks`、`BeginRunVolumeWindow`、`ResetRunLedgers`）继续读写**同名转发成员**，
   与搬类前的字段是同一个槽位。
5. `RootSourceCandidates` 的**懒算实现留在主文件**：`ExtractAsync` 分片的 `BeginRunVolumeWindow`
   直接写 `_rootSourcePath` / `_rootSourceCandidates`（不改其他 partial 的约束下无法迁移），
   所以这两个成员只搬存储、保留同名转发；懒算由
   `_rootSourceCandidates ??= RecursiveSourceCandidatePool.EnumerateNearby(_rootSourcePath)` 一处完成。
6. ⛔ 不改其他 partial / 测试 / XAML、不 commit；按本批约定**未跑** build / test / format（由集成者统一验收）。

## 本批执行与证据

| 项 | 数值 |
|---|---|
| 被搬分片 | `Extraction/RecursiveExtractor.cs` 497 行 → **369 行**（−128，−25.8%；`git diff --numstat` = +167 / −295） |
| 新协作类 | `Steps/RecursiveCoreState.cs` **382 行**（1 个顶层类型，file-scoped namespace） |
| 类型总行数 | HEAD 口径 4,486 − 128 = **4,358 行**（新类是另一个类型，不计入）；当前工作树 12 分片合计 **1,958 行**（含 7 个并行子代理在飞改动，不可单独归因本块） |
| 成员面（Roslyn 对照 HEAD） | 原 48 个成员名**全部**仍在主文件，且**类型 + 可访问性逐字相同**；新增唯一成员 `_core`（私有）。新类 34 个成员（ctor + 6 依赖 + 25 状态/档位 + 2 转调），全部 `internal` |
| 初始值（Roslyn 对照） | 原文件 14 个字段/属性初始化式：5 个常量留在主文件；9 个随状态搬进新类，**9/9 表达式逐字相同**（`string.Empty` / `new()` / `ContentKeepRules.Empty` / `new(StringComparer.OrdinalIgnoreCase)`） |
| 文档（Roslyn 对照） | 搬走的 33 个成员 XML doc 块：**17/33 与 HEAD 逐字相同**；其余 16 处差异全部可归三类：① 4 个原**没有** doc 的注入依赖字段（`_engine` / `_prober` / `_passwordProvider` / `_limits`）在新类补了最短说明；② 跨类 cref 加 `RecursiveExtractor.` 前缀 / private 成员改 `<c>`（`_sourceCheck`、`CurrentWorkspace`、`_rootSourcePath`、`_crossBoundaryLinks`、`_supersededWorkspaces`、`VerboseLog`、`PasswordVerified`）＋换行重排（`_log`、`KeepRules`）；③ 2 处把"本类"改成"递归核心"的措辞澄清（`LayerReclaim`、`UnresolvedPieceRescueDirectory`）；`_rootSourceCandidates` 那一块是缓存槽语义改写（懒算指到宿主）。判据、数字、口径文字未删改 |
| 层入口 | `ExtractLayerAsync` HEAD 439–460 行 ↔ 新文件 311–332 行，**文本逐字相同** |
| 构造 | 原 6 条赋值（engine/prober/passwordProvider/limits/log/sourceCheck）搬进新类 ctor，顺序、空值兜底、`ArgumentNullException` 参数名与默认值**逐条相同**；`_layerPlanner = new RecursiveLayerPlanner(BuildPasswordCandidates, Log)` 留在主文件且原样 |
| 两个既有转调 | 方法体与调用目标/顺序不变：`RememberUnresolvedPiece(path, 账本, <rescueDir>)`、`RemoveCrossBoundaryLinks(CurrentWorkspace, <links>)`，只是引用从私有字段换成同名转发属性 |
| 懒算 | `if (x == null) { x = Enumerate(path); } return x;` → `x ??= Enumerate(path)`：右值只求值一次、结果只写一次、非空流返回；`??=` 的同类写法（属性左值 + 非空返回）在 `ToolLocator.EnsureSevenZipLocation` / `SevenZipEngine.Capabilities` 已绿 |
| 跨批编译面 | 8 个在飞 Handler 的 46 个 `_host.*` 成员名在 12 个宿主分片里**逐个可解析（0 缺失）**；其他分片对搬走成员的用法只有 `.Clear/.Add/.AddRange/.Count/.ToArray/.ToList`、读、赋值四类，与属性转发兼容 |
| 语法 | 两文件 Roslyn 8.0.401 语法解析 **0 错 0 警** |
| 守卫口径 | 369 / 382 行（≤800）、各 1 个顶层类型、file-scoped namespace；结构基线未改（新文件未超 800） |
| 卫生 | 两文件 UTF-8 无 BOM、LF、无行尾空白/制表符、末尾单换行 |

## 判定：主文件已达 800 以内，类型总行数仍 > 800

主文件 369 行 ≤ 800（本批"本文件仍 > 800 就停"的停止条件未触发）；但**整个类型** 4,358 行（HEAD 口径）
/ 1,958 行（当前工作树，含并行在飞改动）仍远大于 800 ⇒ 按本批约定停在这里，不再动其他 partial。
本 ADR 只登记"做法成立、单块收益、访问器清单、剩余债务与风险"，不宣布 800 行已达成。

## 风险与集成注意事项

1. **跨批依赖（最重要）**：11 个其他分片与 8 个在飞 Handler 直接按**原名**读写主文件里的 15 个转发成员
   （`_engine` / `_prober` / `_passwordProvider` / `_limits` / `_log` / `_sourceCheck` /
   `_exactStopReasonReported` / `_rootSourcePath` / `_rootSourceCandidates` / `_crossBoundaryLinks` /
   `_consumedVolumeSources` / `_unresolvedVolumePieces` / `_supersededWorkspaces` / `_lastRunArchivePath` /
   `RemoveCrossBoundaryLinks`）与 `RetainedProcessArtifactPath` 的 `private set`。ADR 0006 的那张
   跨批依赖表（`RemoveCrossBoundaryLinks` / `_consumedVolumeSources` / `_unresolvedVolumePieces` /
   `RetainedProcessArtifactPath`）在本批**全部继续成立**；后续任何一批若删/改这些名字，必须一起改调用方。
2. **未编译验证**：按本批约定未跑 build / test / format；本 ADR 的等价证据止于 Roslyn 语法解析、
   HEAD ↔ 新文件的成员面/初始化式/doc 逐字对照、以及跨分片宿主成员可解析性扫描。0 错 0 警与测试绿
   仍以集成者重编为准。
3. **`_prober` 是 HEAD 既有未读成员**（HEAD 里只有一句注释提到它）：本批按"同名转发、行为不变"保留；
   当前树里它同样只被赋值不被读。若将来把 IDE0052 升到 warning，这条会先红（不是本批引入）。
4. **两处注释口径轻微过期**：`RecursiveWorkspaceArtifacts` / `RecursiveSourceCandidatePool` 的类注释还写着
   "账本仍是宿主 RecursiveExtractor 的字段 / 缓存与生命周期仍由宿主持有" —— 存储已搬进
   `RecursiveCoreState`；两者的代码只收 `List`/`Workspace` 参数，行为不受影响，注释留给下一批顺手修。
5. **公开 API 的文档变短**：主文件公开属性的 XML doc 只留最短契约（"完整说明见 RecursiveCoreState"），
   完整"为什么"文档 33 块逐字搬进协作类 ⇒ 宿主 IntelliSense 可读性略降，但少了重复维护。
6. **行为等价边界**：搬走的只有"存放"与两个纯转调；没有 `[CallerFilePath]` / `[CallerLineNumber]` 搬动，
   诊断 source_file 不变；`RootSourceCandidates` 的 `??=` 与旧 if 块在单实例语义下等价（本类不支持
   同实例并发跑两次 `ExtractAsync`，与搬类前同一条约束）。
7. **未提交**：两个文件（一个改、一个新建）+ 本 ADR 留在工作树，由集成者统一提交。
