# 项目规则

## 用途

- 项目用途：把一批**来源不明、后缀被改坏、加密、分卷、可能层层嵌套**的归档文件，用尽可能少的操作，变成整理好的、结果可追踪的文件。
- 适用范围：Windows 桌面工具本体（`src/ArchiveFixer`）+ 测试（`tests/ArchiveFixer.Tests`）。先服务作者本人每天真用，不为想象中的用户写代码。
- 核心循环：导入 → 按魔数识别真实格式（**不信后缀**）→ 修正伪装后缀 → 按密码本试密码 → 解压（含分卷组）→ 汇总（成功/失败/为什么）。
- 次级循环：递归展开内层包 → 结果归集到目标目录 → 按设置处理源包与过程物。

## 技术栈

- 语言：C# 12。
- 框架：WPF（GUI）；核心逻辑不依赖 WPF，为 CLI 留位。
- 目标框架：`net8.0-windows`。⛔ 不许升 net9/net10，不许用更高框架才有的 API。仓库无 `global.json`，SDK 10 构建 net8.0 正常。
- 测试：xUnit。⛔ 不新增 NuGet 运行时依赖；不用第三方 MVVM 框架（沿用现有手写 `INotifyPropertyChanged`）。
- 分层铁律：`Domain / Detection / Engines / Password / Extraction / Security / Storage` **不得引用 WPF**；GUI 只通过 ViewModel 调用它们。
- 四个禁止项（只许存在于 `Engines/SevenZip/` 内部）：① 核心模块直接拼 7z 参数 ② 直接解析 7-Zip 文本输出 ③ GUI 里判断引擎错误字符串 ④ 递归逻辑里写死某引擎的参数。

## 目录

- `src/ArchiveFixer/`：工具本体。`Domain/` 纯模型 · `Detection/` 魔数识别·后缀分析·分卷组 · `Engines/` 引擎抽象与 SevenZip/WinRar 实现 · `Password/` 密码本与候选顺序 · `Extraction/` 单层·分卷·递归·工作区·发布·冲突 · `Security/` 路径预检·预算·危险文件 · `Storage/` 源文件稳定性·空间·工作区根·密码落盘 · `Packing/` 打包 · `Services/ ViewModels/ Views/ Helpers/ Models/ Converters/` GUI 层 · `tools/` 内置 7zip 与 unrar（随包分发、带许可文本）。
- `tests/ArchiveFixer.Tests/`：xUnit。跑不起真 7z 的用例自己跳过。
- `docs/`：规格与文档（见本文件末「文档地图」）。
- `samples/`：只放生成脚本与清单，样本本体不入库。
- `scripts/`：`package.ps1`（发行包）、`installer.ps1`（NSIS 安装包）、`make-icon.ps1`。
- `installer/ArchiveFixer.nsi`：安装包脚本。⚠ 必须 UTF-8 **带 BOM**，否则 makensis 报 Bad text encoding。
- `dist/`：发行产物，不入库。

## 命令

- 构建：`dotnet build ArchiveFixer.slnx`（必须 0 错误 0 警告）
- 运行（GUI）：`dotnet run --project src/ArchiveFixer/ArchiveFixer.csproj`
- 全量测试：`dotnet test tests/ArchiveFixer.Tests/ArchiveFixer.Tests.csproj`（放后台，实测 4–6 分钟）
- 定向测试：同上加 `--filter "FullyQualifiedName~<类名>"`
- 格式检查：`dotnet format ArchiveFixer.slnx --verify-no-changes`（先看差异，不要自动改）
- 真修格式：`dotnet format whitespace ArchiveFixer.slnx`（别手工对齐）
- 重生成图标：`pwsh scripts/make-icon.ps1`（改绘制代码后跑；`-Preview <png>` 出预览图）
- 刷新绿色目录（用户的真实测试位置 `E:\ArchiveFixer\`）：构建 Release 后拷 `ArchiveFixer.exe/dll/pdb/*.json`、`docs\`、`tools\`，拷完核对哈希。⛔ 绝不碰 `E:\ArchiveFixer\data`（用户的日志/密码列表/设置）。
- ⛔ 打包只在用户明说"打包"时做（`dist\*.zip`、`setup.exe`、Release）。

## 功能

GUI 形态：6 个选项卡（① 任务 ② 解压方式 ③ 清理与删除 ④ 密码 ⑤ 打包 ⑥ 设置），菜单只有 文件/视图/帮助。

**识别**
- 按内容魔数判真实格式，不看后缀；能读出加密状态（RAR/ZIP/7z 只读头尾；读不出一律"不知道"，不猜不误报）。
- 后缀被改坏的按检测结果给出建议后缀；「修正后缀」**不动分卷名**（两道闸门：格式未知⇒不改、末尾纯数字⇒那是卷号，不许当后缀）。
- 卷名判据唯一出口 `ExtensionHelper.TrySplitVolumeSegment`（含容忍档与骨架档：卷标记里夹垃圾、归档后缀段写坏都能还原）；基名唯一出口 `FileNameHelper.TryResolveVolumeBaseName`。
- 一组分卷 = 一个任务 = 从入口包启动；同组其余卷按跟班卷收场（不算"没做成"）。
- 分卷组装判定器：六条证据（基名/卷号连续/体积规律/物理同一性/位置推定/硬链接试开）+ 四档结论（完整/缺卷/疑缺卷/判不出）；只有"可证完整"才允许搬走或删除源包。
- 缺卷**不在批首判死**：批首只记缺口，等这一批解压都跑完再补判一次。

**解压**
- 引擎优先级 WinRar → SevenZip，**先按能力筛再用优先级 tiebreak**；没装的引擎直接跳过。
- 引擎回退：`UnsupportedFormat/UnsupportedFeature/EngineUnavailable/ParserRejected/KnownCompatibilityIssue` 可换；`WrongPassword/MissingVolume/UnsafePath/NoDiskSpace/PermissionDenied/SourceChanged/DeviceOffline/UserCancelled/OutputLimitExceeded` **不换**。
- 同一个包在 7-Zip 上"每个密码都错"、在 WinRAR 上第一条就对 ⇒ 那是引擎吃不下，不是密码不对：密码类失败且本机有 WinRAR 时用 `WinRAR.exe` 把同一批候选再试一遍（必须带 `-cfg-`，且命中判据是"退出码 0 且产物真多出来"）。
- 递归展开默认档 =「展开所有分支」；上限四条（单文件/总大小/文件数/展开比）是用户设置，默认 64 GiB / 512 GiB / 20 万 / 1000 倍；嵌套层数默认 5；一键处理每批轮数 ≤10（唯一出口 `OneClickCoordinator.RoundLimit`）。
- 内层包本身是"双面文件"（视频/图片 + 尾部归档）时，先按偏移抠出来再解；尾部只有一个原样存条目时走流式近路（零解压调用）。
- 递归层与单层路行为必须对齐（手动密码进递归候选、加密头包结论、单文件上限、可疑条目报告、工作区清理校验等）。

**落点与整理**
- **落点 = 「谁可以输入密码，就解压到谁那边」的那个包所在的那一层** + 一层包名目录。四族入口包：跨盘 ZIP = 末片 `X.zip`（`.z01` 只是第 1 片）；ZIP 通用分片 = `X.zip.001`；7z 分卷 = `X.7z.001`；RAR 分卷 = 第 1 卷（`X.part1.rar` / 老式 `X.rar`）。判据唯一出口 `Detection/GroupVolumeDirectory.cs`。
- 入口包还压在别的包里时，沿产出链**一层一层往上叠**算它将来落在哪一层（有防环上限）；⛔ 不按任务表顺序、不按"缺卷那一单所在目录"选。
- 落点最少两层文件夹：最外层 = 包名目录，最里层 = 最后一个内层包层；省层只能省中间的内层包层；普通文件夹永不摊平（唯一出口 `Extraction/PackageLayerRules.cs`）。
- 落点这一档是"记忆"的：用户上回选的那一档，下次打开就是那一档。

**源包与过程物**
- 默认档 **不改不删源文件**。例外：**成功 + 输出校验通过 + 未取消 + 属于本任务分卷组**时源包（整组）移入 `其余物`；失败/部分完成/取消 ⇒ 源包原地不动、其余物不生成。
- 要连源包一起删 = 两档组合：源包操作「放入其余物」+ 删除操作「彻底删除/回收站」。
- 逐层回收：续解链每一层「定稿 + 校验通过 + 未取消 + 可证完整」之后当场按删除档处理这一层的过程物；最外层源包留到链尾。只在「彻底删除」档生效。
- 过路层（只出过程物、那份过程物已被下游接手）的其余物也要按档处理；判据 = 没失败没取消 **且** 其余物里每个文件都已被下游接手。
- 其余物空壳（递归看一个文件都没有）就地永久删掉，不进回收站。
- 半套分卷不许进可删的其余物（`RestVolumeCompletenessGate`）；其余物删除前六道门槛一条不许绕过。

**工作区**
- 位置只有一个来源 = `<目标目录>\.ArchiveFixer.work`（隐藏）。⛔ 用户没有指定工作区的权力；拿不到目标目录一律报错指路，绝不回落程序目录/C 盘/源卷根/`%TEMP%`。
- 默认全清（含空壳）：失败/取消/部分完成收尾把这一单自己的工作区整份删掉；只有「失败时保留中间产物」打开时才留现场。
- 三条不随设置变：成功路径的清理口径不改、源包原地不动、已定稿搬出去的内容物不受影响。

**安全与预算**
- 解压前预检条目名（拒 `..`、绝对路径、盘符、UNC、`\\?\`、混合斜杠），解压后校验落点；Windows 保留名、结尾空格/点、备用数据流一律清洗后再落盘。
- 外部进程：`ArgumentList` 传参（⛔ 禁止拼 cmd 字符串）、重定向输出、超时、取消、关句柄、只杀自己启动的 PID 及其子进程（⛔ 禁止按进程名批量杀）。
- 源文件快照（大小+修改时间，分卷整组）：处理中变化即停下报"源文件已变化"；识别完成即拍快照，所有解压入口唯一收口比对，递归每层开工前再问一次。
- 空间不足模式一个布尔管四件事：并发、排序、其余物强制删除、定稿+校验通过后当场删源包。启动前空间不够不是终态，进"等空间"名单，每跑完一个重试一次。

**汇总与显示**
- 状态字符串统一引用 `Models/StatusText.cs`，⛔ 禁止手写中文字面量；新增状态要同时更新 `StatusText` + `StatusToBrushConverter` + `TaskSummaryService`。
- 统计不依赖中文比较：机器终态用 `TaskOutcome` 枚举。
- 批末色带唯一出口 `BatchSummarySeverityRules`（蓝=全成功/橙=有部分完成·跳过·取消·没轮到/红=有失败）；批末诊断唯一出口 `BatchSummaryDiagnosticsRules.Build`（12 档、每组最多 3 个名字）。
- 「一批任务怎么数」唯一出口 `Models/BatchOutcomeTally`：批末「本批汇总」、一键汇总行、日志导出头部三处转调；「未处理」= 总数 − 各分项。
- 跟班卷（同组后续卷）在四处消费点都不算"没做成"。
- 密码类失败批末必须单独指路，文案必须写"可能"。

**其他**
- 一键处理批中间零弹窗（唯一例外 = 批末汇总框）；手动「只解压」才弹确认。
- 子窗一律能缩到任务栏（唯一出口 `Views/WindowMinimizePolicy.cs`，只做"构造时 `ShowInTaskbar = true`"这一件事）；⛔ 不许任何窗口写 `ShowInTaskbar="False"`、不许置灰最小化、不许挂 `StateChanged` 强行还原。
- 日志导出默认目录不得是"程序自己会整份删掉"的地方（其余物/工作区）。
- 说明窗内容 = `HelpContent.Features` + `Glossary`。

## 项目专属规则

**口径与判据**
- **一处改动涉及多条解压路（单层 / 递归 / 轮次续解）时，必须三条都落**；只接一条按未完成处理（用户原话：「这个我不是说了要同步吗」）。唯一例外：补齐会放宽任何判据 ⇒ 那要先问用户。
- **不可逆路径**（删源包、移入其余物、落点/套层、工作区、递归展开、分卷组归类）的改动一律**按大改动对待**：跑全量 + `dotnet format` + `--no-incremental` 重编。
- 改不可逆路径前先过 `archivefixer-unsafe-paths` 那份闸门清单；用真样本做只读验收走 `archivefixer-real-samples`；交付/刷新绿色目录走 `archivefixer-delivery`。
- 同一件事的真值只允许有一个出口（轮数、上限、落点、容器、候选顺序、命名…）；发现第二份实现按缺陷处理。
- 要删/要写盘的动作判据只准读事实（枚举终态、结构化错误码），⛔ 不许比中文文案；判不出 ⇒ 什么都不做。
- **一行"你的文件"经历过的变换只从程序记录拼**（改名：`RenameService.UpdateTaskRenameSuccess` / 协调器整组改名那条记录里的 `旧名 → 新名`；续解：递归报上来的 `第 N 层：<包>` 那一帧；接手：收场记下的是谁解开整组）——⛔ 不许复述、不许推算、不许替程序补细节。唯一出口 `ArchiveTask.DisplayTransformationText`。
- **后缀/卷名判定的第一步 = 先剥掉非 ASCII 杂质（含中文）再判**；统一档与分卷专属档都要，细节见 `docs/需求变更.md`。
- **写给人看的名字只有一个出口** `ArchiveTask.LogName`（= ①页那一行的显示名）：日志前缀、跟班文案、详情窗、复制任务信息都用它；⛔ 裸 `FileName`/`CurrentPath` 只留给内部判据与原始值那几行。
- 新指令覆盖旧指令：按新口径改旧用例/旧文档时**必须汇报四件事** —— 冲突在哪（文件:行号）、怎么判的、还有哪些地方没同步、哪些仍是实现缺口。

**落点与分卷（本项目特有判据）**
- 落点这一档**只影响「未指定位置」那一档的目标根**；「指定位置」那一档一个字不动。
- 跨盘 ZIP 族的头尾在名字编号里是反的（`.zip` 是末片、`z01` 才是第 1 片）⇒ 说"缺哪一片"必须分族，那一族缺 `.zip` 时说「缺的是**末片**」。
- 「这几片是不是同一个包」有两把尺子，**宽窄不同是刻意设计，⛔ 不许顺手统一**：折叠尺（组卷，窄）与搬运删除尺（改名保护 + 整组搬运，宽）。
- 卷名/后缀类改动：**改回标准名只动卷标记那一段**（基名、族、卷序、内容一个字节不动、可逆、绝不覆盖）；容忍/骨架命中的卷名**任何一卷都可改**（⛔ 不许只修第 1 卷）；⛔ 干净的名字绝不许改成脏名字。
- 修分卷名必须**全成或全不成**，中途失败倒序改回原名；改完同步全表任务路径 + 快照。
- 收卷临时物不许留在用户源目录：源目录里只许多出"接片时另起的规范卷名"（零字节硬链接），⛔ 不许出现工作区壳或临时名。
- ⛔ 用户源目录里的片**只硬链接、不改名不搬**（就地改名试过四次都打断管线，未找到第五处引用之前不许再改这一行）。

**工作区与其余物**
- 工作区根 = 这一批的目标目录里；挑锚点时要跳过"入口包还没解出来"的分卷组单（它们的目标目录只是占位值）。
- 「接片」那一档：⛔ **本单自己暂存目录里的那一份只建链接、绝不搬走**（搬走 = 把自己这单唯一的产物拿走 ⇒ 假「解压失败」）；别的过程物目录（递归层 / 其余物）照旧用移动。唯一出口 `ShouldMovePieceIntoGroup`。
- ⛔ 分卷的落点那一份**只在"接片接不进去"时才补**（先接、接不进再补）；接进去之后不许再留多余名（唯一出口 `PublishUnresolvedPieceLandingCopies`）。
- 批末"每一单自己的其余物"按档处理时，只碰**本根任务那棵树里、且自己没失败没取消**的那几单（对全表扫一遍会把三条红线用例打红）；"在不在那棵树里"唯一出口 `IsRestInsideRootTree`（⚠ 参数顺序：第一个是根）。
- 「只出过程物」那一单（停在中途、内容由别单解开）的其余物也要按档删：证据来源 = **接手它的那一单**的完整性结论（唯一出口 `PurgePassThroughRestOfSettledProducer`，幂等）；⛔ 六道门槛一条不绕过，那条证据不许写回它自己的账。
- 日志按级别上色（红 ERROR/黄 WARN/黑 INFO）；**成功任务只留一行** ⇒ 需要看得见的诊断必须写成 WARN。

**验证与基线（本项目数字）**
- 构建 0 错误 0 警告；`dotnet format --verify-no-changes` 通过。
- 全量测试基线：**2874 条（2861 通过 / 13 跳过 / 0 失败）**。⚠ 跳过里含"真样本夹具不在了"与"本机没 WinRAR"两类，⛔ 不许读成"验过了"。
- 行尾：仓库工作区是 **CRLF**（`core.autocrlf=true`）。⛔ 别用 PowerShell `-join "`n"` 整份重写 `.cs`（写出 LF ⇒ `dotnet format` 报一串 WHITESPACE）；已写出就按 CRLF 重写一遍再验。
- 已知 flaky（并发/计时相关，先单跑确认，⛔ 别改断言）：`SpaceTightModeTests.换输出位置_二页那颗选择按钮也会触发空间体检`、`SpaceTrendMonitorTests.周期循环_按间隔采样_取消后立刻停`、`SecurityGuardTests.CheckBeforeExtract_NotEnoughFreeSpace_IsRejectedWithNumbers`、`EngineRoutingTests.MainViewModel把分派引擎接进流水线`（单跑红/全量绿）。
- 回退代码后必须 `--no-incremental` 重编，否则跑的还是红检那一份。
- 样本纪律：样本本体绝不进仓库；`H:\` 上的原件只读；副本不在就跳过并说明。

**文档**
- 改动写进 `修改日志.md`（一条一行流水账）；每轮口径与账目记账；历史现场与逐条推演写 `docs/真机事故复盘.md`。
- 面向用户的字符串里不许写 Markdown（`**加粗**` 会原样显示），强调用「」；守门用例 `UserFacingTextTests` 全量扫描 `.cs` 与 `.xaml`。
- 新增状态/文案必须同时更新 `StatusText`、`StatusToBrushConverter`、`TaskSummaryService` 三处。
- `docs/需求变更.md` 只追加；历史记录里的旧路径按原样保留，不回改。

**文档地图**
- `docs/需求书.md` 需求全集（冻结）· `docs/需求变更.md` 变更追加 · `docs/需求评审与考古.md` 为什么这样定 · `docs/使用说明.md` 面向使用者 · `docs/功能一览.md` 功能详细版 · `docs/设置项.md` 设置项与键名 · `docs/输出与整理模型.md` 落点契约（§1.1.1 = 四族入口/落点）· `docs/分卷组装算法.md` 判定器算法与证据 · `docs/检验等级.md` L0–L6 阶梯 · `docs/引擎与外部工具.md` 引擎与许可边界 · `docs/打包功能.md` 打包设计 · `docs/真机事故复盘.md` 历史现场（含旧版项目 AGENTS 全文附录）· `docs/部分完成发布方案.md` 部分完成发布 · `docs/人工测试清单.md` 手工测试 · `docs/界面重构方案.md` 界面方案 · `docs/WinRAR功能参考.md` 对照笔记。

## 待确认

- 用户拍板的五条 CCCC 解压链口径里，**第 4 条「去掉暗链当出口」仍未完成**：源片就地改名会打断管线（四次试做失败，已排除任务账路径/借片账键/收卷判据/源文件快照四处），需要一次真机复跑日志或授权加临时排障日志定位。
- 其余四条（续卷改名 / 落点逐层叠 / 其余物逐层处理 / 守门用例与基线）已落地，但**均未在真机上复跑验证**（真机样本只读，只能由用户重跑）。
