namespace ArchiveFixer.Storage
{
    /// <summary>
    /// **空间预留账本**：并发解压时"已经许出去多少空间"的唯一记账处。
    ///
    /// <para><b>为什么必须有它</b>（用户点名的反例：一开始就把 5G+6G 两个大包并行排上）：
    /// 没有账本时，每个任务启动前看到的都是"现在还剩 10G"，于是 5G 和 6G 两个都通过了检查，
    /// 一起开跑，最后双双写满磁盘。有了账本，"还剩下多少"= 可用 − 已预留，
    /// 第二个大包会在启动前就被拦下（差多少也算得出来）。</para>
    ///
    /// <para><b>释放与刷新</b>：任务结束时按**当初预留的那个数字**释放（不是按实际用量），
    /// 同时用最新探测到的可用空间刷新账本 —— 「空间不足」模式下源包真的被删掉，多出来的空间会因此
    /// 出现在下一次判断里，这正是那个模式"越跑越宽松"的原因。</para>
    ///
    /// <para>本类是纯数据（无 IO、无 WPF），磁盘探测由调用方喂进来，所以可以被单测精确摆布。</para>
    /// </summary>
    public sealed class SpaceReservationLedger
    {
        private readonly object _sync = new();

        private long _availableBytes;
        private long _reservedBytes;

        /// <param name="availableBytes">目标盘可用字节数（null = 取不到）。</param>
        /// <param name="reserveBytes">要保留的余量；null 时取 <see cref="SpaceGate.DefaultReserveBytes"/>。</param>
        public SpaceReservationLedger(long? availableBytes, long? reserveBytes = null)
        {
            _availableBytes = availableBytes ?? UnknownAvailable;
            ReserveBytes = reserveBytes ?? SpaceGate.DefaultReserveBytes;
        }

        /// <summary>可用空间取不到时的哨兵（-1，与 <see cref="SpaceGateDecision.AvailableBytes"/> 同一口径）。</summary>
        public const long UnknownAvailable = -1L;

        /// <summary>要保留的余量。</summary>
        public long ReserveBytes { get; }

        /// <summary>最近一次知道的可用字节数（-1 = 取不到）。</summary>
        public long AvailableBytes
        {
            get
            {
                lock (_sync)
                {
                    return _availableBytes;
                }
            }
        }

        /// <summary>已经许给"正在跑"的任务的峰值之和。</summary>
        public long ReservedBytes
        {
            get
            {
                lock (_sync)
                {
                    return _reservedBytes;
                }
            }
        }

        /// <summary>账面上**还能给新任务用**的字节数：可用 − 已预留 − 余量。取不到可用空间时返回 -1。</summary>
        public long RemainingBytes
        {
            get
            {
                lock (_sync)
                {
                    return ComputeRemaining(_availableBytes, _reservedBytes, ReserveBytes);
                }
            }
        }

        /// <summary>用最新探测值刷新可用空间（null = 这次没探到，**保留上一次的值**，不假装是 0）。</summary>
        public void RefreshAvailable(long? latestAvailableBytes)
        {
            if (!latestAvailableBytes.HasValue)
            {
                return;
            }

            lock (_sync)
            {
                _availableBytes = latestAvailableBytes.Value > 0 ? latestAvailableBytes.Value : 0L;
            }
        }

        /// <summary>
        /// 申请预留。<see cref="SpaceGateDecision.Allowed"/> 为 true 时**已经记账**，
        /// 调用方必须在任务结束时用同一个数字调 <see cref="Release"/>（成功 / 失败 / 跳过都要调）。
        /// </summary>
        public SpaceGateDecision TryReserve(long peakBytes, long reclaimableBytes = 0)
        {
            long required = peakBytes > 0 ? peakBytes : 0L;

            lock (_sync)
            {
                SpaceGateDecision decision = SpaceGate.Check(
                    required,
                    _availableBytes >= 0 ? _availableBytes : null,
                    ReserveBytes,
                    _reservedBytes,
                    reclaimableBytes,
                    availableIsBookValue: true);

                if (decision.Allowed && !decision.ProbeFailed)
                {
                    // 探测失败时不记账：那时 required 根本没法比较，记进去只会污染后面的判断。
                    _reservedBytes = TaskSpaceEstimate.SaturatingSum(_reservedBytes, required);
                }

                return decision;
            }
        }

        /// <summary>释放预留（按当初申请的数字）。负数与超量都被夹住，绝不让账面变成负数。</summary>
        public void Release(long peakBytes)
        {
            long released = peakBytes > 0 ? peakBytes : 0L;

            lock (_sync)
            {
                _reservedBytes = _reservedBytes > released ? _reservedBytes - released : 0L;
            }
        }

        /// <summary>
        /// 任务开工之后（一般是拿到条目清单、算出了精确需求的那一刻）**改预留**。
        ///
        /// <para>
        /// 为什么要改而不是"开始时大致估一个就算了"：估小了会让"这些任务同时达到各自需求"的总和超过可用空间 ——
        /// 正是并发把磁盘写满的那条路径；估大了会白白拦下本来能跑的任务。
        /// 变更走的是**差值**：<see cref="TryReserve"/> 看到的 <c>已预留</c> 里已经含本任务那一份，
        /// 所以 <c>差值 + 已预留</c> 恰好等于"新需求 + 别的任务的预留"，判断口径与开工时完全一致。
        /// </para>
        /// </summary>
        /// <param name="currentReservation">本任务当前占着的预留（开工时申请的数字）。</param>
        /// <param name="newRequiredBytes">
        /// 按精确清单算出来的新需求（<see cref="TaskSpaceEstimate.FreeSpaceDemandBytes"/>：内容物 + 过程物，
        /// ⛔ 不是含源包的峰值）。
        /// </param>
        /// <returns>新需求下的空间门结论。放行时调用方要把预留改成 <paramref name="newRequiredBytes"/>。</returns>
        public SpaceGateDecision Adjust(long currentReservation, long newRequiredBytes, long reclaimableBytes = 0)
        {
            long current = currentReservation > 0 ? currentReservation : 0L;
            long updated = newRequiredBytes > 0 ? newRequiredBytes : 0L;

            if (updated <= current)
            {
                // 估大了：把多占的还回去，再按新数字给一次结论（这时只会更宽松）。
                Release(current - updated);
            }
            else
            {
                SpaceGateDecision decision = TryReserve(updated - current, reclaimableBytes);

                if (!decision.Allowed)
                {
                    // 没能加上：预留保持原值，让调用方按"空间不足"处理（它会用原值释放）。
                    return decision;
                }
            }

            lock (_sync)
            {
                return SpaceGate.Check(
                    updated,
                    _availableBytes >= 0 ? _availableBytes : null,
                    ReserveBytes,
                    0,
                    reclaimableBytes,
                    availableIsBookValue: true);
            }
        }

        /// <summary>一句话把账面说清楚（进日志：用户要能回答"当时为什么跳过了它"）。</summary>
        public string Describe()
        {
            lock (_sync)
            {
                if (_availableBytes < 0)
                {
                    return "目标盘可用空间取不到，本次不做空间门判断";
                }

                return $"可用 {TaskSpaceEstimate.FormatSize(_availableBytes)}"
                       + $"，已经在跑的任务预留 {TaskSpaceEstimate.FormatSize(_reservedBytes)}"
                       + $"，保留 {TaskSpaceEstimate.FormatSize(ReserveBytes)}"
                       + $"，还能排 {TaskSpaceEstimate.FormatSize(ComputeRemaining(_availableBytes, _reservedBytes, ReserveBytes))}";
            }
        }

        private static long ComputeRemaining(long available, long reserved, long reserve)
        {
            if (available < 0)
            {
                return UnknownAvailable;
            }

            // 两步减，理由同 SpaceGate.Check：三个大数相加会溢出成负数。
            long afterReserved = available > reserved ? available - reserved : 0L;

            return afterReserved > reserve ? afterReserved - reserve : 0L;
        }

        /// <summary>把字节数说成人话（展示用）。</summary>
        internal static string FormatSize(long bytes) => TaskSpaceEstimate.FormatSize(bytes);
    }
}
