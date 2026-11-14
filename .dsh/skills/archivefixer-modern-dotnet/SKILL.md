---
name: archivefixer-modern-dotnet
description: ArchiveFixer 的 M0–M8 现代化执行入口——file-scoped 迁移、LF/格式门禁、Roslyn 结构守卫、分析器分层、UI/Models 边界、测试快慢档和 125 批功能门禁。要改代码风格/拆类型/动测试分层、判断该跑定向还是全量、准备阶段合并或发布时用它。触发词：现代化、file-scoped、LF、format、分析器、结构守卫、M0、M1、M2、M3、全量次数、fast、extended、125批。
---

# ArchiveFixer 现代化执行入口

本 skill 只写“怎么执行”，规则本体在仓库里：

- `.editorconfig`
- `Directory.Build.props` / `Directory.Packages.props`
- `.github/workflows/ci.yml`
- `编码规范性.md`
- `docs/开发/结构基线.json`
- `E:\AF\规范性\修改方案-v2.md`（阶段和验收）

## 1. 先判阶段

| 阶段 | 做什么 | 不许做什么 |
|---|---|---|
| M0 | 配置/CI/ADR/薄 skill | 改产品行为 |
| M0.5 | 测试分档、拆串行集合、125 批运行根 | 改产品逻辑 |
| M1 | 全仓 file-scoped + LF 归一 | 夹带逻辑修改 |
| M2 | 分析器分类、例外登记 | 全仓 NoWarn 一键压掉 |
| M3 | Roslyn 守卫先升级 | 在守卫失效时跑 M1 |
| M4 | UI 边界/ViewModel 协作类 | 用 partial 冒充拆类 |
| M5 | 手写文件 >800 按职责拆 | 为行数切碎方法/搬常量 |
| M6 | Models 纯化/依赖收口 | 把 UI 类型留在 Models |
| M7 | 测试大文件/覆盖率/分类 | 删测试、放宽断言 |
| M8 | 包/SHA256/签名/文档 | 把未签名说成已签名 |

## 2. 命令矩阵

```powershell
# 快速：单文件/小改
dotnet build ArchiveFixer.slnx --nologo -v q
dotnet format ArchiveFixer.slnx --verify-no-changes
dotnet test tests\ArchiveFixer.Tests\ArchiveFixer.Tests.csproj --no-build --filter "FullyQualifiedName~<类名>"

# 批次合并前：一次全量
dotnet test tests\ArchiveFixer.Tests\ArchiveFixer.Tests.csproj --no-build

# 125 批功能门禁：928 备份只读，操作/检测必须共用一个运行根
$env:ARCHIVEFIXER_125_RUN_ROOT = 'E:\DeepSeekProjects\_tmp\ArchiveFixer\125-runs\<唯一名>'
$env:ARCHIVEFIXER_125_KEEP_RUN = '1'
pwsh -NoProfile -File 测试\造包器\125批测试操作.ps1 -Seed 928000000
pwsh -NoProfile -File 测试\造包器\125批测试检测.ps1 -Seed 928000000
```

- 普通全量不要设 `ARCHIVEFIXER_120_SEED`；设了就混入 125 批操作/检测。
- 928 备份只读；917/927 不得再被测试写入。
- 全量只在批次合并/阶段边界/发布跑；小改只跑定向。

## 3. 硬红线

1. 格式迁移不夹带逻辑；行为修改不夹带全仓格式化。
2. 不删测试、不放宽断言；红检必须真的变红。
3. 不按样本名/文件名加分支；不在下游补丁。
4. 不写 `测试\备份位置`；不碰 `H:\`、`E:\ArchiveFixer\data`。
5. 遇到编译警告/测试红先停下来消灭，不在红基线上叠加。
6. 守卫未升级前不得跑 M1；`CodeStructureGuardTests` 的 4 空格正则在 file-scoped 下会假绿。

## 4. 已知坑

- `ArchiveFixerGlobalState` 串行集合：115 个类、1,239 个测试、约 408 秒；M0.5 必须按 surface 拆。
- `Batch120Operation` 当前用 `_tmp\125-runs` 唯一运行根；旧文档/脚本仍写 `测试\日志位置`，操作/检测不共享根就会报 `G0 运行记录缺失`。
- `LargeContentScanTests` 当前会把 917 备份根当 scratch；M0.5 必须改到 `_tmp`。
- 125 批历史 928 操作 125 份、墙钟约 4 分 04 秒；不是全量测试的替代品。
- `ExtractionPipelineFixTests.大目录收尾...` 约 67 秒，归 Stress；发布前跑，不删。

## 5. 阶段收尾

- build 0 错 0 警；
- format `--verify-no-changes`；
- 结构守卫红检（临时违规必须变红）；
- 定向 + 合并前全量；
- 更新 `修改日志.md`、模块地图、结构基线；
- 记录 HEAD、tag、bundle；
- 不把“过程进展”反复汇报，只报净变化和证据。
