---
name: archivefixer-real-samples
description: 用真机样本（H 盘上的真实加密/分卷/伪装包）做只读验收的流程——样本怎么拿、环境变量叫什么、为什么「跳过」不等于「验过」、怎么如实汇报命中哪一份。合成样本过了但怀疑真机不成立、要验加密识别/分卷/引擎措辞时用它。触发词：真样本、真机、样本、只读、环境变量、ARCHIVEFIXER_REAL、跳过、脱敏、验收。
---

# 真机样本验收（只读）

**用户 2026-09-29 深夜定的规则**：必须在**真样本**（或真机文件的**只读副本**）上跑通；**合成样本通过 ≠ 问题解决**。出处 `AGENTS.md` §11.4。

## 1. 样本在哪、怎么拿

- 真样本散在 `H:\BaiduNetdiskDownload\...`（加密 7z / RAR5、分卷 zip、伪装包等）。
- ⛔ **H: 上的原件只读**：只允许复制副本，⛔ 不移动、不重命名、不删除、不"顺手修一下后缀"。
- 副本放 `E:\DeepSeekProjects\_tmp\ArchiveFixer\`（2.1 约定），⛔ **样本本体绝不进仓库**（`samples\` 只放生成脚本 + 清单）。
- ⛔ 副本不在、环境变量没设 ⇒ **跳过并说明**，不许拿合成样本冒充真样本结论。

## 2. 环境变量（已接入的几条）

| 变量 | 覆盖的用例 |
|---|---|
| `ARCHIVEFIXER_REAL_ENCRYPTION_7Z` | 真 `-p` 7z 加密识别 |
| `ARCHIVEFIXER_REAL_ENCRYPTION_ZIP_PLAIN` | ZIP（含 `0x0800` UTF-8 标志但**未**加密） |
| `ARCHIVEFIXER_REAL_ENCRYPTION_RAR` | 真 RAR5 `-p` |
| `ARCHIVEFIXER_REAL_VOLUME_PASSWORD` | 真实分卷成对样本 |
| `ARCHIVEFIXER_REAL_SPACE_CASE_DIR` | 真样本空间核算那一条 |

改/加变量时同步 `AGENTS.md` §11.2 的"两条跳过如实跳过"那段。

## 3. 跑法与读法

```powershell
$env:ARCHIVEFIXER_REAL_ENCRYPTION_RAR = '<副本路径>'
dotnet test tests\ArchiveFixer.Tests\ArchiveFixer.Tests.csproj --filter "FullyQualifiedName~RealEncryptionSampleTests"
```

- ⚠ **没设变量时用例是"提前 return"，报表里照样算"通过"** —— ⛔ 别读成"真样本验过了"。
- 要报真样本结果，必须**设变量单独跑一次**，并写清**命中了哪一份样本**、断言了什么。
- 真样本上**失败就如实报失败**：贴 7-Zip / UnRAR 原话与结论，⛔ 不许说"合成样本通过所以没问题"。

## 4. 脱敏（§8 隐私红线）

样本路径、站点名、个人目录名、密码**不得**出现在：仓库文件、日志、汇报、测试数据、注释里。测试里的密码一律占位符（`<示例密码>`）；文档里的路径用 `H:\...\` 或 `…\` 省略。

## 5. 引擎措辞要用真样本量

7-Zip 的文本措辞**会随版本变**（`SevenZipOutputParser` 解析的就是它）：升级 7z 之后**必须跑全量**。同一句可能对应两种原因 —— 例如 `CRC Failed in encrypted file. Wrong password?` 在"密码错"与"数据坏"下都会出现 ⇒ ⛔ **不许单独拿它定原因**，结论必须带出引擎原话（`Item37SafetyTests` 钉着这条）。
