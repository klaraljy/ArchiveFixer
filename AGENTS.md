# ArchiveFixer 项目规则

适用于 `E:\DeepSeekProjects\ArchiveFixer`。全局规则见 `E:\dsh\home\AGENTS.md`。本文件只保留项目长期有效、必须知道的内容；详细历史与逐条复盘见 `docs/AGENTS-详细备份-20261101.md`、`docs/真机事故复盘.md`、`修改日志.md`。

## 最高优先级

1. 禁止在错误基础上打补丁。
   - 先定位错误产生的那一层，再改代码；不许在下游加兜底。
   - 不许按样本名、目录名、特定文件名加分支。
   - 同一症状出现两次，或修一处导致另一处回退，立即停下重新建模。
   - 改不可逆路径前先看 `archivefixer-unsafe-paths` 的闸门清单。

2. 新算法必须接入既有框架。
   - 内容事实统一走 `FileNatureCard`。
   - 分卷事实统一走 `VolumeGroupUnit` / 族专属算法。
   - 第一/第二大步统一走 `ExtractionCoordinator`，不许另起一套判据。

3. 取证优先。
   - “为什么这个值是它 / 走了哪条分支”先用调试器、断点、调用栈、探针、ProcMon。
   - 读取源码和插日志只能作补充；结论要能指到工具、文件:行和真实值。
   - 同一推断不许连续猜两版代码。

4. 输出精简。
   - 过程只写 `修改日志.md`；同一事实只报一次。
   - 设置目标后禁止反复输出长汇总；目标是持续编码，不是刷文字。

## 用途与边界

把来源不明、后缀损坏、加密、分卷、多层嵌套的归档，整理成可追踪的成品。

核心循环：

```text
导入
→ 按内容识别真实格式
→ 修正伪装后缀（不许动基名）
→ 试密码
→ 解压（含分卷组）
→ 汇总原因
→ 递归展开
→ 归集/处理源包与过程物
```

技术栈：

```text
C# 12 / WPF / net8.0-windows
xUnit
⛔ 不升级 net9/net10
⛔ 不新增 NuGet 运行时依赖
⛔ 不用第三方 MVVM 框架
Domain/Detection/Engines/Password/Extraction/Security/Storage 不得引用 WPF
```

## 常用命令

```text
构建：dotnet build ArchiveFixer.slnx
定向测试：dotnet test tests/ArchiveFixer.Tests/ArchiveFixer.Tests.csproj --filter "FullyQualifiedName~<类名>"
全量测试：dotnet test tests/ArchiveFixer.Tests/ArchiveFixer.Tests.csproj
格式：dotnet format ArchiveFixer.slnx --verify-no-changes
真修空白：dotnet format whitespace ArchiveFixer.slnx
```

纪律：

- 小改动先定向测试；只有动不可逆路径、出现新红、用户明确要求才跑全量。
- 构建必须 0 错误 0 警告。
- 后台任务不要长超时阻塞；后台跑、短轮询。
- 真跑/调试后必须清理夹具，不许塞满 E:。
- 源包只读；样本先复制再跑。

## 第一大步：单一完整包

第一大步只处理“可证单一完整包”。

```text
SingleComplete 且无分卷证据 → 解压
VolumeFirstPiece / VolumePiece → 交接
名字槽/同基名兄弟证明是分卷 → 交接
Unknown → 交接；⛔ 不调引擎、⛔ 不直接判跳过
```

必须遵守：

- `FirstStepFileLedger` 记录闸门通过的所有文件，包括 Unknown 和包装解出的内层片。
- 账本只收已发布产物；⛔ 不收 `.ArchiveFixer.work` 临时路径。
- 同一物理文件多名字按 `FileIdentity` 去重。
- 批末必须扫第一大步所有输出位置，把可能是分卷的产物补进账本；普通内容物（MP4/MP3/图片/文档）不进。
- 分卷片不许调第一大步引擎。

## 交界处与第二大步

第二大步候选唯一来源：

```text
FirstStepFileLedger
```

不是 `VolumeHandoffEntries`、`extraGroupSeeds`、`IsVolumeCandidateSeed` 单独决定。

归组：

```text
SecondStepBaseName + FileIdentity
```

- 同基名跨目录仍属一组；目录不是组键。
- 未知内容片只要同基名/同组证据成立也要进候选。
- 组内再按族 resolver 判完整性、首末片、顺序。

第二大步只解一层：

```text
完整组 → 入口层标准名 → 解一层
Missing/Unknown → 不动，片留着
```

禁止：

- 缺片还调引擎。
- 判不出就删片。
- 用 GroupAnalysis 做通用合并/补片。
- 已完整组被旧 `PartiallyCompleted` 状态无故挡住。

## 分卷三族

### 7z

```text
首卷 001
N = ceil(声明整包 / 首片)
中间片 = 首片大小
末片 <= 首片；未定槽中唯一小于首片者先钉为末片
剩余未定槽才做排列
试开用 TestAsync + 密码候选，不只 ListAsync
⛔ 不写死片数；N 由事实算
```

### ZIP

```text
跨盘 ZIP：.z01... + .zip（末片是入口）
通用分片：.zip.001...（首片是入口）
```

- 跨盘 ZIP 末片 EOCD 盘号决定片数；中间 Unknown 片跟着锚点走。
- 通用 ZIP 看首片本地头/末片 EOCD。
- 强锚点只让已定族的片投票；Unknown 中间片不投票。
- Spanned/Numbered 强锚点冲突 → 不混组，报 Unknown/SizeAnomaly。

### RAR

```text
Rar5：内容卷号为主
Rar4New：内容字段辅助，名字+末卷+试开
Rar4Old：.rar=1，.r00=2，.r01=3
加密头：按分卷形状/标准名，单卷走第一大步
```

## 入口层与删除

- 完整组在 `.ArchiveFixer.work\group-entry\...` 建标准名硬链接；不移动用户原片。
- 引擎从入口层解压；产物写任务落点。
- 成功后按组删除全部过程物路径，不只删 group-entry 链接。
- 用户原包不动；只有整条链完全成功才按用户选择删源包。
- 失败/部分完成/取消：源包不动，最后一层过程物按用户选择留/删。
- 已成功解出自己压缩内容的过程物，自动彻底删除。
- 半套分卷闸门保护用户源，不许挡住用户明确选择的最后一层过程物删除。
- 不许把普通内容物当分卷删。

## 工作区与落点

- 工作区只有一个来源：`<目标目录>\.ArchiveFixer.work`，隐藏目录。
- 缺目标目录时如实报错；不许回落到程序目录、C 盘、`%TEMP%`、源卷根。
- 成功路径清理工作区；失败保留策略按设置。
- 落点：谁可以输入密码，就解压到谁那边；再套一层包名目录。
- 跨盘 ZIP 入口是末片 `.zip`；通用 ZIP 是 `.zip.001`；7z 是 `.7z.001`；RAR 是第 1 卷。
- 不按任务表顺序、不按缺卷目录猜落点。

## 源包与过程物

- 源包 = 用户导入的原包；只按链成功删。
- 过程物 = 解压过程中产生的中间包/片；成功自动删。
- 最后一层过程物在失败时按用户选择处理。
- 收卷临时物不许留在用户源目录。
- 用户源目录里的片只硬链接，不改名不搬。

## 安全与状态

- 解压前检查条目路径：拒 `..`、绝对路径、盘符、UNC、混合斜杠。
- Windows 保留名、结尾空格/点、备用数据流清洗后再落盘。
- 外部进程用 `ArgumentList`，超时、取消、只杀自己启动的 PID。
- 源文件快照用于检测处理中变化。
- 状态字符串统一 `StatusText`；机器终态用 `TaskOutcome`。
- 用户可见字符串禁止 Markdown 加粗；强调用「」。

## 验证与已知坑

- 构建 0 警告 0 错误；格式检查通过。
- 分析器：csproj 已锁 AnalysisLevel=latest + TreatWarningsAsErrors（当前 0 警告）。⛔ 不要开 AnalysisMode=Recommended：实测 2500 条（CA1863 1020 / CA1305 400 / CA1822 384），是独立的一轮清理，不是顺手能开的。
- 已知 flaky 用例单独确认，不先改断言。
- 真样本跳过不等于验证通过，要写明跳过原因。
- 仓库工作区是 CRLF；⛔ 不要用 PowerShell 整份重写 `.cs` 成 LF。
- 临时探针用完必须删；临时排障行不许留在代码里。
- AGENTS 总预算 64KB；本文件必须保持精简，细节放 docs。
- 修改不可逆路径按大改动处理：`--no-incremental` 构建 + 定向/全量测试。

## 文档地图

```text
docs/需求书.md
docs/需求变更.md
docs/需求评审与考古.md
docs/使用说明.md
docs/功能一览.md
docs/设置项.md
docs/输出与整理模型.md
docs/分卷组装算法.md
docs/检验等级.md
docs/引擎与外部工具.md
docs/打包功能.md
docs/真机事故复盘.md
docs/部分完成发布方案.md
docs/人工测试清单.md
docs/界面重构方案.md
docs/WinRAR功能参考.md
修改日志.md
```

## 当前清理备注

原来的详细项目规则已备份到：

```text
docs/AGENTS-详细备份-20261101.md
```

需要查历史细节时读该备份和 `docs/真机事故复盘.md`，不要重新把历史条目塞回本文件。
