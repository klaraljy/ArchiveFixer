# ArchiveFixer

Windows 桌面工具：批量识别**被改坏后缀**的归档文件、按真实格式修正文件名、按密码本批量解压（含分卷），
并给出可追踪的结果 —— 用尽可能少的操作，处理"来源不明、后缀可疑、加密、分卷"的资源包。

## 项目状态

| 项 | 值 |
|---|---|
| 当前状态 | **M1–M5 全部里程碑完成**（里程碑定义见 `AGENTS.md` §10） |
| 代码基线 | 2026-08-09 快照（原"第十一版"），2026-09-21 迁入本仓库 |
| 验证 | `dotnet build` 0 错误 / 4 个既有可空性警告；`dotnet test` **452 通过 / 0 失败**；`dotnet format --verify-no-changes` 通过 |
| 规则 | `AGENTS.md`（含用户明确指示，与其它文档冲突时以它为准） |
| 需求 | `docs/需求书.md`（长期愿景，已冻结）/ `docs/需求变更.md`（只追加的变更日志） |

## 现在能做什么

- **格式识别**：读文件头魔数判真实格式，不信扩展名。覆盖 ZIP / 7Z / RAR4 / RAR5 / GZIP / BZIP2 / XZ / TAR /
  CAB / ARJ / LZH / Z / Zstandard / LZ4 / RPM / ISO。
- **后缀分析**：后缀正常 / 缺失 / 不匹配 / 多重后缀疑似伪装 / **分卷后缀**（分卷单独归类，不会被误当成伪装）。
- **批量改名**：智能修正、添加、替换、删除最后一个、删除多个 —— **一律先生成预览**，预览里可手工改新文件名。
  分卷文件受保护，不会被"智能修正"切断分卷链。
- **分卷**：**一组分卷 = 一个任务**，只从第一卷启动；缺卷时直接报"缺哪几个"并拒绝开始。
  认五种命名：`.001` / `.z01` / `.r00` / `.partN.rar` / 本体（`x.zip`、`x.rar` 就是第一卷）。
- **密码**：统一密码、单任务密码、密码本（**列表式**一行一个 + **映射式** `名称:密码`，按归档名命中）、
  最近成功密码优先；可显式开启从**压缩包同目录的说明文件**里提取候选。密码只存内存。
- **密码尝试上限**：每一层最多真的试 `MaxPasswordAttemptsPerLayer` 个候选（设置里可改，缺省 10，范围 1~1000）；
  到上限的状态是**达到密码尝试上限**，不是"密码错误" —— 候选没试完不等于密码本里就没有。
- **批量解压**：并发数按设置 `MaxParallelExtractCount`（1~8，默认 1 = 串行）；"停止后续"与"取消当前"是两套独立语义；
  解压前做**条目路径预检 + 资源预算**，解压后做**落点校验 + 结果校验（条目数/总大小）**。
  产物一旦越出目标根目录（含"产物目录里混进了目录联接点 / 符号链接"）就是**失败结论**：
  不归集、不处理源包，原因写进任务状态与错误信息 —— 不会只留一行日志却照样报"解压成功"。
- **递归解压**：三种模式 —— **只解当前层（默认）** / 单链自动展开（只有一个内层包时才继续）/ 展开所有分支；
  多分支**必须问你**；层数、文件数、总大小、展开比、密码尝试次数都有硬上限，到顶停下并说清停在哪一层。
  内层包同样要过安全预检与落点校验，越界即失败（与单层同一口径）。
- **其余物（原「过程物」）**：为得到内容物而产生、用户不需要的东西**集中一处** `其余物\` ——
  内层归档、分卷、抠出来的中间 ZIP、纯壳文件夹，**以及源包本身**。
  包本来就有自己目录时就是 `111\222\其余物\`；多个包共用同一个输出根时（解压到当前目录 / 直接解到指定目录）
  按包名分层 `111\其余物\222\`，不会互相撞名。老版本留下的 `过程物\` 仍然认（删除功能能清掉它）。
- **源包处理（一键处理，默认移入其余物）**：**一键处理**在"内容物已定稿 + 输出校验通过 + 未取消 + 属于本任务分卷组"
  四条同时成立之后，按设置处理源包 —— `MoveToRest`（**默认**，整组移入 `其余物\`，同名加 `(1)` 绝不覆盖，
  跨盘先复制成功再删原件）/ `KeepInPlace`（不动）/ `DeleteAfterVerify`（沿用清理源包的规则）。
  搬不动（只读 / 被占用）时任务标「部分完成」并写明"内容物已好，源包未能移入其余物"，内容物结论不受影响。
  ⚠ **手动「只解压」这条地基路径永远不动源包**，与这里的设置无关。
- **收尾**：结果可**归集**到统一目录（同名自动改名不覆盖）；可**可选清理源包**（默认关闭，
  只有"解压成功 + 校验通过"才删；只管手动「只解压」这条路径）；失败清单可复制或导出成 txt。
- **一键处理**：识别 → 修正伪装后缀（出一次预览确认）→ 按密码本解压 → **自动续解本轮新出现的内层包**
  （最多 3 轮；达到上限会明说"还有更深的内层包没解"）→ 一行汇总
  （成功 / 失败 / 跳过等分项**互斥且可加**，合计恒等于任务总数，不会一个任务被算两次）。
  没有任何需要改名的任务时，改名这步整个跳过，真正零确认；按下「停止后续」后不再续解。
  默认会把**源包**（含分卷整组）移入输出目录里的 `其余物\`，整理完删一个目录就干净了；可在设置里改成"留在原地"或"校验通过后删除"。
- **可恢复**：中间产物放在 `<程序目录>\data\work`；中断/取消时**不发布、不清理**，
  启动时报告未完成的工作区（只报告，不自动删）。
- **中间工作区不留垃圾**：解压**成功且输出校验通过**之后，本任务抠出来的内嵌归档工作区
  （`data\work\<任务名>\`，双面文件动辄几百 MB）会被清掉，删前删后各写一条日志；
  取消 / 部分完成 / 校验失败 / 越界检出时**一律保留**（这些中间件是用户唯一的线索）。

## 环境要求

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（含 Windows Desktop 运行时）
  - 本机同时装了 .NET 10 SDK，但**项目固定 `net8.0-windows`**，不随 SDK 漂移
- 7-Zip 命令行已内置在 `ArchiveFixer/tools/7zip/`（26.01），构建时自动复制到输出目录，**不需要另外安装**

## 构建 / 运行 / 测试

```powershell
dotnet build ArchiveFixer.slnx                          # 构建（Debug）
dotnet build ArchiveFixer.slnx -c Release               # 构建（Release，桌面快捷方式指向这个）
dotnet run --project ArchiveFixer/ArchiveFixer.csproj   # 运行 GUI
dotnet test ArchiveFixer.Tests/ArchiveFixer.Tests.csproj # 单元测试 + 基线冒烟测试
dotnet format ArchiveFixer.slnx --verify-no-changes      # 格式检查（只看差异）
```

### 桌面一键启动

桌面上有一个 `ArchiveFixer` 快捷方式，直接指向：

```
ArchiveFixer\bin\Release\net8.0-windows\ArchiveFixer.exe
```

- **改了代码之后**要重新 `dotnet build ArchiveFixer.slnx -c Release`，快捷方式才会启动到新版本
  （它指向构建产物，不是源码）。
- 程序图标是 `ArchiveFixer/Assets/ArchiveFixer.ico`（多尺寸 16–256，`ArchiveFixer.csproj` 里用
  `<ApplicationIcon>` 嵌进 exe），快捷方式直接复用 exe 的图标。
- 配置、日志、临时文件、工作区都写 **`<程序目录>\data\`**（绿色分发，跟着程序目录走，
  **不写 C 盘的 `%AppData%`**）。因此程序要放在**可写目录**（别放 `Program Files` 这类需要提权的目录）；
  正常使用不需要管理员权限。

## 配置

配置文件：**`<程序目录>\data\appsettings.json`**。首次启动时，若 `data\` 下还没有配置，
会把旧版本放在**程序目录**或 `%AppData%\ArchiveFixer\` 的同名文件复制过来（两个历史位置都认）。
日志、临时文件、工作区在同一位置下的 `logs\` / `temp\` / `work\`。

> `data\` 不可写时日志会自动退化成"只写屏幕日志"（不弹错、不崩），所以别把程序放进需要提权的目录。

主要配置项：

| 配置 | 说明 |
|---|---|
| `RecursiveScan` | 文件夹扫描是否递归 |
| `ScanMode` | `ScanAllFiles` / `ScanKnownArchiveExtensions` / `ScanSuspiciousFiles` |
| `UnknownFormatAction` | 未知格式：`MarkUnknown` / `Skip` / `TryExtract` |
| `DefaultExtension` | 智能修正的默认目标后缀 |
| `ExtractToOriginalDirectory` / `CustomOutputDirectory` / `KeepArchiveNameFolder` | 输出位置 |
| `ConflictAction` / `OverwriteMode` | 改名冲突、解压覆盖策略 |
| `TestBeforeExtract` | 解压前是否先跑 `7z t`（默认关：开着会让"无密码的包"看起来像卡住） |
| `TryEmptyPasswordFirst` / `UseGlobalPasswordForAllTasks` | 密码尝试策略 |
| `MaxPasswordAttemptsPerLayer` | 每层最多试几个密码候选（默认 10，1~1000）；单层与递归内层共用同一个值，改完当场生效 |
| `MaxParallelExtractCount` | 并发解压数（1~8，默认 1 = 串行）；**真的生效**，不是摆设 |
| `RecursionMode` / `MaxRecursionDepth` | 递归模式（默认 `SingleLayer`）与最大层数（1~10）；改完当场生效，不必重启 |
| `CollectResultsToDirectory` / `CollectTargetDirectory` | 结果归集开关与目标目录 |
| `SourceHandling` | **一键处理**里怎么处理源包：`MoveToRest`（默认，移入其余物）/ `KeepInPlace`（不动）/ `DeleteAfterVerify`（校验通过后删）。**手动「只解压」不受它影响** |
| `DeleteSourceAfterExtract` | 手动「只解压」路径：解压成功后清理源包（默认关；只有"解压成功 + 校验通过"才删） |
| `EnableSidecarPassword` | 从压缩包同目录的说明文件里提取密码候选（默认关） |

## 目录结构

```
ArchiveFixer/               WPF 主程序
  Models/ Services/ ViewModels/ Views/ Converters/ Helpers/     模型、服务与界面
  Detection/ Engines/ Extraction/ Password/ Security/ Storage/   核心分层（不得引用 WPF）
  tools/7zip/               内置 7-Zip 命令行
  appsettings.json          默认配置模板（运行时实际读 <程序目录>\data\appsettings.json）
ArchiveFixer.Tests/         xUnit：单元测试 + BaselineSmokeTests（M1 验收）
docs/                       需求书 / 需求评审与考古 / 需求变更
samples/                    样本生成脚本 + 清单（样本本体不入库）
AGENTS.md                   项目规则与里程碑
```

分层规则见 `AGENTS.md` §4：`Detection/ Engines/ Extraction/ Password/ Security/ Storage/`
**不得引用 WPF**（纯模型仍在 `Models/`），GUI 只通过 ViewModel 调用它们。

## 测试样本

```powershell
pwsh -File samples/generate-samples.ps1        # 生成到 samples/generated/（不入仓库）
```

- 只依赖内置 7z.exe；覆盖第一批类别（普通 / 加密 / 分卷 / 伪装后缀 / 损坏 / 仅名字像压缩包 / 复合后缀）
- **自动化冒烟测试不依赖这个目录**：`BaselineSmokeTests` 自己把样本生成到临时目录，
  所以全新克隆直接 `dotnet test` 就能跑
- 样本里出现的密码一律是合成密码；**任何真实密码都不得写进仓库**（`AGENTS.md` §8）
- 清单与待补类别见 `samples/MANIFEST.md`

## 已知限制

这些是**已知且记录在案**的，不是"没想到"：

| 限制 | 说明 / 计划 |
|---|---|
| 密码明文出现在进程命令行 | 7z 命令行的固有限制（`-p<密码>`）；日志/报告/清单层已脱敏，但进程列表能看到。**不假装解决了** |
| 无解压进度百分比 | `-bsp0` 关掉了 7z 进度输出，界面只有"处理中/完成"两态 |
| 压缩炸弹**拦不住已写出的那一次** | 只能"解压前按清单拦 + 解压后按实测量拦并停止后续"；7z 写盘时我们看不到它的写入 |
| 落点校验只看得见目标目录**里面** | 引擎绕过我们直接写到别的目录去，进程外发现不了。两道防线（解压前条目预检 + 解压后落点校验）也只能覆盖"产物目录内可观察"的越界；已经写出去的那一次同样拦不住 |
| 无危险文件检测 | `.exe` / `.scr` / `.lnk` 之类只做展示，没有专门识别与提示 |
| 无源文件变化检测 | 任务开始时记录快照、处理中比对，尚未实现（`AGENTS.md` §6 第 11 条） |
| 递归模式的工作区**成功后不自动清理** | 只有走单层收尾（`RecursionMode = SingleLayer`，含一键处理的每一轮）的任务会在成功后清掉自己抠出来的内嵌归档工作区；递归解压的中间产物（`data\work\recursive\`）成功后仍留在工作区，启动时会被算进"未完成的工作区"报告。`ExtractionWorkspace.Cleanup()` 已实现但递归路径还没调用（后续项） |
| 符号链接条目看不出来 | 归档条目列表里没有链接目标，名字干净的链接条目仍可能把内容写到别处；现在靠"解压后落点校验"发现（**发现即判失败，不归集、不清理**），但拦不住那一次写入 |
| 外接盘 / 网络路径未专门适配 | 是非目标（`AGENTS.md` §2）：只保证不写死盘符、不在源目录建工作区 |
| 4 个可空性编译警告 | `EmptyStringToTextConverter` 的 `CS8600`，不影响行为 |
| LICENSE 未定 | 对外分发前必须确定，并一并核对内置 7-Zip 的 LGPL + unRAR 条款 |

## 许可与第三方

- 内置的 7-Zip（`7z.exe` / `7z.dll`，26.01）版权归 7-Zip 作者，采用 **LGPL + unRAR restriction**；
  本仓库内置它只是为了这个工具能直接调用，未做任何修改。对外分发前需核对许可证条款。
- 本仓库**尚无 LICENSE**（未定，见 `AGENTS.md` §12）；未定之前不创建空许可证文件。

## 开发约定（摘要）

完整规则见 `AGENTS.md`。最容易被忘的三条：

1. 界面状态字符串统一引用 `Models/StatusText.cs` 常量，**禁止手写中文字面量**。
2. 密码只存内存；日志、报告、剪贴板一律脱敏。
3. "停止后续"与"取消当前"是两个独立取消源，**不得**合并成一个。
