# ArchiveFixer 项目规则

> 适用版本：v0.2.2（代码 HEAD 以 `git log -1 --oneline` 为准）。代码是事实，文档是声明；文档与代码冲突时以代码为准并修正文档。
> 全局规则见 `D:\DSH-Data\AGENTS.md`；详细历史只放 `docs/历史/`，不把历史结论当现行判据。

## 用途

把来源不明、后缀损坏、加密、分卷、多层嵌套的归档，整理成可追踪的成品。核心循环：

```text
导入 → 内容识别 → 修正伪装后缀 → 试密码 → 解压 → 汇总原因 → 递归展开 → 归集/处理源包与过程物
```

## 技术栈

- C# 12 / WPF / net8.0-windows；xUnit；不升级 net9/net10。
- 不新增 NuGet 运行时依赖；不用第三方 MVVM 框架。
- Domain/Detection/Engines/Password/Extraction/Security/Storage 不得引用 WPF。

## 编码规范

- 唯一编码规范：[`编码规范性.md`](编码规范性.md)；结构基线：[`docs/开发/结构基线.json`](docs/开发/结构基线.json)。
- 新代码按该文件执行；结构守卫 `CodeStructureGuardTests` 只允许基线缩小，不允许新增巨型/多类型文件。

## 常用命令

```text
构建：dotnet build ArchiveFixer.slnx
无增量构建：dotnet build ArchiveFixer.slnx --no-incremental
定向：dotnet test tests/ArchiveFixer.Tests/ArchiveFixer.Tests.csproj --filter "FullyQualifiedName~<类名>"
全量：dotnet test tests/ArchiveFixer.Tests/ArchiveFixer.Tests.csproj
格式：dotnet format ArchiveFixer.slnx --verify-no-changes
```

真跑/调试后清理夹具；不碰 `E:\ArchiveFixer\data\` 与 `H:\` 原件。

## 当前架构不变量

### 事实与准入

- “这一份是什么”统一走 `Detection/FileNature.cs` 的 `FileNatureCard`：格式、分卷身份/卷号、名字线索、媒体结构、物理身份。
- `FileNature.Inspect` **不再做内容识别**；内容身份统一走 `Detection/FileContentProbe`，并且只能从 `Detection/ContentAdmission` 进入。
- `ContentAdmissionScope`：
  - `None`：普通产物，直接 `Unknown`，不打开文件；
  - `UserSource`：用户明确导入的源包；
  - `VolumeCandidate`：已经进入同基名/分卷候选池的文件。
- 自动路径不得对普通图片/视频/音频/文档/文本逐张做内容识别；只有同基名/分卷候选才允许。4,000 个普通内容物的专项测试要求内容探测次数 <100。
- 媒体前缀的完整归档（`EmbeddedOffset>0` / `ContentClass=Archive`）仍按归档处理。
- 缓存：`FileContentProbe` 有界淘汰（禁止整表 Clear），浅探测已定案时深探测复用；`FileNature` 按路径+大小+mtime 缓存；内容物跳过写终态缓存。

### 第一大步 / 第二大步

- 第一大步只处理可证单一完整包；分卷首片/分卷片/名字槽/同基名证据成立 ⇒ 交接，第一大步不解。
- 第二大步候选唯一来源：`FirstStepFileLedger`；组键 = `SecondStepBaseName + FileIdentity`，目录不是组键。
- 第二大步只解一层：完整组 → 入口层标准名 → 解一次；缺片/判不出 ⇒ 不动，不删片，不调引擎。
- 分卷三族：7z（首卷 `.001`，片数由事实推）、ZIP（跨盘/编号两族）、RAR（RAR5 内容卷号为主，RAR4 名+末卷+试开）。
- 完整组入口层用逻辑规范名/硬链接，源片保持原名；不靠第二大步物理改名。
- 用户源包不动；失败/部分完成/取消不动源包；成功且校验通过才按用户档处理源包。
- 过程物 = 解压中产生的中间包/片；用户源目录里的片只硬链接，不命名/搬动。

### 落点 / 工作区 / 删除

- 工作区唯一来源：`<目标目录>\.ArchiveFixer.work`；缺目标目录如实报错，不回落程序目录/C盘/`%TEMP%`/源卷根。
- 源包/过程物/其余物按用户档；判不出 ⇒ 什么都不做。
- 删除/移动/清理前按 `archivefixer-unsafe-paths` 闸门；半套分卷、内容容器、源包不得误删。
- 危险条目/路径穿越/Windows 保留名必须拦截；外部进程用 `ArgumentList`，只杀自己启动的 PID。

### UI 与性能

- 产物扫描整段在后台线程执行，只把候选/日志返回 UI；任务表只由 UI 线程写。
- `UiActivity.Mark` 只写 UI 阶段；后台清理用 `MarkBackground`，不得覆盖 UI 阶段；性能 WARN 带标记时间/线程。
- 工作区清理已在 `PostProcessSuccessStep` 中用 `Task.Run`；不要重复包一层。

## 硬红线

1. 禁止打补丁：先定位错误产生的那一层，再改代码；不许按样本名/目录名/特定文件名加分支；不许用下游兜底压症状。
2. 同一推断不许连续猜两版；先取运行期事实（调试器/断点/调用栈/ProcMon/无头真跑），再改。
3. 碰到编译警告/测试红先消灭，不在错误上继续改；改不可逆路径按大改动处理。
4. 用户可见字符串禁止 Markdown 加粗；强调用「」。
5. 状态统一 `StatusText`；机器终态用 `TaskOutcome`。
6. 不碰用户源包与非空用户目录；不把历史文档当现行判据。

## 验证纪律

- 小改动先定向；跨模块/公共契约/核心流程/发布收尾跑全量。
- 新增守门要做红检：临时撤掉修复，对应测试必须变红；恢复后重编。
- 真实内容物/候选/分卷改动至少跑：
  - `LargeContentScanTests`（4,000 内容物，内容探测 <100）；
  - `ContentKeepKeywordPipelineTests`、`AndroidPackageGateTests`、`VolumeGroupResolverTests.真案三`；
  - 125 批标准测试：`测试\造包器\125批测试操作.ps1 -Seed 928000000`、`125批测试检测.ps1 -Seed 928000000`；底层环境变量仍是 `ARCHIVEFIXER_120_SEED`。
- 构建 0 错 0 警；format 通过；全量 0 失败。真样本跳过 ≠ 验证通过，要写明。
- 已知 flaky 单独确认，不先改断言。

## 文档地图（现行）

- `docs/README.md`：文档总索引（现行/历史/参考）。
- 用户：`docs/使用说明.md`、`docs/功能一览.md`、`docs/设置项.md`。
- 开发：`docs/开发/架构总览.md`、`模块地图.md`、`任务生命周期与终态.md`、`第一与第二大步.md`、`分卷组装算法.md`、`内容扫描与准入.md`、`输出与整理模型.md`、`设置与行为矩阵.md`、`检验等级.md`、`引擎与外部工具.md`、`打包功能.md`、`批量样本与测试体系.md`、`测试矩阵.md`、`人工测试清单.md`、`当前缺口与风险.md`、`部分完成发布.md`、`并发调度.md`、`诊断日志.md`。
- 参考：`docs/参考/WinRAR功能参考.md`。
- 测试流程：`测试\造包设计文档\` 六份；`测试\造包器\` 生成器/总控；`测试\备份位置\` 只读母本。
- 历史：`docs/历史/`（只追溯；顶部有“历史文档”横幅，不代表当前行为）。
- 变更日志：根目录 `修改日志.md`（只追加）。

## 交付

- 安装包固定同步到 `E:\MyGithubProjects\ArchiveFixer\v <版本>\`（当前 `v 0.2.2`）：每次交付都要替换该目录的独立 zip、框架依赖 zip、setup.exe，并核对 SHA256；用户不需要再指定路径。
- 交付优先用带最终 git 提交的 Release 包；版本号没改时，文件名仍是 0.2.2，但包内容必须是当次提交。

## 待确认

- 无。遇到范围外决定或不可逆路径冲突，先按 `docs/开发/检验等级.md` 的“判不出 ⇒ 不动”处理并记录。
