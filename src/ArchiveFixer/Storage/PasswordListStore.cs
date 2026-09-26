using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 记忆里的一条密码（值 / 来源 / 启用状态 / 备注）。**顺序即列表顺序**。
    ///
    /// <para>⚠ <see cref="Value"/> 是明文，只允许存在于内存与**加密后的**记忆文件里：
    /// 绝不进日志、绝不进报告（AGENTS.md §8 隐私红线）。</para>
    /// </summary>
    public sealed class PasswordListEntry
    {
        public string Value { get; set; } = string.Empty;

        public string Source { get; set; } = "ManualList";

        public bool Enabled { get; set; } = true;

        public string Remark { get; set; } = string.Empty;
    }

    /// <summary>
    /// 一次要落盘的密码列表全貌：条目（含顺序）+ 记住的密码本路径 + 墓碑 + **每条密码的成功次数**。
    ///
    /// <para><b>墓碑</b> = 用户删掉 / 清空过的那些值。启动时把密码本合并回来时会**跳过**它们，
    /// 否则"删掉一条、重启又从 txt 里复活"——那正是用户 2026-09-24 报的那类现象的另一半。</para>
    ///
    /// <para><b>成功次数</b>（用户 2026-09-24 第 14 条："密码本里面的优先级差不多就是使用次数的频繁程度"）
    /// = 这条密码在这台机器上成功解开过多少次。它跟着列表一起加密落盘，用来给同层级的候选排序
    /// （次数多的排前面；次数相同保持原顺序）。⚠ 空密码（<c>""</c>）不记账：它不是"一条密码"，
    /// 而是"不需要密码"那一步。</para>
    /// </summary>
    public sealed class PasswordListSnapshot
    {
        public List<PasswordListEntry> Entries { get; set; } = new();

        public List<string> BookPaths { get; set; } = new();

        public List<string> Tombstones { get; set; } = new();

        /// <summary>密码值 → 成功次数（明文值，只允许存在于内存与**加密后的**文件里，§8 红线）。</summary>
        public Dictionary<string, int> SuccessCounts { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>读记忆的结论。**失败时条目一律为空**，调用方按"没有可用记忆"继续。</summary>
    public enum PasswordListLoadStatus
    {
        /// <summary>还没有这个文件（第一次运行）：不是错误，也不是"记忆为空"。</summary>
        NoFile = 0,

        /// <summary>成功读到了一份可用的记忆。</summary>
        Loaded = 1,

        /// <summary>解不开：换机器 / 换 Windows 账号 / 文件被改过 / 不是本机加密的。</summary>
        Undecryptable = 2,

        /// <summary>不是我们的格式（版本号读不懂、JSON 结构对不上）。</summary>
        NotOurFormat = 3,

        /// <summary>读不动（权限、被占用、半截文件、IO 错误）。</summary>
        Unreadable = 4,

        /// <summary>用户关掉了「记住密码列表」：**不读**。</summary>
        Disabled = 5
    }

    /// <summary>读记忆的结果。<see cref="Snapshot"/> 在失败时是空的一份，不是 null（调用方少写分支）。</summary>
    public sealed class PasswordListLoadResult
    {
        public PasswordListLoadResult(PasswordListLoadStatus status, PasswordListSnapshot? snapshot, string failureReason)
        {
            Status = status;
            Snapshot = snapshot ?? new PasswordListSnapshot();
            FailureReason = failureReason ?? string.Empty;
        }

        public PasswordListLoadStatus Status { get; }

        public PasswordListSnapshot Snapshot { get; }

        /// <summary>失败原因（**已经脱敏**：只说文件名与类别，不带完整路径、不带任何密码）。</summary>
        public string FailureReason { get; }

        /// <summary>真的读到了一份可用记忆（<see cref="PasswordListLoadStatus.Loaded"/>）。</summary>
        public bool HasMemory => Status == PasswordListLoadStatus.Loaded;

        /// <summary>是不是一次"值得告诉用户"的失败（没有文件 / 用户关掉了开关都不算）。</summary>
        public bool IsFailure =>
            Status is PasswordListLoadStatus.Undecryptable
                or PasswordListLoadStatus.NotOurFormat
                or PasswordListLoadStatus.Unreadable;
    }

    /// <summary>写记忆的结论。<see cref="FailureReason"/> 同样**不含路径与明文**。</summary>
    public sealed class PasswordListSaveResult
    {
        public PasswordListSaveResult(bool success, string failureReason, string filePath)
        {
            Success = success;
            FailureReason = failureReason ?? string.Empty;
            FilePath = filePath ?? string.Empty;
        }

        public bool Success { get; }

        public string FailureReason { get; }

        public string FilePath { get; }

        /// <summary>写失败时的日志文案：**只有文件名 + 类别**（§8：个人路径不入日志）。</summary>
        public string DescribeForLog() =>
            Success
                ? $"密码列表记忆已更新（{PasswordListStore.FileName}）。"
                : $"密码列表记忆写入失败（{PasswordListStore.FileName}）：{FailureReason}";
    }

    /// <summary>
    /// 密码列表记忆：把「列表内容 / 顺序 / 启用状态 / 手工条目 / 记住的密码本 / 墓碑」
    /// 加密存到 <c>&lt;DataRootDirectory&gt;\password-list.dat</c>。
    ///
    /// <para><b>加密方式（用户 2026-09-24 亲自选定，⚠ 代价必须如实告知）</b>：
    /// Windows 自带的 DPAPI，取**机器范围**（<c>CryptProtectData</c> + <c>CRYPTPROTECT_LOCAL_MACHINE</c>），
    /// 外加一段固定的应用 entropy。因此它**跟 Windows 账号无关** —— 同一台机器上换谁登录都能用、
    /// 不弹任何框；代价有两条，用户已知并接受：</para>
    /// <list type="number">
    /// <item><description><b>同机任何本机用户都可能解开它</b>（机器范围的固有性质，不是实现缺陷）；</description></item>
    /// <item><description><b>换机器 / 重装系统解不开</b> —— 那时必须**忽略并提示**：
    /// 不崩、不覆盖、不删文件，并告诉用户可以用「写回密码本」把列表带走。</description></item>
    /// </list>
    ///
    /// <para>文件放 <c>&lt;程序目录&gt;\data\</c>（沿用既有 <c>PathService.DataRootDirectory</c>），
    /// 用户明确要求"存在安装的地方"，**绝不写 C 盘**。</para>
    ///
    /// <para>本类**不引用 WPF**（AGENTS.md §4 分层铁律），也不新增任何 NuGet 依赖
    /// （DPAPI 是 P/Invoke <c>crypt32.dll</c>）。</para>
    /// </summary>
    public sealed class PasswordListStore
    {
        /// <summary>记忆文件名（唯一来源：日志、界面提示、测试都引它，别再各写一份字面量）。</summary>
        public const string FileName = "password-list.dat";

        /// <summary>
        /// 固定的应用 entropy。
        ///
        /// <para>它的作用是"把这份密文绑到本程序的用途上"：别的程序即使也调用机器范围的 DPAPI，
        /// 少了这段 entropy 也解不开我们的文件（反过来我们也解不开别人的）。</para>
        /// <para>⚠ 它**不是密钥**（就写在源码里），所以别把它当成一道安全防线 ——
        /// 真正的边界是"机器范围 DPAPI"本身，而那条边界用户已经知道并接受。</para>
        /// </summary>
        public const string ApplicationEntropy = "ArchiveFixer.PasswordList.v1";

        /// <summary>
        /// 载荷版本号。读到不认识的版本一律当"不是我们的格式"，**绝不猜着解析**。
        ///
        /// <para>
        /// 版本历史：
        /// <list type="bullet">
        /// <item><description><b>1</b>：条目 / 密码本路径 / 墓碑。</description></item>
        /// <item><description><b>2</b>（2026-09-24 第 14 条）：加 <c>SuccessCounts</c>（每条密码的成功次数）。
        /// 旧文件（v1）**照样读得进来** —— 缺的字段按"没有成功记录"处理；
        /// 读进来的旧文件下次保存时会自动升成 v2。</description></item>
        /// </list>
        /// </para>
        /// </summary>
        public const int CurrentVersion = 2;

        /// <summary>
        /// 还认的最老版本。低于它 = 不是我们的格式（绝不猜着解析）。
        ///
        /// <para>为什么不是"只认当前版本"：记忆文件是**用户跨重启的资产**，
        /// 加一个字段就把它判成"读不懂"，用户看到的是"我的密码列表没了"。</para>
        /// </summary>
        public const int OldestSupportedVersion = 1;

        private const int CryptProtectUiForbidden = 0x1;

        private const int CryptProtectLocalMachine = 0x4;

        private const string Crypt32 = "crypt32.dll";

        private const string Kernel32 = "kernel32.dll";

        private static readonly JsonSerializerOptions PayloadJsonOptions = new()
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// 记忆文件所在目录（沿用既有数据根目录，**不新造路径来源**）。
        /// 空 = 没装配好，保存会明确失败（绝不悄悄写到一个猜出来的位置）。
        /// </summary>
        public string DataRootDirectory { get; set; } = string.Empty;

        /// <summary>记忆文件全路径。</summary>
        public string FilePath => string.IsNullOrWhiteSpace(DataRootDirectory)
            ? string.Empty
            : Path.Combine(DataRootDirectory, FileName);

        /// <summary>临时文件名（原子写用）。后缀固定，测试与排障都能一眼认出来。</summary>
        public string TempFilePath => FilePath.Length == 0 ? string.Empty : FilePath + ".tmp";

        /// <summary>
        /// 读记忆。
        ///
        /// <para><b>任何失败都不许覆盖、不许删除这个文件</b>：解不开就当作"没有可用记忆"回去，
        /// 原因如实返回；用户换回原来的机器 / 系统时那份记忆还在。</para>
        /// </summary>
        public PasswordListLoadResult Load()
        {
            string path = FilePath;

            if (path.Length == 0)
            {
                return new PasswordListLoadResult(
                    PasswordListLoadStatus.Unreadable,
                    null,
                    "没有可用的数据根目录");
            }

            if (!File.Exists(path))
            {
                return new PasswordListLoadResult(PasswordListLoadStatus.NoFile, null, string.Empty);
            }

            byte[] cipher;

            try
            {
                cipher = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                return new PasswordListLoadResult(
                    PasswordListLoadStatus.Unreadable,
                    null,
                    DescribeException(ex));
            }

            // 空文件 / 只有一个字节之类的半截文件：DPAPI 只会报"参数错误"，
            // 单独判一次能让原因更准确（"文件不完整"而不是"解不开"）。
            if (cipher.Length == 0)
            {
                return new PasswordListLoadResult(
                    PasswordListLoadStatus.Unreadable,
                    null,
                    "记忆文件是空的（可能上次写到一半被打断）");
            }

            if (!TryUnprotect(cipher, out byte[] plain, out int win32Error))
            {
                /*
                 * 解不开的两种典型来源，用户看到的措辞必须能区分"该怎么办"：
                 * ① 换机器 / 重装系统 / 换 Windows 账号（机器范围 DPAPI 解不开）→ 忽略并提示；
                 * ② 文件被改过一个字节（损坏或被人动过）→ 同样忽略，但不要谎称"是你换机器了"。
                 * 我们无法从错误码上区分这两者（都是 ERROR_INVALID_DATA），所以文案两件事都说。
                 */
                return new PasswordListLoadResult(
                    PasswordListLoadStatus.Undecryptable,
                    null,
                    win32Error == 0
                        ? "密文无法解开（可能换了机器或系统，也可能文件被改过）"
                        : $"密文无法解开（可能换了机器或系统，也可能文件被改过；错误码 {win32Error}）");
            }

            PasswordListSnapshot? snapshot = TryParsePayload(plain, out string parseReason);

            if (snapshot == null)
            {
                return new PasswordListLoadResult(PasswordListLoadStatus.NotOurFormat, null, parseReason);
            }

            return new PasswordListLoadResult(PasswordListLoadStatus.Loaded, snapshot, string.Empty);
        }

        /// <summary>
        /// 写记忆（**原子写**）：先写 <c>password-list.dat.tmp</c>，再整体替换正式文件。
        ///
        /// <para><b>替换失败必须把旧文件保住</b>：正式文件只在"新内容已经完整写好"之后才被换掉，
        /// 两条替换路径（<see cref="File.Replace(string,string,string)"/> 与
        /// <see cref="File.Move(string,string,bool)"/> 的覆盖档）**都不会先删旧的**，
        /// 所以任何一步失败的结果都是"旧的原样在、新的没进去"，绝不会两边都没了。</para>
        /// </summary>
        public PasswordListSaveResult Save(PasswordListSnapshot snapshot)
        {
            string path = FilePath;

            if (path.Length == 0)
            {
                return new PasswordListSaveResult(false, "没有可用的数据根目录", string.Empty);
            }

            snapshot ??= new PasswordListSnapshot();

            string tempPath = TempFilePath;

            try
            {
                Directory.CreateDirectory(DataRootDirectory);

                byte[] plain = BuildPayloadBytes(snapshot);

                if (!TryProtect(plain, out byte[] cipher, out int win32Error))
                {
                    return new PasswordListSaveResult(
                        false,
                        $"加密失败（错误码 {win32Error}）",
                        path);
                }

                // 顺序不能反：先把全新的内容完整写进 .tmp，再谈替换。
                File.WriteAllBytes(tempPath, cipher);

                string replaceError;

                if (File.Exists(path))
                {
                    if (!TryReplace(tempPath, path, out replaceError))
                    {
                        TryDeleteTemp(tempPath);

                        return new PasswordListSaveResult(false, replaceError, path);
                    }
                }
                else if (!TryMoveIntoPlace(tempPath, path, out replaceError))
                {
                    TryDeleteTemp(tempPath);

                    return new PasswordListSaveResult(false, replaceError, path);
                }

                return new PasswordListSaveResult(true, string.Empty, path);
            }
            catch (Exception ex)
            {
                // 走到这里说明是没预料到的异常（建目录失败、写 .tmp 失败……）。
                // 一样：旧文件一个字节都没动过，如实返回原因（**不含路径、不含明文**）。
                TryDeleteTemp(tempPath);

                return new PasswordListSaveResult(false, DescribeException(ex), path);
            }
        }

        /// <summary>
        /// 正式文件已存在时的替换：先试 <see cref="File.Replace(string,string,string)"/>
        /// （NTFS 上最原子的那一条），失败再试"覆盖式 Move"。
        ///
        /// <para>两条都是在**不删旧文件**的前提下换掉它；两条都失败时旧文件仍然原样在盘上。</para>
        /// </summary>
        private static bool TryReplace(string tempPath, string path, out string error)
        {
            try
            {
                File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);

                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = DescribeException(ex);
            }

            if (TryMoveIntoPlace(tempPath, path, out string moveError))
            {
                error = string.Empty;
                return true;
            }

            error = error + "；覆盖式移动也失败：" + moveError;

            return false;
        }

        private static bool TryMoveIntoPlace(string tempPath, string path, out string error)
        {
            try
            {
                File.Move(tempPath, path, overwrite: true);

                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                error = DescribeException(ex);
                return false;
            }
        }

        private static void TryDeleteTemp(string tempPath)
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // 留一个 .tmp 不影响正确性（下次保存会覆盖它），不值得再抛一次。
            }
        }

        /// <summary>
        /// 记忆文件的失败原因：**只保留异常类型 + 文件名，绝不带上完整路径**。
        ///
        /// <para>为什么这么严：这些字符串会被写进日志（§8 隐私红线：个人路径不入日志），
        /// 而 <c>UnauthorizedAccessException</c> 之类的 Message 里恰好带着完整路径。
        /// 用户真正需要知道的是"哪一类失败"，路径在这里没有价值。</para>
        /// </summary>
        private static string DescribeException(Exception ex)
        {
            string type = ex.GetType().Name;

            return type switch
            {
                "UnauthorizedAccessException" => $"{FileName} 不可写（权限不足或被设成只读）",
                "IOException" => $"{FileName} 写入被占用或磁盘出错（IOException）",
                "DirectoryNotFoundException" => $"{FileName} 所在目录不存在",
                "NotSupportedException" => $"{FileName} 路径不受支持",
                "PathTooLongException" => $"{FileName} 路径过长",
                _ => $"{FileName} 操作失败（{type}）"
            };
        }

        /// <summary>
        /// 把快照序列化成载荷字节（**加密之前**的明文 JSON）。
        ///
        /// <para><c>internal</c> 是为了让测试能直接验"旧版本载荷读得进来"（见 <see cref="TryParsePayload"/>）：
        /// 载荷是加密落盘的，测试没法从外面造一份"版本 1"的文件出来，只能在这一层验。</para>
        /// </summary>
        internal static byte[] BuildPayloadBytes(PasswordListSnapshot snapshot)
        {
            var payload = new PayloadDto
            {
                Version = CurrentVersion,
                Entries = new List<EntryDto>(snapshot.Entries.Count),
                BookPaths = new List<string>(snapshot.BookPaths),
                Tombstones = new List<string>(snapshot.Tombstones),
                SuccessCounts = new Dictionary<string, int>(snapshot.SuccessCounts, StringComparer.Ordinal)
            };

            foreach (PasswordListEntry entry in snapshot.Entries)
            {
                if (entry == null)
                {
                    continue;
                }

                payload.Entries.Add(new EntryDto
                {
                    Value = entry.Value ?? string.Empty,
                    Source = entry.Source ?? string.Empty,
                    Enabled = entry.Enabled,
                    Remark = entry.Remark ?? string.Empty
                });
            }

            string json = JsonSerializer.Serialize(payload, PayloadJsonOptions);

            return Encoding.UTF8.GetBytes(json);
        }

        /// <summary>
        /// 解析载荷（**解密之后**的明文 JSON）。
        ///
        /// <para>版本兼容在这里：<see cref="OldestSupportedVersion"/>～<see cref="CurrentVersion"/> 之间的
        /// 版本都读得进来，缺的新字段按"没有"处理（v1 没有 <c>SuccessCounts</c> → 空表）。</para>
        /// <para><c>internal</c> 的理由见 <see cref="BuildPayloadBytes"/>。</para>
        /// </summary>
        internal static PasswordListSnapshot? TryParsePayload(byte[] plain, out string reason)
        {
            reason = string.Empty;

            string json;

            try
            {
                json = Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex)
            {
                reason = "记忆内容不是有效的 UTF-8 文本（" + ex.GetType().Name + "）";
                return null;
            }

            PayloadDto? payload;

            try
            {
                payload = JsonSerializer.Deserialize<PayloadDto>(json, PayloadJsonOptions);
            }
            catch (JsonException)
            {
                // 解开了却读不懂 = 不是我们的格式（可能是被别的程序用同一段 entropy 写坏的内容）。
                reason = "记忆内容不是本程序写的格式";
                return null;
            }

            if (payload == null)
            {
                reason = "记忆内容为空";
                return null;
            }

            if (payload.Version < OldestSupportedVersion || payload.Version > CurrentVersion)
            {
                reason = $"记忆格式版本不认识（{payload.Version}）";
                return null;
            }

            var snapshot = new PasswordListSnapshot();

            foreach (EntryDto dto in payload.Entries ?? new List<EntryDto>())
            {
                if (dto == null)
                {
                    continue;
                }

                snapshot.Entries.Add(new PasswordListEntry
                {
                    Value = dto.Value ?? string.Empty,
                    Source = string.IsNullOrWhiteSpace(dto.Source) ? "ManualList" : dto.Source,
                    Enabled = dto.Enabled,
                    Remark = dto.Remark ?? string.Empty
                });
            }

            foreach (string book in payload.BookPaths ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(book))
                {
                    snapshot.BookPaths.Add(book);
                }
            }

            foreach (string tombstone in payload.Tombstones ?? new List<string>())
            {
                if (tombstone != null)
                {
                    snapshot.Tombstones.Add(tombstone);
                }
            }

            /*
             * 成功次数（v2 才有的字段）：v1 的旧文件里没有它 → 反序列化后是 null → 空表。
             * 空值 / 负数一律不当账（宁可"没有记录"，也不要让它参与排序时把别的条目压下去）。
             */
            foreach (KeyValuePair<string, int> pair in payload.SuccessCounts ?? new Dictionary<string, int>())
            {
                if (pair.Key == null || pair.Value <= 0)
                {
                    continue;
                }

                snapshot.SuccessCounts[pair.Key] = pair.Value;
            }

            return snapshot;
        }

        // ================================================================
        // DPAPI（机器范围）—— P/Invoke，不引第三方库
        // ================================================================

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int cbData;

            public IntPtr pbData;
        }

        [DllImport(Crypt32, SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DataBlob pDataIn,
            string? szDataDescr,
            ref DataBlob pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            out DataBlob pDataOut);

        [DllImport(Crypt32, SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlob pDataIn,
            out IntPtr ppszDataDescr,
            ref DataBlob pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            out DataBlob pDataOut);

        [DllImport(Kernel32)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        /// <summary>
        /// 加密。
        ///
        /// <para>两个标志位缺一不可：<c>CRYPTPROTECT_LOCAL_MACHINE</c>（机器范围，与 Windows 账号无关，
        /// 用户亲自选定）与 <c>CRYPTPROTECT_UI_FORBIDDEN</c>（**绝不弹任何框** ——
        /// 这个调用可能发生在启动路径上，弹框就是卡住用户）。</para>
        /// </summary>
        private static bool TryProtect(byte[] plain, out byte[] cipher, out int win32Error)
        {
            cipher = Array.Empty<byte>();
            win32Error = 0;

            byte[] entropyBytes = Encoding.UTF8.GetBytes(ApplicationEntropy);

            IntPtr dataPtr = IntPtr.Zero;
            IntPtr entropyPtr = IntPtr.Zero;
            DataBlob outBlob = default;

            try
            {
                dataPtr = Marshal.AllocHGlobal(plain.Length);
                Marshal.Copy(plain, 0, dataPtr, plain.Length);

                entropyPtr = Marshal.AllocHGlobal(entropyBytes.Length);
                Marshal.Copy(entropyBytes, 0, entropyPtr, entropyBytes.Length);

                var dataIn = new DataBlob { cbData = plain.Length, pbData = dataPtr };
                var entropy = new DataBlob { cbData = entropyBytes.Length, pbData = entropyPtr };

                bool ok = CryptProtectData(
                    ref dataIn,
                    null,
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectLocalMachine | CryptProtectUiForbidden,
                    out outBlob);

                if (!ok)
                {
                    win32Error = Marshal.GetLastWin32Error();
                    return false;
                }

                cipher = new byte[outBlob.cbData];

                if (outBlob.cbData > 0)
                {
                    Marshal.Copy(outBlob.pbData, cipher, 0, outBlob.cbData);
                }

                return true;
            }
            catch
            {
                // P/Invoke 层出意外（极罕见）：如实当作"加密失败"，绝不落一份明文下去。
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }
            finally
            {
                if (outBlob.pbData != IntPtr.Zero)
                {
                    LocalFree(outBlob.pbData);
                }

                if (dataPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(dataPtr);
                }

                if (entropyPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(entropyPtr);
                }
            }
        }

        private static bool TryUnprotect(byte[] cipher, out byte[] plain, out int win32Error)
        {
            plain = Array.Empty<byte>();
            win32Error = 0;

            byte[] entropyBytes = Encoding.UTF8.GetBytes(ApplicationEntropy);

            IntPtr cipherPtr = IntPtr.Zero;
            IntPtr entropyPtr = IntPtr.Zero;
            IntPtr descriptionPtr = IntPtr.Zero;
            DataBlob outBlob = default;

            try
            {
                cipherPtr = Marshal.AllocHGlobal(cipher.Length);
                Marshal.Copy(cipher, 0, cipherPtr, cipher.Length);

                entropyPtr = Marshal.AllocHGlobal(entropyBytes.Length);
                Marshal.Copy(entropyBytes, 0, entropyPtr, entropyBytes.Length);

                var dataIn = new DataBlob { cbData = cipher.Length, pbData = cipherPtr };
                var entropy = new DataBlob { cbData = entropyBytes.Length, pbData = entropyPtr };

                bool ok = CryptUnprotectData(
                    ref dataIn,
                    out descriptionPtr,
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectLocalMachine | CryptProtectUiForbidden,
                    out outBlob);

                if (!ok)
                {
                    win32Error = Marshal.GetLastWin32Error();
                    return false;
                }

                plain = new byte[outBlob.cbData];

                if (outBlob.cbData > 0)
                {
                    Marshal.Copy(outBlob.pbData, plain, 0, outBlob.cbData);
                }

                return true;
            }
            catch
            {
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }
            finally
            {
                if (outBlob.pbData != IntPtr.Zero)
                {
                    LocalFree(outBlob.pbData);
                }

                // CryptUnprotectData 顺带返回的描述串（我们没传描述进去，通常为空）：
                // 它由 LocalAlloc 分配，不释放就是一处内存泄漏。
                if (descriptionPtr != IntPtr.Zero)
                {
                    LocalFree(descriptionPtr);
                }

                if (cipherPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(cipherPtr);
                }

                if (entropyPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(entropyPtr);
                }
            }
        }

        // ================================================================
        // 载荷 DTO（字段名就是 json 里的键，改动即等于改格式 → 必须同时抬版本号）
        // ================================================================

        private sealed class PayloadDto
        {
            public int Version { get; set; }

            public List<EntryDto>? Entries { get; set; }

            public List<string>? BookPaths { get; set; }

            public List<string>? Tombstones { get; set; }

            /// <summary>v2 起：密码值 → 成功次数（旧文件里没有这一项，读出来是 null）。</summary>
            public Dictionary<string, int>? SuccessCounts { get; set; }
        }

        private sealed class EntryDto
        {
            public string? Value { get; set; }

            public string? Source { get; set; }

            public bool Enabled { get; set; } = true;

            public string? Remark { get; set; }
        }
    }
}
