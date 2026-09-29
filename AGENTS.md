# AGENTS.md — ArchiveFixer

> 本文件只写**这个项目特有**的规则，给任何新接手的 AI / 开发者看。
> 目录约定、Git/GitHub、临时文件、通用代码风格见全局 `E:\dsh\home\AGENTS.md`，此处不重复。
> **本文件 2026-09-26 由旧版 135 KB 的 `AGENTS.md` 精简而来**（用户原话与逐条落地史不在这里，见 §12）；
> 旧版全文保留在仓库根的 `旧AGENTS.md`。

---

## 1. 这是什么

Windows 桌面工具：把一批**来源不明、后缀被改坏、加密、分卷、可能层层嵌套**的归档文件，用尽可能少的操作，
变成整理好的、结果可追踪的文件。

- **核心循环**（必须能一口气走完）：导入一批文件/文件夹 → 识别真实格式（不信后缀）→ 修正伪装后缀（先预览再改）
  → 按密码本试密码 → 解压（含分卷组）→ 汇总报告（成功/失败/为什么）。
- **次级循环**：递归展开内层包 → 结果归集到目标目录 → 可选清理源包。
- **为谁做**：先为作者本人做到**每天真用**（本机磁盘上的大批量资源包整理）；向外扩展之前，不为想象中的用户写代码。
- **形态**：6 个选项卡（① 任务 ② 解压方式 ③ 清理与删除 ④ 密码 ⑤ 打包 ⑥ 设置），菜单只有 文件/视图/帮助。

## 2. 非目标（第一版明确不做）

**非目标比目标重要。** 以下都不做，且不允许"顺手"加进来：

- 外接硬盘 / U 盘 / 网络路径 / 盘符变化的专门适配（不做设备身份、断连恢复、卷 GUID；只要求"不写死盘符、不在源目录建工作区"）
- 资源管理器右键菜单、"发送到"菜单、下载目录监控、任务模板、文件清单导出
- 专项安装包提取（WIM/ESD、Inno Setup、InstallShield、MSI、SquashFS、LHA）—— 规则是**有真实样本才加**
- WinRAR 的"捆绑 / 默认依赖 / 自动探测安装"；只保留"用户自装、自选路径、程序只检测与调用"
- CLI 前端（第一版只做 GUI；但核心逻辑不得依赖 WPF，为 CLI 留位）、多语言（中文单语，不引 i18n 框架）
- 任何联网功能（不自动下载引擎、不上传任何东西）、自动更新（绿色目录分发）
- **安装包**：v1 原本不做；**用户 2026-09-27 明确要求"我是要 .exe 安装文件的"** → 已做
  （`installer\ArchiveFixer.nsi` + `scripts\installer.ps1`）。规则见 §11「安装包」那段，改之前先读。
- 压缩炸弹的"智能判定"（只做硬预算上限，不猜意图）

## 3. 技术栈与分层铁律

- **C# / .NET 8 + WPF**，目标框架 `net8.0-windows`（⚠ 仓库里**没有** `global.json`，
  本机 `dotnet --version` = **10.0.400** —— 用 SDK 10 构建 net8.0 目标框架是正常的；
  ⛔ 不许把 `TargetFramework` 升到 net9/net10，也不许引入只在更高框架才有的 API）
- 不用第三方 MVVM 框架（沿用现有手写 `INotifyPropertyChanged` 风格）；**不新增 NuGet 运行时依赖**；测试用 xUnit
- **分层铁律**：`Domain / Detection / Engines / Password / Extraction / Security / Storage`
  **不得引用 WPF**（`System.Windows.*`）；GUI 只通过 ViewModel 调用它们
- **多引擎架构自 M1 起成立**（不是"只要 7z"）：`IArchiveEngine`（probe/list/test/extract）、`EngineCapabilities`、
  `EngineRegistry` / `EngineSelector` / `ToolLocator`（**唯一**的外部工具路径来源）、统一错误码 / 进度 / 取消
- **四条禁止项**（只允许存在于 `Engines/SevenZip/` 内部）：① 核心模块直接拼 7z 参数 ② 直接解析 7-Zip 文本输出
  ③ GUI 里判断引擎错误字符串 ④ 递归逻辑里写死某个引擎的参数
- **引擎优先级** `WinRar → SevenZip →（后续）`，**先按能力筛、再用优先级 tiebreaker**，不可用的引擎直接跳过
  （⛔ 不许因为"排第一但没装"就打不开包）；UnRAR.exe 来源 = 用户自选路径 → 已装 WinRAR 目录 → 内置 `tools\unrar\`
- **引擎回退**：`UnsupportedFormat / UnsupportedFeature / EngineUnavailable / ParserRejected / KnownCompatibilityIssue` 可换；
  `WrongPassword / MissingVolume / UnsafePath / NoDiskSpace / PermissionDenied / SourceChanged / DeviceOffline / UserCancelled / OutputLimitExceeded` **不换**；
  损坏归档**默认先报损坏**；备用引擎只解出部分文件时状态必须是 `PartiallyCompleted`
- 升级 7z 之后**必须跑全量测试**（`SevenZipOutputParser` 解析的是 7-Zip 的文本措辞，版本会改措辞）
- 许可边界：内置 7-Zip（LGPL）+ 内置 UnRAR（RARLAB freeware，允许随包分发）；
  **`Rar.exe` / `WinRAR.exe` 绝不打包、绝不复制**（只检测与调用）；细节见 `docs/引擎与外部工具.md`

## 4. 目录结构

> 2026-09-26 按用户要求重排：本体进 `src/`、测试进 `tests/`，根目录只留项目级文件。

```
ArchiveFixer.slnx              解决方案（指向下面两个项目）
src/ArchiveFixer/              工具本体（WPF + 纯逻辑分层）
  Domain/        纯模型：ArchiveDescriptor / ArchiveTask / TaskState / ErrorCode / RenamePlan
  Detection/     魔数识别 / 后缀分析 / 分卷组识别
  Engines/       IArchiveEngine / SevenZipEngine / EngineRegistry / EngineSelector / ToolLocator
  Password/      密码本解析（列表式+映射式）/ 候选顺序 / 尝试上限 / 旁路说明文件
  Extraction/    单层 / 分卷 / 递归 / 工作区 / 发布 / 冲突
  Security/      路径预检 / 资源预算 / 危险文件 / 输出落点校验
  Storage/       源文件稳定性 / 目标空间检查 / 工作区根 / 密码列表落盘
  Packing/       打包
  Services/ ViewModels/ Views/ Helpers/ Models/ Converters/  （既有，逐步把逻辑下沉到上面几层）
  tools/         内置外部工具（7zip / unrar），随程序分发、随包带许可文本
tests/ArchiveFixer.Tests/      xUnit（与本体分开；跑不起真 7z 的用例自己跳过）
docs/                          规格与文档（见 §10 文档地图）
samples/                       只放**生成脚本 + 清单**，样本本体不入仓库
scripts/package.ps1            生成 dist 发行包（**用户说暂不打包**）
scripts/installer.ps1          出 .exe 安装包（NSIS；内容默认取 dist\ArchiveFixer-<版本>-独立）
installer/ArchiveFixer.nsi      安装包脚本（⚠ 必须 UTF-8 **带 BOM**，否则 makensis 报 Bad text encoding）
scripts/make-icon.ps1 + icon-gen/  重生成 `src/ArchiveFixer/Assets/ArchiveFixer.ico`
                               ⛔ icon-gen **不在解决方案里**，主程序构建/测试/format 都不碰它
dist/                          发行产物（不入库）
README.md  AGENTS.md  旧AGENTS.md  修改日志.md   一页纸 / 开发规则 / 历史快照(旧规则全文) / 流水账
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

- **⛔ 用户说暂不打包**：不生成 `dist\*.zip` 发行包（他连续真机测试期间，发行包会把旧版本固化住）。
  构建只用于刷新绿色目录；**等用户明说"打包"再打**。
- 换了 `.ico` 之后**必须重新构建**（图标由 `/win32icon` 在编译期塞进 exe）+ 刷新绿色目录；
  核对办法：从 `E:\ArchiveFixer\ArchiveFixer.exe` 抽图标出来看（`ExtractAssociatedIcon`）。
- 代码风格：4 空格缩进、私有字段 `_camelCase`、`Nullable` + `ImplicitUsings` 开启；注释写**为什么**
  （尤其"旧逻辑 → 新逻辑"这类踩坑记录要保留），不写"这行在做什么"。

### 5.1 绿色目录 `E:\ArchiveFixer\`（用户的真实测试位置）

- `bin\Release\...` **不是**用户数据所在地；重新构建后把运行时文件拷过去：
  `Copy-Item src\ArchiveFixer\bin\Release\net8.0-windows\{ArchiveFixer.exe,ArchiveFixer.dll,ArchiveFixer.pdb,*.json} E:\ArchiveFixer\`
  再拷 `src\ArchiveFixer\tools\` **和 `src\ArchiveFixer\bin\Release\net8.0-windows\docs\`**（2026-09-27 起
  csproj 会把面向用户的四份文档复制到输出目录的 `docs\` 下 —— 帮助菜单的「使用说明」按
  `<程序目录>\docs\使用说明.md` 找它，不拷过去就会报"路径错误"，真机上正是这么被逮到的）。
- **⛔ 绝不碰 `E:\ArchiveFixer\data`** —— 那里是用户的日志 / 密码列表 / 设置。

### 5.2 并发纪律

- **同一个 checkout 里不许并发跑构建/测试**：`--no-incremental` 的构建会重建 `bin\Debug`，正在跑的测试会看到
  "内置 7z 忽然不在"而**假红**；两个进程同时写 `obj\` 还会报 `MSB3021/MSB3027` 文件被锁定。
  同一时间只允许一个构建/测试在跑（多代理并行时用 `git worktree` 或排队）。
- 长活（构建 / 全量测试）**放后台任务**，别阻塞干等。

## 6. 不变量（红线，任何改动都不得违反）

1. **默认不改源文件、不删源文件**。删源只能是显式开启且校验通过的情形。
   - **例外（用户 2026-09-22 指示，版本二，已推翻版本一）**：**"成功 + 输出校验通过 + 未取消 + 属于本任务分卷组"** 时，
     源包（分卷组则整组）**移入 `其余物`**；**一键处理与手动「只解压」两条路径都照此办**。
   - **失败 / 部分完成 / 取消** → 源包**原地不动**、`其余物` **不生成**（红线，不变）。
   - 口径：`SourceHandling` **默认 `KeepInPlace`**（程序默认一个字节都不碰源包）；要连源包一起删，用**两档组合**：
     源包操作 = 放入其余物 + 删除操作 = `Delete`（彻底）/`RecycleBin`（回收站）；旧第三档 `DeleteAfterVerify` 已退役（旧值在 `Normalize` 里迁移）。
2. **识别和解压不绑定**：看着识别结果可以直接解压，也可以不改名。
3. **改名必须先预览 —— 这条只管手动档**：一键处理 = **自动改名自动解压**（算好计划直接执行，不弹任何框）；
   手动档仍然"先预览再改"，两条路用**同一份改名实现**。改名冲突默认"跳过或自动重命名"、**不得默认覆盖**；
   `Overwrite` 必须"先移到临时名 → 再删 → 再落位"（⛔ 禁止"先 `File.Delete` 再 `Move`"）；
   名称交换（`A.zip ↔ B.zip`）必须用临时名两阶段完成。
4. **解压产物不得越出目标根目录**。7z.exe 是外部进程，必须两道：解压前预检条目名（拒绝 `..`、绝对路径、盘符、
   UNC、`\\?\`、混合斜杠），解压后校验落点。Windows 保留名（`CON`/`PRN`/`AUX`/`NUL`/`COM1-9`/`LPT1-9`）、结尾空格、
   结尾点、备用数据流（`:`）一律清洗后再落盘（`FileNameHelper` 已实现，**不要回退**）。
5. **密码默认只存内存；用户 2026-09-24 明确允许"按本机加密落盘"**。
   - **日志、报告、剪贴板、详情窗口、异常信息一律脱敏**，含 `-p` 参数；`-p<明文>` 会出现在进程命令行，这是
     7z 命令行的固有限制，**必须在"已知限制"里写明，不得假装解决了**。
   - 允许按本机 **DPAPI（机器范围）**加密落盘到 `<程序目录>\data\password-list.dat`（**绝不写 C 盘**、绝不进日志），
     由 `RememberPasswordList`（默认开）整个关掉（关掉 = **既不写也不读**）。
     **代价（实现与文档都必须如实写明）**：同机任何本机用户都可能解开它；换机器 / 重装系统解不开 ——
     那时一律"忽略并提示"（不崩、不覆盖、不删文件），出路是「写回密码本」。
6. **部分成功不得显示为成功**；取消、超时、设备离线、分卷缺失一律不得显示成功。
7. **分卷缺失不得开始不可完成的任务**，必须报"缺哪几个"。
8. **递归必须有硬上限**（层数 / 文件数 / 总大小 / 单文件 / 展开比 / 密码尝试次数），且**多分支默认不展开**，必须问。
   - ⚠ **例外（一键处理档）**：一键档**批中间零弹窗**，多分支不问 —— 按保守档办（不展开）+ 写日志
     （判据 `ExtractionCoordinator` 的 `expandAll = !oneClickRun` 与 `SuppressDecisionPromptsForOneClickRun()`，
     见 §11"一键处理期间零弹窗"那段）。手动档照旧**必须问**。
   - 上限**必须存在，但不许写死在代码里**：解压前那四条（单文件 / 总大小 / 文件数 / 展开比）是**用户设置**
     （⑥设置 →「安全上限」，默认 **64 GiB / 512 GiB / 20 万 / 1000 倍**），唯一出口
     `ResourceBudgetOptions.FromSettings` + `ExtractionCoordinator.BudgetLimits`。超范围一律**夹回并说明**；
     上限类拒绝文案必须带 `StatusText.SecurityCapHint`（**空间不足那一档刻意不带**）。
   - 默认最大嵌套层数 = **5**（用户 2026-09-26 定）；一键处理每批轮数 = `OneClickCoordinator.RoundLimit`（夹在 1~10）；
     ⛔ **不许再有第二套轮数**（"界面写 5、程序按 10 跑"正是被拆掉的东西）。到顶把剩余内层包加进列表并勾好 + ①页「继续解」。
9. **单个任务失败不得中断整批**；反过来，用户点"停止后续"不得变成"强杀当前"（两者是独立取消源）。
10. **外部进程必须能收干净**（8 条全都要做）：安全传参（用 `ArgumentList`，**禁止拼 `cmd.exe` 字符串**）、重定向
    stdout/stderr、支持超时、支持取消、正确关闭句柄、正确终止子进程。取消**只杀自己启动的 PID 及其子进程**；
    **禁止按进程名批量杀 `7z.exe`**。
11. **源文件变化后不得继续使用旧识别结果**：任务开始时记录大小/修改时间快照，处理中变化即停下并报"源文件已变化"。
    （已实现：`Storage/SourceFileSnapshot.cs`，分卷整组，**连"记的时候在不在"一起记**；识别完成即拍快照，
    所有解压入口在 `ExtractSingleTaskAsync` 开头唯一收口比对，递归每层开工前再问一次。边界：**不比内容哈希**、
    **解压进行中**改源文件拦不住那一次、改名 / 源包搬进其余物**不算**变化。）
12. **工作区、源文件、最终输出三者相互独立**：中间产物不得写进源目录，也不得直接写最终目录。
    - 默认位置**跟着输出盘走** = `<输出盘>\.ArchiveFixer.work`（点开头的隐藏目录）；用户显式设过
      `CacheRootDirectory` 时永远以它为准（`<它>\work`）；拿不到盘才回落程序目录 + WARN。
    - **默认全清（含空壳）**：失败 / 取消 / 部分完成收尾时，把这一单**自己的任务工作区目录整份删掉**
      （含 stage、内嵌归档副本、已解出的中间件），**递归逐层工作区同样清掉**；零文件空壳无论如何都删。
      只有 `KeepFailedWorkspace`（默认 false，③页「工作区残留」）打开时才保留现场（有文件一律留，并写 INFO 说清）。
    - 三条红线不随设置变：**成功路径的清理口径一个字不改**、**源包在任何情况下原地不动**、**已定稿搬出去的内容物不受影响**；
      删除前两道容器内校验（在生效的工作区根之下 + 目录里只许有我们自己造的子目录名），越界只写 WARN、一个字节都不删。
13. **清空/清理任务不得误删工作区之外的文件**；清工作区必须先经用户确认。
14. **结果可追溯**：每个最终结果都能追到具体任务与具体引擎，报告里必须带引擎名 + 版本。

## 7. 状态与文案（既有资产，不要推翻）

- 所有界面状态字符串统一引用 `Models/StatusText.cs` 常量，**禁止手写中文字面量**。
- 失败分类沿用既有中文口径：`密码错误 / 文件损坏 / 权限不足 / 输出路径冲突 / 分卷缺失 / 路径过长 / 已取消 / 未知错误`，
  以及 `7z不存在` / **`没有可用的解压引擎`**（7z 与 UnRAR 都不可用时用它）/ `文件名已加密` / `达到密码尝试上限` /
  `长时间无响应`（最后一条是**提示文案不是状态**）。
- 新增状态必须同时更新 `StatusText` + `StatusToBrushConverter` + `TaskSummaryService`，三处缺一不可。
- **统计不得依赖中文文案比较**：`Status` 需要能被机器判定（`TaskState` 枚举；中文留给显示层）。
- 日志按级别上色：红 = ERROR / 黄 = WARN / 黑 = INFO；**要判"留不留细节"只准看机器终态**（`TaskOutcome`），不许比中文。

## 8. 隐私红线

- **不读取工作区以外的用户个人目录**（下载目录、密码本、私人文件夹）。需要本机真实样本时，由用户把样本拷进
  `samples/`（或告知具体路径）后再用。
- 密码、真实站点名、个人路径**不得**出现在：仓库文件、日志、汇报、截图、测试数据、注释里。文档与测试里的密码
  一律用占位符（`<示例密码>`）。
- 应用自身日志允许记录密码**状态**（是否加密、是否试过、是否成功、是否超上限），**不记录内容**。

## 9. 干活的纪律

**§9.1 桌面与鼠标：默认不碰，除非用户当前明确授权**（用户 2026-09-26 收回 → **2026-09-27 晚重新授权**）

- **当前状态（2026-09-27 晚）**：用户原话「现在你可以使用桌面和鼠标来操控」，用于他点名的真机测试
  （`…\测试\BBB\…` 与 `…\测试\CCC\CCC` 两个目录）。授权**以用户最新一条明确指令为准**：
  他说可以就可以，说了停就停 —— ⛔ 不要拿"以前被收回过"当理由拒绝，也不要拿"他以前同意过"当长期许可。
- 授权期间仍然不许：抢前台骚扰他干活、改桌面状态（分辨率 / 任务栏 / 别的窗口）、
  **删除或修改他给的样本目录以外的东西**、跑会弹 UAC 的操作。
- 没有授权时的替代做法（一直有效）：`docs/人工测试清单.md` 里 A/B/C 各组**真机手动项交给用户**；
  代理只做**造样本 + 逐步操作说明 + 读程序自己的日志与文件结果 + 改代码**，
  端到端验收走不碰桌面的路（读日志、临时目录里跑流程层、写自动化测试）。
- ⚠ 这一条管代理与脚本，**不管程序自己**：程序在"需要用户输入"时把自己的窗口拎到前面是用户明确要求的行为；
  但触发条件已收窄 —— **只有窗口没能出现在最前面时才闪 + 响**，已经摆在他眼前的那种一声不响、一下不闪
  （判据 `WindowAttention.ShouldAlert`，两种结论都进日志）。
- **真机自动化备忘（2026-09-27 实测）**：这台机器上 WPF 主窗口的 UIA 树**只到 TabItem 为止**，
  页内控件（按钮 / 勾选框 / 列表）在 UIA 里看不见（`FromPoint` 一律返回 `Tab`），
  所以驱动 GUI 只能靠**截图定位 + 鼠标/键盘**（`_tmp\ArchiveFixer\shot.ps1` 用 Windows PowerShell 5.1 截屏，
  它才有 `System.Drawing`；`ui.ps1` 做点击 / 输入）。每一步都截图核对，比盲点坐标靠谱得多。
  另一个坑：`Set-Clipboard` + `Ctrl+V` 是往文件夹对话框里填中文路径最稳的办法（`SendKeys` 打不干净 CJK）。

**§9.2 测试（按改动范围分级 —— 用户 2026-09-27 指示，全局 §14 规矩 25）**：
验证强度与**改动影响面**成正比，⛔ 不是"改一行也要跑全量"——

- **小改动**（单点逻辑 / 文案 / 样式 / 一处 UI 交互）：只跑**与改动点直接相关**的定向测试
  （受影响用例、最近的测试类），报告里写清**跑了哪几条、结果如何**；⛔ 不跑全量回归。
- **大改动**（跨模块、动公共契约或不变量、改设置序列化 / 引擎与管线主路径、依赖或工具链升级、
  发布或收尾前的最后一轮）：跑**全量**。
- 判据是"**这次改动会碰坏谁**"（直接调用方 + 覆盖它的断言），不是改了几行。
  本项目里凡动 **`ExtractionCoordinator` 的候选循环 / `OutputVerifier` 口径 / 工作区与其余物的清理**，
  一律按大改动对待（它们是不可逆路径）。
- **红检** = 修一个缺陷就要能**把修复临时撤掉、亲眼看到对应测试变红**（写完再恢复修复），
  新增守门测试要说明红在哪；这条与小改动不冲突（定向跑一条也是跑）。
- ⛔ 跑不起真 7z 的用例自己跳过，不许改成"假装跑过"；⛔ **没跑过的验证不许说成通过了**。
- 遇到 §11 那几条已知 flaky，**单跑确认**，别去改产品代码或断言。

**§9.3 用户可见字符串里不许写 Markdown**：`**加粗**` 会原样显示成星号，强调一律用「」；
有 `UserFacingTextTests` 全量扫描 `.cs` **与 `.xaml`**（跳过 `<!-- -->` 注释），新写文案后跑它。

**§9.4 文档与记录**：**改动要写进 `修改日志.md`**（一条一行流水账）；每条新需求追加进 `docs/需求变更.md`
（该文件只追加）。目录结构 / 功能变了要同时改"现状说明"类文档（README、本文件 §4 §5、`scripts/package.ps1`）；
而 `docs/需求变更.md`、`修改日志.md`、`docs/打包功能.md` §10 这些**历史记录**里的旧路径**按原样留着**。
README 只做"一页纸 + 跳转"，⛔ 细节不许再往回收。

**§9.5 一条被反复踩出来的工程习惯**：同一件事的**真值只允许有一个出口**（轮数、上限、落点通知、容器类型、
候选顺序…）；"同一件事在多处出现"必须**值 + 通知**两条都钉住（值对而界面不刷新 = 用户读成"没生效"）；
要删/要写盘的动作判据只准读事实（枚举终态），**兜底一律落在"什么都不做"那一档**；排错先**量化**（行数 / 字节数）
再改，改完用**同一口径**进回归；用户要求的"提醒"就是提醒，⛔ 不许顺手把它做成自动行为。

## 10. 文档地图（`docs/` 里每一份是什么）

| 文档 | 一句话 |
|---|---|
| `需求书.md` | 项目总需求说明 / AI 开发任务书，2026-09-21 起**冻结**为需求全集（此后只追加到需求变更）；**文末一节 = 「用户指示汇总」**（由旧 AGENTS.md §0 压缩而来，一行一条） |
| `需求变更.md` | **只追加**的需求变更日志：冻结之后每条新需求 / 变更的完整落地记录 |
| `需求评审与考古.md` | 为什么这样定：08-09 基线真实完成度、设计.md 问题清单与逐章归属表 |
| `输出与整理模型.md` | 落点与整理模型的**实现契约**（落点模型 v2、其余物、特定解压例外档、续解层规则） |
| `界面重构方案.md` | 6 个选项卡的界面重构方案（用户已过目拍板） |
| `打包功能.md` | 打包功能的设计说明 + 实施记录（流程、分卷规则、许可边界） |
| `引擎与外部工具.md` | 能用哪些引擎与外部工具的工程说明书 + **许可证边界** |
| `WinRAR功能参考.md` | 对外部产品 WinRAR 的对照研究笔记（阅读理解的产物，不是需求） |
| `功能一览.md` | 现在能做什么的**详细版**（README 只留一行一条，细看翻这里） |
| `设置项.md` | 设置项一览的**详细版**（字段名 = `data\appsettings.json` 里的键） |
| `使用说明.md` | 面向**使用者**的说明书（不需要懂编程，看完这一份就够） |
| `人工测试清单.md` | 给人照着点的人工测试清单（A/B/C 各组，与使用说明配套） |

> 另有 `<仓库外>\ArchiveFixer\项目设计.md` = **需求全集 / 长期愿景**，有效但需按非目标裁剪。
> ⛔ 别把"没写进本文件"理解成"被否决了"。

## 11. 当前验证状态（2026-09-27，落点模型 v2 落完并全量通过之后）

- **v0.1.0（第一个对外版本，2026-09-27 用户定）**：`ArchiveFixer.csproj` 的 `Version/AssemblyVersion/FileVersion`
  全部改成 **0.1.0**（旧的 11.0.0 只是开发期编号，从没发布过）；`docs/使用说明.md` / `docs/人工测试清单.md`
  里的版本号同步（`docs/需求评审与考古.md` 里那一处是**历史证据**，按 §9.4 留着）。
- **已发布**：仓库 `https://github.com/klaraljy/ArchiveFixer`（MIT、公开、About = 那句功能简介 + 13 个 topics），
  Release **v0.1.0** 附两个资产（本地 `dist\` 里是中文名 `ArchiveFixer-0.1.0-框架依赖.zip` 2.19 MiB / 18 项、
  `ArchiveFixer-0.1.0-独立.zip` 70.83 MiB / 477 项；⛔ 别写成 `-独立版.zip`，README / CHANGELOG 各错过一次）：
  - ⚠ **GitHub 会把资源名里的非 ASCII 字符直接抹掉** —— 实测 `…-独立.zip` 落到服务端变成 `…-.zip`
    （用纯 ASCII 的百分号编码 URL 上传也一样），而 **`label` 保留中文**。所以发布件一律
    「**ASCII 文件名 + 中文 label**」：`ArchiveFixer-0.1.0-framework-dependent.zip`（label `…-框架依赖.zip`）/
    `ArchiveFixer-0.1.0-standalone.zip`（label `…-独立.zip`）；README / CHANGELOG 里写的就是这两个名字。
  - 本地另留一份可直接跑的独立版 `E:\ArchiveFixer-0.1.0-独立\`（用户 2026-09-27："在本地也给我留一份，独立的"）。
  - ⚠ **传资产**（2026-09-27 晚用户更新代理后复测过的结论）：
    - 代理下 `gh release upload` **小资产可以**（39 B 附件 3.7 秒成功），**70 MB 会在约 86 秒时 `EOF` 断掉**
      （代理顶不住大 POST；更早的版本则是对 `uploads.github.com` 直接 **HTTP 404**，`gh` 会顺手把整个 release 回滚掉）。
    - **大文件走直连**（临时清掉 `HTTP_PROXY`/`HTTPS_PROXY`）：`gh release upload` 直连传 70 MB **成功过**（约 3 分钟），
      `curl.exe -X POST -H "Authorization: Bearer $(gh auth token)" -H "Content-Type: application/zip"
      --data-binary "@<zip>" "https://uploads.github.com/repos/<owner>/<repo>/releases/<id>/assets?name=<ascii>&label=<urlencode(中文)>"`
      直连也成功过（约 370 KB/s，200 秒）；直连时 `api.github.com` 偶发 `EOF`，重试即可。
    - ⛔ `gh release upload --clobber` **连别的资产一起删**（实测把已传好的框架依赖包删了，回到 Release 前先看 `gh release view`）。
    - `gh` **不能设中文 label**，但传完可以补：`gh api -X PATCH repos/<o>/<r>/releases/assets/<数字 id> --input <{"label":"…"}>`
      （已验证；数字 id 从 REST 接口取，`gh release view --json` 给的是 `RA_…` 节点 id，拿去 DELETE/PATCH **不认**）。
- **分卷名后面粘着垃圾（2026-09-28 真机事故，用户当场报）**：百度网盘给每个分卷名缀「删除」
  （`giu910.7z.001删除` / `.002删除` / `.003删除`）。老判据要求"卷标记必须是最后一段" → 三卷散成三个任务，
  而且「修正后缀」把它们改成 `amb909.7z` / `amb909(1).7z` —— **卷号被吃掉、分卷链断掉**（内容没坏，名字对不上）。
  修法：`ExtensionHelper.TrySplitVolumeSegment`（**判据只此一处**）把段拆成「分卷标记 + 粘着的垃圾」，
  `FileNameHelper.IsVolumePartFileName` / `StripVolumeMarkers` 与 `VolumeGroupDetector` 全转调它，
  垃圾记进 `VolumeNameInfo.Tail`（缺卷提示才会带同样的尾巴）；`RenameService` 那道"分卷不许改名"的闸门因此自动生效。
  - ⛔ 老口径**不许回退**：另起一段的后缀（`x.7z.001.txt` / `x.001.bak`）照旧**不算**分卷；纯数字尾巴（`0012`）不猜。
  - ⚠ **遗留**：程序只做到"认得出 + 不改坏"；带垃圾尾巴的组交给 7-Zip 仍会报缺卷（7z 只认 `<基名>.001` 这套名）。
    下一步要做的是**显式**的「把整组名字改回标准名」（沿用 `VolumeNameRepair` 的口径：用户点了才改、只改名字、绝不覆盖）。
  - 用真实数字钉住：`giu910` 三卷 7,516,192,768 ×2 + 3,975,934,338；`amb909` 现场 = 第一卷 2,147,483,648（带 `37 7A BC AF 27 1C`）
    + 第二卷 1,890,791,346。用例 `VolumeJunkTailTests`（**31 条** / 10 个方法），红检时把修复 stash 掉会红 8 条。
  - ⚠ **判据不许"一见 zip 成员就让位"**（2026-09-29 复核逮到的真缺陷）：名字路（`VolumeNameRepair.PlanJunkTailGroup`）
    原来看"手上这一片内容是跨盘 zip 成员"就把整组让给内容级那条路。可 **7-Zip 自己造的 `-tzip -v` 分卷 zip**
    第 1 片也是"PK 开头、结尾没标记的成员"，而它的**末片 EOCD 写的是盘号 0**（不是 PKZIP 那套跨盘 EOCD）→
    内容级拼不出组 → 名字路又让了位 → **两边都不动**：`enc.zip.001删除` 一个名字都没改，
    7-Zip 打不开（实测 `7z l` 报 ERRORS、`Type = zip`），任务还把它报成「密码错误」。
    现在让位条件是"**目录里真有一片自述带盘号的跨盘 zip 末片**"（`HasSpannedZipTailInDirectory`，判据仍只问
    `VolumeNumberFromContent`）；真 PKZIP 那套（`.z01`/`.zip`）照旧走内容级。用例 `EncryptedVolumePipelineTests`（2 条）。
- **安装包（用户 2026-09-27 追加："我是要 .exe 安装文件的"）**：`installer\ArchiveFixer.nsi`（NSIS 3.10，
  `D:\Codex Tools\NSIS\nsis-3.10\makensis.exe`）+ `scripts\installer.ps1`（默认拿 `dist\ArchiveFixer-<版本>-独立`
  当内容）→ `dist\ArchiveFixer-0.1.0-setup.exe`（**47.82 MB**，164.57 MB 内容压到 29.1%，编译约 110 秒）。
  - 装法：**每用户、`RequestExecutionLevel user`（不弹 UAC）**，默认 `%LOCALAPPDATA%\ArchiveFixer`（安装页可改）；
    ⛔ 绝不能默认 `C:\Program Files\`（程序要往自己目录的 `data\` 写日志 / 设置 / 密码列表，那儿没写权限）。
  - 卸载**默认保留 `data\`**（交互式会问一次；`/S` 静默卸载一律保留）。⛔ 不许改成"卸载就清空"。
  - ⚠ `.nsi` **必须 UTF-8 带 BOM**（否则 makensis 直接报 `Bad text encoding`）；
    ⚠ **注释行末尾不要留反斜杠**（NSIS 当续行符，会把下一行命令吞掉 —— 实测 FindFirst 那一行被吞过）；
    ⚠ 命令行开关的判据只读 `${GetOptions}` **返回值**，别靠 `IfErrors`（实测搞反过 → 静默装也建快捷方式）；
    ⚠ 卸载删桌面 `.lnk` 前必须看安装时写的 `DesktopShortcut` 标记，否则会删掉**用户自己的**快捷方式（实测踩到，
      用户桌面那个 `E:\ArchiveFixer\` 的快捷方式被删过一次，已重建）。
  - **验收（2026-09-28 凌晨，静默模式走完两条路）**：`/S /D=<临时目录>` 装出 **478 个文件 / 164.73 MB**，
    `ArchiveFixer.exe` / `ArchiveFixer.dll` / `tools\7zip\7z.exe` 与发布目录**逐字节相同**（SHA256 比对）；
    静默装**不建**快捷方式、不写标记；`/S /SHORTCUTS=1` 会建桌面 + 开始菜单并写标记（卸载时按标记删干净）；
    两轮卸载后程序文件 / `tools` / `docs` / 本地化目录全清、注册表两条键全清、**`data\keepme.txt` 都保住了**、
    用户桌面快捷方式（`E:\ArchiveFixer\ArchiveFixer.exe`）**毫发无损**；装出来后跑过一次，程序正常起窗口、
    自己建出 `data\appsettings.json` + `password-list.dat` + 日志。
  - 本地另存一份：`E:\ArchiveFixer-0.1.0-setup.exe`（用户："这个本地也要留一份就放在独立版旁边"）；
    Release 上的资源名 `ArchiveFixer-0.1.0-setup.exe`（label `ArchiveFixer-0.1.0-安装包.exe`）。
- **README 截图**在 `docs/images/`（`task-tab.png` / `one-click-confirm.png` / `batch-done.png`），
  是**用生成的示例包**（3 个几十字节的假包：`说明-N.txt` + 一张示例图）在独立版临时副本里实拍的 ——
  ⛔ 不许把带真实路径 / 样本包名 / 作者邮箱的截图放进仓库（§8）；主界面那两张特意裁掉了标题栏
  （标题栏里有作者邮箱），裁剪脚本 `_tmp\ArchiveFixer\crop.ps1`（裁 x10/y40/1380x850）。
- **「说明」窗多了功能详解**（用户 2026-09-27："里面有专有名词的详细解释，但是没有功能的详细解释"）：
  `HelpContent.Features`（16 条：六个选项卡 + 一键处理 + 落点 / 冲突 / 空间三件事 + 安全 + 排障），
  与 `HelpContent.Glossary`（术语表）分两段、两个小标题显示；`BuildPlainText`（复制全部说明）两段都带。
- **开源前的脱敏**（用户 2026-09-27："完整上传至 GitHub，而且开源"）：仓库里凡是**个人路径 / 样本包名 /
  素材站点名**一律换占位符 —— `<盘符>:\<下载目录>\…` → `<测试目录>\…`、样本包名 → `<样本包>`、
  真实站点 → `example.com`、`密码本2` → `密码本示例`、仓库外的分析与临时目录 → `<仓库外>\` / `<临时目录>\`。
  ⛔ 这是 §8 隐私红线压过 §9.4"历史记录里的旧路径按原样留着"的一次**有意例外**（`旧AGENTS.md` 与历史日志一并改了）。
- `dotnet build ArchiveFixer.slnx`：**0 错误 0 警告**；`dotnet test`：**1994 条**（通过 **1993** / 失败 0 / 跳过 1；
  2026-09-29 深夜跑的全量，3 分 24 秒。⚠ 跳过的那 1 条是"真机副本 + 有密码时解出内容" ——
  这一组是 `-mhe`（文件名也加密）的包，密码由用户放进环境变量 `ARCHIVEFIXER_REAL_VOLUME_PASSWORD` 才会跑，
  没拿到就**如实跳过、不伪装成验过**）；`dotnet format ArchiveFixer.slnx --verify-no-changes`：**通过**。
  ⚠ 这条数字**只在这里写一次**：README 等文档要报数字就从这里抄，别再各写一份。
- **「不删原包」安全档 + 空间侦察**（2026-09-27 晚，用户追加）：
  - ⚠ **「不删原包」是"测试期专用"的**（用户 2026-09-27 原话："这个我想好像在测试完成之后就不用留着，
    因为这个本来就是测试，如果用户有这个空余空间那就不用测试了，直接解压就行了，**打包软件的时候我会让你删掉的**"）
    —— 到"打包发行"那一轮要把它整块删掉（`MainViewModel.SpaceTightKeepSource`、①页那颗勾、
    `_spaceTightKeepSourceThisBatch` 与它那两条文案、`StatusText.SpaceTightKeepSource*`、
    相关用例），**空间侦察（`SpaceTrendMonitor`）保留**（它不属于测试专用，是"空间怎么变"的常规交代）。
  - ①页「空间不足」**右边**那颗勾 = 那个模式的**安全档**（`MainViewModel.SpaceTightKeepSource`，同样是运行期开关）：
    并发 / 排序 / 其余物删除三件事与默认档完全一样，唯一差别是**源包一个字节都不动**
    （`ExtractionCoordinator._spaceTightKeepSourceThisBatch` → `PostProcessSuccessAsync` 里那一支什么都不做）。
    两档耦合只有一条：勾安全档 → 顺带勾模式；关模式 → 安全档一起关。文案分两条
    （`StatusText.SpaceTightKeepSource*`），⛔ 不拿"会删源包"那条去套安全档。
  - `Storage/SpaceTrendMonitor.cs`：每 **5 秒**探一次目标盘可用空间，变化 ≥ **64 MiB 且 ≥ 1%** 才写一行
    `空间变化：…`；批首 / 每个任务开工 / 收尾 / 批末各采一针，批末一条
    `空间曲线（本批 N 针）：起 … → 最低 … → 收 …，全程最多同时占用约 …`。
    ⛔ **只观察不判断**（不参与放行与调度）；取不到可用空间时如实记"这几针取不到"。
    ⚠ **任务那两针只记不报、注脚不写文件名**：第 45 条那条"成功的任务只留一行"是用
    "含该文件名的行数 ≤ 3" 钉着的（`Item45LogAndPasswordTests`），多写一行或让曲线带上文件名都会把它废掉
    —— 全量回归实测逮到过（`SpaceTrendMonitorTests.任务边界的采样只进曲线_不单独报一行` 现在钉着这个理由）。
  - **真机实测（2026-09-27 晚，用户点名目录）**：
    `BBB` 10 个包 5.22 GiB **不删原包**档解压 10/10 成功、曲线 `起 26.74 GiB → 最低 20.43 → 收 21.52`、
    最多同时占用 6.31 GiB；`CCC\CCC` 指定位置解压 10/10 成功、曲线 `起 21.52 → 最低 15.27 → 收 16.3`；
    **源包逐字节没动**、`其余物` 链尾清空、设置没被改。
    **删除档**（用户确认"AAA 有备份"后跑同一批包）：10/10 成功，`.7z` 从 10 → **0**，
    产物 `CCC\CCC` **519 个文件 / 5,605,054,622 字节**；
    **"源位置 + 解压位置"的实时合计全程封顶 5.22 GB**（开工前 5,346 MB → 跑完 5,345 MB，
    中间"源目录减、解压目录增、合计不变"，还出现过内层 `.rar` 被清掉时的回落）；
    程序曲线 `起 26.74 GiB → 最低 23.56 → 收 26.74 GiB，最多同时占用 3.18 GiB`。
    细节见 `docs/需求变更.md` 第四轮 §8–§10。
  - **真机逮到的缺陷（已修）**：确认框里那一行「源包：留在原地」与红字"会自动覆盖为「源包 → 放入其余物」"
    自相矛盾 —— 确认框打开时会按折叠区单选框**重算**那一行，而单选框只反映设置。
    修法：`OneClickConfirmFacts.SourceEchoLocked` + `OneClickOptionsWindow` 里 `if (!_sourceEchoLocked)` 守卫。
- **「空间不足」模式**（2026-09-27 用户拍板）：①页主操作栏那个黄色勾选框，**运行期开关**
  （`MainViewModel.SpaceTightMode`，⛔ 不写设置、不记忆），批首钉死、批尾清掉。
  一个布尔管四件事（判据全在 `ExtractionCoordinator._spaceTightThisBatch`）：
  并发由 `ExtractionScheduler.ResolveSpaceTightParallelCount` 定（体积相近的小包 5 / 其余 3，
  与空间建议取小、绝不取 0；**再与用户的「最大并发解压数」取小** —— 2026-09-27 追加：
  模式上限与「全速」照旧不生效，但**允许用户把并发压得更低**，慢盘上少开几个反而更快也更不卡）、
  排序按 `TaskSpaceEstimate.NetOccupancyBytes`（**只改顺序，放行仍按峰值**）、
  `PrepareRestHandlingForBatch` 强制 `Delete`、源包在定稿 + 校验通过时由
  `PurgeSourcePackageForSpaceTight` **当场永久删除**（执行体 = `Extraction.SourceCleanupService`，
  它在管线上的**唯一**调用点就是这里）。
  动手前必须看得见：确认框红字 `StatusText.SpaceTightConfirmText`（正文两行同时换成覆盖后的值）+
  ②③页橙色提示 `StatusText.SpaceTightOverrideNotice`。
  契约在 `docs/输出与整理模型.md` §3.4.2，用例在 `SpaceTightModeTests` / `SpaceModeTests`。
- **「进度 / 部分完成」两处显示口径**（2026-09-27 真机反馈改的）：
  - **成功的任务显示「解压成功 100%」**：判据是**机器终态**（`Outcome == Succeeded` → 一律补 100），
    ⛔ 不读那个瞬时百分比（引擎最后一帧常常只到 99，而且有的收尾分支会 `ClearProgress()` 清成 -1）；
    成功收尾同时落 `EndTime`（否则"耗时"会一直往上涨）。失败 / 部分完成 / 取消**不显示**百分比。
  - **批末汇总里「部分完成」单独一档**（以前被算进"未处理"，与一键处理汇总的说法不一致）。
  - ①页「大小」列宽 **118**（`1112.53 MiB` 这种最长形状要能完整显示，⛔ 别再收窄）。
- **导入后的空间体检**（2026-09-27）：**三个触发点** —— 导入完成（`ScanCoordinator.AddPathsAsync`）、
  ①页「选择…」（`MainViewModel.SelectOutputDirectory`）、②页「指定位置 → 选择…」
  （`SettingsViewModel.OutputDirectoryPicked` → MainViewModel 的回调）——
  都调 `MainViewModel.CheckSpaceForTasksAsync` → `ReportSpaceCheck`
  （判据复用 `ExtractionCoordinator.BuildSpaceAdvice`，⛔ 不另写一套）。
  放不下时 WARN + ERROR 两条日志 + `DialogService.ShowSpaceShortageWarning` 弹窗点名；
  ⛔ 它只是提醒：不改设置、不改勾选、不拦着不让跑。
- **落点模型 v2**（2026-09-27）：终点落法固定（不再让用户选）、手动「解压到当前文件夹」、
  一律不塌缩、续解层按三条优先判据（**同层多个内层包 → 建**；**父层已出内容物 → 不建、并进父层**；
  剩余"干净单链过路层"看开关 `OmitMiddleContinuationLayers`，默认关 = 忠实档）。
  契约在 `docs/输出与整理模型.md` §1.1 / §3.1 / §3.3.1；判据出口三处：
  `OutputPlacement.ResolveDestinationDirectory`（落点）、
  `OneClickCoordinator.ShouldAddContinuationLevelLayer`（续解该不该建那一层）、
  `ResultFinalizer.Plan(..., suppressPackageFolderLayer:)`（定稿要不要套包名那一层）。
- **一键处理期间零弹窗，唯一例外是批末汇总**（用户 2026-09-27 两次拍板："以后不要出现弹窗" →
  "最终汇总框可以留、其他都删"）：`ExtractionCoordinator.SuppressDecisionPromptsForOneClickRun()` 一处收口
  （冲突询问复用既有的 `_conflictPromptUnavailable`），多分支 / 缺卷补救各自读 `oneClickRun`；
  批**跑完之后**那一个 `ShowInfo(outcome.Summary)` 保留（那时不会卡住任何任务），日志那一行照旧写。
  ⛔ 以后**不许**在一键档的**批中间**新增任何"要用户点一下"的框 —— 需要决策就按保守档办 + 写日志。
- **递归多层的结果校验**：展开 > 1 层时**不拿第 0 层清单当预期**（`PostProcessSuccessAsync` 的
  `recursion` 参数），只做非空 / 落点 / 预算三道 —— 否则必然误报「解压失败」（真机 13 个包全中过）。
- **已知 flaky（全量并发下偶发假红，单跑必过 —— 遇到先单跑确认，别去改产品代码或断言）**：
  - `RecursiveExtractorTests.用户确认继续后…` / `递归取消_工作区保留` / `previousDecision_只处理候选里的归档不重新全盘扫描`：
    真 7z 用例在全量并发下偶发"引擎操作失败"（实测 `无法创建工作区目录 …\data\work\recursive\branches_…` ——
    多集合并行时多个 7z 进程抢磁盘），是**测试侧争用**，不是产品缺陷。
  - `SecurityGuardTests.CheckBeforeExtract_NotEnoughFreeSpace_IsRejectedWithNumbers`：先读一次可用空间、再拿
    "可用 + 1 字节"当需求，中间任何别的进程释放 ≥1 字节就会翻成 Allowed（实测 6 次全量偶发 1 次）。
  - `PasswordListStoreTests.启动_先加载记忆再逐本合并_日志只写条数与文件名`：读日志文本类断言在全量并发下偶发假红
    （它断言"日志里不出现数据根完整路径"，而启动那条「工作区残留」日志按设计要写实际位置）。
  - `AsyncDeadlockGuardTests.递归解压_在单线程同步上下文里同步等待_不会死锁`：真 7z 调用套在**单线程同步上下文**里、
    外面只给 **30 秒**的界 —— 全量并发下（多个真 7z 同时抢盘）实测偶发超时假红；**单跑 3 次全绿**。
    判据本身没问题（它防的是 sync-over-async 死锁），只是那个界在满负载下不够宽；遇到先单跑确认。
  - `EnginePriorityTests.运行时设置能改变选择结果`：它动的是**进程级静态**（`EngineRuntimeSettings.SetPriority`
    / `ResetToDefaults`）并且用 `EngineRegistry.CreateDefault(new ToolLocator())` 读**真引擎可用性** ——
    全量并发下别的用例（尤其那些构造 `MainViewModel` 的，它们会写 `ToolLocator` 的静态路径）会把它搅翻，
    实测 2026-09-29 全量偶发 1 次、**单跑 14 条全绿**；遇到先单跑确认。
- **真样本验收：RAR4 / 跨盘 ZIP 的内容级识别（2026-09-29，用户三套真包 `<测试目录>\AAA`）**：
  - 样本**不进仓库**：用例 `RealVolumeSampleTests`（6 条）从 `<仓库同级>\<临时目录>\aaa-real\` 找，
    或读环境变量 `ARCHIVEFIXER_REAL_SAMPLE_DIR`；**样本不在就跳过并说明**（不是失败）。原目录**只读**，用例只在临时副本上改名。
  - 真样本当场揪出两个老代码的真缺陷（合成样本永远证明不了"真实产品写出来的字节我读得对不对"）：
    ①**跨盘 zip 第 1 片**以 `PK\x07\x08` 开头（不是本地文件头）→ 老判据「头是本地文件头 **且** 尾是跨盘标记」两条都不满足；
    现在非末片只认**开头是任意 PK 签名**，顺序仍由 EOCD 盘号 + 片数 + **尺寸规律**定。
    ②**RAR4 卷号**在卷尾 `EARC_VOLNUMBER`，是紧跟 `EARC_DATACRC` 的 **2 字节**（`HEAD_SIZE = 20 = 7+4+2+7`）；
    老写法当 4 字节、按 `块起点 + HEAD_SIZE − 11` 读 → 真样本读出 `0xCF7C` / `0x01EF02` → 分卷一律拒绝。
    现在**按字段顺序算偏移**并要求「各可选字段总长 == HEAD_SIZE」，布局不符**不认**（⛔ 不猜）。
  - ⚠ **命名**：`Rar!\x1A\x07\x00` 是 **RAR 1.5–4.x**（WinRAR 里叫 **RAR4**）；RAR3 与 RAR4 **共用同一签名**，
    ⛔ 代码 / 注释 / 文案都不许写"这是 RAR3"（`ReadRar4Legacy`、`Rar4Legacy*`）。
  - ⚠ **三套真包内容是加密的**（每个 mp4 `Encrypted = +`，空密码报 `Wrong password`）→ 真样本上**验不到"解出的字节一致"**，
    只能验到"认得出 / 定得序 / 改得回标准名 / 引擎真打开了这一组"（如实断言「密码错误 + 输出目录 0 字节」）。
    补上"逐字节一致"的是**本机 WinRAR 命令行造的真 RAR4 三卷**（`-ma4 -m0 -v1m`，不加密）。
  - ⚠ **RAR5 至今没有真实样本**（只有构造字节用例）；老式编号族 `.rar`/`.r00` 按设计**不认**。
  - ⚠ **回退代码后一定要 `--no-incremental` 重编**：`Copy-Item` 会把旧时间戳带回来，MSBuild 会跳过重编 ——
    实测表现为"红检恢复后复跑还是 12 条红"，跑的是红检那份二进制。
- **四路只读核查查出并修掉的三条真缺陷（2026-09-29，用户"不要停"那一轮）**：
  - ⚠ **密码记忆读失败会被自己的启动接线覆盖**（唯一会真丢数据的一条）：读不出来时（换机器 / 重装系统 /
    文件损坏）老代码只写一句 WARN 就返回，而文案还写着"程序没有覆盖、也没有删除那份记忆" ——
    紧接着 `SetRememberedBookPaths` → `PersistPasswordList` 就**替换式落盘**，那份解不开的记忆当场没了。
    现在：读失败先 `PasswordListStore.TryBackupUnreadableFile` **复制**一份
    `password-list.dat.unreadable-<时间戳>.bak`（⛔ 只复制，绝不移动 / 删除原件），文案如实点名备份名。
    用例 `PasswordListStoreTests.记忆_读不出来时先另存备份_之后落盘不再毁掉原件`。
  - ⛔ **一键档的"零弹窗"抑制必须排在 `ResetBatchConflictState()` 之后**：那个方法会把
    `_conflictPromptUnavailable` 置回 false，先抑制后清零 = 没抑制（实测设置里选「询问」时一键档批中间
    仍会弹聚合询问框）。用例 `ConflictAskTests.一键档_设置里选了询问_批中间也一次都不问`。
  - ⛔ **失败名单有两处，必须一致**：`OneClickCoordinator.IsFailureStatus` 与
    `TaskSummaryService.IsExtractFailureStatus`。少列一条就出现"同一任务在①页算解压失败、在一键汇总里算未处理"
    （实测漏的是「没有可用的解压引擎」与「磁盘空间不足」）。用例
    `TaskSummaryBucketingTests.空间不足与没有引擎_在一键汇总里也算失败`。
  - ⚠ **一键档那个"手动密码"框已经被撤掉了**（用户 2026-09-29 第二次改口径，推翻当天早些时候的"填在确认面板里"）：
    他原话——"这个一键处理点击后出现一个输入框非常的奇怪，这个给他移除，可以弄一个提醒字样，
    **本次的解压可能需要密码，请在密码页一键导入**"。现在的口径：
    · 一键档**批中间不弹任何框**，面板里**也没有**密码输入格（`OneClickRunOptions.ManualPasswords` 已删）；
      没填又有加密包时只写一条 INFO 指路「密码」页一键导入（`StatusText.OneClickConfirmManualPasswordSkippedLog`）；
    · 确认框 B 段（`OneClickConfirmNoPasswordFormat`）文案带上同一句指路；
    · **批末多一条红字**（ERROR 级，`StatusText.BatchPasswordSuspectsLogFormat`）：把终态是
      `密码错误 / 达到密码尝试上限 / 文件名已加密` 的包点名，并**必须写"这只是可能"**
      （用户原话："这里就要注意的是可能，因为有的时候出错不仅仅是在这里"）；
    · 手动「只解压」保留那个弹窗，但**判据收紧了**：老判据只看"这包看起来要密码"，密码本里明明有也弹
      （用户问："我的密码本里面明明有这个密码，为什么还是会出现"）——现在复用与解压同一套参数算候选，
      **一个可用候选都没有才问**（`FindSuspectsWithoutUsablePassword`）。
    用例：`PipelineWiringTests.一键档_没有密码输入框_只写一条去密码页导入的指路日志` /
    `.手动档_密码本里有可用候选时_不再走手动密码弹窗` / `.手动档_一个可用候选都没有时_照旧走手动密码弹窗` /
    `.批末_密码类失败会单独写一条红字_而且写明只是可能`。
  - ⛔ **「修正后缀」绝不许动分卷名**（2026-09-29 真机事故）：`amb909.7.01`（2 GiB，带 7z 魔数）与
    `amb909.z.2`（1.89 GB，认不出格式）本是一组名字被改坏的分卷，被「修正后缀」各改一次 ——
    `.01` → `.7z`（**卷号被吃掉**）、`.2` → `.7z`（未知格式的"建议后缀"空着就**兜底成设置里的默认后缀**）→
    7-Zip 再也拼不起整组，从"还能救"变成「文件损坏」。现在两道闸门（`RenameService.BuildFixByDetectedFormatFileName`）：
    **格式未知 → 一个字都不改**；**末尾是纯数字 → 那是卷号，不许当后缀替换**。
    用例 `VolumeNameProtectionTests`（3 条，含"普通伪装后缀照旧要修"的反向对照）。
  - ✅ **短数字卷号其实不用靠名字认 —— 内容路本来就能认**（2026-09-29 实测澄清，用户当场质疑"你不是从底层看
    文件识别的吗"）：用内置 7z 造一组 `-v1m` 三卷再改名为 `amb909.7.01` / `amb909.z.2` / `amb909..3`，实测
    **名字路 `CanRepair=False`（"这个名字里没有卷号，程序不敢替它猜一个标准名"）而内容路 `CanRepair=True`（3 卷全认出）**
    —— 判据是"第 1 卷有 7z 魔数 + 同目录同族兄弟 + 硬链接试开通过"。
    所以那次真机失败的**根因不是"认不出"，而是「修正后缀」跑在前面把名字改坏了**（`.01`→`.7z`），
    把内容路的输入毁掉；`VolumeNameProtectionTests` 那两道闸门修好之后这条路才走得通。
    ⛔ 结论：**不要再给"短数字卷号"另加一套名字判据**（那会变成第二套真相）；要动就动"谁先跑、谁不许动名字"。
  - ⚠ **待修（同一次事故暴露，本轮没修）：分卷跨目录拼装** —— 用户报的现场是"两个子文件夹各一个 .rar，
    第一个解出 `.7z.001/.002`、第二个解出 `.7z.003/.004/.005`"，即一组分卷落在不同目录里。
    现在的判据只在**同一个目录**里找兄弟；要支持它得先想清楚"跨目录凭什么认定是同一组"（⛔ 不许凭名字像就拼）。
  - ✅ **缺卷时去"认不出的兄弟文件"里找**（2026-09-29 真机第二次报，本轮修好）
    —— ⚠ **但"修好"当时只对"没加密的包"成立**，真机那一对是 `-mhe`（文件名也加密），
    下一段是同一天深夜的复核结论（用户当场质疑"什么叫复刻，你做的程序到底有没有用"）：现场
    `<测试目录>\amb909\` 只有两个文件 —— `amb909.7.01`（**正好 2 GiB**，带 `37 7A BC AF 27 1C`）
    与 `amb909.z.2`（1.89 GB，7-Zip 认不出格式 = 一段**裸的续卷**）。老行为：第一卷报
    「分卷缺失 —— 同目录里也没有找到像后续卷的文件」，第二个被当"不是压缩包"跳过，**能救的一组救不回来**。
    真因是"尺寸规律"要的是"有与第一卷等长的文件"，而**两卷**时第二卷就是余量、必然更短 —— 那条规律
    **永远**给不出证据（`-v2g` 切两个 2 GiB 级文件正是这个形状）。
    改法（判据仍然**只有一处**，全在 `Detection/VolumeContentInference`）：
    ① 候选池加一条"**不是已识别的归档**"（用户原话）—— 7z 的续卷是裸字节流，有魔数的文件必然是另一个独立包，
    留在池里只会白试一次；**认不出格式的那些恰恰是真正的续卷**；
    ② 闸门从"只有尺寸规律"放宽成**两张门票取或**：尺寸规律 **或** `HasTwoVolumeShapeEvidence`
    （名字里的短数字尾巴接得上 `.01`→`.2`／第一卷正好是整数 MiB = 像满片）；
    ③ `BuildOrderings` 在"一个等长候选都没有"时也给出唯一顺序（`[末卷]`）；
    ④ 排序**先按短数字尾巴**（用户原话"可以靠后缀数字 2 的情况猜一猜是第二卷"），老的全排列照旧兜底。
    ⛔ 放宽的只是"**敢不敢试一次**"：成不成立仍然只由 `VolumeProbeVerifier` 的硬链接试开回答
    （⛔ 绝不复制大文件、绝不改用户文件），试不出来就一个字节都不动。
    用例：`DisguisedVolumeContentProbeTests`（+3 条：两卷名字只剩号 / 两卷名字没号靠体积规律 /
    只剩第一卷 + 无关小文件时必须什么都不做）+ `VolumeContentInferenceTests`（+5 条纯逻辑）。
    红检：把放宽撤掉（闸门只留尺寸规律 + `full.Count==0` 返回空）→ 那 2 条两卷的端到端用例
    报「解压失败」（红在"没改名、引擎拼不起这一组"），撤掉的那两处一恢复就全绿。
  - ⛔ **上一轮那次"修好"是假绿**（2026-09-29 深夜复核；用户当场质疑："什么叫复刻，你做的程序到底
    有没有用，如果你那个成功了，我这个失败了这不就还是说明程序问题吗"）。真机上那条路**走到了、
    引擎也答了，只是程序没听懂**；上一轮只验了**合成样本**（`7z a -mx0 -v1m`，**没带 `-p/-mhe`**），
    而真机那一对分卷是 **`-mhe`（文件名也加密）** 的包：
    - `amb909.7.01` 第 0 字节的 Start Header 给出 `NextHeaderOffset=4038274896` + 头长 66
      → 归档头正好落在 **`.z.2` 的最后一字节**上（2147483648+1890791346 = 4038274994 = 4038274928+66）
      —— **两卷齐得很**，缺的从来不是卷。
    - 只读副本实测（内置 7z 26.03）：**卷齐** → `Cannot open encrypted archive. Wrong password?`
      （引擎结构化结论 = `EngineErrorTypes.EncryptedHeaders`）；**卷不齐 / 拿别的文件顶替** →
      `Cannot open the file as [7z] archive` + `Unexpected end of archive`。两条能分开，靠的就是这个。
    - **断点**（`文件:行号`）：`Extraction/VolumeProbeVerifier.cs` 的硬链接试开**只认 `list.Success`**
      （"列得出清单"）→ 加密头这一档**永远**"试不成立" → `Extraction/VolumeNameRepair.cs` 判 `Cannot`
      → `ViewModels/ExtractionCoordinator.cs` 的 `NormalizeDisguisedVolumeNamesAsync` **静默 return**
      （一句日志都没有 —— 真机上"看不到任何试开痕迹"就是这么来的）→ 管线随后在
      `ExtractionCoordinator.cs` 的通用分片闸门上报**「分卷缺失」**（**误诊**：卷齐、缺密码）。
    - **修法**（判据仍**只有一处**、仍在**那一次硬链接试开**里，⛔ 不是新的"这是不是分卷"判据）：
      把"引擎认出了这一组是一份**头加密**的归档"（`EngineErrorTypes.EncryptedHeaders`，
      与 `IsRawSplitStream` 同一类**结构化**结论，⛔ 不比文案）也算**肯定**回答 ——
      7z 的头写在最后一卷、偏移由第一卷给出，能报出"加密归档"就说明它真读到了那份头；
      **单卷试开的同一结论要反过来用**：单卷就报"加密归档"= 那份头在这个文件**里** = 它本身就是完整包
      → **拒绝改名**（不然真会把完整包改坏）。
      另补一行 INFO：试开**真跑过**但不成立时把结论写进日志（只对跑过试开的情形写，
      ⛔ 不破坏第 45 条"成功的任务只留一行"；判据是 `VolumeNameRepairPlan.TrialAttempted`，不是文案）。
    - **用例**：`DisguisedVolumeContentProbeTests` 三条（`-mhe` 两卷端到端**改回标准名 + 解出内容逐字节一致** /
      同一形状只剩第一卷 + 无关小文件 → 什么都不做 / **完整**加密包改名 → 不许被当第一卷改名）+
      `RealAmb909VolumePairTests`（**真机那两个文件的只读副本** ← `_tmp\ArchiveFixer\amb909-copy\`：
      整组改回标准名 + 逐字节未变 + 引擎报 `EncryptedHeaders` 而不是"通用分片" +
      任务状态从「分卷缺失」变成「文件名已加密」）。
      **红检成立**：撤掉这条接受判据 → 上面两条端到端**正好变红**（红在"没改名、引擎拼不起这一组、
      状态落「分卷缺失」"），恢复（`--no-incremental` 重编）后全绿。
  - ⛔ **验收规则（用户 2026-09-29 深夜当场定，就指着我上一轮的验收说）**：
    **必须在真样本（或真机文件的只读副本）上跑通；合成样本通过 ≠ 问题解决**。
    合成样本只能当**定位手段**，它证明得了"按我以为的格式写出来的字节我处理得对"，
    证明不了"用户手上那份东西我读得对"（真机那对文件是 `-mhe`，我造的样本从来不是）。
    真样本副本放仓库同级 `_tmp\ArchiveFixer\`（⛔ 样本本体绝不进仓库；H: 上的原件**只读**，
    一个字节都不许动），副本不在就**跳过并说明**。
  - ⚠ **待修（同一轮复核发现，与上一条同源、形状更窄）**：一个**完整**的、**文件名也加密**的 7z，
    若名字末尾那一段是**纯数字**（`.01`），在**没有密码**时会被 7-Zip 退化成"通用分片"处理器
    （清单里只有一条 = 文件自己），于是 `RawSplitStreamDetector` 把那句**误诊**成「分卷缺失」。
    判据本身没错（通用分片正是它要拦的），错的是"没有密码"与"名字被改坏"在那一处**分不开** ——
    解压前那次 list 现在排在任何密码尝试之前。要修就得让那次 list 也带密码候选，
    ⛔ 别在 `RawSplitStreamDetector` 里加"看名字猜"的第二套判据。用例
    `DisguisedVolumeContentProbeTests.真七z_完整包文件名也加密_只改了后缀_不许被当成第一卷改名`
    钉着"名字一个字都不许改"，并在跑测时如实打印这个状态。

  - ⚠ **待修（用户 2026-09-29 点名，本轮只写设计没实现）：伪装成 `.mp4` / `.apk` 的续卷**。
    真的 mp4/apk 是**无用物**、伪装的（把 `.002` 改名叫 `.mp4`）是**续卷** —— 两者的头都不是我们的魔数，
    `SniffFormat` 一律返回 `Unknown`，所以**判据只能是"体积 / 位置 + 试开验证"，⛔ 绝不许只凭后缀判**。
    现状（够用但不完整）：本轮这两张门票已经能接住"第一卷正好整数 MiB"的那一档
    （伪装成 `.mp4` 也不影响试开），**接不住**的是"切分上限不是整数 MiB **且**尾部数字被 `.mp4` 挡住"
    这一档（`TryReadShortNumberTail` 只看最后一段）；真要补，补的是**第三张门票**
    （"倒数第二段是接得上的短数字"）而不是新的"这是不是分卷"判据 —— 判据仍然只能有一处。
    动它之前先想清楚：真 mp4/apk 会被这第三张门票放进候选，**唯一**的兜底就是那次试开
    （试不出 → 什么都不做），所以任何"看到 `.mp4` 就当续卷"的写法都是错的。
  - ⚠ **文档别再说"每次解压前都会弹提醒"**：导入那条弹窗 2026-09-28 已退休（无用物改成导入一完成就
    自动移出任务列表），只剩**手动「只解压」**会弹；一键档并进唯一那个确认框。
- ⚠ **待修（2026-09-29 实测逮到，本轮没修）：RAR 卷内容加密，但识别阶段读不出"加密"**。
  实测用户三套真包：`222` 的跨盘 zip 第一片 `IsEncrypted=True`，而 `111` / `333-Rar4` 两套 RAR4 卷
  **`IsEncrypted=False`**（内容确实是加密的，`7z l -slt` 每个 mp4 都是 `Encrypted = +`）。
  后果：① 「本批有 N 个包没有可用密码」那句预判（`FindTasksWithoutUsablePassword` 只看 `IsEncrypted`）
  对 RAR **漏报**；② 一键档"要不要提醒你去确认框填密码"也跟着漏。
  修的时候注意：**别只按后缀猜**，要么在识别阶段真去读 RAR 的加密标志，要么把这句预判改成
  "识别认不出就问引擎列一次目录"（代价是每包多一次调用，得量化）。

## 12. 细节去哪看

| 想知道 | 去哪看 |
|---|---|
| 用户原话、每条指示的逐条落地史 | `旧AGENTS.md`（旧版全文 135 KB，含那张巨型「用户明确指示」表） |
| 用户指示汇总（压缩版，一行一条） | `docs/需求书.md` **文末那一节** |
| 每条新需求 / 变更的完整记录 | `docs/需求变更.md` |
| 某次改动具体做了什么 | `修改日志.md`（一条一行流水账） |
| 怎么用这个软件 / 现在能做什么 / 有哪些设置项 / 人工怎么验 | `docs/使用说明.md` · `docs/功能一览.md` · `docs/设置项.md` · `docs/人工测试清单.md` |
