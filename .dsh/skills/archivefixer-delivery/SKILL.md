---
name: archivefixer-delivery
description: ArchiveFixer 的"交付"流程——把新构建拷进绿色目录 E:\ArchiveFixer\、核对哈希、程序在跑就跳过、⛔ 不碰 data\、安装包与发布资产的红线。构建完成后要刷新用户真正在用的那份程序、要出安装包或发行包时用它。触发词：绿色目录、E:\ArchiveFixer、交付、拷贝、刷新、安装包、NSIS、发布、Release、dist、打包。
---

# 交付：绿色目录 / 安装包 / 发布

## 1. 绿色目录 `E:\ArchiveFixer\`（用户真实测试位置）

`bin\Release\...` **不是**用户数据所在地。构建完成后：

```powershell
Get-Process ArchiveFixer -ErrorAction SilentlyContinue        # ⛔ 在跑就先问/先跳过，绝不强杀他的程序
Copy-Item src\ArchiveFixer\bin\Release\net8.0-windows\{ArchiveFixer.exe,ArchiveFixer.dll,ArchiveFixer.pdb,*.json} E:\ArchiveFixer\
Copy-Item src\ArchiveFixer\bin\Release\net8.0-windows\docs\* E:\ArchiveFixer\docs\ -Recurse -Force
Copy-Item src\ArchiveFixer\tools\* E:\ArchiveFixer\tools\ -Recurse -Force
```

- **必须一起拷**：`docs\`（csproj 会把用户文档复制到输出目录；「使用说明」按 `<程序目录>\docs\使用说明.md` 找，漏拷会报"路径错误"）与 `tools\`（内置 7-Zip / UnRAR，漏拷 = 没引擎）。
- ⛔ **绝不碰 `E:\ArchiveFixer\data`** —— 那里是他的日志 / 密码列表 / 设置。
- 拷完**核对哈希或时间戳**（`Get-FileHash` / `Get-Item ... LastWriteTime`）确认真的换成新版，⛔ 别只说"已拷贝"。
- 换了 `.ico` 必须先重新构建（图标由 `/win32icon` 编译期塞进 exe）再刷新。

## 2. 打包与发布

- ⛔ **用户说"暂不打包"时不许生成 `dist\*.zip`**（连续真机测试期间，发行包会把旧版本固化住）；等他说"打包"再打。
- 安装包 = `installer\ArchiveFixer.nsi` + `scripts\installer.ps1`（NSIS，免 UAC、每用户）：
  - ⛔ 默认安装目录**绝不**能是 `C:\Program Files\`（程序要往自己目录的 `data\` 写日志/设置/密码列表）；
  - ⛔ 卸载**默认保留 `data\`**，不许改成"卸载就清空"；
  - `.nsi` 必须 UTF-8 **带 BOM**（否则 makensis 报 Bad text encoding）；注释行末尾不留反斜杠；开关判据只读 `${GetOptions}` 返回值；删桌面 `.lnk` 前先看 `DesktopShortcut` 标记。
- 发布（GitHub Release）：资产名一律「**ASCII 文件名 + 中文 label**」（非 ASCII 会被 GitHub 抹掉）；大资产走**直连**（先清 `HTTP_PROXY` / `HTTPS_PROXY`）；⛔ `gh release upload --clobber` 会连别的资产一起删。
- 面向用户的截图与文档一律**脱敏**（个人路径 / 样本包名 / 站点名 / 作者邮箱都不进仓库）。
- 推送远程走本机代理（端口见全局 `AGENTS.md` §0），只对这一次调用加 `-c http.proxy=...`，⛔ 不写全局配置。

## 3. 交付前最后一轮

发布或收尾前的那一轮按**大改动**对待：全量测试 + `--no-incremental` 重编 + `format --verify-no-changes`，再刷新绿色目录。⛔ **CI（GitHub Actions）不是阻塞项**：推送后直接继续干活，不 `gh run list` / 不轮询，除非用户明确说"看一下 CI"。
