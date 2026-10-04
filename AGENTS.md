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
- **安装包**：**用户 2026-09-27 要求"我是要 .exe 安装文件的"** → 已做（`installer\ArchiveFixer.nsi` + `scripts\installer.ps1`），规则见 §11.1
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

> 本体进 `src/`、测试进 `tests/`（2026-09-26 按用户要求重排），根目录只留项目级文件。

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
scripts/package.ps1            生成 dist 发行包（发布时才跑；**用户说"打包"才打**）
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

- **⛔ 打包只在用户明说时做**：他明确说"打包"才出 `dist\*.zip` / 安装包 / Release（v0.1.1 = 2026-10-02 那一轮）；平时构建只用于刷新绿色目录。
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

- **当前状态**：桌面与鼠标授权（用于他点名的真机测试）**以用户最新一条明确指令为准**：说停就停 —— ⛔ 别拿"以前被收回过"当理由拒绝，也别当长期许可。
- 授权期间仍不许：抢前台骚扰他干活、改桌面状态（分辨率 / 任务栏 / 别的窗口）、**动他给的样本目录以外的东西**、跑会弹 UAC 的操作。
- 没授权时的替代做法：`docs/人工测试清单.md` 的 A/B/C 手动项**交给用户**；代理只做**造样本 + 逐步操作说明 + 读程序日志与文件结果 + 改代码**，走不碰桌面的路。
- ⚠ 这一条管代理与脚本、**不管程序自己**：程序要用户输入时把窗口拎到最前面是用户要求的行为，但**只有窗口没能出现在最前面时才闪 + 响**（判据 `WindowAttention.ShouldAlert`）。
- 真机自动化办法见 `docs/人工测试清单.md` 末节（UIA 只到 TabItem、截图定位 + 鼠标/键盘）。

**§9.2 测试（按改动范围分级；级距定义见全局《作业模式》规矩 25）**：
验证强度与**改动影响面**成正比，⛔ 不是"改一行也要跑全量"——小改动只跑定向并报清**跑了哪几条、结果如何**；大改动跑**全量**。

- 判据是"**这次改动会碰坏谁**"，不是改了几行。凡动 **`ExtractionCoordinator` 的候选循环 / `OutputVerifier` 口径 / 工作区与其余物的清理**，一律按大改动对待（不可逆路径）。
- **红检** = 修一个缺陷要能**把修复临时撤掉、亲眼看到对应测试变红**，新增守门测试要说明红在哪。
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
| `真机事故复盘.md` | **历史现场与推演**：原 §11 正文**逐字归档**（32 节）；§43 = 「程序会不会写坏源包」的只读审计（结论：找不到这样的路） |
| `部分完成发布方案.md` | 「部分完成」发布的**方案 + 实施记录**（口径 A；§6 = 用户定的口径，§7 = 已落地什么 / 红检 / 未验） |

> 另有 `<仓库外>\ArchiveFixer\项目设计.md` = **需求全集 / 长期愿景**，有效但需按非目标裁剪。
> ⛔ 别把"没写进本文件"理解成"被否决了"。

## 11. 当前验证状态（结论速查；现场、根因与红检见 `docs/真机事故复盘.md`）

> 只放现在的事实与口径（版本/发布物/测试数字/红线/"唯一出口"的名字）；每条末尾 `〔X〕`=复盘见 `docs/真机事故复盘.md`「X」（现象/`文件:行号` 根因/修法/红检/遗留）。
> ⛔ 不写日期现场与长推演；⛔ 不许删结论（结论是下次改代码的红线）。

### 11.1 版本/发布/安装包

- v0.1.0=第一个对外版本（`ArchiveFixer.csproj` 三处版本号=0.1.0）〔v0.1.0：第一个对外版本〕
- v0.1.1=真机缺陷修复轮（**2026-10-02 用户拍板**；只修问题、默认档一个都没变，逐条见 `CHANGELOG.md`）
- 已发布：`https://github.com/klaraljy/ArchiveFixer`（MIT、公开）+ Release v0.1.0 / v0.1.1（三个资产：`setup.exe` + `standalone.zip` + `framework-dependent.zip`）〔已发布：仓库 / Release v0.1.0〕
  - ⛔ 资产名=「ASCII 文件名+中文 label」；⛔ 大资产直连传（清 `HTTP_PROXY`/`HTTPS_PROXY`）；⛔ `gh release upload --clobber` 会连别的资产一起删。
- 安装包=`installer\ArchiveFixer.nsi`+`scripts\installer.ps1`（NSIS；免 UAC、每用户）〔安装包（NSIS .exe）〕
  - ⛔ 默认安装目录不许是 `C:\Program Files\`；⛔ 卸载默认保留 `data\`。
  - `.nsi` 必须 UTF-8 带 BOM；注释行末尾不留反斜杠；开关判据只读 `${GetOptions}` 返回值；卸载删桌面 `.lnk` 前看 `DesktopShortcut` 标记。
- 面向用户的截图与文档脱敏（⛔ 个人路径/样本包名/站点名/作者邮箱都不进仓库）〔开源前的脱敏〕
- 「说明」窗=`HelpContent.Features`（17 条）+`Glossary`〔说明窗〕

### 11.2 构建/测试基线

- `dotnet build ArchiveFixer.slnx`=0 错误 0 警告；`dotnet format ArchiveFixer.slnx --verify-no-changes`=通过。
  - ⚠ 警告口径：日常构建 0 警告；**强制还原**那档多 4 条 `warning NU1900`——⛔ 不许写成"0 警告一定成立"。
- `dotnet test` 全量（主 checkout 内）：**2580 条（2577 通过 / 3 跳过 / 0 失败）**〔构建 / 测试 / 格式基线〕
  - ⚠ 旧基线 2453 / 2448 / 2428 / 2419 / 2415 / 2411 / 2393 条分别是"分卷族系统化 A+B0 之前 / 逐层回收闸门收尾（①上界 ②邻路）之前 / 逐层回收那一轮之前 / 修 §51 那组分卷名之前 / 修四处日志口径之前 / 修 ① 与 ⑦ 之前"的数（逐层回收那轮 +20、闸门收尾 +5；A+B0 +59 = `ArchiveBaseNameTests` 52 + `InnerLayerDisguiseRestoreTests` 7）。⛔ 数字只在这里写一次。
  - ⚠ **修 ① 漏改的一条用例**（`TwoLayerLayoutTests.发布_只解了一层时照旧摊掉无意义外壳`，断言的是已删掉的"发布侧摊外壳"）一直红着，本轮才改成同口径；确认办法 = 在干净 HEAD 上 `git stash` 后单跑（红与本轮改动无关）。
- ✅ **「部分完成也把已解出的内容放进目标目录」已做完**（2026-10-02，口径 A；取舍/红线/红检见 `docs/部分完成发布方案.md` §7）。四块：① 引擎点名的坏条目 `ArchiveOperationResult.{FailedEntryNames,ReportedSubItemErrors}`（⚠ **中文版 UnRAR 点不出名**）；② **纯函数** `Extraction/PartialPublishPlanner`（逐条对账 + **五道闸门**：无清单 / 自报计数对不上 / 一个都发不出 / 阈值不过 / **引擎说失败但盘上对不上账**）；③ `Extraction/PartialPublishRunner`（对账 → **发布前二次空间体检** → 真搬）；④ 收尾接线 + 其余物半份清理。
  - ⛔ **判据里没有"引擎没报错就算好"这种话**：**大小对得上且引擎没点名**才算可发布；判不出（拿不到清单 / 点不出名 / 缺 >5 且 <95% / **被取消** / 空间不够）一律**一个字节都不发布**。
  - ⛔ **源包在任何一档下原地不动**；其余物只留**最外层源包**，**它不在盘上 ⇒ 其余物一个字节都不删**（`RestItemPurger.PurgeExcept`）；⛔ **与「空间不足」模式硬互斥**（`PartialPublishActive`）+ **空间账加一整个源包**（`TaskSpaceEstimate.CountsSourceAsNewOccupancy`）。
  - ⛔ **取消闸门读的是任务自己的取消令牌**（⛔ 不是中文、也不是终态：单层取消只落中文状态、终态会被兜底改成「失败」）。⚠ **真机未验**；递归那条路只覆盖"最深那一层失败"这一档。
- ⚠ worktree 里跑全量多 6 条跳过（共 8）：真样本根按 `ArchiveFixer.slnx` 父目录解析（worktree 里解不出来）；设 `ARCHIVEFIXER_REAL_SAMPLE_DIR`/`ARCHIVEFIXER_REAL_VOLUME_PAIR_DIR` 复原 2 条。⛔ 这 6 条是"样本路径解不出来"、不是样本不在。
- 2 条跳过=发现阶段条件跳过（⛔ 不伪装成验过；条件式 `FactAttribute` 构造时设 `Skip`）：① `RealAmb909VolumePairTests.真机副本_有密码时_既有管线真的解出这一组的内容` 要 `ARCHIVEFIXER_REAL_VOLUME_PASSWORD`；② `SpaceDemandAccountingTests.真样本只读_那一组真实分卷_判据里不含源包_真机可用空间下必须放行` 要 `ARCHIVEFIXER_REAL_SPACE_CASE_DIR`（或 `<slnx父目录>\_tmp\ArchiveFixer\space-real`）。
- ⚠ 真样本用例没设环境变量时提前 return，报表照样算"通过"——⛔ 别读成"验过了"；要报真样本结果必须设变量单跑并写清命中哪份。
- ⚠ 数字只在这里写一次。
- 已知 flaky（并发假红；先单跑确认，⛔ 别改断言）〔已知 flaky 清单〕：`SpaceTightModeTests.换输出位置_二页那颗选择按钮也会触发空间体检`（测试侧竞态、**不是产品 bug**，读日志已改走 `LogContains`）、`SpaceTrendMonitorTests.周期循环_按间隔采样_取消后立刻停`（计时敏感）、`SecurityGuardTests.CheckBeforeExtract_NotEnoughFreeSpace_IsRejectedWithNumbers`（真实盘可用空间；需求改成"可用 + 60 GiB"，断言一个字没改）；⛔ 等待时长 / 断言 / 被等内容一个字没改；三条现场见 `docs/真机事故复盘.md` §48.5。
- ⚠ 回退代码后必须 `--no-incremental` 重编（否则跑的还是红检那份）〔真样本验收〕

### 11.3 空间：判据/模式/批末汇总

- ⛔ 源包不许算两遍：唯一出口 `TaskSpaceEstimate.FreeSpaceDemandBytes`=`ContentBytes+ProcessArtifactBytes`（`ScheduledExtractionItem.RequiredBytes` 与 `ExtractionCoordinator.ReconcileReservation` 都只读它）；`PeakBytes`（含源包）只作描述，⛔ 不许拿它比可用空间〔源包不许算两遍（2026-09-29）〕
- 「空间不足」模式（`MainViewModel.SpaceTightMode`）一个布尔管四件事：并发（`ExtractionScheduler.ResolveSpaceTightParallelCount` 与「最大并发解压数」取小）、排序、其余物强制 `Delete`、定稿+校验通过后当场永久删源包（`PurgeLayerSourcePackage(..., LayerPurgeTrigger.SpaceTight)` → `SourceCleanupService`；⚠ 它现在有**两个触发点**，另一个是 `LayerPurgeTrigger.LayerReclaim`＝普通档逐层回收，见 §11.3 末两条）〔空间不足模式〕
- 「不删原包」安全档（`MainViewModel.SpaceTightKeepSource`）测试期专用：⛔ 发行那轮整块删掉；`SpaceTrendMonitor` 保留〔安全档「不删原包」〕
- ⛔ **「启动前空间不够」不是终态，只是"现在排不上"**（现场与数字见 `docs/真机事故复盘.md` §45.1）：并发高时"已在跑的预留"会把整块盘许光 ⇒ 后面小包也被判「还差…」；⇒ 拒下的任务进"等空间"名单（**不记失败、不改状态**，只写一行 INFO），**每跑完一个就重试一遍**，没人跑了才走 `MarkSpaceBlocked` 如实报。⛔ 两条铁律：**补跑环必须与"等所有任务结束"写在同一个环里**、**每跑完一个当场 `RefreshAvailable` 重探一次**。放行判据仍然只有 `TryReserve` 一个。用例 `SpaceTightModeTests.启动前空间不够_先排队不记失败_等腾出空间后自动补跑`（红检：改回 `MarkSpaceBlocked + continue` ⇒ 变红）。
- ⛔ **多层递归：每一层开头都要把进度基准拉回来**（同 §45.2）：进度条记的是"引擎最后一次报的百分比"，第 0 层结束时是 100%，第 1 层从头开始却没有任何一帧拉回基准 ⇒ 那一层跑多久界面就停在 100% 多久。⇒ `RecursiveExtractor.ExtractLayerAsync` 每层开头合成一帧 `Percent=0`、当前条目写「第 N 层：<内层包名>」。⛔ 只补这一帧，引擎报的数一个都不改。
- ⛔ **「空间不足」的机器终态只有一个：`MarkSpaceBlockedCore` 里显式落 `Failed`**：过去只写 `Status` 不写 `Outcome` ⇒ 同一个"空间不足"在三条路上有三种说法（失败 / 未处理 / 跳过）。⛔ 落 `Failed` 的同时**源包一个字节都没动**（原地、其余物不生成）。同批那行清单的措辞也从「跳过」改成「没做成」。用例 `SpaceRiskAndSummaryTests.中途空间不足_一键档只弹一次纯提示` / `_手动档照旧只写日志`。
- 空间回收的五条口径（⛔ 别按旧口径改回去）〔空间回收与批末汇总〕
  1. 多层链每一层各删各的：判据排在 `task.IsContinuationTask` 之前。
  2. 某一层失败只影响那一层：失败层源包留着，已收走的更外层不回滚（⛔ 别当 bug 去"修"）。
  3. 动手前提醒"多层可能中途空间不足"：唯一出口 `Storage/MultiLayerSpaceRiskRules`、文案 `StatusText.MultiLayerSpaceRiskFormat`；⛔ 只用 `Σ FreeSpaceDemandBytes` 比可用空间；只提醒、不拦任务。
  4. 中途撞上空间不足 ⇒ 一键档弹一次纯提示（非模态、同一批一次）；⛔ 手动档只写日志。
  5. 批末汇总框色带：唯一出口 `Models/BatchSummarySeverityRules`（只读 `TaskOutcome`/校验枚举，⛔ 不比中文），蓝=全成功/橙=有部分完成·跳过·取消·没轮到/红=有失败；窗口只管长相（`AppDialogWindow.ResolveSummaryBannerBrushKey`），⛔ 不许在 XAML 里再判断。
- ⭐ 同一个框还要"具体指出错在哪"〔复盘同上一节〕
  - 唯一出口 `Models/BatchSummaryDiagnosticsRules.Build`：`BatchProblemKind` 12 档（顺序=显示顺序）、`Severity` 与文字同一次调用；每组最多 3 个名字（其余「还有 K 个」）、只写文件名（§8）；全成功/空批不写；密码那组注脚必须带"可能"（⛔ 不许断言"就是密码问题"）。
  - 分组不新造第二套分类（只读状态常量/机器终态兜底 ⇒ 没做成的不会消失；`Succeeded` 且校验没判否的不进任何组）；补充数只取现成的（`ArchiveTask.SpaceBlocked`、`MissingVolumeNames`，⛔ 不重算）；⚠ 未做：色带+清单真机 GUI 没看过、第 3 条那句是上界（⛔ 不许改成"精确预测"）、中途提示只带第一个。
- ⛔ **跟班卷（`CountsTowardBatchOutcome == false`）在四处消费点都不算"没做成"**：① 色带 `BatchSummarySeverityRules`；② 批末诊断 `BatchSummaryDiagnosticsRules`；③ 一键汇总行 `OneClickCoordinator.BuildSummaryLine`；④ **批末「本批汇总」**（`ExtractionCoordinator.AppendBatchSummary`，见 §49.4）。③④ 用**同一句文案**（唯一出口 `StatusText.VolumeGroupFollowerSummaryFormat`）；⛔ **恒等式「各分项之和 + 未处理 = 本次任务数」两处都不许破**（跟班卷从"跳过"剔出去后 `pending`/`untouched` 要减掉它）；⛔ `notArchive` 也排除跟班卷。用例 `BatchSummaryDiagnosticsTests.汇总那一行_跟班卷从跳过里剔出去_且恒等式不许破` + `RealMachine20261002FixesTests.本批汇总与一键汇总_跟班卷都不算跳过且恒等式不破`。
- ⛔ **「一批任务怎么数」也只有一个出口**（见 §49.6）：`Models/BatchOutcomeTally`（`Count` / `BuildParts` / `DescribeFollowerNote` / `IsCountedAsSuccess` / `IsCountedAsFailure`）—— 批末「本批汇总」、一键汇总行、**日志导出头部**三处转调，⛔ 不许再自己 `Count(task => …)`；判据只有 `Outcome` + `OutputVerification` + 事实位 `CountsTowardBatchOutcome`；「未处理」= 总数 − 各分项（恒等式永远成立）；「终态说成功、校验却判否」落**失败**侧（失败逐条清单读同一个判据）；分项顺序三处统一。⚠ ①页顶部那排统计（`Summary.SkippedCount`，标签「已跳过」）**照旧含跟班卷** —— 那是"行状态人口普查"，与"有没有活没干"不是一回事，⛔ 别顺手改。
- ⛔ **空间曲线要在"其余物处理完之后"补一针**（见 §49.1）：批末曲线的"收"由 `StartExtractAsync` 收尾写，而**其余物删除在更晚**的 `OneClickCoordinator` 链尾 ⇒ 真机「收 26.3 GiB」之后又删掉约 6 GB。唯一出口 `ExtractionCoordinator.RecordSpaceTrendPostscript(note)`（**取走即清 = 一批一针**）+ `SpaceTrendMonitor.DescribePostscript` + `StatusText.SpaceCurvePostscriptFormat`；⛔ **不许新开第二套取数或第二套文案**；没有侦察器 / 取不到 ⇒ 什么都不写。用例 `RealMachine20261002FixesTests.一键处理_其余物处理完之后补记一针真实可用空间`。
- ⛔ **空间门放行那一行的"可用"是账面值**（见 §49.2）：账本只在**有任务等空间**时才重探（`:9861`），平时是批首那一针（`:9623`）。唯一出口 `SpaceGate.Check(..., availableIsBookValue)`：账本路传 true ⇒ `StatusText.SpaceGatePassedBookValueFormat`（「……账面可用 X（……不是此刻的真实可用 —— 实时可用看日志里的「空间变化」行）……」）；打包路（刚探到）照旧 `SpaceGatePassedFormat`。⛔ 这个开关**只管措辞**。

- ⛔ **空间需求按"净增量"算**（用户 2026-10-03 拍板）：唯一出口仍是 `TaskSpaceEstimate.FreeSpaceDemandBytes`，但 `NestedExpansionAllowance` **1.0 → 0.0**（内层包本体已算在内容物里，再按整份扣一遍＝重复计）⇒ 真三层链实测「放行需求 4,195,346 → 2,097,673（−50%）」；⛔ **需求 ≥ 内容物**永远成立；⛔ 「空间不足」模式口径一个字不改。用例 `SpaceNetIncrementTests`(2)（红检：改回 1.0 ⇒ D 红）。
- ⛔ **逐层回收其余物（普通档，用户 2026-10-03 拍板；A/B/② 三条见方案书 §7）**：续解链**每一层**「定稿 + 输出校验通过 + 未取消 + 可证完整」之后**当场**按「删除操作」处理这一层的过程物；**最外层源包只留到链尾**。**只在「彻底删除」档生效**（批内唯一事实位 `_layerReclaimThisBatch`，写入点唯一 `PrepareRestHandlingForBatch`）；**「移入回收站」档照旧链尾一次**（回收站不释放盘上空间，逐层做零收益只多造条目）；「空间不足」模式照旧每层各删各的（含源包当场删）。用例 `ChainLayerReclaimTests`(12)。✅ **上界已关**（2026-10-03）：目标根**之内**的候选**一路上溯到目标根、无层数上限**（`SplitGateAncestorLevels=3` 只剩"候选不在目标根里"那一档兜底 —— 防止顺着 `CustomOutputDirectory` 的旧地址爬 `C:\Users\…`）；H2 先在 HEAD 上跑成红（整组被删光）再绿，红检 = 把上限加回去 ⇒ 只 H2 红。
- ⛔ **其余物的空壳不进回收站**（用户 2026-10-03 真机：回收站里堆了很多 1 KB 的同名「其余物」文件夹）：递归地一个文件都没有 ⇒ **就地永久删掉空目录**（判据唯一出口 `RestItemPurger.IsFileFreeDirectoryTree`，`Purge`/`PurgeExcept` 两处转调，排在六道门槛之后）；那一行文案按**事实位** `RestPurgeOutcome.RemovedAsEmptyShell` 选（唯一出口 `ExtractionCoordinator.DescribeRestPurgedLine`，⛔ 不许按「0 项 0 B」猜）。用例 `RestEmptyShellPurgeTests`(5)。
- ⚠ **「不动其余物」档随层清中间件 = 实测为空操作**：跑完一层后 `.ArchiveFixer.work` 整棵已不在、全程只有工作区里 `stage\<产物>` 一种形状 ⇒ 没有残渣可清（⛔ 没硬加删除点）；用例里那条只读实测钉住现状。

### 11.4 分卷/格式识别

- ⛔ 「修正后缀」不许动分卷名：两道闸门=格式未知 → 一个字都不改 + 末尾是纯数字（那是卷号）→ 不许当后缀替换〔修正后缀不动分卷名〕
- 卷名判据唯一出口 `ExtensionHelper.TrySplitVolumeSegment`（三处转调它）〔分卷名粘垃圾〕
  - ⛔ 老口径不许回退：另起一段的后缀（`x.7z.001.txt`/`x.001.bak`）不算分卷；纯数字尾巴（`0012`）不猜。
  - ⛔ **`partN` 族的"尾巴粘垃圾"（`X.part1.rar删除`）**（见 §51）：判据唯一出口 `ExtensionHelper.TrySplitPartNumberedVolume`（**卷标记与 rar 尾巴都可粘 ≤2 个字符的垃圾**，只用既有那两把尺子还原），**三处转调同一份** —— `VolumeGroupDetector.Analyze`（卷序/归组）、`FileNameHelper.StripVolumeMarkers`（包基名 ⇒ 同组判定与落点，⚠ 位置必须在"跨段伪装"搜索**之前**）、`FileNameHelper.IsVolumePartFileName`（批首改名的放行门）。改名目标**必须连 `.rar` 一起保留**（`VolumeNameRepair.TrySplitDisguised` ⓪ 档，且**只接"尾巴真的脏"那一档** —— 尾巴干净的留给既有那两条路，否则 `444.p1art2.partN.rar` 这种"基名自己还带一段"的会被拆错）。递归层里**一组卷只算一个内层归档**（`RecursiveExtractor.CollapseSameGroupVolumes`，复用 `IsSameGroupContinuationVolume`）。⛔ 老写法在三处各写一遍 `tail == "rar"` ⇒ 尾巴一粘垃圾**三处同时失效**（4 卷 = 4 个"第 1 卷本体"、递归 4 个分支、改名还会把 `.rar` 吃掉）；⚠ 包**里面**的伪装名没有「修正后缀」这一步先擦尾巴，所以这条路是它们唯一的救法。
  - ⛔ 不许"一见 zip 成员就让位"：让位条件只看"目录里真有一片自述带盘号的跨盘 zip 末片"（`HasSpannedZipTailInDirectory`）。
- ⛔ **三种分卷格式的"内容级自述能力"差得很远 —— ⛔ 不许当成一样**（逐格实测表见 `docs/真机事故复盘.md` §48.1；用例 `VolumeContentProbeCapabilityTests`）：

  - ⇒ **定序能力**：RAR 靠内容就能定序（不需要名字）；PKZIP 跨盘 zip 只有 **2 片**能靠内容消去法定序（≥3 片的中间片顺序**只有名字能回答** —— `VolumeNumberFromContent.ResolveZipGroup` 里 `total > 2` 直接 `Refuse(ZipTooManyDisks)`，用户 2026-09-29 定的规矩）；7z 只能"尺寸排序候补 + 硬链接试开"。
  - ⛔ 别再写"三种格式一样"的代码或注释：`VolumeNumberFromContent` 的三个分支就是按这份表写的。
- 缺卷补救：候选池只收"不是已识别的归档"的；⛔ 放宽的只是"敢不敢试"：成不成立只由 `VolumeProbeVerifier` 硬链接试开回答（⛔ 绝不复制大文件、不改用户文件）〔缺卷补救：第二次真机报〕
  - 头加密那一档也算肯定回答（`EngineErrorTypes.EncryptedHeaders`，结构化结论、不比文案）；单卷试开就报"加密归档" ⇒ 拒绝改名（它本身就是完整包）〔假绿复核：-mhe 两卷〕
- 分卷组装判定器 `Detection/VolumeGroupResolver`：六条证据（基名/卷号连续/体积规律/物理同一性/位置推定/硬链接试开）+ 四档结论（`Complete`/`IncompleteMissingVolume`/`IncompleteSuspected`/`Undetermined`）+ 唯一可删出口 `CanEnterDeletableRestItems`（弱证据/判不出=false ⇒ 不删源、不移源）；跨盘或拿不到工作区根 ⇒ 不试开、降「判不出」（⛔ 不许退到源卷根偷开工作区）；详见 `docs/分卷组装算法.md` §6.10/§6.11。
- ✅ 带垃圾尾巴的组（`111.parts1.racr` 这种"另起一段的后缀"）交给 7-Zip 会报缺卷 ⇒ 由 `VolumeNameRepair` 在开工前改回标准名（只改名、不覆盖、判不出就不改）。
- ⛔ **"本体干净、只有几个续卷被改坏"也要修**（见 §41）：
  - 入口判据是 `IsVolumePartFileName(自己) || task.IsVolumeGroup`（⛔ 只看前面那一档就漏 —— `.zip` 是本体后缀、不是卷标记）；
  - 计划出口 `VolumeNameRepair.PlanDisguisedVolumesBesideStandardSelf`（排在"名字里得有卷号 / 必须是第 1 卷"那两道门**之前**）：按"去杂质后基名逐字相等"收兄弟、各改成 `基名.规范卷标记`；⛔ 卷标记必须互不相同、目标名一个都不许被占、只改名、全成或全不成；
  - ⚠ 计划的 `CurrentPath/TargetPath` 必须指向**真要改的那一卷**（指向已经标准名的本体会被 `TryApply` 判 `AlreadyStandard` 整份拒掉）。用例 `VolumeNameRepairTests`（5 条）。
  - ⚠ 遗留：**名字里完全没有卷号的 ≥3 片跨盘 ZIP** 仍判不出（内容路对 ZIP 只支持 2 片）—— 要真修得先有真样本。
- ⛔ **跨盘 ZIP 的"专属算法"= 按归档自己的索引定盘 + 定不了就试拼**（见 §42；逐条实现细节 / 入口第三档 / 「缺的是末片」诊断已挪进 `docs/真机事故复盘.md` §50.1）：
  - 索引定盘 `Detection/SpannedZipIndex.cs`（⛔ 不看名字、不用引擎、不用密码）；试拼定序上限 **3 片**、再多如实报"定不下来"；统一算法是**保底**（索引路不适用 ⇒ 返回 `null` 原样往下走；判不出来只出结论、**一个字节不动**）。
  - ⛔ **两道"ZIP64 收尾"的旧闸门已拆**（见 §44.2）：**跨盘 zip 超 4 GiB 必然在"中央目录"与"EOCD"之间插 76 字节 ZIP64 收尾**（记录 56 + 定位器 20），老两处都不认它 ⇒ 整个跨盘组被当过程物、最终**整份永久删除**（后果链与只读复核数字见 §48.1）。
  - ✅ **"其余物里的分卷是半套"这道保险已加**：判据唯一出口 `Extraction/RestVolumeCompletenessGate.DescribeBlocker`（公开纯函数，其余物顶层每个归档件算一个包基名，成品目录树里还有同基名的归档件 ⇒ 什么都不做 + 一行 WARN 点名两边；⛔ 普通内容文件与同名**目录**都不算伙伴）。
    - ⛔ **这道闸门现在也管"逐层回收"那条路**（2026-10-03）：判据本体抽成公开纯函数 `RestVolumeCompletenessGate.DescribeBlockerForCandidates(候选清单, 成品根, requireRecognizedCandidates)`，「其余物」那一档转调它（文案逐字不变），逐层那一档走 `DescribeLayerReclaimBlocker`（候选清单 = 真删清单，同调 `SourceCleanupService.BuildTargetList`；**认不出就拦**）。⛔ 两条兜底都必须落在"拦下"：**某棵树读不动 ⇒ 拦下**（`TryEnumerateFilesSafe`，⛔ 不许再 `catch ⇒ 空`）、**一棵能扫的树都取不到 ⇒ 拦下**；⛔ 扫到 `.ArchiveFixer.work` 里的文件一律跳过（改名前的暂存副本会被误判成"成品里的另一片"）；⛔ 扫描上界 = 这一批的目标根（爬进用户的源目录会误拦合法回收）。用例 `ChainLayerReclaimTests` 的 G/H/I（各自红检成立）。
    - ✅ **邻路已修**（2026-10-03）：那两处删的是**发布目标里刚搬进来的那一份**（生产路径 = `<目标目录>\.ArchiveFixer.work\<taskId>\stage`，用户看不见；⚠ 但发布目标可以是别的目录 ⇒ 仍按不可逆对待）。现在按**事实表 `movedFromTo`** 判"这一份是不是本步搬进来的"（位置上只是别人的同名残留 ⇒ **不动它** + WARN）；整组删除要过**两道闸门**（A 每个成员都查得到我们搬到哪 / B 转调 `RestVolumeCompletenessGate` 看这棵树还有没有同基名归档件）⇒ 任一不过**整组一个都不删** + WARN。用例 `PackageLayerRulesTests` 4 条（红检：撤事实判据 ⇒ `FileNotFoundException`；撤闸门 B ⇒ 整组被删光）。⚠ 未验：那一行 WARN 的端到端日志、真机。
- ⛔ **本地化界面的 UnRAR 列出的清单认不出来时，绝不许当成"这个包是空的"**（唯一允许搬走/永久删除源包的判据；见 §48.4）。判据 = `UnRarListParser.ParsedListing.HeaderRecognized`（**见没见过 `Archive:` 这一行**；⛔ 不许用"认出了任意一个键"）；认不出 ⇒ `UnRarEngine.InterpretListing` 返 `ParserRejected` ⇒ `EngineRouter` 改问 7-Zip，**解压仍优先 UnRAR**。兜底两道：`LayerManifest.From` 与 `OutputVerifier.Verify` 把"成功但 0 个文件 0 字节"按**没有清单**办（`ManifestCrossChecked=false` ⇒ 判不出 ⇒ 什么都不做）〔本地化 UnRAR 的假清单〕
- ⛔ **「密码已经证实」之后失败 = 数据层面，不许再试密码候选**（现场见 §48.4）：老口径把 `CRC Failed in encrypted file` 当成"这个候选不对" ⇒ **解压完又回去试剩下的候选**、已解出的产物全扔、报「密码错误」。
  - 判据唯一出口 `Extraction/ProducedContentGate`（**只数事实**：产物目录里**非空文件数 ≥ 2 且字节 > 0**）—— 「密码错」的签名是第一份数据就过不去（0 字节桩 / 至多一个文件），「密码对、个别文件坏」的签名是整个包基本都解出来了。
    - ⛔ **两条跑解压的路都必须问它**：单层 `ExtractionCoordinator` 与递归 `RecursiveExtractor`（只接一条的代价见 `docs/真机事故复盘.md` §44.1）。⛔ 判据只此一处，别再各写一份。
  - 命中 ⇒ 两个分支（`WrongPassword` 与两义档 `PasswordOrCorrupted`）**当场 break**，收场落 `StatusText.PasswordProvenDataCorruptedFormat`（「文件损坏：密码已经证实是对的……换密码不会改变结果」）、`PasswordStatus=PasswordCorrect`、`Outcome=Failed`。
  - ⛔ **保守边界不许放宽**：只解出一个文件 / 零字节桩 / 数不出来 ⇒ 一律按老口径**继续试候选**。用例 `ExtractionPipelineFixTests.解出了大半个包之后失败_不再当密码候选不对_也不再往下试候选` + 对照 `只解出一个文件时_仍按老口径继续试下一个候选`。
  - ✅ 缺口已补（2026-10-02）：已解出的内容按「部分完成」发布（见 §11.2）。该档的结论连同 §11.4 那条一起看。
- ⛔ **日志导出不许落在"程序自己会整份删掉"的地方**：保存对话框默认开在"上次用过的文件夹" ⇒ 导出曾经落进 `…\其余物\`（见 §48.4）。
  - 判据唯一出口 `ProcessArtifactLayout.IsInsideDeletableProcessFolders`（其余物 / 旧名过程物 / `.ArchiveFixer.work`）。
  - `AppSettings.LastLogExportDirectory` **只**决定"下次对话框从哪开"（⛔ 不是默认导出位置、⛔ 不是自动行为）；命中上面那一档的目录**不记也不用作起始目录**（退回系统默认）。用例 `ExtractionPipelineFixTests.日志导出不记其余物和工作区这种会被删掉的目录`。
- ⛔ **"是源包坏了还是程序坏了"有一个只读、免密码的判据**：**读 7z 的 Start Header（32 字节，`-mhe` 也不加密）**——自述 `NextHeaderOffset` + `NextHeaderSize` 加 32 =**整包应当有的字节数**；与各卷字节数一比就知道**卷齐不齐**。再配上能列出/解开大部分条目、**`Sub items Errors: 1`**、空间曲线没触底三条 ⇒ **是源包那一段字节坏了，不是程序**。⚠ 排除不掉"瞬时位翻转 / 引擎解错那一个文件"，验证办法 = **同一个包再跑一遍**。⇒ 逐条只读审计见 §43。
- ⛔ **「末卷更小」这条直觉被实测否掉了一半 —— 不许拿它当闸门**：实测它只成立一半（数字见 §48.1）。⇒ 代码里只用**真成立的那一半**：**除末片外每一片彼此等大**（`SpannedZipIndex` 的候选池 = "满片尺寸的众数"），⛔ 不要求末片更小。
- ⛔ **一组分卷 = 一个任务 = 从首卷启动**（见 `修改日志.md`）：
  ① 整组改名在**批首**（`NormalizeDisguisedVolumeNamesForBatchAsync`，`ApplyBatchWorkspaceRoot` 之后 / 并发之前）；② 后续卷那一单落 `Skipped`（`SkipWhenAnotherTaskOwnsThisVolumeGroup` + `ArchiveTask.IsVolumeGroupFollower`）；③ 归组**只算不写**（`OneClickCoordinator.ResolveVolumeGroupFromDirectory`）且**只增不减**；④ 跟班**不进链尾裁决**（`DescribeChainVerificationGap` / `CompleteRootSourcePackagesAfterChainAsync`）。用例 `AaaReplayPipelineTests`。
  - ⛔ **递归层里的一组分卷也算"一个内层归档"**（`RecursiveExtractor.IsSameGroupContinuationVolume`：**同目录 + 同包基名 + 是后续卷**三条同时成立）：不加这一条就会报「1 个内层归档（多分支）」而保守停住（见 §47）。⚠ 只在递归 `SingleChain` 档走到这条判据。用例 `InnerLayerContinuationTests.单链模式_一层里是分卷组_…`（红检：撤判据 ⇒ `Rounds` 2→变红）〔分卷组不是多分支〕
- ⛔ **"给目标找个不撞名的名字"必须把"本计划里已经排出去的名字"也算进去**（见 §39）：让位只问文件系统（`SafePathHelper.AutoRenameFilePath`）时，**同一份计划里两条同名文件会算出同一个目标名** ⇒ 第二条撞 `already exists` 而**留在原地**。唯一写法与源包搬运逐字相同：`reserved` HashSet + `ProcessArtifactLayout.MakeUniqueTarget(..., reserved, probe)`，⛔ 永远不覆盖。
  - 链尾收内层包的计划是纯函数 `ExtractionCoordinator.PlanChainInnerPackageMoves(rootTask, chainTasks, restDirectory, outputRoot, out warnings)`；执行前**再看一眼**目标名；用例 `ChainInnerPackageMovePlanTests`。
  - ⛔ 「成品目录里还留着 N 个内层包没被清理」那句 `leftBehind` 警告，判据必须与搬运那一边**同一套**（`candidateSucceeded || rootSucceeded` 都不算"还留着"）—— 否则会出现"警告说还留着、下一行就搬走删掉"的自相矛盾。
- ⛔ **"续解开 / 不开"只有一个开关，别到处找**（见 §40）：`ExtractionCoordinator.cs:12718` 的 `Settings.RecursionMode != "SingleLayer"` —— 出厂默认档**不进递归核心**，走单层路径（内层包原样留着当**内容物**，继续解交给一键处理的轮次 / 手动「继续解」）；选了递归那两档才进 `RunRecursiveAsync`。⛔ `RunRecursiveAsync` 里那个 `AllBranches ? AllBranches : SingleChain` **不是缺陷**（红检证明那条分支到不了），别再去"修"它。
  - 两条路各自谁清内层包：**不开续解** ⇒ 内层包是下一轮的**源包** ⇒ `SourcePackageMover.Plan`；**开续解** ⇒ 内层包是**链上过程物** ⇒ `PlanChainInnerPackageMoves`。⇒ 撞名这一类缺陷两条路都堵住了。
  - 用例 `ChainManifestCompletenessTests`：`默认档只解当前这一层_手动只解压时内层包原样留着` + 对照 `单链自动展开_同一份夹具手动只解压也要解到叶子层`（用**手动「只解压」**隔离掉轮次续解）。用户文档 `docs/使用说明.md` §10.1.0 有两张表的白话版。
- ⛔ **改名之后，任务上每一处指向这个文件的旧路径都要改过来**（见 §38；展开见 §50.4）：`RenameService.UpdateTaskRenameSuccess(task, oldPath, newPath)` 必须**同时**改 `CurrentPath` 与 `VolumePaths` 里那一项 —— 只改一条 ⇒ 账上留下"盘上已不存在"的旧名字 ⇒ `ProcessArtifactLayout.SourcePackageMover.ResolveSourceGroup` 判"清单非空、却不含任务自己" ⇒ **整组搬运退化成只搬一份**。
  - ⛔ **"清单够不够全"不许按条数比**；⛔ "只改 `CurrentPath`"这种写法已被 `Extraction/TaskPathSync`（**唯一出口**，三处调用）取代，兜底一律"整组一份都不搬"。
- ✅ **"去杂质之后是什么"只有一个出口**（`ExtensionHelper.TryRecoverDisguisedArchiveBody`；真机现场见 `docs/真机事故复盘.md` §48.1）：删掉**最多 2 个非数字字符**后若**唯一地**变成**已知归档后缀**才算（本来就干净、或删出来有歧义 ⇒ 一律不认）。它同时喂三个消费点（见 §50.3）；⛔ 红线一个字没放宽：变体名字不标准 ⇒ `HasRenamedVolume=true` ⇒ **整组不许进可删的其余物**；⛔ 尺子只加在**归档本体**那一档上。
- ⛔ **引擎这一趟写出来的东西不是过程物**（`ExtractionCoordinator.IsEngineOutputFile`，唯一事实来源 =
  `RecursionResult.FinalOutputPath` 指向的那一层产物目录）：定稿那两道分卷组闸门（组完整性 / 孤儿卷）**都要先问它**
  —— 引擎刚解出来的产物被老判据当成"待续解的过程物" ⇒ 整层作废、产物一个字节没落地，日志还写着"解压成功 ｜ 校验通过"（真机现场见 `docs/真机事故复盘.md` §48.3）。
  ⛔ 拿不到递归结果（预检那一档）⇒ 退回老判据，一个字不变。
- ⛔ **跟班卷不在续解链上**（`ChainCompletionGate` 排除 `IsVolumeGroupFollower`）：一组分卷的后续卷那一单
  **按设计**落 `Skipped`，链尾闸门若把它算成"链没跑完"⇒ **每一批都会被自己组里的跟班卷拦下**，其余物一个都删不掉。真·续解成员 `Skipped` 照旧拦。
- RAR 命名：`Rar!\x1A\x07\x00`=RAR 1.5–4.x，⛔ 代码/注释/文案都不许写"这是 RAR3"；RAR5 有真样本（签名 `52 61 72 21 1A 07 01 00`，只读不入库）；老式编号族 `.rar`/`.r00` 按设计不认〔真样本验收〕
- ⛔ 验收规则（用户定）：必须真样本（或真机只读副本）跑通；合成样本通过 ≠ 问题解决；样本本体绝不进仓库、`H:` 原件只读，副本不在就跳过并说明〔验收规则：必须真样本跑通〕
- 待修（还没做，别当成"已解决"）
  - ✅ **「末卷更小」当独立证据：已做，而且实测纠正了一半** —— 用在两处：① `SpannedZipIndex` 的候选池（**满片尺寸 = 众数**）；② 「**缺的是末片**」诊断（清一色满片 + 一片开头是跨盘标记 ⇒ 缺 `.zip`，比含糊的「分卷缺失」准）。⛔ 但**没有**要求"末片更小"。
  - **跨目录找同组的卷：已做**（原话见 §48.1）。候选池唯一出口 `VolumeNameRepair.EnumerateVolumeCandidatesNearby`：**看** = 自己这一层 + 自己的直接子目录 + 父目录这一家（父目录本身 + 它的各直接子目录；这一块过 ≥ 16 KiB 粗筛 + 上限 500 份 / 200 个目录）；**不看** = 祖父及以上、孙目录、与这一家无关的目录（⛔ §8：不替用户满盘找文件；归档目录是卷根时退化）。
    - ⛔ **光改名不够：散在两层目录里的卷必须"收进入口那一层"**（`BuildPlanFromOrder` 的目标目录 = **引擎要打开的那一卷**所在目录：跨盘 zip 是末片 `X.zip`、RAR/7z 是第 1 卷）—— 引擎找兄弟卷**只看入口文件旁边那一层**，散着放即使名字都对也解不开（实测 7-Zip 就是这样）。计划里记 `GatheredVolumes`（> 0 时日志明说"几卷散在别的文件夹里、已一并收进来"）。
    - ⛔ **只收同一盘上的**：任何一卷与目标目录不同卷 ⇒ **整组不改**（`VolumeRepairGatherAcrossVolumeFormat`）—— ⛔ 不跨盘搬、绝不复制大文件。
  - ✅ **跨盘 zip ≥3 片 + 中间片名字里没有任何卷号：已做**（见上面"专属算法"那条）。⚠ 仍未做的是**排列爆炸那一档**：定不下来的片 > 3（6 种排列）时如实报"顺序定不出来"，⛔ 不猜；且**加密的包必须手上有对的密码**才验得出顺序。
  - 完整加密包+名字末尾纯数字 → 被 7-Zip 当"通用分片" ⇒ 误诊「分卷缺失」（别在 `RawSplitStreamDetector` 里加"看名字猜"）。
  - 伪装成 `.mp4`/`.apk` 的续卷：判定器已把"无卷号的同目录候选"按体积/位置推定+硬链接试开收进来（真案 ③），⛔ 绝不只凭后缀判；⚠ 试开做不了时（跨盘/拿不到工作区根）只到「疑缺卷」。
  - ✅ **7z「标准名第 1 卷 + 中间卷名字全丢（`111`）+ 标准名末卷」已接住**（2026-10-04，阶段 B）：入口判据从"**手上这一卷**名字里有没有卷号"换成"**这一组的名字自不自洽**"（`Detection/VolumeContentInference.cs:299 ReadSiblingShape`：基名逐字相同 / 卷标记连续 / 除末片外等大 **三条同时成立** + 组里真有丢名成员；⛔ 不是"入口是标准名第 1 卷就放宽"）；手上这卷没魔数时去同目录找锚点（两个锚点 ⇒ 判不出 ⇒ 什么都不做）。新增 `Detection/SevenZipStartHeader.cs`（只读 32 字节）拿起始头自述的 `32+NextHeaderOffset+NextHeaderSize` 比 Σ 卷长：**小于 ⇒ 如实报"还差 N 字节"且一次引擎都不调**（事实位 `ByteBudgetMismatch`）、**正好 ⇒ 字节数证据成立**、**大于 ⇒ 只接受唯一说得通的缩池**。基名与试开**都从锚点推**（⛔ 不再从"手上这卷"的名字推）。⛔ **改名仍只有硬链接试开成功才认**（试不开 / 判不出 ⇒ 一个名字都不改）。实测：入口 `.001` 与入口 `111` 得**同一份计划**（`CanRepair=True`、`111→111.7z.002`、另两卷不动、出计划不碰盘）。用例 `NamelessMiddleVolumeContentPathTests`(4) + `SevenZipStartHeaderCompletenessTests`(2) + `SuffixMarkerDigitHintTests`(3)。⚠ 真机未验（用户 5G/5G/3G 真样本没跑）。
  - 7z 头部被压缩时读不出加密（`-p` 与不加密包 64 KiB 内逐字节同构；不引依赖/不调引擎 ⇒ 如实不报）
  - 7z `-mhe` 与 ZIP AES 没有真样本；跨盘 zip 中间片一律 Unknown
- ✅ 加密判读=只读头/尾（RAR/ZIP/7z）：判据器 `Detection/{Rar,Zip,SevenZip}EncryptionReader`（词表 `ArchiveEncryptionState/Reading`，不调引擎/不引依赖）；唯一出口 `ArchiveDetectService.ApplyEncryptionVerdict`。逐条判据 / 用例 / 真样本变量见 §48.4 + §50.2。
  - ⛔ 读不出来一律"不知道"（`Unknown` ≠ `NotEncrypted`，⛔ 不猜、不误报）；⛔ 分卷要进"可删的其余物"必须先证明整组完整；⛔ `CRC Failed in encrypted file` 不许单独定原因（结论必须带出 7-Zip 原话）。
  - ⛔ RAR 两义句：错密码 = **退出码 3** + 「校验和错误。文件已损坏或密码错误。」⇒ 判据 = 退出码 3 + `RarEncryptionReader` 判"不是明确没加密"（⛔ 不比中文）落 `PasswordOrCorrupted`，候选循环**继续试下一个**；**非加密包退出码 3 仍是「文件损坏」**。
- ✅ **列表显示：名字上明确是同一组的分卷 ⇒ 一律只留一行，并在那一行注明「共 N 卷」**（用户 2026-10-02 拍板；取证与实测见 §49.5 / §50.5）：
  - **实测确认**（2026-10-02 19:21 那次导入，他原话「其实屏幕上显示没有毛病」）：`测试\BBB` 6 套样本 = **10 行 = 5 个归档组各一行 + 5 个 mp4**（盘上 25 个文件、一卷都没多出一行）；空间体检「10 个源包共 3.79 GiB」与盘上 4,071,857,429 字节逐字对上。
  - 归组**只看名字**（`FileScanService.cs:114` → `VolumeGroupingService.ApplyVolumeGrouping`），「共 N 卷」唯一出口 `VolumeGroupingService.BuildVolumeInfoText`（①页「分卷」列已放出来）；⛔ 红线不动：判不出 ⇒ 什么都不做、`VolumeGroupResolver` 四档 / 六条证据一个字没放宽。⚠ **"RAR 两套 4 卷各自成一行"在 HEAD 上复现不出来**（⛔ 不许写成"已复现并修好"）。


- ✅ **基名只有一个出口**（2026-10-03，阶段 A）：`Helpers/FileNameHelper.cs:344 TryResolveVolumeBaseName`（5 档：组键 / 剥标记 / 包名 / 锚点 / 伪装卷），5 处旧实现全部转调（`VolumeGroupDetector` / `StripVolumeMarkersCore` / `OutputPlacement` / `VolumeNameRepair` / `VolumeNumberFromContent`，私有助手全删）；**只搬家、不改结论**（改前改后 45 名字 × 11 列逐字相同，`ArchiveBaseNameTests` 52 条）。⚠ **两档本来就是两件事、⛔ 别合并**：**组键**保留归档后缀段（`giu910.7z`）、**包名 / 锚点**剥掉它（`giu910`）；另有"跨段保不保留后缀段""杂质归不归一"两格差异仍未合并（合并＝改结论，要用户拍板）。守卫 `StripVolumeMarkersCore(` 只许出现在 1 个文件里。
- ✅ **「还原」工序进了递归层**（2026-10-03，阶段 B0；口径全文见 `_tmp\方案-分卷族系统化识别.md` §2.1）：递归层**组卷之前**先「按魔数认底层（续卷退到同组第 1 卷的魔数）→ 改回规范名（**替换不追加**、基名不动）→ **回族证据重判**」，执行体仍只有既有 `TryApply`（全成或全不成、绝不覆盖、目标名被占 ⇒ 整份不做），⛔ 只碰本层产物目录（**用户源目录一个字节不动**，有用例的 SHA256 快照钉住）。用例 `InnerLayerDisguiseRestoreTests`(7)（红检：撤掉那次调用 ⇒ 7z 红在**内容断言**、RAR 红在**日志断言**）。⚠ 批首挂点①未动；真机未验。
- ✅ **阶段 C+D（2026-10-04）**：**RAR 族的入口放开** —— `Detection/VolumeNumberFromContent.cs` 的 `ResolveGroup` 新增 `requireCurrentFirstVolume`（默认 true = 老口径），改名那条路传 false ⇒ **入口可以是"调用方手上那一卷"**（跨盘 zip 仍是末片）。⛔ **卷号证据一个字没放宽**（仍只由内容给出、仍连成 1..N、RAR4 基数两解仍拒、`MHD_FIRSTVOLUME` 仍交叉核对）；⛔ 跨盘 ZIP 索引路 / 试拼 3 片上限 / `total > 2 ⇒ Refuse` 原样；⛔ RAR 老式编号族（`.rar`/`.r00`）按设计不认；⛔ 数字弱线索仍排在内容证据之后。**D 没有新造第三把尺子**：批首链 `Services/RenameService.cs` 本来就转调同一把（两道闸门「格式未知 ⇒ 不改」「末尾纯数字＝卷号」未动）⇒ D 只钉口径 + 补用例（批首预览与还原工序给出**逐字同一份目标名**）。用例 `VolumeEntryPointTests`(11)。⚠ **未验**：真机；「入口那一卷」这条路径**只有推理没有用例**（`BuildPlanFromOrder` 的 primary 本来就挑要改名的那一卷 ⇒ 只有"手上那卷在别的目录"时才分叉）。

- ✅ **改名反向 + 骨架相似（2026-10-04）**：① **目标名一律从锚点推**（⛔ 不再从"手上这一卷"推），放行前多一道闸门「**建议名自己必须是规范名**」（`VolumeNameRepair.cs:491`）⇒ 再也不会出现"把干净名改成脏名"（`x.7z.001 → x.7z.001.txt` 那种反向计划）；跨档比较收口成同档（`ResolveAnchorBaseName`）；`111.7z.001 + 111.7z.002.txt + 111.7z.003` 只改脏的那一卷。② **相似算法升级成"通用骨架命中"**（唯一实现 `ExtensionHelper.TryMatchKnownSkeleton`；卷标记那份 `TryMatchPartNumberedSkeleton`）：骨架字符**按顺序**出现即可、中间夹的全是杂质，但**必须首尾对齐**（`7_______z` ✓；`x.rarity` / `x.zipper` 连"命中"都不算）+ **结果唯一**（`7zip` 同时命中 `7z` 与 `zip` ⇒ 判不出）⇒ `333.78a8fuaz.003` 认得出是 7z 分卷。③ **卷标记段也吃骨架档，但"取最右那个自洽的卷标记"**（`allowPartNumberedSkeleton` 默认 false，只有 `<基名>.<卷标记>.rar` 尾巴逐字 `rar` 才传 true）⇒ `444.pa8rt1.rar` 认得成 `part1`，而 `444.p1art2.part2.rar` 仍取最右那个干净的 `part2`、基名 `444.p1art2`（红检：撤"取最右" ⇒ `Expected "444.p1art2" / Actual "444"`）。④ ⛔ **两个"包基名"不许合并**（用户 2026-10-04 同意）：闸门 `RestVolumeCompletenessGate` 用的是**归档基名**（`GetArchiveBaseName`；实测 `222.zscip` → `222`，与伙伴 `222.z删除01` 相等 ⇒ **拦下**），换成包基名（`222.zscip`）⇒ **放行** —— "其余物里是半套"那道**防数据丢失**闸门就失效了（2026-09-30 25 GB 的形状）；两值两结论钉进 `RestVolumeGateBaseNameTests`。⚠ 未验/限制：真机未验；**`444.pa8rt1.rar` 只做到"认得出来"，改名/还原那一步没接**（它与 `p1art2` 同形，要动 `TrySplitDisguised` ⓪ 档）。

### 11.5 管线（落点/弹窗/校验/显示/密码）

- ⛔ 落点最少两层文件夹：最外层=包名目录、最里层=最后一个内层包层，⛔ 塌缩/不套层分支不许吃掉最里层；省层只能省中间的内层包层，⛔ 普通文件夹永不摊平（唯一出口 `Extraction/PackageLayerRules.cs`；递归=就地替换）
- ⛔ **展开恰好一层时，L3 预期取"这一层解压前就列过的那份清单"**（`ChainManifestResolver.Resolve`：`knownList` → **本层清单** → 才现问引擎）：递归那条路的调用方给"现问"传的是**空密码**，`-mhe` / `-hp` 的包**必然**问不出来 ⇒「完整性：无法确认」⇒ 链尾拒删 ⇒ 其余物留在用户目录里（见 §47）。用例 `ChainManifestResolverTests`(5) + `ChainManifestCompletenessTests.形状6_单层加密头包_…`（红检 2 红）〔其余物为什么不删〕
- 落点模型 v2：判据出口三处——`OutputPlacement.ResolveDestinationDirectory`（落点）、`OneClickCoordinator.ShouldAddContinuationLevelLayer`（续解层）、`ResultFinalizer.Plan(..., suppressPackageFolderLayer:)`（定稿套层）；契约 `docs/输出与整理模型.md` §1.1/§3.1/§3.3.1〔落点模型 v2〕
- ⛔ **落点这一档是"记忆"的：用户上回选的那一档，下次打开就是那一档**（用户 2026-10-01：「要不然每次解压都要点击一下未选择指定位置」；现场见 `docs/真机事故复盘.md` §38.5）。两条口径：
  - 一键处理确认框 → `Confirmed` 之后**一律** `SaveOneClickOptionsAsDefaults`（记忆范围只有面板上看得见的那几档；⛔ 取消 / 没弹框时一个字节都不写）；面板初值 = `OneClickRunOptions.FromSettings(settings, _vm.SelectedOutputDirectory)`。
  - ⛔ **"搬值"不许走 `SelectedOutputDirectory` 的 setter**（它的语义是"用户指定了一个位置"，会顺手把 `ExtractToOriginalDirectory` 改成 false）：启动填回记住的目录 / 写盘后收尾 / 恢复默认三处一律走 `MainViewModel.SyncOutputLocationFromSettings(rememberedDirectory = null)`（只搬值 + 发通知，不改档位、不写设置）。⛔ 别再让"填回一个记住的路径"把用户选的「未指定位置」翻面。用例 `OutputLocationOnTaskTabTests`（两个方向都钉）。
- ⛔ 整组改名（`VolumeNameRepair` 改整组**每一卷**）后必须**同步全表任务路径+快照**：唯一出口 `ExtractionCoordinator.SyncTasksAfterVolumeRename`（按 plan.Items 重写全表 CurrentPath/VolumePaths + 重拍快照；调用点 `:6227`、`MainViewModel.cs:5360` 手动「修复分卷名并重试」）；只同步驱动那一单 ⇒ 兄弟任务下一次比对假报「源文件已变化」。不变量 11 口径一个字没放宽。
- ⛔ 两处"整组改名"实现都**必须全成或全不成**：`VolumeNameRepair.TryApply`（源包）与 `BrokenVolumeChainRepair.TryApply`（内层续解链）—— 中途失败要**倒序改回原名**（⛔ 不是"改回目标名"：那等于接着把改名做完，日志说失败、盘上却变了）、回滚失败**如实点名**。用例 `VolumeNameRepairRollbackTests` / `BrokenVolumeChainRepairRollbackTests`。
- 一键处理期间零弹窗（唯一例外=批末汇总框）：`ExtractionCoordinator.SuppressDecisionPromptsForOneClickRun()` 一处收口，且必须排在 `ResetBatchConflictState()` 之后（先抑制后清零=没抑制）；⛔ 不许在一键档批中间新增任何"要点一下"的框〔一键处理零弹窗（唯一例外：批末汇总）〕
- 递归多层的结果校验：展开 >1 层不拿第 0 层清单当预期（只做非空/落点/预算三道），否则误报「解压失败」〔递归多层的结果校验〕
- 显示口径：成功任务显示「解压成功 100%」（判据=机器终态 `Outcome == Succeeded`，⛔ 不读瞬时百分比）并落 `EndTime`；「部分完成」在批末单独一档；①页「大小」列宽 118（别再收窄）〔显示口径〕
- 导入后的空间体检：三个触发点走 `MainViewModel.CheckSpaceForTasksAsync` → `ReportSpaceCheck`（复用 `ExtractionCoordinator.BuildSpaceAdvice`）；只提醒：不改设置、不改勾选、不拦任务〔导入后的空间体检〕
- 密码三条口径〔四路只读核查一轮：三条真缺陷 + 手动密码框撤掉〕：
  - 密码口径：一键档无手动密码输入框（只写一条"去「密码」页一键导入"的 INFO）+ 确认框 B 段同一句指路 + 批末一条红字（必须写"只是可能"）；手动「只解压」无可用候选才问。
  - 密码记忆读不出来（换机器/重装系统/文件损坏）⇒ 先复制一份 `password-list.dat.unreadable-<时间戳>.bak`（`PasswordListStore.TryBackupUnreadableFile`；⛔ 只复制，绝不移动/删除原件），文案如实点名备份名。
  - ⛔ 批末合并提示的**登记判据含三档**：`密码错误` / `密码错误或文件损坏`（`PasswordOrCorrupted`，RAR 两义句）/ `达到密码尝试上限`。漏掉两义档 ⇒ 真机那批 RAR `-p` 包**一条指路都不出现**。`RecordPasswordFailure` 与 `ShowPasswordFailuresSummaryAsync` 的分档必须**同时**改（§9.5）；文案里"可能""不等于确认是密码问题"一个字不许省（⛔ 也不许算成"达到尝试上限"）。
  - ⛔ **撞上「每层密码尝试上限」时批末必须补一句指路**（用户 2026-10-02 原话："如果没有试出来可以提醒用户可能是没有密码，或者密码位置在 10 条之后，让用户调整顺序"）：唯一出口 `StatusText.PasswordAttemptLimitHintFormat`（`{0}` = 上限），只在 `attemptLimitCount > 0` 时补一行 —— "可能本来就没有密码 / 也可能对的密码排在候选更靠后 ⇒ 去④页调顺序或调大上限"。⛔ 上限**保持默认 10**（他："10 条刚好"，④页可改 1–1000）；⛔ 不新增状态（终态仍是 `达到密码尝试上限`）；⛔ 既有"候选还没试完就按上限停了，不等于密码错误"那两句原话一个字不改。
- 导入时的无用物弹窗已退休（导入即自动移出列表）：只剩手动「只解压」会弹，一键档并进唯一那个确认框

### 11.6 工作区/检验等级/终态口径

- 工作区口径全文见 §6 不变量 12（唯一出口 `Storage/WorkspaceRootResolver.cs`）；工作区那棵树不算产物（`Storage/WorkspaceTree.cs`）。
- 检验等级 L0–L6（唯一写处 `docs/检验等级.md`；原话见 §48.3）：每级一道闸门，判不出 ⇒ 什么都不做；不可逆动作只允许在"L4=可证完整+L5 复核通过+未取消+属于本任务"四条同时成立时发生；L4 三态唯一出口 `Extraction/ResultCompleteness.cs`（`Complete`/`Incomplete`/`Undeterminable`，默认值=`Undeterminable`），判不出如实写"无法确认完整性"（`StatusText.CompletenessUndeterminableFormat`）。
- L4 清单**按层取**：展开 >1 层用**叶子层**清单，唯一出口 `Extraction/ChainManifestResolver.cs`；拿不到 ⇒ 如实「判不出」+ 日志点名**哪一层**，⛔ 不许退回第 0 层顶替。
- 「为什么没删源包」唯一出口 `ResultCompletenessVerdict.Blocker`；链尾收尾**逐条判据**（`ApplyRestHandlingAfterChainAsync`+`StatusText.ChainRestBlockedPrefix`），⛔ 不许静默 return。
- 终态口径唯一出口 `Models/TaskOutcomeClassifier.cs`：①页分桶/一键汇总行/批末诊断读同一份失败名单（⛔ 不许再各写一份），判据只读状态常量+机器终态。
- 盘满=`EngineErrorTypes.NoDiskSpace`（`Engines/EngineIds.cs`）：两个引擎解析器都落「磁盘空间不足」，⛔ 不再误报「权限不足」。
- ⛔ 预检拦下必须落 Failed 终态：唯一出口 `ExtractionCoordinator.MarkStoppedBeforeExtract`（缺卷 / 文件不存在 / 落点无效 / 源文件已变化四处共用，走既有 `ArchiveTask.MarkFailed`）；不落 `EndTime` ⇒ Outcome 永停 `Pending`（汇总说「未处理」）。Failed 仍只由唯一收口落。
- ✅ 缺口 ⑨ 已修（2026-10-01）：递归工作区"没人配过根"时**不再回落 `%TEMP%`**——`AllowSystemTempWorkspaceFallback` 默认 false（拿不到根就报错指路），测试宿主 `TestAssemblyInitialize` 显式打开；用例 `WorkspaceTempFallbackGateTests`。
  - ⚠ 另：上一轮验收证据全是**合成样本**（形状 1/2/5），**真机验收仍空白**。
  - ⚠ 上一轮新记：三处修复**未在真机复验**。

## 12. 细节去哪看

- 用户原话、每条指示的逐条落地史 → `旧AGENTS.md`（旧版全文 135 KB，含「用户明确指示」表）
- 某次事故 / 某条红线**当初为什么这么定**（现场、根因、红检）→ `docs/真机事故复盘.md`（原 §11 正文**逐字**归档，32 节）
- 某次改动具体做了什么 → `修改日志.md`（一条一行流水账）
- 其余都在 `docs/` 里 —— 每份文档见 §10 文档地图（含用户指示汇总、需求变更、使用说明 / 功能一览 / 设置项 / 人工测试清单）。
