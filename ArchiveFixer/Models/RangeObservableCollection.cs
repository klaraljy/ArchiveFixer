using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 支持**批量变更只通知一次**的 <see cref="ObservableCollection{T}"/>。
    ///
    /// <para><b>为什么需要它</b>（用户 2026-09-24 第 12 条："一键处理直接就卡死机了"、第 19.2 条同类现象）：
    /// 导入一个文件夹动辄几百个包，旧代码是逐个 <c>Tasks.Add(task)</c> —— 几百次
    /// <see cref="INotifyCollectionChanged.CollectionChanged"/>，而每一次界面（DataGrid）都要
    /// 处理一遍行集合变化、重新求值绑定。几百项时界面线程被这一串通知拖着走，
    /// 用户看到的就是"卡死"。这里把一批变更合并成**一次 Reset 通知**。</para>
    ///
    /// <para><b>代价（如实写明）</b>：Reset 通知不带"哪一项"的信息，WPF 会当成"整份变了"处理 ——
    /// 对虚拟化的 DataGrid 来说只重建**可见行**，代价远低于几百次增量通知；
    /// 但调用方不能再依赖"某一项被单独通知过"。需要逐项语义的操作（上下移、单项替换）
    /// 请照旧用 <see cref="ObservableCollection{T}"/> 自己的方法。</para>
    ///
    /// <para>它不含任何 WPF 类型（只是 <c>System.Collections.ObjectModel</c>），所以放在 Models 下也合规。</para>
    /// </summary>
    /// <typeparam name="T">元素类型。</typeparam>
    public class RangeObservableCollection<T> : ObservableCollection<T>
    {
        /// <summary>挂起期间不发通知（批量操作内部用）。</summary>
        private bool _suppressNotification;

        public RangeObservableCollection()
        {
        }

        public RangeObservableCollection(IEnumerable<T> items)
            : base(items)
        {
        }

        /// <summary>
        /// 追加一批，**只发一次通知**（Reset）。
        ///
        /// <para>空集合时不发通知：没有变化却让界面重建一遍是纯浪费。</para>
        /// </summary>
        public void AddRange(IEnumerable<T>? items)
        {
            List<T> list = Materialize(items);

            if (list.Count == 0)
            {
                return;
            }

            RunSuppressed(() =>
            {
                foreach (T item in list)
                {
                    Add(item);
                }
            });
        }

        /// <summary>
        /// 整份替换（清空 + 填入），**只发一次通知**（Reset）。
        ///
        /// <para>替换成"一模一样的内容"时也要发通知：调用方（导入收尾）依赖这一次通知
        /// 把"列表已经换过了"传播出去；省掉它反而会让界面停在旧内容上。</para>
        /// </summary>
        public void ReplaceAll(IEnumerable<T>? items)
        {
            List<T> list = Materialize(items);

            if (list.Count == 0 && Count == 0)
            {
                // 本来是空的、要放的也是空的：确实什么都没变。
                return;
            }

            RunSuppressed(() =>
            {
                Clear();

                foreach (T item in list)
                {
                    Add(item);
                }
            });
        }

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            if (_suppressNotification)
            {
                return;
            }

            base.OnCollectionChanged(e);
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            if (_suppressNotification)
            {
                return;
            }

            base.OnPropertyChanged(e);
        }

        private static List<T> Materialize(IEnumerable<T>? items)
        {
            if (items == null)
            {
                return new List<T>();
            }

            return items.Where(item => item != null).ToList();
        }

        /// <summary>
        /// 挂起逐项通知 → 执行批量变更 → 恢复 → 补发**一次** Reset。
        ///
        /// <para>补发的三个属性名（<c>Count</c> / <c>Item[]</c> / Reset 事件）与
        /// <see cref="ObservableCollection{T}"/> 自己的做法一致，少一个都可能让某个绑定不刷新。</para>
        /// </summary>
        private void RunSuppressed(Action changeItems)
        {
            _suppressNotification = true;

            try
            {
                changeItems();
            }
            finally
            {
                _suppressNotification = false;
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
