# ADR 0005：ArchiveTask 的 INotifyPropertyChanged 保留与退出条件

- 状态：接受（M6 的明确例外，非永久豁免）
- 日期：2026-11-15
- 关联：ADR 0002 / M6

## 事实

- `Models/ArchiveTask.cs`（1,833 行）仍实现 `INotifyPropertyChanged`，并直接作为 XAML DataGrid/TreeView 的绑定源；公开属性 119 个、通知点 55 处。
- M6b 已把 `OperationLogItem`、`PasswordItem`、`RenamePreviewItem`、`TaskSummary`、`RangeObservableCollection` 五个绑定类型移到 `ViewModels/BindingModels/`；`Models` 中 `ObservableCollection` 命中 = 0，只剩 `ArchiveTask` 的 INPC。
- `Extraction`/`Detection`/`Services` 等下层大量直接使用 `ArchiveTask`；把整个类型搬到 `ViewModels` 会让下层反向依赖 UI 层，违反现有分层守卫。

## 决策

- 本轮保留 `ArchiveTask` 的 INPC；`Models` 的 M6 纯化以“其余绑定类型全部移出 + 下文退出条件”为完成口径，并在结构守卫中保留 Models 反向依赖禁令。
- 退出条件（放到 M7 之后的 UI 适配层）：
  1. 新增 `ViewModels/ArchiveTaskBinding`（或等价适配器）承担 INotifyPropertyChanged 与绑定通知；
  2. `ArchiveTask` 变为纯事实/状态模型，`Mark*`/`Refresh*` 返回事实而不是直接发事件；
  3. 更新 XAML 绑定类型与所有 `PropertyChanged` 订阅点；
  4. 删除本条例外并让 `Models` 的 INPC 命中归零。
- 禁止为了“看起来达标”给 `ArchiveTask` 加 partial 或把事件转发到另一个仍住在 Models 的类；那只是搬家不是分层。

## 后果

- M6 的 `Models` 仍有一个 INPC 例外；本 ADR 与 ADR 0002 一起构成 M5/M6 的未结项，不计为“目标全绿”。
- 其余五个绑定类型已经移出；`Services/Storage` 对 `ViewModels/BindingModels` 的引用是已知方向债，M7 若引入 `BindingModels` 独立层需一并收口。
- 任何“Models 纯化完成”的汇报必须引用本 ADR，不得口头宣称全部达标。
