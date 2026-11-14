using System;
using System.Collections.Generic;

namespace ArchiveFixer.Models;

/// <summary>
/// <see cref="AppSettings.Normalize"/> 的归一化实现：旧配置迁移、非法值夹回、系统固定值钉死。
///
/// <para>为什么单独成类：归一化是「用户存过的值 → 程序真正要跑的值」的唯一出口，规则多且每条都有
/// 明确的落点（哪一档不可逆、哪一档只是容错）。放在设置类里会和序列化契约、出厂默认值混在一起；
/// 拆出来之后 <see cref="AppSettings"/> 只保留公开属性与兼容 API。</para>
///
/// <para>⛔ <b>组的调用顺序就是语义</b>：组与组之间互不读取对方归一的字段，所以按职责分组不改变结果；
/// 但组内顺序（例如 <see cref="PinSystemFixedDefaults"/> 必须在最后、总大小上限必须先夹单文件上限）
/// 是判据的一部分，改动前先看 <c>FixedDefaultsTests</c> / <c>ExtractionLimitSettingTests</c> 的钉法。</para>
/// </summary>
internal static class AppSettingsNormalizer
{
    /// <summary>就地把一份设置归一化到「程序真正要跑的值」。</summary>
    internal static void Normalize(AppSettings settings)
    {
        AppSettingsSerialization.RemoveRetiredUnknownSettings(settings);

        NormalizeScanAndIdentity(settings);
        NormalizeOutputPolicy(settings);
        NormalizeConcurrency(settings);
        NormalizeSourceAndRestHandling(settings);
        NormalizePasswordBooks(settings);
        NormalizeEnginePriority(settings);
        NormalizeContentKeepKeywords(settings);
        NormalizeRecursion(settings);
        NormalizePasswordAttemptLimit(settings);
        NormalizeExtractionBudgetCaps(settings);
        NormalizeToolPaths(settings);
        PinSystemFixedDefaults(settings);
    }

    /// <summary>扫描与识别：模式 / 未知格式 / 默认后缀 / 单文件大小上限（彼此独立，均为空值与非法值容错）。</summary>
    private static void NormalizeScanAndIdentity(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ScanMode))
        {
            settings.ScanMode = "ScanAllFiles";
        }

        if (string.IsNullOrWhiteSpace(settings.UnknownFormatAction))
        {
            settings.UnknownFormatAction = "MarkUnknown";
        }

        if (string.IsNullOrWhiteSpace(settings.DefaultExtension))
        {
            settings.DefaultExtension = ".7z";
        }

        if (!settings.DefaultExtension.StartsWith(".", StringComparison.Ordinal))
        {
            settings.DefaultExtension = "." + settings.DefaultExtension;
        }

        if (settings.MaxFileSizeLimit < 0)
        {
            settings.MaxFileSizeLimit = 0;
        }
    }

    /// <summary>输出策略：同名冲突档 / 覆盖策略 / 落点的两个布尔与旧模式字段 / 归集与自定义根的空值兜底。</summary>
    private static void NormalizeOutputPolicy(AppSettings settings)
    {
        /*
         * 同名冲突处理档：空 / 非法一律回落 AutoRename（**绝不默认覆盖**，不变量 3）。
         *
         * 归一化放在设置层，与 RecursionMode / TerminalLayoutMode / SourceHandling 同一口径：
         * 到了冲突那一刻才发现"这个档读不懂"是最糟的 —— 用户已经点了解压，程序却要临时猜一个处置方式。
         * 判定本身只有一处实现（ConflictActions.Normalize），这里不另写一套字符串比较。
         */
        settings.ConflictAction = ConflictActions.Normalize(settings.ConflictAction);

        if (string.IsNullOrWhiteSpace(settings.OverwriteMode))
        {
            settings.OverwriteMode = "SkipExisting";
        }

        /*
         * 落点旧配置迁移（用户 2026-09-24 第 13 条：删掉"解压到当前目录"与"直接解到指定目录"两档）。
         *
         * 落盘一直是**两个布尔**（ExtractToOriginalDirectory + KeepArchiveNameFolder），
         * 旧的那两档就是 (true,false) 与 (false,false)。它们现在已经没有对应档位了，
         * 所以这里把 KeepArchiveNameFolder 归一成 true：
         * · 旧配置读进来**不炸**，行为落到用户要的四条规则上（源目录家族 → 111\222\；
         *   指定位置 → 顺手保留那个根，但补上同名子文件夹）；
         * · 归一化放在设置层，与 ConflictAction / SourceHandling 同一口径 ——
         *   到解压那一刻才发现"这个组合读不懂"是最糟的，那时用户已经点了一键处理。
         * ⚠ 刻意**不**动 ExtractToOriginalDirectory：它决定"有没有指定位置"，是两档的判据本身。
         */
        settings.KeepArchiveNameFolder = true;

        /*
         * 落点只有**用户可见的两个布尔 + 自定义根**这一份真值。
         *
         * `PlacementMode` 是旧版本留下的持久化字段，只作为兼容输出保留：
         * 它一旦与用户界面的两个布尔不一致，就会把“指定位置”悄悄压成“原地”
         * （2026-11-05 真机：ExtractToOriginalDirectory=false + CustomOutputDirectory=H:\...\333
         * 仍在源目录解压，因为旧 PlacementMode=0 赢了）。
         * 所以每次 Normalize 都从两个布尔重新派生，绝不允许旧字段反向覆盖。
         *
         * ⚠ 派生规则本身在 <see cref="OutputPlacementRules"/>（Models 里的纯函数）：
         * <c>Extraction/OutputPlacement</c> 的同名方法只是 1:1 转发，两条路给出同一个结果。
         */
        settings.PlacementMode = OutputPlacementRules.NormalizeLegacyMode(
            OutputPlacementRules.FromLegacyFlags(
                settings.ExtractToOriginalDirectory,
                settings.KeepArchiveNameFolder,
                settings.CustomOutputDirectory));

        settings.CustomOutputDirectory ??= string.Empty;
        settings.CollectTargetDirectory ??= string.Empty;
    }

    /// <summary>并发：用户可见的 1–4 与内部/测试专用覆盖 0–8（两组互不覆盖，改一个绝不动另一个）。</summary>
    private static void NormalizeConcurrency(AppSettings settings)
    {
        if (settings.MaxParallelExtractCount < 1)
        {
            settings.MaxParallelExtractCount = 1;
        }

        if (settings.MaxParallelExtractCount > 4)
        {
            settings.MaxParallelExtractCount = 4;
        }

        if (settings.ConcurrencyOverride < 0)
        {
            settings.ConcurrencyOverride = 0;
        }

        if (settings.ConcurrencyOverride > 8)
        {
            settings.ConcurrencyOverride = 8;
        }
    }

    /// <summary>源包处理档与「删除操作」两档：只认当前词表，认不出回落最保守的那一档。</summary>
    private static void NormalizeSourceAndRestHandling(AppSettings settings)
    {
        /*
         * 源包处理档：只认当前枚举值；认不出回落默认档。
         */
        settings.SourceHandling = AppSettingsSerialization.ParseSourceHandling(settings.SourceHandling).ToString();

        /*
         * 「删除操作」两档：空 / 非法一律回落 **Keep（留下）** —— 保守方向是"宁可不动，绝不误删"。
         * ⚠ 老配置里存过的 `RecycleBin`（2026-10-15 整条下线）也落进 Keep：
         * ⛔ 绝不许把它当成 Delete（那是把"可还原"悄悄升级成"不可恢复"，方向正好反了）。
         * 判定只有一处实现（RestHandlingModes.Normalize），这里不另写一套字符串比较。
         */
        settings.RestHandlingAfterVerify = RestHandlingModes.Normalize(settings.RestHandlingAfterVerify);
    }

    /// <summary>密码本清单（用户 2026-09-24：多本密码本）：旧单值字段并进有序清单，再按清单最后一项回写旧字段。</summary>
    private static void NormalizePasswordBooks(AppSettings settings)
    {
        /*
         * 密码本清单（用户 2026-09-24：多本密码本）。
         *
         * 迁移放在这里，与其它设置项的容错同一口径：旧配置里只有单个 PasswordBookPath，
         * 读出来是个 null 列表 —— 这里把它补进去，老用户升级后的"自动加载哪一本"行为不变。
         *
         * ⚠ 刻意**不清空老字段** PasswordBookPath：用户回退到旧版本时，那一边仍然靠它认路
         *（清空 = 回退后"密码本没配过"，等于把用户的配置吃掉一次）。
         * 于是这份清单在每次 Normalize 时都会把老字段的值并进来 —— 用 Contains 去重，幂等。
         */
        settings.PasswordBookPath ??= string.Empty;

        List<string> books = AppSettingsSerialization.NormalizeBookPaths(
            settings.PasswordBookPaths,
            settings.PasswordBookPath);

        settings.PasswordBookPaths = books;
        settings.PasswordBookPath = books.Count > 0 ? books[books.Count - 1] : string.Empty;
    }

    /// <summary>引擎优先级：空 / 缺字段 / 手改坏的值一律收拾成「规范写法 + 补全所有已知引擎」。</summary>
    private static void NormalizeEnginePriority(AppSettings settings)
    {
        /*
         * 引擎优先级：空 / 缺字段 / 手改坏的值一律收拾成"规范写法 + 补全所有已知引擎"。
         *
         * 归一化放在设置层，与其它设置项同一口径：到引擎选择那一刻才发现"这个列表读不懂"
         * 是最糟的 —— 用户已经点了处理，程序却要临时猜用哪个引擎。
         * 判定只有一处实现（EngineIds.Normalize），这里不另写一套字符串比较。
         */
        settings.EnginePriority = EngineIds.Normalize(settings.EnginePriority);
    }

    /// <summary>内容物保留关键词：null（旧配置）与空列表等价，都是「这个功能不生效」。</summary>
    private static void NormalizeContentKeepKeywords(AppSettings settings)
    {
        /*
         * 「内容物保留关键词」（用户 2026-10-04）：
         *
         * 归一化放在设置层，与 EnginePriority 同一口径 —— 到解压那一刻才发现
         * "这一行读不懂"是最糟的（用户已经点了一键处理）。判定只有一处实现
         * （ContentKeepRules.NormalizeKeywords），这里不另写一套字符串比较。
         *
         * ⚠ null（旧配置里没有这个字段）与空列表**在这里是同一个结果**：都是"这个功能不生效"。
         */
        settings.ContentKeepKeywords = ArchiveFixer.Security.ContentKeepRules
            .NormalizeKeywords(settings.ContentKeepKeywords);
    }

    /// <summary>递归层数：一次性迁移（旧默认 3 → 10）+ 夹到 1–10。</summary>
    private static void NormalizeRecursion(AppSettings settings)
    {
        /*
         * 一次性迁移（用户 2026-09-24 拍板"两个上限统一成 10"）：
         *
         * 旧默认值是 3，而**从没主动选过 3** 的用户读到的那个 3 其实是"旧默认"，不是他的选择 ——
         * 光把默认值改成 10 对他没用（他的设置文件里存着 3）。所以这里迁一次，并落一个标记：
         * 迁移只发生一次，他之后在②页把 3 改回来就是他的选择，不会被再次顶掉。
         */
        if (!settings.RecursionDefaultUnifiedToTen)
        {
            if (settings.MaxRecursionDepth == 3)
            {
                settings.MaxRecursionDepth = 10;
            }

            settings.RecursionDefaultUnifiedToTen = true;
        }

        // 层数下限 1（只解当前层），上限 10：再深就不是"帮用户省事"而是失控了。
        if (settings.MaxRecursionDepth < 1)
        {
            settings.MaxRecursionDepth = 1;
        }

        if (settings.MaxRecursionDepth > 10)
        {
            settings.MaxRecursionDepth = 10;
        }
    }

    /// <summary>每层密码尝试上限：夹到 1–1000（旧配置没有这一项时读到的就是默认值 10，不需要兼容分支）。</summary>
    private static void NormalizePasswordAttemptLimit(AppSettings settings)
    {
        // 密码尝试上限：下限 1（至少试一个候选），上限 1000（再大就不是省事而是烧时间）。
        // 旧配置文件里没有这一项 → 反序列化后拿到的是默认值 10，不需要额外兼容分支。
        if (settings.MaxPasswordAttemptsPerLayer < 1)
        {
            settings.MaxPasswordAttemptsPerLayer = 1;
        }

        if (settings.MaxPasswordAttemptsPerLayer > 1000)
        {
            settings.MaxPasswordAttemptsPerLayer = 1000;
        }
    }

    /// <summary>解压前的资源预算上限：四条都夹回合法区间（夹的规则唯一实现在 <see cref="ExtractionResourceCapRules"/>）。</summary>
    private static void NormalizeExtractionBudgetCaps(AppSettings settings)
    {
        /*
         * 解压前的资源预算上限（用户 2026-09-25 第 36 条）：四条都夹回合法区间。
         * 夹的规则只有一处实现（ExtractionResourceCapRules），`ResourceBudgetOptions.FromSettings`
         * 读的时候用的是同一批函数 —— 界面、落盘、真正判包三处口径不许分叉。
         */
        settings.MaxSingleExtractedFileGiB = ExtractionResourceCapRules.NormalizeSingleFileGiB(settings.MaxSingleExtractedFileGiB);
        settings.MaxExtractedTotalGiB = ExtractionResourceCapRules.NormalizeTotalGiB(
            settings.MaxExtractedTotalGiB,
            settings.MaxSingleExtractedFileGiB);
        settings.MaxExtractedFileCount = ExtractionResourceCapRules.NormalizeFileCount(settings.MaxExtractedFileCount);
        settings.MaxExtractionRatio = ExtractionResourceCapRules.NormalizeRatio(settings.MaxExtractionRatio);
    }

    /// <summary>自定义工具路径：只清「本地盘还在、文件确实不存在」的那一条，外接盘/网络盘离线时保留用户填的值。</summary>
    private static void NormalizeToolPaths(AppSettings settings)
    {
        settings.CustomSevenZipExePath ??= string.Empty;
        settings.CustomUnRarExePath ??= string.Empty;
        settings.CustomRarExePath ??= string.Empty;

        // 自定义 7z 路径要么是有效文件，要么当没填 —— 留一个失效路径会让整个程序找不到引擎。
        if (!string.IsNullOrWhiteSpace(settings.CustomSevenZipExePath) && ShouldClearToolPath(settings.CustomSevenZipExePath))
        {
            settings.CustomSevenZipExePath = string.Empty;
        }

        /*
         * 自定义 UnRAR 路径同理；但**兜底方向不同**：UnRAR 还有"已装 WinRAR 目录"与"内置"两档，
         * 所以清空它只是回到"自动解析"，不会让引擎不可用（ToolLocator 里两级回落）。
         */
        if (!string.IsNullOrWhiteSpace(settings.CustomUnRarExePath) && ShouldClearToolPath(settings.CustomUnRarExePath))
        {
            settings.CustomUnRarExePath = string.Empty;
        }

        /*
         * 自定义 Rar.exe 路径同理（打包的外层 rar 用）。清空它只是回到"自动解析"，
         * 不会让打包不可用 —— 界面上还可以把外层容器改成 7z 或"不做外层"。
         *
         * ⚠ 这里**不做任何"帮用户找一份 Rar.exe 塞进来"的事**：那是分发共享软件的第一步
         * （AGENTS.md §3.1）。这一格永远是用户自己填的。
         */
        if (!string.IsNullOrWhiteSpace(settings.CustomRarExePath) && ShouldClearToolPath(settings.CustomRarExePath))
        {
            settings.CustomRarExePath = string.Empty;
        }
    }

    /// <summary>
    /// 只有“本地盘还在、文件确实不存在”才清空工具路径。
    /// 外接盘/网络盘拔掉时 <see cref="System.IO.File.Exists(string)"/> 也是 false，
    /// 但那是暂时离线，不能把用户填的路径永久清掉。
    /// </summary>
    private static bool ShouldClearToolPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || System.IO.File.Exists(path))
        {
            return false;
        }

        try
        {
            string? root = System.IO.Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && System.IO.Directory.Exists(root);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 系统固定默认值（用户 2026-10-14 最终设计 + 当场逐项拍板）：界面入口已删的项在这里钉死，
    /// 否则用户盘上的旧值会变成界面上看不见的暗档。
    /// </summary>
    private static void PinSystemFixedDefaults(AppSettings settings)
    {
        /*
         * 「失败时保留中间产物」（用户 2026-09-25 第 25 条追加）：**bool 不需要归一化**。
         *
         * 它只有开 / 关两种取值，读不出第三种；而旧配置里没有这个字段时，
         * System.Text.Json 反序列化后就是属性默认值 false —— 与"默认档 = 失败不留残留"完全一致。
         * 所以迁移段**一个字都不写**：刻意留这段说明，免得以后有人顺手在迁移段加一句
         * `KeepFailedWorkspace = false;` 把用户打开的那一档每次启动都顶掉。
         * （下面这一批是**没有界面入口的系统固定值**，值固定成 false 属于用户 2026-10-14 的拍板。）
         */

        /*
         * ================================================================
         * 系统固定默认值（用户 2026-10-14 最终设计 + 当场逐项拍板）
         * ================================================================
         *
         * 用户原话：「我觉得有些删除的选项里面都应该时系统底层会做的而不是让用户来选择的」
         * ＋ 对"删除 = 从界面移出、值固定成系统默认（功能一个都不删）"的当场确认：「对，就是这个意思」。
         *
         * ⇒ 这一批设置项的**界面入口已经删掉**（设置页「启动时行为 / 扫描与识别」两组、
         *    ②页「续解省略中间层 / 解压覆盖策略」、③页「部分完成的内容」等），所以必须在这里把值钉死：
         *    否则用户机器上 appsettings.json 里存过的旧值会变成**界面上看不见的暗档** ——
         *    行为跟他以为的不一样，还没地方改回来（这是最坏的一种退化）。
         *
         * ⛔ 钉的就是用户逐项确认过的那张表（出处 docs/大改动-2026-10-14-口径与冲突.md §17.6）；
         * ⛔ 这些项**从此没有用户入口**：界面写什么程序就跑什么，所以他存过的旧值一律归到这里。
         * ⚠ 与"只改出厂默认、不迁移用户存过的值"那条老规矩的区别：那条针对的是**还有入口**的项；
         *    这一批已经没有入口了，保留旧值等于让用户背着一个改不掉的暗档。
         */
        settings.RememberLastOutputDirectory = true;      // 记住上次输出目录（开）
        settings.LowProcessPriority = false;              // 低运行优先级（关 —— 用户 2026-10-14 定的是"不降"）
        settings.RecursiveScan = true;                    // 递归扫描文件夹（开）
        settings.AutoScanAfterDrop = true;                // 导入后自动扫描（开）
        settings.ScanMode = "ScanAllFiles";               // 扫描模式（扫描全部文件）
        settings.UnknownFormatAction = "MarkUnknown";     // 未知格式处理（标记未知）
        settings.DefaultExtension = ".7z";                // 默认补充后缀（这一格是**后缀字符串**，出厂值 .7z）
        settings.OverwriteMode = "AutoRenameExisting";    // 解压覆盖策略（自动重命名已存在文件）
        settings.KeepFailedWorkspace = false;             // 工作区（失败 / 取消也默认清干净）
        settings.RememberPasswordList = true;             // 记住密码列表（系统默认操作，界面上没有开关）

        /*
         * 嵌套：**一律展开所有分支**（用户 2026-10-14 口径逐字：「我不是将单层路给删掉了吗，
         * 现在就是全面解压所有分路」＋最终设计「三档功能全部删除，只默认解开所有分支」）。
         *
         * ⚠ 用户 2026-10-14 夜当场纠正过我一次：我原先把这一项"留到不再有单层路那一刀"，
         * 那是理解偏了 —— **单层路已经删了**，所以老配置里存过的 SingleLayer / SingleChain
         * 必须现在就归一掉（否则就是一个界面上看不见、还在生效的档位）。
         *
         * ⚠ 跟着红的 5 条既有守门（`WorkspaceRootTests` 2 / `PartialPublishTests` 2 /
         * `RecursionLogAndProbePathFixesTests` 1）属于"场景建立在**已删模式**上"——
         * 按新现实重写（见各自注释），⛔ 不是把判据放宽。
         */
        /*
         * ⛔ 2026-10-15 红检记录（临时撤掉这一行跑过一轮，已还原）：撤掉之后
         * `InnerLayerContinuationTests` 23 红 → 2 红、`ExtractionPipelineFixTests` 10 → 0、
         * `ChainManifestCompletenessTests` 3 → 0、`OutputVerificationGateTests` 3 → 0
         * ⇒ **这一条 pin 是那 ~36 条红的唯一根因**（夹具把 `SingleLayer` 当"只造一层场景"的工具，
         * 而 `Save()` → `Normalize()` 会把它覆盖掉）。⚠ 这些用例测的是**已删的"一轮 = 一层"**那条路
         * ⇒ 按 `AllBranches` 新现实重写（⛔ 不是撤掉 pin —— 撤掉就违反用户口径）。
         */
        /*
         * ⛔ 这里**故意没有**钉 `PartialPublishEnabled`（"部分完成的内容也发布到成品目录"）。
         *
         * 用户口径是「这个功能我要淘汰，就是将这个功能做到底层设计上面，就是『损坏压缩包的解压情况』，
         * 这种情况下我们会自动开启，而且用户不能操作」—— 要的是**损坏压缩包那一档自动发布**。
         * 而这个开关现在同时管着一堆**无关**行为（取消途中不发布、工作区探针产物、逐层回收口径），
         * 实测直接钉 true 会打红 5 条既有守门（`PartialPublishTests` 3 条 + `WorkspaceRootTests` 2 条
         * + `RecursionLogAndProbePathFixesTests` 1 条）⇒ 那是"改错了层"。
         * ⇒ 留给「损坏压缩包 ⇒ 自动留内容物」那一刀：先把语义收窄到那一档，再钉值、再删界面。
         */
    }
}
