# 自动并发（实验，暂停）

状态：**未完成 / 暂停（2026-11-05）**。这些文件保留用于以后恢复，不接入产品路径。

产品当前行为：用户在②页手动选择并发 **1–4**（默认 2），普通/特定解压都只认这个全局 N；
LPT 只决定启动顺序，不改变并发数。空间不足模式仍按它自己的空间规则接管并发（独立通道）。

## 保留的文件

- `AdaptiveConcurrencyController.cs`（含 `ConcurrencyProfileStore`）
- `CapacityPolicy.cs`
- `DevicePoolScheduler.cs`
- `SlotSchedulerRunner.cs`
- `ProcessIoProbe.cs`
- `SystemResourceProbe.cs`

## 为什么暂停

真机 H:（USB 盘）同一批 15 个包：

- 手动串行：707 秒，15.4 MB/s
- 自动并发 3–4 个：1153 秒，9.4 MB/s（总吞吐 -39%）
- 54 MB 包：4 秒 → 120 秒；1.53 GB 包：101 秒 → 582 秒；2.62 GB 包：170 秒 → 971 秒

已确认的缺陷：

1. 特定解压把“包所在目录”当设备根，同一块物理盘被拆成多个池。
2. `target` 只写日志，实际启动由各池容量和全局硬顶决定。
3. 特定解压路径没有设置 `StartTime`，完成观察/容量反馈没有真正执行。
4. `CapacityPolicy` 只从初始值往上探测，HDD 从 3 起步时不会回试 1/2。

## 恢复前必须完成

先测出目标设备的 `T(n)`（n=1..4，同一批真实两层包，记录物理盘 `Disk Bytes/sec`、
`Avg. Disk sec/Transfer`、`Current Disk Queue Length`），再重新设计“共享磁盘吞吐曲线 +
向上/向下探测”。没有这条曲线，不要重新接回产品。
