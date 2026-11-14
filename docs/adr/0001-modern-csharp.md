# ADR 0001：现代化 C# 基线（M0）

- 状态：接受
- 日期：2026-11-14
- 决策人：项目设计者

## 背景

现有代码全部是 block-scoped namespace，`.editorconfig` 明确不切换 file-scoped；格式、行尾、命名规则停留在 suggestion 级别。继续在旧格式上叠加新文件，会让代码审查、AI 生成和格式化工具长期分叉。

## 决策

1. 所有 C# 源文件和测试文件统一使用 file-scoped namespace。
2. 仓库与工作区统一 LF、UTF-8 无 BOM、文件尾换行、无尾随空白。
3. 机械格式迁移单独提交，不夹带逻辑变更；迁移提交加入 `.git-blame-ignore-revs`。
4. 规范本体放在 `.editorconfig`、`Directory.Build.props`、CI 和 Roslyn 结构守卫；项目 skill 只做执行入口。
5. 先完成 M0 治理和 M3 守卫，再做 M1 全仓迁移；守卫失效时不得开始迁移。

## 后果

- M1 会产生一次覆盖约 1,165 个 `.cs` 的大 diff，只允许出现一次。
- `dotnet format --verify-no-changes`、CI、结构守卫成为固定门禁。
- 不允许再以“减少 diff”为理由新增 block-scoped namespace。
- 相关指标和验收见 `E:\AF\规范性\修改方案-v2.md`。
