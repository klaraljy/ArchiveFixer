# 内置 7-Zip

| 项 | 值 |
|---|---|
| 版本 | **26.03 (x64)**，发布日 2026-09-03 |
| 来源 | 官方安装包 `https://www.7-zip.org/a/7z2603-x64.exe` |
| 安装包 SHA256 | `0859C524B8A63551848F0C246ABDDCB1D0B7B656B0FBFE879F8D85E61A9E6EDD` |
| 获取日期 | 2026-09-22 |
| 取用方式 | 用 7-Zip 本体解出安装包，**只取 `7z.exe`、`7z.dll`、`License.txt` 三个文件**，从未运行安装程序 |

| 文件 | 字节 | SHA256 |
|---|---|---|
| `7z.exe` | 577536 | 见仓库提交时的实际值（`Get-FileHash 7z.exe`） |
| `7z.dll` | 1906688 | 同上 |
| `License.txt` | 6031 | 官方原文，未改动 |

## 许可
7-Zip 主体是 **GNU LGPL**，另含 **unRAR 限制条款**（不得用其 unRAR 代码重建 RAR 压缩算法）。
`License.txt` 是官方原文，必须随分发保留。详见 `docs/引擎与外部工具.md`。

## 升级步骤
1. 从 <https://www.7-zip.org/download.html> 取官方安装包（**不要**用 Extra 包：它只有精简版 `7za.exe`，不支持 RAR）；
2. 用仓库内现有 7z 解出安装包：`tools\7zip\7z.exe x 7zXXXX-x64.exe -o<临时目录> -y`；
3. 只替换 `7z.exe` / `7z.dll` / `License.txt` 三个文件，并更新本文件的版本、来源、SHA256；
4. **必须跑全量测试**：`SevenZipOutputParser` 解析 7-Zip 的文本输出，版本升级可能改动措辞；
5. 跑一次真样本验收（`_tmp` 里的无 UI 宿主），确认识别/抠包/分卷/密码路径都正常。
