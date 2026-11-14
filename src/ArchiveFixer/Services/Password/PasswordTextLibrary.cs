using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Services.Password;

/// <summary>
/// **内置文本库**：程序自带的 <c>passwords.txt</c>（顺序 = 尝试顺序）与它的附属元数据
/// <c>passwords.meta.json</c>（启用状态 / 备注；密码值仍以 txt 为唯一来源）。
///
/// <para>它负责"文本库这一层的语义"，而不是 p/invoke 细节（那在 <see cref="PasswordTextStore"/> /
/// <see cref="PasswordMetadataStore"/> 里）：把清单读进列表状态、旧文件加密升级、孤儿元数据的合并、
/// 以及"内置清单算不算已写回"的唯一判据。生产入口用 <see cref="UseTextStore"/> 打开它作为主存储；
/// 单测默认关掉，保持旧 DPAPI 夹具语义。</para>
/// </summary>
internal sealed class PasswordTextLibrary
{
    private readonly PasswordListState _state;
    private readonly PasswordListHealth _health;

    internal PasswordTextLibrary(PasswordListState state, PasswordListHealth health)
    {
        _state = state;
        _health = health;

        SetDataRootDirectory(string.Empty);
    }

    /// <summary>内置清单本体（对外仍由 <c>PasswordService.TextStore</c> 暴露，生产入口会设 EncryptAtRest）。</summary>
    internal PasswordTextStore TextStore { get; } = new();

    /// <summary>passwords.txt 的启用 / 备注元数据。</summary>
    internal PasswordMetadataStore MetadataStore { get; } = new();

    /// <summary>是否把内置 <c>passwords.txt</c> 当主存储（生产入口默认 true；单元测试默认 false）。</summary>
    internal bool UseTextStore { get; set; }

    /// <summary>文本库与记忆共用同一个数据根目录（两处分开设过一次就会互相看不见）。</summary>
    internal void SetDataRootDirectory(string? value)
    {
        string normalized = value ?? string.Empty;

        TextStore.DataRootDirectory = normalized;
        MetadataStore.DataRootDirectory = normalized;
    }

    internal bool Exists => TextStore.Exists;

    /// <summary>同目录里有没有产品自己的 <c>passwords.txt.bak</c>（用于判断"主清单存在过"）。</summary>
    internal bool BackupExists => File.Exists(TextStore.FilePath + ".bak");

    internal bool MetadataExists => MetadataStore.Exists;

    /// <summary>主清单不见了、同目录还留着 <c>.bak</c> 时按备份恢复（只在主文件不存在时用）。</summary>
    internal bool TryRestoreFromBackup(out string failureReason) =>
        TextStore.TryRestoreFromBackup(out failureReason);

    /// <summary>这个值是不是躺在**内置文本库**里（"已写回"判据的一部分）。</summary>
    internal bool ContainsValue(string value) =>
        UseTextStore
        && TextStore.Exists
        && TextStore.TryLoad(out List<string> textValues, out _)
        && textValues.Contains(value, StringComparer.Ordinal);

    /// <summary>
    /// 内置清单是主存储时，把 <c>passwords.txt</c> + 元数据读进列表状态。
    ///
    /// <para>返回值 = 调用方该不该直接收工：<see cref="PasswordListLoadStatus.Loaded"/>（读成功）/
    /// <see cref="PasswordListLoadStatus.NoFile"/>（加密文件解不开 → 本次禁止写回）；
    /// <c>null</c> = 这里没处理，回落到旧的 DPAPI 记忆。</para>
    /// </summary>
    internal PasswordListLoadStatus? TryLoadIntoState()
    {
        try
        {
            if (!TextStore.TryLoad(out List<string> textValues, out _))
            {
                return null;
            }

            // 旧明文密码本读入后立即升级为 DPAPI 密文；写失败也不阻断本次启动，
            // 下一次保存或启动会再试。
            if (TextStore.EncryptAtRest && !TextStore.LastLoadWasEncrypted)
            {
                if (!TextStore.TrySave(textValues, out string upgradeError))
                {
                    _health.Warn("密码本加密升级失败，仍在使用旧明文文件：" + upgradeError);
                }
            }

            bool hasMetadata = MetadataStore.TryLoad(
                out Dictionary<string, PasswordMetadataEntry> metadata,
                out string metadataFailure);

            if (!hasMetadata && metadataFailure.Length > 0)
            {
                _health.Warn("密码启用状态 / 备注读取失败，已按启用、无备注恢复：" + metadataFailure);
            }

            _state.ClearOrphanMetadata();

            if (hasMetadata)
            {
                var currentValues = new HashSet<string>(textValues, StringComparer.Ordinal);

                foreach (var pair in metadata)
                {
                    if (!currentValues.Contains(pair.Key))
                    {
                        _state.SetOrphanMetadata(pair.Key, pair.Value);
                    }
                }
            }

            _state.Suspend();

            try
            {
                _state.Passwords.Clear();

                foreach (string value in textValues)
                {
                    metadata.TryGetValue(value, out PasswordMetadataEntry? entry);

                    _state.Passwords.Add(new PasswordItem
                    {
                        Value = value,
                        Source = "BuiltInText",
                        IsEnabled = entry?.IsEnabled ?? true,
                        Remark = entry?.Remark ?? string.Empty
                    });
                }
            }
            finally
            {
                _state.Resume();
            }

            _state.RemoveDuplicatePasswordsKeepOrder();

            return PasswordListLoadStatus.Loaded;
        }
        catch (Exception ex)
        {
            if (TextStore.LastLoadWasEncryptedFailure)
            {
                /*
                 * 加密密码本解不开（换 Windows 用户/机器、文件损坏）时绝不能回落到旧 DPAPI
                 * 记忆再覆盖它；本次禁止写回，用户可以用备份/导出恢复。
                 */
                _health.TextStoreWriteBlocked = true;
                _health.Warn(
                    "内置密码本无法解密（可能换了 Windows 用户/机器，或文件被改动）；"
                    + "本次不写回任何密码文件，原文件保留。");

                return PasswordListLoadStatus.NoFile;
            }

            _health.Warn("内置密码清单读取失败，已回落到旧的密码记忆：" + ex.Message);

            return null;
        }
    }

    /// <summary>
    /// 把列表写成内置文本库：<c>passwords.txt</c> 在前、元数据在后（与旧口径逐字一致）。
    /// 返回空串 = 成功或未启用；否则是给用户的失败原因（已带"哪一步失败"的前缀）。
    /// </summary>
    internal string Save(PasswordListState state)
    {
        if (!UseTextStore)
        {
            return string.Empty;
        }

        // 内置 txt 是主存储：顺序 = 当前列表顺序（上下移动后立即写）。
        if (!TextStore.TrySave(state.Passwords.Select(item => item.Value), out string textFailure))
        {
            return "内置密码清单写入失败：" + textFailure;
        }

        var metadataEntries = new List<PasswordMetadataEntry>(
            state.Passwords.Select(item => new PasswordMetadataEntry
            {
                Value = item.Value ?? string.Empty,
                IsEnabled = item.IsEnabled,
                Remark = item.Remark ?? string.Empty
            }));

        if (state.OrphanMetadata.Count > 0)
        {
            var currentValues = new HashSet<string>(
                metadataEntries.Select(entry => entry.Value),
                StringComparer.Ordinal);

            foreach (var pair in state.OrphanMetadata)
            {
                if (!currentValues.Contains(pair.Key))
                {
                    metadataEntries.Add(pair.Value);
                }
            }
        }

        if (!MetadataStore.TrySave(metadataEntries, out string metadataFailure))
        {
            return "密码启用状态 / 备注写入失败：" + metadataFailure;
        }

        return string.Empty;
    }
}
