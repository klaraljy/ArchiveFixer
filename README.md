# ArchiveFixer

Windows 桌面工具：批量识别**被改坏后缀**的归档文件、按真实格式修正文件名、按密码本批量解压（含分卷），
并给出可追踪的结果 —— 用尽可能少的操作，处理"来源不明、后缀可疑、加密、分卷"的资源包。

## 项目状态

| 项 | 值 |
|---|---|
| 当前里程碑 | **M1 基线固化**（里程碑定义见 `AGENTS.md` §10） |
| 代码基线 | 2026-08-09 快照（原"第十一版"），2026-09-21 迁入本仓库 |
| 规则 | `AGENTS.md`（含用户明确指示，与其它文档冲突时以它为准） |
| 需求 | `docs/需求书.md`（长期愿景，已冻结）/ `docs/需求变更.md`（只追加的变更日志） |

## 现在能做什么

- **格式识别**：读文件头魔数判真实格式，不信扩展名。覆盖 ZIP / 7Z / RAR4 / RAR5 / GZIP / BZIP2 / XZ / TAR /
  CAB / ARJ / LZH / Z / Zstandard / LZ4 / RPM / ISO。
- **后缀分析**：后缀正常 / 缺失 / 不匹配 / 多重后缀疑似伪装。
- **批量改名**：智能修正、添加、替换、删除最后一个、删除多个 —— **一律先生成预览**，预览里可手工改新文件名。
- **批量解压**：串行执行；支持"停止后续"与"取消当前"两种独立取消语义；密码候选逐个尝试。
- **密码管理**：统一密码、单任务密码、密码列表（txt 导入，一行一个，不 Trim）。
- **日志与统计**：屏幕日志 + 文件日志 + 失败清单；密码只记状态，不记内容。

## 环境要求

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（含 Windows Desktop 运行时）
  - 本机同时装了 .NET 10 SDK，但**项目固定 `net8.0-windows`**，不随 SDK 漂移
- 7-Zip 命令行已内置在 `ArchiveFixer/tools/7zip/`（26.01），构建时自动复制到输出目录，**不需要另外安装**

## 构建 / 运行 / 测试

```powershell
dotnet build ArchiveFixer.slnx                          # 构建
dotnet run --project ArchiveFixer/ArchiveFixer.csproj   # 运行 GUI
dotnet test ArchiveFixer.Tests/ArchiveFixer.Tests.csproj # 单元测试 + 基线冒烟测试
dotnet format ArchiveFixer.slnx --verify-no-changes      # 格式检查（只看差异）
```

## 配置

配置文件：`%AppData%\ArchiveFixer\appsettings.json`（首次启动时会把旧版本放在程序目录的同名文件迁移过来）。
日志与临时文件同样在 `%AppData%\ArchiveFixer\`，不写程序目录 —— 避免程序目录不可写时静默失败。

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
| `MaxParallelExtractCount` | **设置项存在（1~8），但当前实现是串行** —— 见"已知限制" |

## 目录结构

```
ArchiveFixer/               WPF 主程序
  Converters/ Helpers/ Models/ Services/ ViewModels/ Views/
  tools/7zip/               内置 7-Zip 命令行
  appsettings.json          默认配置（运行时实际读 %AppData%）
ArchiveFixer.Tests/         xUnit：单元测试 + BaselineSmokeTests（M1 验收）
docs/                       需求书 / 需求评审与考古 / 需求变更
samples/                    样本生成脚本 + 清单（样本本体不入库）
AGENTS.md                   项目规则与里程碑
```

目标分层（`Domain/ Detection/ Engines/ Password/ Extraction/ Security/ Storage/`）见 `AGENTS.md` §4，
其中 `Engines/` 等不得引用 WPF。

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
| 密码明文出现在进程命令行 | 7z 命令行的固有限制（`-p<密码>`）；日志层已脱敏，但进程列表能看到。不假装解决了 |
| 改名 `Overwrite` 无回滚 | 先 `File.Delete` 再 `File.Move`，中间失败会丢文件 → **M1 修**（`AGENTS.md` §6 第 3 条） |
| 无解压进度百分比 | `-bsp0` 关掉了 7z 进度输出，界面只有"处理中/完成"两态 |
| 并发解压未实现 | `MaxParallelExtractCount` 只到 `ExtractOptions` 为止，服务层是串行（第十一版的刻意决策） |
| 分卷只认 `.001` | `.part1.rar` / `.r00` / `.z01` 命名与"分卷组 = 一个任务"尚未实现 → M2（`AGENTS.md` §9.3） |
| 无递归解压 | 内层归档不会自动展开 → M4 |
| 无路径穿越防护 / 危险文件检测 / 空间检查 / 源文件变化检测 | 外部引擎自己写盘，防护只能"解压前预检 + 解压后校验落点" → M5 |
| 7-Zip 路径有本机硬编码回退 | `ExtractService` 里写死了 `D:\7-Zip\7z.exe`，且 7z 路径在三个文件各拼一遍 → M1 收敛到 `ToolLocator` |
| 4 个可空性编译警告 | `EmptyStringToTextConverter` 的 `CS8600`，不影响行为，随 M2 清理死开关时一并处理 |

## 许可与第三方

- 内置的 7-Zip（`7z.exe` / `7z.dll`，26.01）版权归 7-Zip 作者，采用 **LGPL + unRAR restriction**；
  本仓库内置它只是为了这个工具能直接调用，未做任何修改。对外分发前需核对许可证条款。
- 本仓库**尚无 LICENSE**（未定，见 `AGENTS.md` §12）；未定之前不创建空许可证文件。

## 开发约定（摘要）

完整规则见 `AGENTS.md`。最容易被忘的三条：

1. 界面状态字符串统一引用 `Models/StatusText.cs` 常量，**禁止手写中文字面量**。
2. 密码只存内存；日志、报告、剪贴板一律脱敏。
3. "停止后续"与"取消当前"是两个独立取消源，**不得**合并成一个。
