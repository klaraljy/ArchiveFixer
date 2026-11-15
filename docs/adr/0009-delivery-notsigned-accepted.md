# ADR 0009：无代码签名证书时的交付口径（用户已确认）

- 状态：已接受（用户 2026-11-15 明确选择“NotSigned + 包内自检”）
- 日期：2026-11-15
- 关联：M8

## 背景

- 本机没有可用的代码签名证书；`Get-AuthenticodeSignature` 对 `ArchiveFixer.exe` 与 `ArchiveFixer-0.2.2-setup.exe` 均返回 **NotSigned**。
- 本机已有真实 HKCU `ArchiveFixer` 安装；`scripts/verify-installer.ps1` 实测以“已有安装”主动拒绝执行。按硬约束不能覆盖真实安装/注册表/用户数据，因此无法在本机静默安装到临时目录再卸载验证。
- 交付包本身已通过自检：包内 `ArchiveFixer.exe`、`tools/7zip/7z.exe`、`tools/unrar/UnRAR.exe`、LICENSE/第三方许可齐全；setup.exe 用 7z 检查含 19 个文件及 `ArchiveFixer.exe`。

## 决策

用户明确接受：

1. **不伪造签名**：交付包保持 **NotSigned**，文档和 CI 如实报告 `Get-AuthenticodeSignature` 状态。
2. **以包内自检代替本机安装升级/回滚验证**：安装包内容用 7z 清单核对 + `package.ps1` 自检；真实安装验证留给有隔离机器/临时用户的场景。
3. 交付包、SHA256、SBOM 必须对应最新 HEAD；本次已在 `42ff0c77` 重出。

## 后果

- M8 的“代码签名状态真实”按“如实 NotSigned”验收；“安装升级回滚验证”按用户接受的包内自检口径验收，不再声称已在本机安装验证。
- 未来获得证书或在隔离环境执行 `verify-installer.ps1` 时，应补做真实安装/升级/卸载验证并更新本 ADR。
- 交付文件：`dist/ArchiveFixer-0.2.2-独立.zip`、`ArchiveFixer-0.2.2-框架依赖.zip`、`ArchiveFixer-0.2.2-setup.exe`、`ArchiveFixer-0.2.2.sha256`、`ArchiveFixer-0.2.2-sbom.json`。
