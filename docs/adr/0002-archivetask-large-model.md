# ADR 0002：ArchiveTask 拆分边界与 800 行门槛

- 状态：已执行（M6i/M6j 已把 ArchiveTask 移入 BindingModels，INPC 与 UI 事实类离开 Models；文件仍 1,833 行，ADR 0008 记录方法债务）
- 日期：2026-11-14
- 关联：M5 / M6

## 背景

`Models/ArchiveTask.cs` 原有 2,449 行，同时承担：XAML 绑定表面、任务状态机、身份/路径显示、进度文案、卷组/内容组事实、源快照、残留账本、识别结果落地与 INotifyPropertyChanged 管道。

M5 先按职责抽出了 `UserFileIdentity`、`TaskDisplayFacts`、`TaskStatusPresenter`、`TaskProgressText`、`TransformationChain`、`ContentGroupFacts`、`VolumeGroupFacts`、`TaskSourceSnapshot`、`LeftBehindPackageLedger`、`DetectResultApplier`，主类降到 1,833 行。

## 决策

- 继续按职责拆：身份显示、进度文本、状态投影、内容组/卷组事实、源快照、残留账本、识别落地已经下沉；主类保留**公开绑定属性、任务状态机写点、兼容 API、INPC 通知**。
- 1,833 行**不是长期默认例外**：它超过 M5 的 800 行硬上限，已登记在结构基线（`Models/ArchiveTask.cs: 1833`），并在本 ADR 记录退出条件。
- 退出条件（M6）：把 INotifyPropertyChanged/绑定通知移到 ViewModels 适配层，把 `ArchiveTask` 收敛为纯数据/事实模型；状态机写点改为返回事实的状态转换，由适配层发通知。届时必须重新评估行数并删除或收紧基线条目。
- 不允许为了到 800 行：删公开属性契约文档、把属性压成一行转发、按行数切方法、用 partial 冒充拆分。

## 后果

- 现有 XAML 绑定名、公开属性语义、通知次数/顺序零变化；测试零改动。
- 主类仍大，但剩余代码没有可独立失败的分支，继续抽只会增加转发层。
- M6 必须完成 INPC/纯模型收口，否则本 ADR 不得关闭。
