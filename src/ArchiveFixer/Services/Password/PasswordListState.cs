using ArchiveFixer.BindingModels;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace ArchiveFixer.Services.Password;

/// <summary>
/// 密码列表的**内存状态**：条目集合、顺序编辑、墓碑、以及"每条值上一次是什么"的追踪。
///
/// <para>它只管"列表现在是什么样"，完全不碰磁盘：任何会被记住的变更（集合增删/移动、
/// 条目属性变化、墓碑新增/撤销、密码本清单变化由调用方显式调用 <see cref="NotifyChanged"/>）
/// 都通过 <see cref="Changed"/> 交给 <see cref="PasswordListMemory"/> 决定要不要落盘。</para>
///
/// <para><b>批量编辑</b>（加载记忆、导入/合并密码本、加一条密码）用
/// <see cref="Suspend"/>/<see cref="Resume"/> 包住：期间仍然完整执行孤儿元数据与 lastKnown 值追踪
/// （那是内存事实，和写不写盘无关），只是不触发落盘通知 —— 等价于原来的 <c>_persistSuspensions</c>。
/// 批量结束后由操作方显式 <see cref="NotifyChanged"/> 一次，于是"一次操作一次落盘"的口径不变。</para>
/// </summary>
internal sealed class PasswordListState
{
    private readonly List<string> _tombstones = new();

    private readonly Dictionary<PasswordItem, string> _lastKnownValues = new();

    /// <summary>
    /// passwords.meta.json 里存在、但当前 passwords.txt 列表里暂时没有的条目。
    /// 用户删掉/改掉一条密码时不能把它的备注和启用状态一起静默丢掉；值以后重新出现时可以恢复。
    /// </summary>
    private readonly Dictionary<string, PasswordMetadataEntry> _orphanMetadata =
        new(StringComparer.Ordinal);

    private int _notificationSuspensions;

    internal PasswordListState()
    {
        /*
         * 变更即落盘（用户 2026-09-24 要求：内容 / 顺序 / 启用状态 / 手工条目跨重启保留）。
         *
         * 为什么挂在集合的 CollectionChanged 上、而不是逐个改这些方法：
         * 「启用 / 禁用」那两档是界面直接把 PasswordItem.IsEnabled 改掉的（复选框两向绑定），
         * 服务这边没有任何方法入口 —— 只改方法的写法会漏掉这一种，用户勾一下重启就白勾。
         */
        Passwords.CollectionChanged += Passwords_CollectionChanged;
    }

    internal ObservableCollection<PasswordItem> Passwords { get; } = new();

    /// <summary>墓碑（副本，只读；供测试与排障核对）。</summary>
    internal IReadOnlyList<string> Tombstones => _tombstones;

    /// <summary>暂时不在列表里的值 → 它的启用状态/备注（保存时原样合并回元数据）。</summary>
    internal IReadOnlyDictionary<string, PasswordMetadataEntry> OrphanMetadata => _orphanMetadata;

    /// <summary>列表（或别的会被记住的事实）变了，订阅方负责落盘。挂起期间不发。</summary>
    internal event Action? Changed;

    /// <summary>
    /// 当前是否在批量编辑里（原来 <c>_persistSuspensions &gt; 0</c> 那道闸门）。
    /// 订阅方落盘前也要查它：并行解压的线程可能在批量编辑期间记账，那一笔同样不该写盘。
    /// </summary>
    internal bool IsChangeSuspended => _notificationSuspensions > 0;

    /// <summary>开始批量编辑：期间的变更不触发 <see cref="Changed"/>。</summary>
    internal void Suspend()
    {
        _notificationSuspensions++;
    }

    /// <summary>结束批量编辑（总是与 <see cref="Suspend"/> 配对）。</summary>
    internal void Resume()
    {
        if (_notificationSuspensions > 0)
        {
            _notificationSuspensions--;
        }
    }

    /// <summary>显式报告一次会被记住的变更（批量编辑结束后、或书清单变化时由操作方调用）。</summary>
    internal void NotifyChanged()
    {
        if (_notificationSuspensions == 0)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>记一条墓碑（值被用户删掉 / 清空）。</summary>
    internal void RecordTombstone(string? value)
    {
        value ??= string.Empty;

        if (!_tombstones.Contains(value, StringComparer.Ordinal))
        {
            _tombstones.Add(value);
        }
    }

    /// <summary>撤销墓碑（用户又把这个值加回来了：导入与手动添加都算）。</summary>
    internal void ForgetTombstone(string? value)
    {
        value ??= string.Empty;
        _tombstones.RemoveAll(existing => string.Equals(existing, value, StringComparison.Ordinal));
    }

    /// <summary>整份替换墓碑（从记忆读回时用；不触发落盘通知）。</summary>
    internal void ReplaceTombstones(IEnumerable<string> values)
    {
        _tombstones.Clear();
        _tombstones.AddRange(values);
    }

    /// <summary>清空孤儿元数据（内置文本清单重新接管时用）。</summary>
    internal void ClearOrphanMetadata()
    {
        _orphanMetadata.Clear();
    }

    /// <summary>记一条暂时不在列表里的元数据（读元数据文件时用）。</summary>
    internal void SetOrphanMetadata(string value, PasswordMetadataEntry entry)
    {
        _orphanMetadata[value] = entry;
    }

    /// <summary>手动添加一条密码；已存在（按值精确比较）返回 false。</summary>
    internal bool Add(string password)
    {
        password ??= string.Empty;

        if (Passwords.Any(x =>
                string.Equals(x.Value, password, StringComparison.Ordinal)))
        {
            return false;
        }

        Suspend();

        try
        {
            // 用户重新加回一个删过的值 = 他改主意了：墓碑要撤掉，否则下次启动又被书里的旧值吃掉。
            ForgetTombstone(password);

            Passwords.Add(new PasswordItem
            {
                Value = password,
                Source = "ManualList",
                IsEnabled = true,
                Remark = "手动添加"
            });

            RemoveDuplicatePasswordsKeepOrder();
        }
        finally
        {
            Resume();
        }

        NotifyChanged();

        return true;
    }

    /// <summary>删除一条密码（先记墓碑再删，顺序见调用处注释）。</summary>
    internal bool Remove(PasswordItem item)
    {
        if (item == null)
        {
            return false;
        }

        /*
         * 先记墓碑、再删。
         *
         * 顺序不能反：删除会让集合变更处理器立刻落盘，而墓碑必须在那一次落盘里就位 ——
         * 反过来的话，落盘的是一份"这条没了、墓碑也没有"的记忆，重启时密码本把它原样加回来，
         * 用户看到的就是"删了又活"。
         */
        RecordTombstone(item.Value);

        return Passwords.Remove(item);
    }

    /// <summary>清空列表：所有被清掉的值都进墓碑。</summary>
    internal void Clear()
    {
        // 清空同理：**所有**被清掉的值都要进墓碑，否则重启整份密码本又回来了。
        foreach (PasswordItem item in Passwords)
        {
            RecordTombstone(item?.Value);
        }

        Passwords.Clear();
    }

    internal bool MoveUp(PasswordItem item)
    {
        if (item == null)
        {
            return false;
        }

        int index = Passwords.IndexOf(item);

        if (index <= 0)
        {
            return false;
        }

        // Move 也是集合变更 → 变更处理器会立刻落盘（顺序就是尝试顺序，必须跨重启保留）。
        Passwords.Move(index, index - 1);

        return true;
    }

    internal bool MoveDown(PasswordItem item)
    {
        if (item == null)
        {
            return false;
        }

        int index = Passwords.IndexOf(item);

        if (index < 0 || index >= Passwords.Count - 1)
        {
            return false;
        }

        Passwords.Move(index, index + 1);

        return true;
    }

    /// <summary>按值去重、保留首次出现的位置（顺序就是尝试顺序）。</summary>
    internal void RemoveDuplicatePasswordsKeepOrder()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<PasswordItem>();

        foreach (PasswordItem item in Passwords)
        {
            string value = item.Value ?? string.Empty;

            if (used.Add(value))
            {
                unique.Add(item);
            }
        }

        if (unique.Count == Passwords.Count)
        {
            return;
        }

        Passwords.Clear();

        foreach (PasswordItem item in unique)
        {
            Passwords.Add(item);
        }
    }

    private void Passwords_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (PasswordItem item in e.OldItems)
            {
                item.PropertyChanged -= PasswordItem_PropertyChanged;

                // 删除/清空后，原值的启用状态与备注继续留在元数据里，
                // 以后同一个值再加回来时还能恢复；不在这里静默丢掉。
                string oldValue = _lastKnownValues.TryGetValue(item, out string? tracked)
                    ? tracked
                    : item.Value;

                if (!string.IsNullOrEmpty(oldValue))
                {
                    _orphanMetadata[oldValue] = new PasswordMetadataEntry
                    {
                        Value = oldValue,
                        IsEnabled = item.IsEnabled,
                        Remark = item.Remark ?? string.Empty
                    };
                }

                _lastKnownValues.Remove(item);
            }
        }

        if (e.NewItems != null)
        {
            foreach (PasswordItem item in e.NewItems)
            {
                item.PropertyChanged -= PasswordItem_PropertyChanged;
                item.PropertyChanged += PasswordItem_PropertyChanged;
                _lastKnownValues[item] = item.Value ?? string.Empty;
            }
        }

        // 增 / 删 / 清空 / 上下移（Move 也是集合变更）统统落一次盘：顺序就是尝试顺序，必须留住。
        NotifyChanged();
    }

    private void PasswordItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        /*
         * 启用开关与备注是**界面直接改**的（没有走服务的方法），所以这里必须跟着落盘。
         * 值（Value）也一起管：用户改了一条密码的值，重启后当然该是改过的那个。
         */
        if (e.PropertyName is nameof(PasswordItem.IsEnabled)
            or nameof(PasswordItem.Value)
            or nameof(PasswordItem.Remark)
            or nameof(PasswordItem.Source))
        {
            if (e.PropertyName == nameof(PasswordItem.Value) && sender is PasswordItem changed)
            {
                string oldValue = _lastKnownValues.TryGetValue(changed, out string? tracked)
                    ? tracked
                    : string.Empty;

                string newValue = changed.Value ?? string.Empty;

                if (!string.Equals(oldValue, newValue, StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(oldValue))
                {
                    _orphanMetadata[oldValue] = new PasswordMetadataEntry
                    {
                        Value = oldValue,
                        IsEnabled = changed.IsEnabled,
                        Remark = changed.Remark ?? string.Empty
                    };
                }

                _lastKnownValues[changed] = newValue;
            }

            NotifyChanged();
        }
    }
}
