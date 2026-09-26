# tools/unrar —— 内置的 RAR 解压引擎（RARLAB `UnRAR.exe`）

> 本目录只放**解压**用的 RARLAB 官方免费件与其许可文本。
> 这里是**事实记录**：谁的文件、哪个版本、从哪来、校验值是多少、许可能做什么。
> 工程边界与许可边界见 `docs/引擎与外部工具.md`；规则见 `AGENTS.md` §3.1。

---

## 1. 这是什么

| 项 | 值 |
|---|---|
| 文件 | `UnRAR.exe`（RARLAB 官方**免费**命令行解压工具，不是共享软件的 `Rar.exe` / `WinRAR.exe`） |
| 自报版本 | `UNRAR 7.23 x64 freeware`（实测：直接运行 `UnRAR.exe` 输出的第一行） |
| 文件版本资源 | `7.23.0` |
| 大小 | 560 848 字节 |
| **SHA256** | `0D3715001790F0FD18D3E850F947B540530B2D2DEB9A2E6A9E84F2ED7B234235` |
| 架构 | x64（本项目最小支持 Windows x64；32 位系统不在范围内） |
| 获取日期 | 2026-09-22 |
| 许可文本 | 同目录 `license.txt`（UnRAR freeware 许可，1509 字节） |

`license.txt` 的 SHA256：`48C3EBE5E74E4B9312CDF6B337B04F87F220416CB54CA4F4DF01A1F504859D09`

---

## 2. 来源（可复现）

**二进制来源包（稳定版）**

| 项 | 值 |
|---|---|
| URL | `https://www.rarlab.com/rar/winrar-x64-723.exe` |
| 是什么 | RARLAB 官方 **WinRAR 7.23 x64 英文稳定发布包**（RARLAB 下载页 "English WinRAR and RAR release" 一栏；下载页实测版本行：`WinRAR x64 (64 bit) 7.23`） |
| 包大小 / 时间戳 | 3 775 056 字节 / `Sat, 27 Jun 2026 11:34:26 GMT` |
| 包 SHA256 | `8FF0DAF3ED564CC743C0E23FF2E253997FFC74460F9673F0B6DD037B2DB4CE7B` |

**提取方式**：用本机已安装的 `UnRAR 6.11` 以 `x` 命令**解包**（`UnRAR.exe x -y -- winrar-x64-723.exe <目标目录>\`），
**从未运行该安装程序**；提取出的 `UnRAR.exe` 与 `7zxa.dll`、`Rar.exe`、`WinRAR.chm` 等并列，
本仓库只取了其中 **UnRAR 这一个组件**。

**为什么不用 `https://www.rarlab.com/rar/unrarw64.exe`（UnRAR 免费件专用包）**：
2026-09-22 实测该 URL 给出的当前版本是 **`UNRAR 7.30 beta 1 x64`**
（SHA256 `C4F98E25A824DF50F8C80304715F8E6EDB9C287225D5D70D423B8B2B12D311CD`，`Last-Modified: Sat, 19 Sep 2026`）——
即 RARLAB 在 beta 期把免费件下载位换成了 beta 构建。产品里不塞 beta，所以改从**稳定发布包**里取同款 freeware `UnRAR.exe`。

> ⚠️ 若将来 `unrarw64.exe` 恢复为稳定版，优先换回该专用免费件包（来源更单一），
> 换完记得同步更新本文件的版本号、大小与两个 SHA256。

**许可文本来源**：`https://www.rarlab.com/rar/unrarw64.exe`（UnRAR 免费件专用包）内附的 `license.txt`。
该文件在两个包里是**同一份 UnRAR freeware 许可文本**（内容与版本无关，未作任何改动）。
WinRAR 发布包自带的 `License.txt` 是**共享软件**EULA（6880 字节），**没有**放进本目录。

---

## 3. 许可要点（原文为准，这里只做工程摘录）

UnRAR freeware 许可（本目录 `license.txt`）第 2 条：

> "The UnRAR utility may be freely distributed. It is allowed to distribute UnRAR inside of other software packages."

→ **允许随其它软件包分发**（这正是"可以内置"的依据）。分发时**必须带上这份许可文本**（已随目录放入）。

RARLAB 稳定发布包自带 EULA（《WinRAR license》）第 3 条第 a 项：

> "Nobody may distribute separate parts of the package, **with the exception of the UnRAR components**, without written permission."

→ 单独分发包里**除 UnRAR 组件以外**的部分是不允许的 —— 反过来说，**单独取 UnRAR 组件是明确允许的**。
所以本目录**只**放 `UnRAR.exe` + `license.txt`，一个字节的 `Rar.exe` / `WinRAR.exe` / `7zxa.dll` / 帮助文件都没有。

第 4 条（红线，永不触碰）：

> "Neither RAR binary code, WinRAR binary code, UnRAR source or UnRAR binary code may be used or reverse engineered
> to re-create the RAR compression algorithm, which is proprietary, without written permission of the author."

→ **只用于解压（读）**。不许用它建包、修包、改包，更不许拿它去重建 RAR 压缩算法。
本仓库里**没有任何**调用它的压缩/写入命令（`a` / `d` / `u` / `m` / `k` / `r` 一律不接）。

---

## 4. 程序里怎么用它（工程约定）

- 路径**只有一个来源**：`Engines/ToolLocator.cs`。解析顺序是
  **① 用户自选路径 → ② 用户已装的 WinRAR 目录（`%ProgramFiles%\WinRAR\UnRAR.exe` 等）→ ③ 本目录（内置）**。
  别的文件里**不许**再拼一次 `UnRAR.exe` 的路径（`AGENTS.md` §3.1）。
- 调用只走 `Engines/WinRar/UnRarEngine`（`IArchiveEngine` 的 probe / list / test / extract 四件事）；
  参数拼接、输出解析、退出码到错误码的映射**只允许**待在 `Engines/WinRar/` 内。
- 能力位**如实**：只认 RAR（`RAR` / `RAR4` / `RAR5`），支持密码与分卷、保留 Unicode 名称；
  zip / 7z / tar 等格式**一律声明不支持**，让 `EngineSelector` 自动回落到 7-Zip。
- 这个目录**不进版本控制之外的任何地方**：随程序目录分发（`ArchiveFixer.csproj` 里按 `tools\7zip\` 的同一写法复制到输出目录）。

---

## 5. 升级/替换清单（换版本时逐条做完）

1. 确认新版本是**稳定版**（不是 beta），并记录 `UnRAR.exe` 自报版本；
2. 记录来源包的 URL、字节数、`Last-Modified`、SHA256；
3. 只替换 `UnRAR.exe`，更新本文档的版本 / 大小 / SHA256 / 获取日期；
4. 同步 `Engines/WinRar/UnRarEngine.cs` 里 `EngineVersionInfo.VerifiedOn` 的能力验证记录；
5. 跑 `dotnet test tests\ArchiveFixer.Tests\ArchiveFixer.Tests.csproj`（含真样本对照用例）确认无回归。
