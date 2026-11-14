using System;
using System.Collections.Generic;

namespace ArchiveFixer.Models;

/// <summary>
/// appsettings.json 的**序列化契约**：未知键的保留与退役键的清理、枚举值 ↔ 落盘字符串的容错解析、
/// 密码本清单的迁移与去重。读写文件本身由 <c>Services/SettingsService</c> 负责，这里只解释「值」。
///
/// <para><b>已退役的键一律安静忽略、不迁移、不报错</b>（<c>System.Text.Json</c> 默认丢掉不认识的成员）：</para>
/// <list type="bullet">
/// <item><description><c>RestRemovalDefaultMode</c>（2026-10-15 整条删除）：手工清理只剩「彻底删除」一个动作，
/// 没有「档」可存 ⇒ 系统固定值。两道确认照旧：先预览条目数与总大小 → 红色确认 →
/// 勾「我知道不可恢复」再确认（见 <c>MainViewModel.DeriveCleanupDecision</c>）。</description></item>
/// <item><description><c>PreservePasswordLeadingTrailingSpaces</c>（2026-09-22 删除）：全项目没有任何一处读它，
/// 是一句谎话；「密码不 Trim」本来就是硬行为（AGENTS.md §9.1）。</description></item>
/// <item><description><c>CacheRootDirectory</c>（2026-09-30 彻底删除）：用户没有定工作区的权力，
/// 数据根固定 <c>%LOCALAPPDATA%\ArchiveFixer</c>、工作区由目标目录派生。它是唯一一个需要
/// **主动从扩展数据里清掉**的键 —— 保留它等于把已经取消的「用户自定义工作区」权限又带回配置里。</description></item>
/// </list>
/// </summary>
internal static class AppSettingsSerialization
{
    /// <summary>
    /// 明确退役、不能原样保留的旧键；其它未知键按「未来版本设置」原样保留
    /// （<see cref="AppSettings.UnknownSettings"/>）。
    /// </summary>
    internal static void RemoveRetiredUnknownSettings(AppSettings settings)
    {
        if (settings.UnknownSettings == null || settings.UnknownSettings.Count == 0)
        {
            return;
        }

        // 2026-09-30 用户拍板彻底删除「缓存根目录」；保留它等于把已经取消的
        // “用户自定义工作区”权限又带回配置里，因此只对这一个键做清理。
        settings.UnknownSettings.Remove("CacheRootDirectory");

        if (settings.UnknownSettings.Count == 0)
        {
            settings.UnknownSettings = null;
        }
    }

    /// <summary>
    /// 解析源包处理档：空 / 非法一律回落 <see cref="SourceHandlingMode.KeepInPlace"/>。
    ///
    /// 容错放在这里（与 <c>ParsePlacementMode</c> 同一口径）：这个字符串可能来自
    /// 旧配置（缺字段 → 反序列化后是默认值）、用户手改的 json，或将来改名后的枚举。
    /// 读不懂时**退回最不意外的那一档**，而不是到解压那一刻才报错或猜一个别的行为。
    ///
    /// <para>⚠ 回落档必须与 <see cref="AppSettings.SourceHandling"/> 的默认值 /
    /// <see cref="AppSettings.CreateDefault"/> **一致**（2026-09-25 第 32 条把默认档改成
    /// 「留在原地」时，这里漏改过一处：一个手改坏的字符串会把用户从没同意过的「搬走源包」打开 ——
    /// 而这一档是**不可逆**的）。读不懂 = 什么都不做。</para>
    /// </summary>
    internal static SourceHandlingMode ParseSourceHandling(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            Enum.TryParse(value.Trim(), ignoreCase: true, out SourceHandlingMode mode) &&
            Enum.IsDefined(mode))
        {
            return mode;
        }

        return SourceHandlingMode.KeepInPlace;
    }

    /// <summary>反解成落盘字符串（界面 ↔ 解压管线共用同一份口径）。</summary>
    internal static string ToSourceHandlingValue(SourceHandlingMode mode)
    {
        return mode.ToString();
    }

    /// <summary>
    /// 把「记住的密码本清单」收拾干净：去掉空项、去掉重复（大小写不敏感，Windows 路径口径），
    /// 并把老字段 <paramref name="legacyBookPath"/> 里那一本**补进清单**（迁移）。
    ///
    /// <para>顺序保留（第一本在最前）：合并密码本时「各本书的新条目按书顺序追加」依赖它。</para>
    /// </summary>
    internal static List<string> NormalizeBookPaths(IEnumerable<string>? paths, string? legacyBookPath)
    {
        var result = new List<string>();

        void TryAdd(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            string trimmed = candidate.Trim();

            foreach (string existing in result)
            {
                if (string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            result.Add(trimmed);
        }

        if (paths != null)
        {
            foreach (string path in paths)
            {
                TryAdd(path);
            }
        }

        // 迁移：旧配置只有 PasswordBookPath 一个字段（清空它会让用户回退版本后"没配过"）。
        TryAdd(legacyBookPath);

        return result;
    }
}
