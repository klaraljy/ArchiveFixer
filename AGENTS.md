# AGENTS.md — ArchiveFixer

> 本文件只写**这个项目特有**的规则，给任何新接手的 AI / 开发者看；目录约定、Git/GitHub、临时文件、
> 通用代码风格见全局 `E:\dsh\home\AGENTS.md`，此处不重复。
> **旧版 135 KB 的全文在仓库根 `旧AGENTS.md`**（2026-09-26 精简而来，含那张巨型「用户明确指示」表）；
> 用户原话与逐条落地史见 §12。

---

## 1. 这是什么

Windows 桌面工具：把一批**来源不明、后缀被改坏、加密、分卷、可能层层嵌套**的归档文件，用尽可能少的操作，
变成整理好的、结果可追踪的文件。

- **核心循环**（必须能一口气走完）：导入一批文件/文件夹 → 识别真实格式（不信后缀）→ 修正伪装后缀（先预览再改）→ 按密码本试密码 → 解压（含分卷组）→ 汇总报告（成功/失败/为什么）。
- **次级循环**：递归展开内层包 → 结果归集到目标目录 → 可选清理源包。
- **为谁做**：先为作者本人做到**每天真用**（本机磁盘上的大批量资源包整理）；向外扩展之前，不为想象中的用户写代码。
- **形态**：6 个选项卡（① 任务 ② 解压方式 ③ 清理与删除 ④ 密码 ⑤ 打包 ⑥ 设置），菜单只有 文件/视图/帮助。

## 2. 非目标（第一版明确不做）

**非目标比目标重要。** 以下都不做，也不允许"顺手"加进来：

- 外接硬盘 / U 盘 / 网络路径 / 盘符变化的专门适配（不做设备身份、断连恢复、卷 GUID；只要求"不写死盘符、不把工作区散在源目录里"）
- 资源管理器右键菜单、"发送到"菜单、下载目录监控、任务模板、文件清单导出
- 专项安装包提取（WIM/ESD、Inno Setup、InstallShield、MSI、SquashFS、LHA）—— **有真实样本才加**
- WinRAR 的"捆绑 / 默认依赖 / 自动探测安装"；只保留"用户自装、自选路径、程序只检测与调用"
- CLI 前端（第一版只做 GUI；但核心逻辑不得依赖 WPF，为 CLI 留位）、多语言（中文单语，不引 i18n 框架）
- 任何联网功能、自动更新（绿色目录分发）
- **安装包**：v1 原本不做；**用户 2026-09-27 要求"我是要 .exe 安装文件的"** → 已做（`installer\ArchiveFixer.nsi` + `scripts\installer.ps1`），规则见 §11.1
- 压缩炸弹的"智能判定"（只做硬预算上限，不猜意图）

## 3. 技术栈与分层铁律

- **C# / .NET 8 + WPF**，目标框架 `net8.0-windows`（⚠ 仓库里**没有** `global.json`；本机 `dotnet --version` = **10.0.400**，用 SDK 10 构建 net8.0 正常；⛔ 不许升 net9/net10，不许引入更高框架才有的 API）
- 不用第三方 MVVM 框架（沿用现有手写 `INotifyPropertyChanged` 风格）；**不新增 NuGet 运行时依赖**；测试用 xUnit
- **分层铁律**：`Domain / Detection / Engines / Password / Extraction / Security / Storage` **不得引用 WPF**（`System.Windows.*`）；GUI 只通过 ViewModel 调用它们
- **多引擎架构自 M1 起成立**：`IArchiveEngine`（probe/list/test/extract）、`EngineCapabilities`、`EngineRegistry` / `EngineSelector` / `ToolLocator`（**唯一**的外部工具路径来源）、统一错误码 / 进度 / 取消
- **四条禁止项**（只允许存在于 `Engines/SevenZip/` 内部）：① 核心模块直接拼 7z 参数 ② 直接解析 7-Zip 文本输出 ③ GUI 里判断引擎错误字符串 ④ 递归逻辑里写死某个引擎的参数
- **引擎优先级** `WinRar → SevenZip →（后续）`，**先按能力筛、再用优先级 tiebreaker**，不可用的引擎直接跳过（⛔ 不许因为"排第一但没装"就打不开包）；UnRAR.exe 来源 = 用户自选路径 → 已装 WinRAR 目录 → 内置 `tools\unrar\`
- **引擎回退**：`UnsupportedFormat / UnsupportedFeature / EngineUnavailable / ParserRejected / KnownCompatibilityIssue` 可换；`WrongPassword / MissingVolume / UnsafePath / NoDiskSpace / PermissionDenied / SourceChanged / DeviceOffline / UserCancelled / OutputLimitExceeded` **不换**；损坏归档**默认先报损坏**；备用引擎只解出部分文件时状态必须是 `PartiallyCompleted`
- 升级 7z 之后**必须跑全量测试**（`SevenZipOutputParser` 解析 7-Zip 的文本措辞，版本会改）
- 许可边界：内置 7-Zip（LGPL）+ 内置 UnRAR（RARLAB freeware，允许随包分发）；**`Rar.exe` / `WinRAR.exe` 绝不打包、绝不复制**（只检测与调用）；细节见 `docs/引擎与外部工具.md`

## 4. 目录结构

> 2026-09-26 按用户要求重排：本体进 `src/`、测试进 `tests/`，根目录只留项目级文件。

```
ArchiveFixer.slnx              解决方案（指向下面两个项目）
src/ArchiveFixer/              工具本体（WPF + 纯逻辑分层）
  Domain/        纯模型：ArchiveDescriptor / ArchiveTask / TaskState / ErrorCode / RenamePlan
  Detection/     魔数识别 / 后缀分析 / 分卷组识别（VolumeGroupResolver）
  Engines/       IArchiveEngine / SevenZipEngine / EngineRegistry / EngineSelector / ToolLocator
  Password/      密码本解析 / 候选顺序 / 尝试上限 / 旁路说明文件
  Extraction/    单层 / 分卷 / 递归 / 工作区 / 发布 / 冲突（PackageLayerRules）
  Security/      路径预检 / 资源预算 / 危险文件 / 输出落点校验
  Storage/       源文件稳定性 / 目标空间检查 / 工作区根 / 密码列表落盘
  Packing/       打包
  Services/ ViewModels/ Views/ Helpers/ Models/ Converters/（既有）
  tools/         内置外部工具（7zip / unrar），随程序分发、随包带许可文本
tests/ArchiveFixer.Tests/      xUnit（与本体分开；跑不起真 7z 的用例自己跳过）
docs/                          规格与文档（见 §10 文档地图）
samples/                       只放生成脚本 + 清单，样本本体不入仓库
scripts/package.ps1            生成 dist 发行包（**用户说暂不打包**）
scripts/installer.ps1          出 .exe 安装包（NSIS）
installer/ArchiveFixer.nsi      安装包脚本（⚠ 必须 UTF-8 **带 BOM**，否则 makensis 报 Bad text encoding）
scripts/make-icon.ps1 + icon-gen/  重生成 `Assets/ArchiveFixer.ico`（⛔ icon-gen **不在解决方案里**）
dist/                          发行产物（不入库）
README.md / AGENTS.md / 旧AGENTS.md / 修改日志.md
```

## 5. 命令

```powershell
dotnet build ArchiveFixer.slnx                                       # 构建
dotnet run --project src/ArchiveFixer/ArchiveFixer.csproj            # 运行（GUI）
dotnet test tests/ArchiveFixer.Tests/ArchiveFixer.Tests.csproj       # 测试（必须全绿才算"改了东西"）
dotnet format ArchiveFixer.slnx --verify-no-changes                  # 格式检查（先看差异，不要自动改）
dotnet format whitespace ArchiveFixer.slnx                           # 真有格式差异时用它修，别手工对齐
pwsh scripts/make-icon.ps1                                           # 重新生成图标（改绘制代码后跑）
pwsh scripts/make-icon.ps1 -Preview D:\tmp\icon.png                  # 顺带出一张预览图，自己看一眼
```

- **⛔ 用户说暂不打包**：不生成 `dist\*.zip` 发行包；构建只用于刷新绿色目录；**等用户明说"打包"再打**。
- 换了 `.ico` 之后**必须重新构建**（图标由 `/win32icon` 在编译期塞进 exe）+ 刷新绿色目录；核对办法：从 `E:\ArchiveFixer\ArchiveFixer.exe` 抽图标出来看（`ExtractAssociatedIcon`）。
- 代码风格：4 空格缩进、私有字段 `_camelCase`、`Nullable` + `ImplicitUsings` 开启；注释写**为什么**（"旧逻辑 → 新逻辑"这类踩坑记录要保留），不写"这行在做什么"。

### 5.1 绿色目录 `E:\ArchiveFixer\`（用户的真实测试位置）

- `bin\Release\...` **不是**用户数据所在地；重新构建后把运行时文件拷过去：`Copy-Item src\ArchiveFixer\bin\Release\net8.0-windows\{ArchiveFixer.exe,ArchiveFixer.dll,ArchiveFixer.pdb,*.json} E:\ArchiveFixer\`
  再拷 `src\ArchiveFixer\tools\` **和输出目录的 `docs\`**（csproj 会把四份用户文档复制到输出目录 `docs\`；不拷会报"路径错误"）。
- **⛔ 绝不碰 `E:\ArchiveFixer\data`** —— 那里是用户的日志 / 密码列表 / 设置。

### 5.2 并发纪律

- **同一个 checkout 里不许并发跑构建/测试**：`--no-incremental` 会重建 `bin\Debug`，正在跑的测试会看到"内置 7z 忽然不在"而**假红**；两个进程同时写 `obj\` 会报 `MSB3021/MSB3027` 文件锁定。多代理用 `git worktree` 或排队。
- 长活（构建 / 全量测试）**放后台任务**。

## 6. 不变量（红线，任何改动都不得违反）

1. **默认不改源文件、不删源文件**。删源只能是显式开启且校验通过的情形。
   - **例外（用户 2026-09-22 指示，已推翻版本一）**：**"成功 + 输出校验通过 + 未取消 + 属于本任务分卷组"**时，源包（分卷组则整组）**移入 `其余物`**；一键处理与手动「只解压」两条路都照此办。
   - **失败 / 部分完成 / 取消** → 源包**原地不动**、`其余物` **不生成**（红线）。
   - 口径：`SourceHandling` **默认 `KeepInPlace`**（一个字节都不碰源包）；要连源包一起删，用**两档组合**：源包操作 = 放入其余物 + 删除操作 = `Delete`（彻底）/`RecycleBin`（回收站）；旧第三档 `DeleteAfterVerify` 已退役（旧值在 `Normalize` 里迁移）。
2. **识别和解压不绑定**：看着识别结果可以直接解压，也可以不改名。
3. **改名必须先预览 —— 这条只管手动档**：一键处理 = **自动改名自动解压**（算好计划直接执行，不弹框）；手动档仍然"先预览再改"，两条路用**同一份改名实现**。改名冲突默认"跳过或自动重命名"、**不得默认覆盖**；`Overwrite` 必须"先移到临时名 → 再删 → 再落位"（⛔ 禁止"先 `File.Delete` 再 `Move`"）；名称交换（`A.zip ↔ B.zip`）必须用临时名两阶段完成。
4. **解压产物不得越出目标根目录**。7z.exe 是外部进程，必须两道：解压前预检条目名（拒绝 `..`、绝对路径、盘符、UNC、`\\?\`、混合斜杠），解压后校验落点。Windows 保留名（`CON`/`PRN`/`AUX`/`NUL`/`COM1-9`/`LPT1-9`）、结尾空格、结尾点、备用数据流（`:`）一律清洗后再落盘（`FileNameHelper` 已实现，**不要回退**）。
5. **密码默认只存内存；用户 2026-09-24 允许"按本机加密落盘"**。
   - **日志、报告、剪贴板、详情窗口、异常信息一律脱敏**，含 `-p` 参数；`-p<明文>` 会出现在进程命令行，这是 7z 命令行的固有限制，**必须在"已知限制"里写明，不得假装解决了**。
   - 允许按本机 **DPAPI（机器范围）**加密落盘到 `<程序目录>\data\password-list.dat`（**绝不写 C 盘**、绝不进日志），由 `RememberPasswordList`（默认开）整个关掉（关掉 = **既不写也不读**）。**代价（实现与文档都必须如实写明）**：同机任何用户都可能解开；换机器 / 重装系统解不开 —— 那时一律"忽略并提示"（不崩、不覆盖、不删文件），出路是「写回密码本」。
6. **部分成功不得显示为成功**；取消、超时、设备离线、分卷缺失一律不得显示成功。
7. **分卷缺失不得开始不可完成的任务**，必须报"缺哪几个"。
8. **递归必须有硬上限**（层数 / 文件数 / 总大小 / 单文件 / 展开比 / 密码尝试次数），且**多分支默认不展开**，必须问。
   - ⚠ **例外（一键处理档）**：一键档**批中间零弹窗**，多分支不问 —— 按保守档办（不展开）+ 写日志（判据 `expandAll = !oneClickRun` 与 `SuppressDecisionPromptsForOneClickRun()`，见 §11.5）。手动档照旧**必须问**。
   - 上限**必须存在，但不许写死在代码里**：解压前那四条（单文件 / 总大小 / 文件数 / 展开比）是**用户设置**（⑥设置 →「安全上限」，默认 **64 GiB / 512 GiB / 20 万 / 1000 倍**），唯一出口 `ResourceBudgetOptions.FromSettings` + `ExtractionCoordinator.BudgetLimits`。超范围一律**夹回并说明**；上限类拒绝文案必须带 `StatusText.SecurityCapHint`（**空间不足那一档刻意不带**）。
   - 默认最大嵌套层数 = **5**（用户 2026-09-26 定）；一键处理每批轮数 = `OneClickCoordinator.RoundLimit`（1~10）；⛔ **不许再有第二套轮数**（"界面写 5、程序按 10 跑"正是被拆掉的东西）。到顶把剩余内层包加进列表并勾好 + ①页「继续解」。
9. **单个任务失败不得中断整批**；反过来，用户点"停止后续"不得变成"强杀当前"（两者是独立取消源）。
10. **外部进程必须能收干净**（8 条全都要做）：安全传参（用 `ArgumentList`，**禁止拼 `cmd.exe` 字符串**）、重定向 stdout/stderr、支持超时、支持取消、正确关闭句柄、正确终止子进程。取消**只杀自己启动的 PID 及其子进程**；**禁止按进程名批量杀 `7z.exe`**。
11. **源文件变化后不得继续使用旧识别结果**：任务开始时记大小/修改时间快照，处理中变化即停下并报"源文件已变化"。（已实现：`Storage/SourceFileSnapshot.cs`，分卷整组，**连"记的时候在不在"一起记**；识别完成即拍快照，所有解压入口在 `ExtractSingleTaskAsync` 开头唯一收口比对，递归每层开工前再问一次。边界：**不比内容哈希**、**解压进行中**改源文件拦不住那一次、改名 / 源包搬进其余物**不算**变化。）
12. **工作区、源文件、最终输出三者相互独立**：中间产物不得写进源目录，也不得直接写最终目录。
    - 位置**只有一个来源** = **本次目标目录里面**：`<目标目录>\.ArchiveFixer.work`（隐藏、走完连壳删掉 —— 用户 2026-09-30 的口径）。⛔ **用户没有指定工作区的权力**（`CacheRootDirectory` 已删、旧键**安静忽略**）；**拿不到目标目录一律报错指路，⛔ 绝不回落程序目录 / C 盘 / 源卷根 / `%TEMP%`**；⛔ **需要临时物的地方一律由调用方把工作区根传进去**（例：分卷试开 `VolumeNameRepair.PlanByContentAsync`），**传不进来就不做那件事**。细则见 §11.6。
    - **默认全清（含空壳）**：失败 / 取消 / 部分完成收尾时把这一单**自己的任务工作区目录整份删掉**（含中间件），**递归逐层同样清掉**；零文件空壳无论如何都删。只有 `KeepFailedWorkspace`（默认 false，③页「工作区残留」）打开时才保留现场（有文件一律留，并写 INFO 说清）。
    - 三条红线不随设置变：**成功路径的清理口径一个字不改**、**源包在任何情况下原地不动**、**已定稿搬出去的内容物不受影响**；删除前两道容器内校验（在生效的工作区根之下 + 目录里只许有我们自己造的子目录名），越界只写 WARN、一个字节都不删。
13. **清空/清理任务不得误删工作区之外的文件**；清工作区必须先经用户确认。
14. **结果可追溯**：每个最终结果都能追到具体任务与具体引擎，报告里必须带引擎名 + 版本。

## 7. 状态与文案（既有资产，不要推翻）

- 所有界面状态字符串统一引用 `Models/StatusText.cs` 常量，**禁止手写中文字面量**。
- 失败分类沿用既有中文口径：`密码错误 / 文件损坏 / 权限不足 / 输出路径冲突 / 分卷缺失 / 路径过长 / 已取消 / 未知错误`，以及 `密码错误或文件损坏`（`StatusText.PasswordOrCorrupted`，RAR 两义句见 §11.4）/ `7z不存在` / **`没有可用的解压引擎`**（两个引擎都不可用时用它）/ `文件名已加密` / `达到密码尝试上限` / `长时间无响应`（最后一条是**提示文案不是状态**）。
- 新增状态必须同时更新 `StatusText` + `StatusToBrushConverter` + `TaskSummaryService`，三处缺一不可。
- **统计不得依赖中文文案比较**：`Status` 需要能被机器判定（`TaskState` 枚举；中文留给显示层）。
- 日志按级别上色：红 = ERROR / 黄 = WARN / 黑 = INFO；**要判"留不留细节"只准看机器终态**（`TaskOutcome`），不许比中文。

## 8. 隐私红线

- **不读取工作区以外的用户个人目录**（下载目录、密码本、私人文件夹）；需要真机样本时由用户拷进 `samples/`（或告知路径）。
- 密码、真实站点名、个人路径**不得**出现在：仓库文件、日志、汇报、截图、测试数据、注释里。文档与测试里的密码一律用占位符（`<示例密码>`）。
- 应用自身日志允许记录密码**状态**（是否加密、是否试过、是否成功、是否超上限），**不记录内容**。

## 9. 干活的纪律

**§9.1 桌面与鼠标：默认不碰，除非用户当前明确授权**（2026-09-27 晚重新授权）

- **当前状态**：用户原话「现在你可以使用桌面和鼠标来操控」，用于他点名的真机测试。授权**以用户最新一条明确指令为准**：说停就停 —— ⛔ 别拿"以前被收回过"当理由拒绝，也别当长期许可。
- 授权期间仍不许：抢前台骚扰他干活、改桌面状态（分辨率 / 任务栏 / 别的窗口）、**动他给的样本目录以外的东西**、跑会弹 UAC 的操作。
- 没授权时的替代做法（一直有效）：`docs/人工测试清单.md` 的 A/B/C 真机手动项**交给用户**；代理只做**造样本 + 逐步操作说明 + 读程序自己的日志与文件结果 + 改代码**，端到端走不碰桌面的路（读日志/临时目录跑流程/写自动化测试）。
- ⚠ 这一条管代理与脚本、**不管程序自己**：程序要用户输入时把窗口拎到最前面是用户要求的行为，但**只有窗口没能出现在最前面时才闪 + 响**（判据 `WindowAttention.ShouldAlert`，两种结论都进日志）。
- 真机自动化办法见 `docs/人工测试清单.md` 末节（UIA 只到 TabItem、截图定位 + 鼠标/键盘）。

**§9.2 测试（按改动范围分级 —— 用户 2026-09-27 指示；级距定义见全局《作业模式》规矩 25）**：
验证强度与**改动影响面**成正比，⛔ 不是"改一行也要跑全量"——小改动只跑定向并报清**跑了哪几条、结果如何**；大改动跑**全量**。

- 判据是"**这次改动会碰坏谁**"（直接调用方 + 覆盖它的断言），不是改了几行。凡动 **`ExtractionCoordinator` 的候选循环 / `OutputVerifier` 口径 / 工作区与其余物的清理**，一律按大改动对待（不可逆路径）。
- **红检** = 修一个缺陷就要能**把修复临时撤掉、亲眼看到对应测试变红**（写完再恢复修复），新增守门测试要说明红在哪。
- ⛔ 跑不起真 7z 的用例自己跳过，不许改成"假装跑过"；⛔ **没跑过的验证不许说成通过了**。
- 遇到 §11.2 那几条已知 flaky，**单跑确认**，别改产品代码或断言。

**§9.3 用户可见字符串里不许写 Markdown**：`**加粗**` 会原样显示成星号，强调一律用「」；有 `UserFacingTextTests` 全量扫描 `.cs` **与 `.xaml`**（跳过 `<!-- -->` 注释），新写文案后跑它。

**§9.4 文档与记录**：**改动要写进 `修改日志.md`**（一条一行流水账）；每条新需求追加进 `docs/需求变更.md`（该文件只追加）。目录结构 / 功能变了要同时改"现状说明"类文档（README、本文件 §4 §5、`scripts/package.ps1`）；而 `docs/需求变更.md`、`修改日志.md`、`docs/打包功能.md` §10 这些**历史记录**里的旧路径**按原样留着**。README 只做"一页纸 + 跳转"，⛔ 细节不许再往回收。

**§9.5 工程习惯（反复踩出来的）**：同一件事的**真值只允许有一个出口**（轮数、上限、落点、容器、候选顺序…）；"同一件事在多处出现"必须**值 + 通知**两条都钉住（值对而界面不刷新 = 用户读成"没生效"）；要删/要写盘的动作判据只准读事实（枚举终态），**兜底一律落在"什么都不做"那一档**；排错先**量化**（行数 / 字节数）再改，改完用**同一口径**进回归；用户要求的"提醒"就是提醒，⛔ 不许顺手把它做成自动行为。

## 10. 文档地图（`docs/` 里每一份是什么）

| 文档 | 一句话 |
|---|---|
| `需求书.md` | 总需求说明 / AI 开发任务书，**冻结**为需求全集（此后只追加需求变更）；文末 = 「用户指示汇总」 |
| `需求变更.md` | **只追加**的需求变更日志 |
| `需求评审与考古.md` | 为什么这样定：08-09 基线完成度、设计.md 问题清单与逐章归属 |
| `输出与整理模型.md` | 落点与整理模型的**实现契约**（落点 v2、其余物、例外档、续解层） |
| `界面重构方案.md` | 6 个选项卡的界面重构方案（用户已拍板） |
| `打包功能.md` | 打包功能的设计 + 实施记录（流程、分卷规则、许可边界） |
| `引擎与外部工具.md` | 引擎/外部工具工程说明书 + **许可证边界** |
| `WinRAR功能参考.md` | 对 WinRAR 的对照研究笔记（不是需求） |
| `功能一览.md` | 现在能做什么的**详细版**（README 只留一行一条） |
| `设置项.md` | 设置项一览的**详细版**（字段名 = `data\appsettings.json` 的键） |
| `使用说明.md` | 面向**使用者**的说明书（看完这一份就够） |
| `人工测试清单.md` | 给人照着点的人工测试清单（A/B/C 各组） |
| `检验等级.md` | **L0–L6 检验阶梯**的唯一写处（判不出 ⇒ 什么都不做） |
| `分卷组装算法.md` | 分卷组装判定器的算法与证据（六条证据 / 四档结论 / §6.10 §6.11） |
| `真机事故复盘.md` | **历史现场与推演**：原 §11 正文**逐字归档**（32 节） |

> 另有 `<仓库外>\ArchiveFixer\项目设计.md` = **需求全集 / 长期愿景**，有效但需按非目标裁剪。
> ⛔ 别把"没写进本文件"理解成"被否决了"。

## 11. 当前验证状态（结论速查；现场、根因与红检见 `docs/真机事故复盘.md`）

> 本节怎么读：只放现在的事实与口径（版本/发布物/测试数字/红线/"唯一出口"的名字）。每条末尾 `〔X〕`=复盘见 `docs/真机事故复盘.md`「X」（现象/`文件:行号` 根因/修法/红检/遗留）。
> ⛔ 不写日期现场与长推演；⛔ 不许删结论（结论是下次改代码的红线）。

### 11.1 版本/发布/安装包

- v0.1.0=第一个对外版本（`ArchiveFixer.csproj` 三处版本号=0.1.0；旧 11.0.0=开发期编号）〔v0.1.0：第一个对外版本〕
- 已发布：`https://github.com/klaraljy/ArchiveFixer`（MIT、公开）+ Release v0.1.0〔已发布：仓库 / Release v0.1.0〕
  - ⛔ 资产名=「ASCII 文件名+中文 label」；⛔ 大资产直连传（清 `HTTP_PROXY`/`HTTPS_PROXY`）；⛔ `gh release upload --clobber` 会连别的资产一起删。
- 安装包=`installer\ArchiveFixer.nsi`+`scripts\installer.ps1`（NSIS；免 UAC、每用户）〔安装包（NSIS .exe）〕
  - ⛔ 默认安装目录不许是 `C:\Program Files\`；⛔ 卸载默认保留 `data\`。
  - `.nsi` 必须 UTF-8 带 BOM；注释行末尾不留反斜杠；开关判据只读 `${GetOptions}` 返回值；卸载删桌面 `.lnk` 前看 `DesktopShortcut` 标记。
- 面向用户的截图与文档脱敏（⛔ 个人路径/样本包名/站点名/作者邮箱都不进仓库）〔开源前的脱敏〕
- 「说明」窗=`HelpContent.Features`（16 条）+`Glossary`〔说明窗〕

### 11.2 构建/测试基线

- `dotnet build ArchiveFixer.slnx`=0 错误 0 警告；`dotnet format ArchiveFixer.slnx --verify-no-changes`=通过。
  - ⚠ 警告口径：日常构建 0 警告；**强制还原**那档多 4 条 `warning NU1900`（漏洞数据下载 404，环境/网络）——⛔ 不许写成"0 警告一定成立"。
- `dotnet test` 全量（主 checkout 内）：2241 条（2239 通过/2 跳过/0 失败）〔构建 / 测试 / 格式基线〕
- ⚠ worktree 里跑全量多 6 条跳过（共 8）：真样本根按「`ArchiveFixer.slnx` 的父目录 `\_tmp\ArchiveFixer\{aaa-real,amb909-copy}`」解析，worktree 解成不存在的 `<wt>\_tmp\…`；设 `ARCHIVEFIXER_REAL_SAMPLE_DIR`/`ARCHIVEFIXER_REAL_VOLUME_PAIR_DIR` 复原 2 条。⛔ 这 6 条是"样本路径解不出来"、不是样本不在。
- 2 条跳过=发现阶段条件跳过（⛔ 不伪装成验过；条件式 `FactAttribute` 构造时设 `Skip`；全仓无 `[Fact(Skip=…)]`、无 `Skip.If`）：① `RealAmb909VolumePairTests.真机副本_有密码时_既有管线真的解出这一组的内容` 要 `ARCHIVEFIXER_REAL_VOLUME_PASSWORD`；② `SpaceDemandAccountingTests.真样本只读_那一组真实分卷_判据里不含源包_真机可用空间下必须放行` 要 `ARCHIVEFIXER_REAL_SPACE_CASE_DIR`，或 `<slnx父目录>\_tmp\ArchiveFixer\space-real` 存在。
- ⚠ 真样本用例没设环境变量时提前 return，报表照样算"通过"——⛔ 别读成"验过了"；要报真样本结果必须设变量单跑并写清命中哪份。
- ⚠ 数字只在这里写一次。
- 已知 flaky（并发假红；先单跑确认，⛔ 别改断言）〔已知 flaky 清单〕
  - `SpaceTightModeTests.换输出位置_二页那颗选择按钮也会触发空间体检`：worktree 全量偶发 `Collection was modified`（测试自己的 `WaitForLogAsync`：`SpaceTightModeTests.cs:877` 枚举 `harness.Log.Logs` 而产品侧在 append）⇒ 测试侧竞态、单跑 3 次全绿、主 checkout 全量通过；⛔ 不是产品 bug。
- ⚠ 回退代码后必须 `--no-incremental` 重编（否则跑的还是红检那份）〔真样本验收〕

### 11.3 空间：判据/模式/批末汇总

- ⛔ 源包不许算两遍：唯一出口 `TaskSpaceEstimate.FreeSpaceDemandBytes`=`ContentBytes+ProcessArtifactBytes`（`ScheduledExtractionItem.RequiredBytes` 与 `ExtractionCoordinator.ReconcileReservation` 都只读它）；`PeakBytes`（含源包）只作描述，⛔ 不许拿它比可用空间〔源包不许算两遍（2026-09-29）〕
- 「空间不足」模式（`MainViewModel.SpaceTightMode`：运行期开关、不写盘、不记忆）一个布尔管四件事：并发（`ExtractionScheduler.ResolveSpaceTightParallelCount` 与「最大并发解压数」取小）、排序、其余物强制 `Delete`、定稿+校验通过后当场永久删源包（`PurgeSourcePackageForSpaceTight`=`SourceCleanupService` 唯一调用点）〔空间不足模式〕
- 「不删原包」安全档（`MainViewModel.SpaceTightKeepSource`）测试期专用：⛔ 发行那轮整块删掉；`SpaceTrendMonitor` 保留〔安全档「不删原包」〕
- 空间回收的五条口径（⛔ 别按旧口径改回去）〔空间回收与批末汇总〕
  1. 多层链每一层各删各的：判据排在 `task.IsContinuationTask` 之前（峰值 ≈ 两倍单层）。
  2. 某一层失败只影响那一层：失败层源包留着，已收走的更外层不回滚（⛔ 别当 bug 去"修"）。
  3. 动手前提醒"多层可能中途空间不足"：唯一出口 `Storage/MultiLayerSpaceRiskRules`、文案 `StatusText.MultiLayerSpaceRiskFormat`；⛔ 只用 `Σ FreeSpaceDemandBytes` 比可用空间；只提醒、不拦任务。
  4. 中途撞上空间不足 ⇒ 一键档弹一次纯提示（非模态、同一批一次）；⛔ 手动档只写日志。
  5. 批末汇总框色带：唯一出口 `Models/BatchSummarySeverityRules`（只读 `TaskOutcome`/校验枚举，⛔ 不比中文），蓝=全成功/橙=有部分完成·跳过·取消·没轮到/红=有失败；窗口只管长相（`AppDialogWindow.ResolveSummaryBannerBrushKey`），⛔ 不许在 XAML 里再判断。
- ⭐ 同一个框还要"具体指出错在哪"〔复盘同上一节〕
  - 唯一出口 `Models/BatchSummaryDiagnosticsRules.Build`：`BatchProblemKind` 12 档（顺序=显示顺序）、`Severity` 与文字同一次调用；每组最多 3 个名字（其余「还有 K 个」）、只写文件名（§8）；全成功/空批不写；密码那组注脚必须带"可能"（⛔ 不许断言"就是密码问题"）。
  - 分组不新造第二套分类（只读状态常量/机器终态兜底 ⇒ 没做成的不会消失；`Succeeded` 且校验没判否的不进任何组）；补充数只取现成的（`ArchiveTask.SpaceBlocked`、`MissingVolumeNames`，⛔ 不重算）；⚠ 未做：色带+清单真机 GUI 没看过、第 3 条那句是上界（⛔ 不许改成"精确预测"）、中途提示只带第一个。

### 11.4 分卷/格式识别

- ⛔ 「修正后缀」不许动分卷名：两道闸门=格式未知 → 一个字都不改 + 末尾是纯数字（那是卷号）→ 不许当后缀替换〔修正后缀不动分卷名〕
- 卷名判据唯一出口 `ExtensionHelper.TrySplitVolumeSegment`（三处转调它）〔分卷名粘垃圾〕
  - ⛔ 老口径不许回退：另起一段的后缀（`x.7z.001.txt`/`x.001.bak`）不算分卷；纯数字尾巴（`0012`）不猜。
  - ⛔ 不许"一见 zip 成员就让位"：让位条件只看"目录里真有一片自述带盘号的跨盘 zip 末片"（`HasSpannedZipTailInDirectory`）。
- ⛔ 不要再给"短数字卷号"另加名字判据（内容路 `Detection/VolumeContentInference` 就能认）；要动就动"谁先跑、谁不许动名字"〔短数字卷号：内容路本来就能认〕
- 缺卷补救：候选池只收"不是已识别的归档"的；⛔ 放宽的只是"敢不敢试"：成不成立只由 `VolumeProbeVerifier` 硬链接试开回答（⛔ 绝不复制大文件、不改用户文件）〔缺卷补救：第二次真机报〕
  - 头加密那一档也算肯定回答（`EngineErrorTypes.EncryptedHeaders`，结构化结论、不比文案）；单卷试开就报"加密归档" ⇒ 拒绝改名（它本身就是完整包）〔假绿复核：-mhe 两卷〕
- 分卷组装判定器 `Detection/VolumeGroupResolver`：六条证据（基名/卷号连续/体积规律/物理同一性/位置推定/硬链接试开）+ 四档结论（`Complete`/`IncompleteMissingVolume`/`IncompleteSuspected`/`Undetermined`）+ 唯一可删出口 `CanEnterDeletableRestItems`（弱证据/判不出=false ⇒ 不删源、不移源）；跨盘或拿不到工作区根 ⇒ 不试开、降「判不出」（硬链接不能跨卷；⛔ 不许退到源卷根偷开工作区）；详见 `docs/分卷组装算法.md` §6.10/§6.11。
- ✅ 带垃圾尾巴的组（`111.parts1.racr` 这种"另起一段的后缀"）交给 7-Zip 会报缺卷 ⇒ 由 `VolumeNameRepair` 在开工前改回标准名（只改名、不覆盖、判不出就不改）。
- ⛔ **一组分卷 = 一个任务 = 从首卷启动**（现场与根因见 `修改日志.md` 2026-10-01）：① 整组改名在**批首**（`NormalizeDisguisedVolumeNamesForBatchAsync`，`ApplyBatchWorkspaceRoot` 之后 / 并发之前）；② 后续卷那一单落 `Skipped`（`SkipWhenAnotherTaskOwnsThisVolumeGroup` + `ArchiveTask.IsVolumeGroupFollower`）；③ 归组**只算不写**（`OneClickCoordinator.ResolveVolumeGroupFromDirectory`）且**只增不减**；④ 跟班**不进链尾裁决**（`DescribeChainVerificationGap` / `CompleteRootSourcePackagesAfterChainAsync`）。用例 `AaaReplayPipelineTests`。
- RAR 命名：`Rar!\x1A\x07\x00`=RAR 1.5–4.x，⛔ 代码/注释/文案都不许写"这是 RAR3"；RAR5 有真样本（`H:` 两处 `-p` 包，签名 `52 61 72 21 1A 07 01 00`，只读不入库）；老式编号族 `.rar`/`.r00` 按设计不认〔真样本验收〕
- ⛔ 验收规则（用户定）：必须真样本（或真机只读副本）跑通；合成样本通过 ≠ 问题解决；样本本体绝不进仓库、`H:` 原件只读，副本不在就跳过并说明〔验收规则：必须真样本跑通〕
- 待修（还没做，别当成"已解决"）
  - 分卷跨目录拼装：候选枚举与"物理同一性"只认同一目录，跨目录如实落「判不出」而非靠名字硬拼。
  - 完整加密包+名字末尾纯数字 → 被 7-Zip 当"通用分片" ⇒ 误诊「分卷缺失」（别在 `RawSplitStreamDetector` 里加"看名字猜"）。
  - 伪装成 `.mp4`/`.apk` 的续卷：判定器已把"无卷号的同目录候选"按体积/位置推定+硬链接试开收进来（真案 ③），⛔ 绝不只凭后缀判；⚠ 试开做不了时（跨盘/拿不到工作区根）只到「疑缺卷」。
  - 7z 头部被压缩时读不出加密（`-p` 与不加密包 64 KiB 内逐字节同构；不引依赖/不调引擎 ⇒ 如实不报）
  - 内嵌 ZIP 不做加密判读（`BuildEmbeddedResult` 没接出口，内嵌 RAR/7z 有）
  - 7z `-mhe` 与 ZIP AES 没有真样本；跨盘 zip 中间片一律 Unknown
- ✅ 加密判读=只读头/尾（RAR/ZIP/7z）：判据器 `Detection/{Rar,Zip,SevenZip}EncryptionReader`（词表 `ArchiveEncryptionState/Reading`，不调引擎/不引依赖）；唯一出口 `ArchiveDetectService.ApplyEncryptionVerdict`。
  - RAR：RAR5 类型 4 ⇒ `-hp`、扩展区 `0x01` ⇒ `-p`；RAR 1.5–4.x 主头 `MHD_PASSWORD(0x0080)` ⇒ `-hp`、文件头 `LHD_PASSWORD(0x0004)` ⇒ `-p`。⚠ `ArchiveFlags` 的 `0x0004` 是 Solid、不是"有密码"（⛔ 别按记忆改）；多卷只看第 1 卷。
  - ZIP：看中央目录通用位 bit0；⚠ `0x0800` 是 UTF-8 文件名标志、不是加密位；中央目录读不出 ⇒ 兜底第一个本地头，那一档只给"加密"或"不知道"，绝不说"没加密"。
  - 7z：AES-256 coder（大端 `0x06F10701`）：`kEncodedHeader(0x17)`+AES ⇒ `-mhe`、`kHeader(0x01)`+AES ⇒ `-p`。
  - ⛔ 读不出来一律"不知道"（`Unknown` ≠ `NotEncrypted`，⛔ 不猜、不误报）：7z 头被压缩、多卷 7z 只给第 1 卷、截断/布局不符。
  - ⛔ 分卷要进"可删的其余物"，必须先证明整组完整（缺卷 ⇒ 计划作废、什么都不动）〔真机 2026-09-30：25 GB 被误删〕
  - ⛔ `CRC Failed in encrypted file` 不许单独定原因（"密码错"与"数据坏"同一句，`Item37SafetyTests` 钉着）；结论必须带出 7-Zip 原话〔真机 2026-09-30〕
  - ⛔ RAR 两义句：错密码 = **退出码 3** + 「校验和错误。文件已损坏或密码错误。」（中英同句，正确密码 0）⇒ 判据 = 退出码 3 + `Detection/RarEncryptionReader` 判"不是明确没加密"（⛔ 不比中文）落 `EngineErrorTypes.PasswordOrCorrupted`，候选循环**继续试下一个**；**非加密包退出码 3 仍是「文件损坏」**。
  - 用例 `{Rar,Zip,SevenZip}EncryptionReaderTests`；真样本 3 条走 `ARCHIVEFIXER_REAL_ENCRYPTION_{7Z,ZIP_PLAIN,RAR}`〔加密识别扩到 ZIP 与 7z〕
- ✅ 识别提速：① `Detection/DetectResultCache`：结论按（字节数+修改时间+头指纹+尾指纹）缓存；② `TailArchiveScanner` 改按 `IndexOfAny` 跳候选首字节。⛔ 识别结论一个字都不许变（逐字段对照"开/关缓存"两跑）、缓存不含 RAR 加密标志（每次现算）、修改时间进键（不变量 11）。用例 `DetectResultCacheTests`+`TailArchiveScannerBoundaryTests`〔识别提速〕

### 11.5 管线（落点/弹窗/校验/显示/密码）

- ⛔ 落点最少两层文件夹：最外层=包名目录、最里层=最后一个内层包层，⛔ 塌缩/不套层分支不许吃掉最里层；省层只能省中间的内层包层，⛔ 普通文件夹永不摊平（唯一出口 `Extraction/PackageLayerRules.cs`；递归=就地替换）
- 落点模型 v2：判据出口三处——`OutputPlacement.ResolveDestinationDirectory`（落点）、`OneClickCoordinator.ShouldAddContinuationLevelLayer`（续解层）、`ResultFinalizer.Plan(..., suppressPackageFolderLayer:)`（定稿套层）；契约 `docs/输出与整理模型.md` §1.1/§3.1/§3.3.1〔落点模型 v2〕
- ⛔ 整组改名（`VolumeNameRepair` 改整组**每一卷**）后必须**同步全表任务路径+快照**：唯一出口 `ExtractionCoordinator.SyncTasksAfterVolumeRename`（按 plan.Items 重写全表 CurrentPath/VolumePaths + 重拍快照；调用点 `:6227`、`MainViewModel.cs:5360` 手动「修复分卷名并重试」）；只同步驱动那一单 ⇒ 兄弟任务下一次比对假报「源文件已变化」。不变量 11 口径一个字没放宽。
- 一键处理期间零弹窗（唯一例外=批末汇总框）：`ExtractionCoordinator.SuppressDecisionPromptsForOneClickRun()` 一处收口，且必须排在 `ResetBatchConflictState()` 之后（先抑制后清零=没抑制）；⛔ 不许在一键档批中间新增任何"要点一下"的框〔一键处理零弹窗（唯一例外：批末汇总）〕
- 递归多层的结果校验：展开 >1 层不拿第 0 层清单当预期（只做非空/落点/预算三道），否则误报「解压失败」〔递归多层的结果校验〕
- 显示口径：成功任务显示「解压成功 100%」（判据=机器终态 `Outcome == Succeeded`，⛔ 不读瞬时百分比）并落 `EndTime`；「部分完成」在批末单独一档；①页「大小」列宽 118（别再收窄）〔显示口径〕
- 导入后的空间体检：三个触发点走 `MainViewModel.CheckSpaceForTasksAsync` → `ReportSpaceCheck`（复用 `ExtractionCoordinator.BuildSpaceAdvice`）；只提醒：不改设置、不改勾选、不拦任务〔导入后的空间体检〕
- 密码三条口径〔四路只读核查一轮：三条真缺陷 + 手动密码框撤掉〕：
  - 密码口径：一键档无手动密码输入框（只写一条"去「密码」页一键导入"的 INFO）+ 确认框 B 段同一句指路 + 批末一条红字（必须写"只是可能"）；手动「只解压」无可用候选才问。
  - 密码记忆读不出来（换机器/重装系统/文件损坏）⇒ 先复制一份 `password-list.dat.unreadable-<时间戳>.bak`（`PasswordListStore.TryBackupUnreadableFile`；⛔ 只复制，绝不移动/删除原件），文案如实点名备份名。
- 导入时的无用物弹窗已退休（导入即自动移出列表）：只剩手动「只解压」会弹，一键档并进唯一那个确认框

### 11.6 工作区/检验等级/终态口径

- 工作区口径全文见 §6 不变量 12（唯一出口 `Storage/WorkspaceRootResolver.cs`）；工作区那棵树不算产物（`Storage/WorkspaceTree.cs`）。
- 检验等级 L0–L6（唯一写处 `docs/检验等级.md`；用户 2026-09-30：「时间可以多花几十秒，不能草率」）：每级一道闸门，判不出 ⇒ 什么都不做；不可逆动作只允许在"L4=可证完整+L5 复核通过+未取消+属于本任务"四条同时成立时发生；L4 三态唯一出口 `Extraction/ResultCompleteness.cs`（`Complete`/`Incomplete`/`Undeterminable`，默认值=`Undeterminable`），判不出如实写"无法确认完整性"（`StatusText.CompletenessUndeterminableFormat`）。
- L4 清单**按层取**：展开 >1 层用**叶子层**清单，唯一出口 `Extraction/ChainManifestResolver.cs`；拿不到 ⇒ 如实「判不出」+ 日志点名**哪一层**，⛔ 不许退回第 0 层顶替。
- 「为什么没删源包」唯一出口 `ResultCompletenessVerdict.Blocker`；链尾收尾**逐条判据**（`ApplyRestHandlingAfterChainAsync`+`StatusText.ChainRestBlockedPrefix`），⛔ 不许静默 return。
- 终态口径唯一出口 `Models/TaskOutcomeClassifier.cs`：①页分桶/一键汇总行/批末诊断读同一份失败名单（⛔ 不许再各写一份），判据只读状态常量+机器终态。
- 盘满=`EngineErrorTypes.NoDiskSpace`（`Engines/EngineIds.cs`）：两个引擎解析器都落「磁盘空间不足」，⛔ 不再误报「权限不足」。
- ⛔ 预检拦下必须落 Failed 终态：唯一出口 `ExtractionCoordinator.MarkStoppedBeforeExtract`（缺卷 / 文件不存在 / 落点无效 / 源文件已变化四处共用，走既有 `ArchiveTask.MarkFailed`）；不落 `EndTime` ⇒ Outcome 永停 `Pending`（汇总说「未处理」）。Failed 仍只由唯一收口落。
- ✅ 缺口 ⑨ 已修（2026-10-01）：递归工作区"没人配过根"时**不再回落 `%TEMP%`**——`AllowSystemTempWorkspaceFallback` 默认 false（拿不到根就报错指路），测试宿主 `TestAssemblyInitialize` 显式打开；用例 `WorkspaceTempFallbackGateTests`。
  - ⚠ 另：上一轮验收证据全是**合成样本**（形状 1/2/5），**真机验收仍空白**。
  - ⚠ 上一轮新记：`TryRepairBrokenVolumeChainName`（内层续解链改名）残留未动；"先测试再解压"候选循环同口径改了但**无专条用例**；`RecordPasswordFailure` **未纳入两义档**；三处修复**未在真机复验**。

## 12. 细节去哪看

| 想知道 | 去哪看 |
|---|---|
| 用户原话、每条指示的逐条落地史 | `旧AGENTS.md`（旧版全文 135 KB，含「用户明确指示」表） |
| 某次事故 / 某条红线**当初为什么这么定**（现场、根因、红检） | `docs/真机事故复盘.md`（原 §11 正文**逐字**归档，32 节） |
| 某次改动具体做了什么 | `修改日志.md`（一条一行流水账） |

> 其余都在 `docs/` 里 —— 每份文档见 §10 文档地图（含用户指示汇总、需求变更、
> 使用说明 / 功能一览 / 设置项 / 人工测试清单）。
