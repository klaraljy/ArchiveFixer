# 测试样本清单

> **样本本体不入仓库**（`.gitignore` 已忽略 `samples/generated/`）。
> 入库的只有这份清单和生成脚本 —— 理由是体积（真实批次可达 GB 级）与隐私（AGENTS.md §8）。
>
> 对应设计.md §三十「测试样本库」。

## 怎么生成

```powershell
pwsh -File samples/generate-samples.ps1              # 默认 1MB 分卷
pwsh -File samples/generate-samples.ps1 -VolumeSizeMb 2
```

- 输出到 `samples/generated/`
- 只用项目内置的 `src/ArchiveFixer/tools/7zip/7z.exe`，不需要系统安装 7-Zip
- **脚本里的密码是合成密码**（`TestPass123!`），任何真实密码都不得写进本目录（AGENTS.md §8）
- 脚本每次运行时**先清空**输出目录，保证结果可重复

自动化冒烟测试（`tests/ArchiveFixer.Tests/BaselineSmokeTests.cs`）**不依赖**这个目录：它自己用同一套规则把样本生成到临时目录，
所以全新克隆直接 `dotnet test` 就能跑。

## 当前批次

标准样本密码：`TestPass123!`

| 目录 | 文件 | 真实格式 | 加密 | 分卷 | 预期识别 | 预期后缀状态 | 预期解压 | 对应设计.md 场景 | 验收里程碑 |
|---|---|---|---|---|---|---|---|---|---|
| `01_普通/` | `normal.zip` | ZIP | 否 | 否 | ZIP | 后缀正常 | 成功 | 普通归档 | M2 |
| `01_普通/` | `normal.7z` | 7Z | 否 | 否 | 7Z | 后缀正常 | 成功 | 普通归档 | M2 |
| `01_普通/` | `normal.tar.gz` | GZIP(tar) | 否 | 否 | GZIP | 后缀正常 | 成功 | TAR.GZ | M2 |
| `02_加密/` | `encrypted.7z` | 7Z | **是**（含文件头加密） | 否 | 7Z | 后缀正常 | 正确密码成功 / 错误密码"密码错误" | 常见密码归档 | M2 |
| `02_加密/` | `encrypted.zip` | ZIP | **是** | 否 | ZIP | 后缀正常 | 同上 | 常见密码归档 | M2 |
| `03_分卷/` | `volume.7z.001` … | 7Z | 否 | **是**（3 卷） | 7Z | 后缀不匹配 | 只给 `.001` 即可成功 | 分卷归档 | M2 |
| `04_伪装后缀/` | `fake.jpg` | 7Z | 否 | 否 | 7Z | 后缀不匹配 | 成功 | 错误后缀 | M2 |
| `04_伪装后缀/` | `fake.7z.pdf.jpg` | 7Z | 否 | 否 | 7Z | 多重后缀疑似伪装 | 成功 | 三重伪装后缀 | M2 |
| `04_伪装后缀/` | `noextension` | 7Z | 否 | 否 | 7Z | 后缀缺失 | 成功 | 无后缀 | M2 |
| `05_损坏/` | `corrupted.7z` | 7Z（头部完好） | 否 | 否 | 7Z | 后缀正常 | **失败**（损坏/截断） | 归档损坏、归档截断 | M2 |
| `05_损坏/` | `truncated.zip` | —（截断的 7z 头） | 否 | 否 | 需实测 | 需实测 | **失败** | 归档截断 | M2 |
| `06_名字像压缩包/` | `text.7z` / `text.zip` | 纯文本 | 否 | 否 | **Unknown** | 格式未知 | 失败（UnsupportedFormat） | 仅名字像压缩包 | M2 |
| `07_复合后缀/` | `data.tar.gz` | GZIP(tar) | 否 | 否 | GZIP | 后缀正常 | 成功 | 复合后缀不得被破坏 | M2 |

> 「预期」列是**验收判据**，不是描述。实测与之不符时：要么改产品代码，要么改这一行并写明理由。

## 还缺哪些（后续按真实样本补）

设计.md §三十 列了 33 个类别，当前批次只覆盖第一批。按里程碑补：

| 里程碑 | 待补类别 |
|---|---|
| M2 | `zip-aes`（AES 加密 ZIP）、`rar4` / `rar5`（需外部样本，7z 造不出真 RAR）、`nested-single-chain`（单链嵌套） |
| M3 | `many-small-files`（大量小文件）、`unicode` / `long-path` / `reserved-name`（`CON`、结尾点与空格）、`output-conflict` |
| M4 | `nested-multi-branch`、`nested-password-chain`、`compression-bomb` |
| M5 | `path-traversal`（`../` 条目）、`symlink` / `junction`、`corrupted-workspace` |

**RAR 样本说明**：7z.exe 不能创建真正的 RAR 文件。要测 RAR 必须由用户提供**自己有权使用的**样本，
或用官方 RAR 试用版生成；不得把无授权的样本提交进仓库。

## 每样本元数据模板（设计.md §三十 的 11 项，目标态）

新样本入库（或登记）时必须填：

```text
样本编号 / 真实格式 / 文件名 / 扩展名 / 是否加密 / 是否分卷 /
预期识别结果 / 预期文件数量 / 预期输出哈希 / 预期可用引擎 / 预期错误类型 / 预期递归行为
```

当前批次先按上表的紧凑列记录；等 M4 引入递归与哈希校验后，再补齐"预期文件数量 / 输出哈希 / 预期递归行为"三列。
