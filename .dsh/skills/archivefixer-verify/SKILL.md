---
name: archivefixer-verify
description: ArchiveFixer 的验证与红检流程——构建、按影响面分级的定向测试、全量回归、dotnet format、红检（临时撤掉修复看用例变红）、如实汇报真实数字。改完代码要跑验证、要报"通过"、测试变红、疑似 flaky、需要 --no-incremental 重编时用它。触发词：跑测试、全量、定向、红检、变红、format、flaky、0 警告、验证、基线。
---

# ArchiveFixer 验证与红检

规矩的出处是仓库根 `AGENTS.md` §5.2 / §9.2 与全局《作业模式》规矩 25；本文件只写**怎么执行**。

## 1. 三条命令（串行，⛔ 同一 checkout 不许并发）

```powershell
dotnet build ArchiveFixer.slnx --nologo -v q                                   # 必须 0 错 0 警
dotnet test tests\ArchiveFixer.Tests\ArchiveFixer.Tests.csproj                 # 全量
dotnet test tests\ArchiveFixer.Tests\ArchiveFixer.Tests.csproj --filter "FullyQualifiedName~XXX"   # 定向
dotnet format ArchiveFixer.slnx --verify-no-changes                            # 必须 exit 0
dotnet format whitespace ArchiveFixer.slnx                                     # 真有差异时用它修，⛔ 别手工对齐
```

- **多个代理/多个 worktree 各自构建没问题**（不同目录），但**同一个 checkout 里同时跑两个构建/测试**会 `MSB3021/MSB3027` 文件锁定或假红（内置 7z "忽然不在"）—— 要并行就 `git worktree`。
- 长活（构建 / 全量测试）**放后台任务**，别阻塞干等，边等边干别的。

## 2. 跑多少（判据 = "这次改动会碰坏谁"）

| 改动 | 跑什么 |
|---|---|
| 单点逻辑 / 文案 / 样式 / 一处 UI 交互 | 只跑**定向**（受影响用例 + 该文件的测试类），报出**用例名与结果** |
| 跨模块 / 动公共契约或不变量 / 设置序列化 / 引擎与管线主路径 / 依赖或工具链升级 / 收尾前最后一轮 | 跑**全量** |

本项目里凡动这几处**一律按大改动**对待（不可逆路径）：`ExtractionCoordinator` 的候选循环、`OutputVerifier` 口径、工作区与其余物的清理。

## 3. 红检（每条修复都要有）

1. 记下修复前/后的现象；
2. **把修复临时撤掉**（注释掉那段判据即可，别改断言）；
3. 跑那条守门用例，**亲眼看到变红**，把失败信息原文抄下来；
4. 恢复修复，重跑变绿。

⚠ 回退代码后**必须 `--no-incremental` 重编**：`Copy-Item` 会带回旧时间戳，MSBuild 跳过重编 ⇒ 跑的还是红检那份二进制，看着"修好了"其实没有。

## 4. 数字怎么报

- 基线数字（多少条 / 通过 / 跳过 / 失败）**只写在 `AGENTS.md` §11.2** —— 每次去那里读，⛔ 别在本文件或别处再写死一份。
- 报数**照抄真实输出**：跑了多少条、通过多少、跳过多少、失败哪几条（贴用例全名 + 失败断言原文）。
- ⛔ **跳过的用例不算通过**；⛔ 真样本用例没设环境变量时是"提前 return"，报表里同样算"通过" —— 要报真样本结果必须**设变量单独跑一次**并写清命中哪一份（见 `archivefixer-real-samples`）。
- ⛔ **没跑过的验证不许说成通过了**；跳过的检查要一句话说明为什么可以跳。

## 5. flaky

先**单跑确认**（`--filter` 那条），确认是全量并发导致的假红就照实写"已知 flaky，单跑通过"，⛔ **别改断言、别改产品代码去迁就它**。已知 flaky 名单见 `AGENTS.md` §11.2，现场见 `docs/真机事故复盘.md`。

## 6. 收工前还要做的

- `dotnet format ArchiveFixer.slnx --verify-no-changes` 必须 exit 0；
- 改了用户可见文案要跑 `UserFacingTextTests`（⛔ 文案里不许写 Markdown，强调用「」）；
- 改了设置序列化 / 目录结构 / 功能 ⇒ 同步"现状说明"类文档（README、`AGENTS.md` §4 §5、`scripts\package.ps1`），历史记录文件里的旧路径**按原样留着**；
- 改动写进 `修改日志.md`（一条一行），新需求追加进 `docs\需求变更.md`（只追加）。
