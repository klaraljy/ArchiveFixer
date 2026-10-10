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
- 全量测试基线：**2897 条（2884 通过 / 13 跳过 / 0 失败）**。⚠ 跳过里含"真样本夹具不在了"与"本机没 WinRAR"两类，⛔ 不许读成"验过了"。
- 行尾：仓库工作区是 **CRLF**（`core.autocrlf=true`）。⛔ 别用 PowerShell `-join "`n"` 整份重写 `.cs`（写出 LF ⇒ `dotnet format` 报一串 WHITESPACE）；已写出就按 CRLF 重写一遍再验。
- 已知 flaky（并发/计时相关，先单跑确认，⛔ 别改断言）：`SpaceTightModeTests.换输出位置_二页那颗选择按钮也会触发空间体检`、`SpaceTrendMonitorTests.周期循环_按间隔采样_取消后立刻停`、`SecurityGuardTests.CheckBeforeExtract_NotEnoughFreeSpace_IsRejectedWithNumbers`、`EngineRoutingTests.MainViewModel把分派引擎接进流水线`（单跑红/全量绿）、`AsyncDeadlockGuardTests.递归解压_在单线程同步上下文里同步等待_不会死锁`（2026-10-08 实测：全量 1 红 / 单跑 2 条全绿 ⇒ 负载下的计时类 flaky）。
- 回退代码后必须 `--no-incremental` 重编，否则跑的还是红检那一份。
- 样本纪律：样本本体绝不进仓库；`H:\` 上的原件只读；副本不在就跳过并说明。

**长任务目标（goal）的收口纪律**
- ⛔ **自动续跑轮里不要只回一句状态文字就结束** —— 那等于告诉 harness"这轮干完了、目标仍 active"，它会**合法地再派下一轮**（实测：连续十几轮原地打转，最后是用户喊停才发现）。
- 自动轮**只有两个合法出口**（出处 `dsh-tool-goal/lib/index.js:356` 的 `completionAuthority`）：`complete`（目标真达成）与 `blocked`（同一阻塞条件已持续 ≥`blockedAfterConsecutiveRounds`，默认 **3** 轮，`:115`）。**这一轮推不动就调 `blocked` 并写明具体阻塞条件**，别拖。
- `pause` / `resume` / `edit` 在**自动轮里必然被拒**：它们要求当前回合含 `source.kind === "user"` 的消息（`:49-52`、`:343`、`:349`），而自动轮的消息是 `source.kind === "goal"`（`:55`）；且 `:352` 写死"模型不能恢复被暂停的 goal，必须用户来"。⇒ **要停只能靠 `blocked`/`complete`，或用户直接说**。

**文档**
- 改动写进 `修改日志.md`（一条一行流水账）；每轮口径与账目记账；历史现场与逐条推演写 `docs/真机事故复盘.md`。
- 面向用户的字符串里不许写 Markdown（`**加粗**` 会原样显示），强调用「」；守门用例 `UserFacingTextTests` 全量扫描 `.cs` 与 `.xaml`。
- 新增状态/文案必须同时更新 `StatusText`、`StatusToBrushConverter`、`TaskSummaryService` 三处。
- `docs/需求变更.md` 只追加；历史记录里的旧路径按原样保留，不回改。

**文档地图**
- `docs/需求书.md` 需求全集（冻结）· `docs/需求变更.md` 变更追加 · `docs/需求评审与考古.md` 为什么这样定 · `docs/使用说明.md` 面向使用者 · `docs/功能一览.md` 功能详细版 · `docs/设置项.md` 设置项与键名 · `docs/输出与整理模型.md` 落点契约（§1.1.1 = 四族入口/落点）· `docs/分卷组装算法.md` 判定器算法与证据 · `docs/检验等级.md` L0–L6 阶梯 · `docs/引擎与外部工具.md` 引擎与许可边界 · `docs/打包功能.md` 打包设计 · `docs/真机事故复盘.md` 历史现场（含旧版项目 AGENTS 全文附录）· `docs/部分完成发布方案.md` 部分完成发布 · `docs/人工测试清单.md` 手工测试 · `docs/界面重构方案.md` 界面方案 · `docs/WinRAR功能参考.md` 对照笔记。

## 待确认

- **📌 用户 2026-10-10 拍板（待做）：⛔ 禁止解压真正的 APK —— 有些是用户真的要转移到手机上安装的。**
  来源：问题单 `E:\用户日志反馈\问题单\2026-10-06-程序不该碰真正的APK（被解开并递归进内部）\问题单.md`
  （真机：`魔方.apk` 被当普通 ZIP 解开、还在 `…\魔方.apk\lib\arm64-v8a\` 里建工作区并继续递归，
  派生出一个注定失败的 `libgojni.so` 任务 + 一句错方向的"补密码"）。
  口径：**判据 = 内容证据（ZIP 里同时有 `AndroidManifest.xml` 与 `classes.dex`）**，⛔ 不看后缀；
  行为 = **不解压 / 不递归进它内部 / 不改名**（改名那条是既有红线）+ 一句人话说明；
  ⛔ 三条路（单层 / 递归 / 轮次续解）都要落，判据同一个出口。
  ⚠ **边界待用户再确认**：只 APK（`.apk`），还是连 `.aab` / `.ipa` 这类"应用安装包"一起不碰？
  （`ExtensionHelper.ZipContainerExtensions` 里另外那 18 个 `.jar/.docx/.epub/…` **默认不动** —— 它们是真归档。）
- **✅ 已修（2026-10-10，用户报"安装器里面其他用户反应有乱码"）：安装包中文乱码的两条根因。**
  ① **v0.1.0 那份安装包是 ANSI 构建**：`git show v0.1.0:installer/ArchiveFixer.nsi` 里**没有** `Unicode true`
  （`git log -S 'Unicode true' -- installer/ArchiveFixer.nsi` 显示它是后来才进的）⇒ 非中文系统上向导与
  中文文件名（`使用说明.md` / `使用说明.lnk` / `卸载 ArchiveFixer.lnk`）必然乱码；**v0.1.1 起已有 `Unicode true`**
  （`git show v0.1.1:` 逐字可见）⇒ 新版不再有这一条。
  ② **版本资源只写了 `/LANG=2052`**（中文），非中文系统的资源管理器「属性 → 详细信息」拿不到英文块时
  会按系统代码页解那几行中文 ⇒ 乱码。**已补一组 `/LANG=1033` 英文兜底**（七个字段一一对应；
  中文系统照旧优先取 2052，行为不变），并重编安装包自检通过。
  ⚠ 仍未核到"朋友到底在哪一屏看到乱码" —— 若 v0.1.1+ 仍有人报，请给一句：哪个界面 / 哪段文字 / 哪个版本。

- **✅ 已解决（2026-10-10 二修，用户真机复跑逮到第一版没生效）：产出方那一份"已经落地的片"按删除档收掉。**
  **第一版为什么没生效（根因定案到行）**：写入点要求"这一片在**调用方给的那一单**的落点树里"，
  而真机那条路（批量收片）给的是**被重判的那一单**（`TryGatherGroupPiecesInto`，`:9764` 传 `task`；
  调用点 `:10624`），它的 `OutputPath` 那一刻是空的 ⇒ 一个字都没记；收场那侧又按"哪一单"查账 ⇒ 没人来收
  ⇒ `111(3)\111(3)\111.z02` 200 MiB 留在盘上（他 20:09 那次的完整日志里没有那一行删除）。
  **修法（判据改成与"谁在调"无关）**：产出方按**盘上事实**认（`ResolveLandedPieceProducer`：先看调用方
  给的那一单，不是它就在全表里按 `OutputPath`/`ContentDirectoryPath` 找"这一片落在谁的落点树里"），
  收场**按"组"收**（`PurgeGroupLandedPieces(consumer, groupBaseName)`，挂在 `SettleGroupPieceProducers`
  里那份"谁吐过片"的账**之前**，幂等）。守门 `SiblingFolderVolumeGatherTests.真机路_接片的人不是产出方_
  产出方那一份照样要收掉`（夹具把"接片时传谁"显式摆成消费方），**红检成立**（退回老判据 ⇒ 1 红）。
  ⚠ **教训（写死）**：**无头夹具跑绿 ≠ 真机那条编排也绿** —— 夹具走的是"产出方自己 post-process"那条路，
  真机并发 4 时走的是"批量收片"那条路。凡"随调用方参数而变"的判据，夹具必须把**两种调用形状**都摆出来。
- **✅ 已定（2026-10-10，用户拍板：「算了不弄这个，麻烦而且完全没必要，就不删除可以留着」）：源树里的空目录残留不清理。**
  现场（他那档设置跑完，无头夹具实测）：源目录树里留下 11 个空壳 —— `111\`、`111(2)\`、`111(2)\111(2)_\`、
  `111(3)\`、`111(3)\111(3)\`、`111(4)\`、`111\111\`、`111\111\111\`、`111\111\111\111\`、`111\111\111\111\111\`、`伪装了四个\`
  （文件一个字节都没留，只剩目录；工作区壳 `.ArchiveFixer.work` 已清）。
  ⇒ **不删、就这样留着**；⛔ 别再为它写清理逻辑（用户原话：不必要）。
- **✅ 已解决（2026-10-10，用户拍板）：「半套分卷」闸门的判据从"只比基名"改成"基名 + 族"。**
  现场（无头夹具 + 你那档设置，空间不足模式）：`111.rar` 的源包**没被当场回收**，被这道闸门以
  「同组的另一片 `111.zip` 还在成品目录里（两边基名都是「111」）」误拦 —— 而 `111.rar` 是 RarOld 族、
  `111.zip` 是 ZipSpanned 族，按唯一那把"同一组 = 族 + 基名"的尺子**不是一组**（守门
  `StalledGroupRegistrationTests.cs:42` 逐字钉着）。修法：`RestVolumeCompletenessGate` 基名命中后再问
  `VolumeGroupDetector.AreProvablyDifferentFamilies`（新出口）——**可证跨族才放行、判不出照旧拦**；
  拦截文案补「、同一族」。守门 `RestVolumeCompletenessGateTests` 新增两条（跨族放行 / 认不出族照旧拦），
  **红检成立**；E2E：改后逐字`111.rar：空间不足模式 —— 定稿 + 校验通过，已立刻永久删除源包 1 个，收回 48.4 MB`，
  同时真半套照旧拦（`111.zip` vs `111.z03`，同族同基名）。
  ⚠ 连带改了 4 条既有用例的**夹具前提**（不是断言）：`ChainLayerReclaimTests` 用例 G/H/H2 与
  `PackageLayerRulesTests.同组那一片名字认不出…` 原来把"同组那一片"写成 **zip 族**的名字（`mid.z删除ip` /
  `Y.z删除ip`），而它们的组是 **7z 数字分卷族** ⇒ 探针实测可证跨族、前提不成立；换成同族坏法
  （`mid.7z.0删除02` / `Y.7删除z`，均经探针两问实测：闸门拦下=True）。⛔ 别改回 zip 族那些名字。
- ⚠ **仍是实现缺口（只报未改，⛔ 没有能红的复现不许动）**：`ProcessArtifactLayout.ResolveOwnFileOnDisk`
  （`:1623`，"改名之后找回自己那一份"）也只比**包基名** ⇒ 同一目录里若同时有 `X.rar` 与 `X.zip`，
  可能认到另一个包（那条路会把源包搬进其余物）。
- **✅ 已解决（2026-10-10，第二大步 S1）：被整组接手的那一片，产出方那一份"留与不留"不一致（用户点名的那条）。**
  用户原话：「这个 `111.z02` 是第一大步的内容物，相当于第二大步的原包，第二大步都已经成功了为什么还要留着，
  而且 `111.z01` 的结构和他是一样的，为什么 `.z01` 删掉了，而这个留着」。
  **机制（定案到行）**：差别只在时序 —— 接片走"移动"还是"建硬链接"取决于那一片**此刻恰好在哪**
  （`ShouldMovePieceIntoGroup`）：`111.z01` 那一档接片时还在过程物目录里 ⇒ 移动 ⇒ 产出方不留；
  `111(3).rar` 的 `111.z02` 定稿早于接片（他日志行 123 早于 183）⇒ 建硬链接 ⇒ 组层那份随其余物删掉、
  **产出方这份留在盘上**。
  **修法**：判据改读"**这一片是不是已经被整组接手**"这条事实 —— 接片唯一出口
  `TryAdoptUnresolvedVolumePiece` 里记一笔（唯一写入点 `RememberAdoptedLandedPiece`：只在
  "没被搬走 + 落在产出方自己的落点树里 + 不在过程物目录里 + 不是产出方自己的源包路径"时才记），
  整组收场唯一实现 `SettleOneConsumedPieceTask` 里按删除档收掉（唯一执行体 `PurgeSettledProducerLandedPiece`
  → `RecycleBinService`）。**只在「删除操作」档生效**；未取消 + 接手方机器终态 = 完成 + L4 可证完整 +
  名字仍属这一组 + 仍在当时那个落点根之内，否则**一个字节都不动**。
  守门 `SiblingFolderVolumeGatherTests.整组接手之后_产出方那一份已经落地的片按删除档收掉`（+ 一条「不动档」对照），
  **红检成立**（注释掉那一调 ⇒ 当场红在自己的断言）；真机夹具 E2E：⑥ 残留清单由
  `111(3)\111(3)\111.z02 200 MiB` 变空，日志逐字「这一片已经随整组解开，产出方这里那一份 111.z02 按「删除操作」彻底删除」。
  ⚠ **未验**：用户 GUI 上还没复跑（真机只能他自己跑）。
- **✅ 已解决（2026-10-10，用户拍板「两句都写」）：自己也跑成了、产物又是分卷组那一片的单，状态格现在两个事实都写。**
  实例：`111(3).rar` 自己「已完成 1 层递归解压 + 定稿完成 + 输出校验通过」（产物 `111(3)\111(3)\111.z02` 在盘上），
  批末那一站又把它按"片被整组接手"收场（`ExtractionCoordinator.cs:10717` 的守卫只在"还不是跟班卷 **或** 没成功"时收场；
  收场里 `Outcome = Succeeded`（`:10733`）+ `MarkSkipped`（`:10746`）⇒ `Status` 成"跳过"，显示层 `ArchiveTask.cs:989` 据此换字）。
  **修法**：收场那一刻把「自己那一趟跑成了」抄成持久事实 `ArchiveTask.OwnRunSucceededBeforeGroupSettlement`
  （判据与批末账目同一把尺子 = 当时**不在**"缺卷待批末判"名单里；⛔ 该事实**只喂显示**，统计/删除/清单都不读它），
  状态格改用 `StatusText.ExtractSucceededThenPieceSettledWithGroupFormat`（复用既有两句措辞）。
  守门 `DisplayIdentityTests.自己也跑成了又被整组接手_状态格两个事实都写`（含两条对照），**红检成立**
  （撤掉那一档 ⇒ `Expected start: "解压成功"` 当场变红）；真机复验后那三行分别是
  「这一片随整组解开…」（`111.rar`/`111(2)_.zip`）、「解压成功；这一片随整组解开…」（`111(3).rar`）。
- **✅ 已解决（2026-10-10，用调试器断点定案 + 真机复跑）：批末"缺卷名单"把不是这一组的单登记进去，连带把做成的单算成跳过。**
  两处都在 `ViewModels/ExtractionCoordinator.cs` 与 `Models/BatchOutcomeTally.cs`：
  ① **`IsStalledGroupOwnGroup`（`:10052`）原来只比基名** ⇒ `111.rar`（**RarOld 族**）解出的 `111.zip`（**ZipSpanned 族**）被当成"它自己那一组" ⇒ 批末拿它的名字去问一个**永不可能凑齐**的老式 RAR 组 ⇒ 同一单既显示「这一片随整组解开（由「111.z03」那单解的）」，又被扣 `[ERROR] 分卷缺失，未开始解压` + 「按部分完成记」。
  **修法**：判据转调唯一那把"同一组"的尺子 `VolumeGroupDetector.BelongsToSameGroup`（**族 + 基名**）。守门 `StalledGroupRegistrationTests`（3 条；红检成立：改回"只比基名"⇒ 当场变红）。
  ② **`BatchOutcomeTally` 的"缺卷待批末判"那一支原来无条件 `skipped++`** ⇒ 跟班卷被印成「跳过 3」（2026-10-01 那条投诉在另一条支上重现）；且 `RecheckDeferredVolumeDeficits` 里"这一组凑齐 ⇒ 交 `toRun` 去跑"那一支**没清 `IsVolumeDeficitDeferred`**（兄弟支 `:10464` 是清的）⇒ 真机 `111.z03` **成功**了却被算进「跳过」，批末印出「成功 1 / 跳过 1」。
  **修法**：那一支**补跟班卷分档**（⛔ 仍不看 `Outcome` —— "还在名单里就按跳过数"由 `DeferredVolumeStatusTests.cs:121` 钉着，不许放宽）+ 协调器那一支**补齐清零**。守门 `BatchOutcomeTallyFollowerTests`（4 条）。
  **真机复验**：批末 =「成功 2 / 失败 0 / 跳过 0，另有 2 个是同一分卷组的后续卷」；`[ERROR]` 行为空；①–⑤ 全绿；四个输入包按档删除、用户自己那片 `111(3)\111(3)\111.z02` 原地保留。
  ⛔ **走过的弯路（如实记账）**：先在 tally 里加 `&& Outcome == Pending` 的门 ⇒ `DeferredVolumeStatusTests.递归中途撞上缺卷_批中间只许说跳过_批末才落真结论`（`:121`）**当场变红** ⇒ **是我改错了层**，已回退、改到协调器那一支。
- ⛔ **取证已收尾，临时排障行已删（2026-10-10）**：`TryAdoptUnresolvedVolumePiece` 里的 `收片排障：…` 与 `PlanFinalLayout` 里的 `[排障·过程物]` 都已删除 —— 它们要回答的问题都有结论了（真正的病灶是"基名尺子 + 批末名单"，不是"搬还是链接"）。§「去掉暗链当出口」那一条仍**未做**（源片就地改名四次试做都打断管线），但从那以后整组已能正常解开，优先级待定。
- ⚠ **上面那条"凭空多套一层"的守门文件 `UnopenedInnerPackageLayerTests.cs` 已不存在**（基线回退时随改动一起撤了）。修法本体仍在（`ResultFinalizer` 两支的"不套层"判据），且**已真机复验**（`…\111\111\111\111.zip` 与 `111(1).zip` 都不再出现）。
- **✅ 已解决（2026-10-10）：真机 EEEE「同一份入口包在盘上出现两个副本」**（用户报的症状措辞「两个 `111.zip`」）。根因**定案到行**、**不是**硬链接、**不是**探针盲区：`Extraction/ResultFinalizer.cs` 的 **`ResolveWrapperName`（`hasInnermostPackage` 那一支）与 `Plan` 末尾"最里层不许被吃掉"的补回闸门**，在**最里层那个内层包从来没被打开过**（`111.rar` 里的 `111.zip` 是跨盘 ZIP 末片，整组缺 `111.z01/.z02/.z03` ⇒ 引擎一次都没调）时，照样按它的基名在落点里**凭空多套一层** ⇒ 同一个包被搬成 `…\111\111\111\111.zip`。修法 = 两支都加同一条**只读树上的事实**（那一层要么有 `<包基名>\` 目录、要么"内容物根上唯一那个文件的归档基名就是内层包基名"）⇒ 否则不套层。⚠ 原守门文件已撤，改由真机环台复验兜住。
- ⚠ **判据 ④ 的读法已订正**：④ =「一轮干净跑完之后，盘上不再残留会被当成新原包的产物」，而**用户流程是"删掉输出目录 → 从备份拷回原包"**（他原话：「我每次测完都会将文件删除，然后从外面重新复制一份」）⇒ ④ 的严格版探针（把输出目录递归全量再导入）**比用户流程更严**，它测出来的"任务数增加"里绝大多数是**用户自己那几个包的第二份拷贝**（`EEE` 备份里就有 `111(2)`/`111(3)`/`111(4)` 三个散包）。③ 达成（不再有同一份的两条名字）是这一档的实质判据。

- 用户拍板的五条 CCCC 解压链口径里，**第 4 条「去掉暗链当出口」仍未完成**：源片就地改名会打断管线（四次试做失败，已排除任务账路径 / 借片账键 / 收卷判据 / 源文件快照四处）。⚠ 2026-10-09 只读取证把范围收窄了：四次试做都是把"建链接"换成 `TryMovePieceIntoGroup`，而它的 target 是**这一组的落点层**（`ResolveBatchGroupDirectory`），**不是片自己所在那一层** ⇒ 用户的源文件被**搬离他自己的目录**，而用例期望的是"同目录内改名"（`SiblingFolderVolumeGatherTests.cs:1178-1182`、`:1430-1434`）。⇒ **下一次试做的第一件事 = 同目录内 `File.Move(piecePath, Path.Combine(Path.GetDirectoryName(piecePath), canonical))`**；若仍红，次选怀疑对象是 `RecursiveExtractor.RootSourceCandidates`（每趟只算一次的惰性池，`:586`）+ `ArchiveTask.OriginalPath`（冻结的导入路径，`Models/ArchiveTask.cs:166`，而 `EnumerateTaskPaths` 把它排在最前）+ `IsBorrowedPieceTask`（裸字符串、没有 `FileIdentity` 兜底，而兄弟判据 `TryResolveConsumedByAnotherTask` 有）。
  **➤ 2026-10-09 已按这条路加上取证探针（只加日志、⛔ 不改判据）**：`ExtractionCoordinator.TryAdoptUnresolvedVolumePiece` 里那一行 `收片排障：…`（**只在「详细日志（排查用）」打开时写**，默认关 ⇒ 默认档一个字节都不多写）。它把推断的判据直接打出来：**`同目录=False` ⇒ 推断成立**（改法就是同目录内改名）；`同目录=True` 却仍打断管线 ⇒ 改查次选那三个。**用户下一次真机复跑时打开「详细日志」即可定案**；⛔ 该行用完就删。
- **本段的 EEEE ② / 链尾 `toRun` / EEEE ③ / 两处终态缺口 都已落地并有守门用例，但一条都没在真机上复跑验证**（真机样本只读，只能由用户重跑）。⚠ 下面每条各自写明了它自己**还差什么**，⛔ 别把"跑过全量"读成"验过了"。
- **EEEE ②（`111.zip` 被判成过程物收进其余物）已修（2026-10-09）**：①页行快照那一半早已修（2026-10-08，快照收成唯一出口 `AppendTaskRowSnapshot`）。**根因那一半本轮定位到行**：`IsEngineOutputFile` 的判据读的是"同一个相对路径在层目录里也还在"，而发布是**就地替换 + 移动** ⇒ 那个事实已被发布本身抹掉，只有"中途停链"那一档暴露。修法 = 加第 ③ 条判据问 `RecursionLayerReport.ManifestEntries`（每层解压前列目录的逐条清单，不受搬运影响）。守门 `DisguisedBodyNameTests.定稿_产物已被发布搬走_层清单照样能证明它是本链产出`，**红检成立**。⚠ 迁就它的**临时排障行**（`[排障·过程物]`）还留在 `PlanFinalLayout` 里，等真机复跑确认后删。
- **链尾 `toRun` 已落结论（2026-10-09）**：`FinalizeDeferredVolumeDeficits` 改成返回那几单并就地落 `Skipped` + 新文案（老写法只打一行日志 ⇒ 它们停在「等待解压」+ `Pending`，批末汇总算进「未处理」）。⚠ **不属于放宽"批中间不落结论"**：走到那一行时整条链已经跑完。⚠ 尚未真机复跑。
- ⛔ **A1 方向试做已撤回（2026-10-09，如实记账）**：把 `isProcessArtifact` 一刀切成 `false` 会让全量 **33 红**，因为它打掉三处闸门（`ExtractionCoordinator.cs:4731`、`:4809`、`:4772`）＝ 25 GB 那次事故的防线，还顺带打死 `MoveSourcePackageRest` 的延期分支（`ExtractionCoordinator.cs:2160` 的 `commit.MovedContentCount == 0` 不再成立）。根因是那一族用例**不传 `engineOutput`**（`VolumeGroupDeletionSafetyTests.cs` 七处）⇒ "**判不出来路 ⇒ 什么都不做**"这条兜底一个字都不能放宽；⛔ 别再把"本链产出 = 内容物"往"stage 里一切都是内容物"那个方向推。
- **两处"跳过了却被算成别的"的机器终态缺口（2026-10-09 定位到行并已修）**：
  ① `ExtractionCoordinator.cs` 未识别那一支（魔数不认、引擎也列不出来 ⇒ 写 `Status = Skipped` + 「不是压缩包：7-Zip 也打不开」然后 `return`）**原来不写 `EndTime` 也不写 `Outcome`** ⇒ 外层 `finally` 只对取消 / 异常补 `EndTime`，终态收口 `FinalizeOutcomeIfPending` 的判据（要求 `EndTime` 有值）不触发 ⇒ 状态写着「已跳过」、汇总却按 `Pending` 算进**「未处理」**。**已修**：显式落 `Outcome = Skipped` + `EndTime`；守门 `DeferredVolumeStatusTests.未识别的东西_只跳过_机器终态必须收口不许算未处理`，**红检成立**（撤掉 `Outcome = Skipped` ⇒ 报 `Expected: Skipped / Actual: Failed`，当场实证了下面那条"只补 `EndTime` 更糟"）。
  ② 同名冲突按用户选择「跳过」那一支（`ConflictChoice.Skip`）原来写了 `EndTime` 但**没写 `Outcome`** ⇒ 落进 `FinalizeOutcomeIfPending` 的 else 支（`Status != VolumeMissing` ⇒ **`Failed`**）⇒ 用户自己选的"跳过"被算成**失败**。**已修**：显式落 `Outcome = Skipped`；守门 `ConflictAskTests.冲突框里选跳过_任务算跳过不许被算成失败`，**红检成立**（撤掉那一行 ⇒ `Expected: Skipped / Actual: Failed`）。
  ⛔ **共同的关键教训**：两处都**不能只补 `EndTime`** —— 终态收口会接手并按"不是缺卷 ⇒ `Failed`"落结论，比不修更糟（① 的红检已当场实测到这个 `Failed`）。必须**显式**写 `Outcome`。
- **EEEE ③（工作区根锚点）已按"上游没做完的事回上游补"落地（2026-10-09）**：在 `ApplyBatchWorkspaceRoot` **之前**给这一批每一单补跑一次 `OneClickCoordinator.ApplyVolumeGroupingFromDirectory`（续解层的内层任务走 `AddPathsAsync(suppressAutoScan: true)`，那条路不跑扫描期归组 ⇒ `IsVolumeGroup` 恒 false ⇒ 那道"跳过入口包还没解出来的分卷组单"的守卫一律放行）。⛔ **没有**放宽守卫判据；⚠ 已编译、未真机复跑。
- **C「`ApplyBatchWorkspaceRoot` 两段拆」仍未做，本轮把它的前提核实了（2026-10-09 只读到行，⛔ 未改）**：
  ① **注释与代码确实相反**（三处行号）：`:14327` `ApplyBatchWorkspaceRoot(selectedTasks)` → `:14350` `NormalizeDisguisedVolumeNamesForBatchAsync(...)` → `:14361` `_pathService.GroupProducerEntryResolver = ...`；而 `:14354-14360` 那段注释写的是「位置：**改名归一之后**、**工作区根之前**（`ApplyBatchWorkspaceRoot` 里那一遍落点计算也要读到同一个答案）」—— 前半句成立、**后半句不成立**（它排在工作区根**之后**）。
  ⇒ **后果（有据）**：`:15412` 那一遍 `_pathService.ResolveOutputPlacement(task, options)`（落点计算）跑的时候 `GroupProducerEntryResolver` **还是上一批残留或 null** ⇒ 入口包还压在别的包里的那一单推不出产出链、退回公式占位值 ⇒ 正是真机 EEEE 的 `…\EEEE\111(4)\111`。⚠ 上一批补的"提前归组"只治了 `IsVolumeGroup == false` 那一半，**治不了这一半**。
  ② **正确顺序**（四条，缺一不可）：**(a) 定工作区根 → (b) 批首改名归一 → (c) 挂 resolver → (d) 算落点**。⛔ 不能简单把 `ApplyBatchWorkspaceRoot` 整块挪到 (c) 之后 —— `:15412` 那一遍落点同时喂给 `WorkspaceRootResolver.Resolve` 与残留空壳清理（`:15458-15469`，注释写明"必须在 `Resolve` **之前**"）；也⛔ **不能图省事把 (b) 改名归一挪到 (a) 前面**（那样顺序就变成 (b)(c)(a+d)、看着更简单）——
  **判据（有出处，不是推断）**：未解析时的工作区根是 `<数据根>\work`（`PathService` 的默认值，守门用例 `SettingsCacheRootRemovalTests.cs:117-120`：「未解析时的工作区根是老位置（<数据根>\work）」那一句断言），而**硬链接不能跨卷** ⇒ 批首改名归一的试开探针会落到**数据根那个卷**（多半 C:），与用户源文件（`H:\` / `E:\` …）不同卷 ⇒ 探针必然建不出来 ⇒ **内容那条路的改名在批首静默失效**（不是报错，是"判不出 ⇒ 什么都不做"，最难发现的那种退化）。
  ⇒ 真要拆，得把 `ApplyBatchWorkspaceRoot`（`:15395`）切成"算 destinations"与"定根 + 清理空壳"两段，把**前者**挪到 (c) 之后、后者留在 (a)。
  ⛔ **动手之前必须先做出能红的复现**（本轮没做：要造"某一单的入口包由同批另一单产出"的 E2E 并断言工作区根，预算不够）。这一档动的是**工作区根 + 落点**（不可逆路径），⛔ 没有复现不许改 —— 同一天「定稿侧漏闸门」那条正是**先做复现再改**才没出错（那条的红检第一次还假绿过，见 `修改日志.md` 第八十九轮续七）。
