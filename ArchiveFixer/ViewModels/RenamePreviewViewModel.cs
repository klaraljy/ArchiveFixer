using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace ArchiveFixer.ViewModels
{
    public class RenamePreviewViewModel : INotifyPropertyChanged
    {
        private bool? _dialogResult;
        private RenamePreviewItem? _selectedItem;
        private string _message = string.Empty;

        public ObservableCollection<RenamePreviewItem> Items { get; }

        public ObservableCollection<RenamePreviewItem> PreviewItems => Items;

        public RenamePreviewItem? SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (_selectedItem != value)
                {
                    _selectedItem = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool? DialogResult
        {
            get => _dialogResult;
            private set
            {
                if (_dialogResult != value)
                {
                    _dialogResult = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Message
        {
            get => _message;
            set
            {
                if (_message != value)
                {
                    _message = value ?? string.Empty;
                    OnPropertyChanged();
                }
            }
        }

        public int TotalCount => Items.Count;

        public int SelectedCount => Items.Count(x => x.IsSelected);

        public int CanRenameCount => Items.Count(CanItemRename);

        public int ConflictCount => Items.Count(x =>
            string.Equals(x.Status, StatusText.TargetExists, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Status, StatusText.WillAutoRename, StringComparison.OrdinalIgnoreCase));

        public int SkipCount => Items.Count(x =>
            string.Equals(x.Status, StatusText.RenameWillSkip, StringComparison.OrdinalIgnoreCase));

        public bool HasExecutableItems => Items.Any(x => x.IsSelected && CanItemRename(x));

        public ICommand SelectAllCommand { get; }

        public ICommand SelectNoneCommand { get; }

        public ICommand InvertSelectionCommand { get; }

        public ICommand SelectCanRenameCommand { get; }

        public ICommand ConfirmCommand { get; }

        public ICommand CancelCommand { get; }

        public RenamePreviewViewModel()
        {
            Items = new ObservableCollection<RenamePreviewItem>();

            SelectAllCommand = new RelayCommand(_ => SelectAll());
            SelectNoneCommand = new RelayCommand(_ => SelectNone());
            InvertSelectionCommand = new RelayCommand(_ => InvertSelection());
            SelectCanRenameCommand = new RelayCommand(_ => SelectCanRename());
            ConfirmCommand = new RelayCommand(_ => Confirm(), _ => HasExecutableItems);
            CancelCommand = new RelayCommand(_ => Cancel());

            Items.CollectionChanged += (_, e) =>
            {
                if (e.OldItems != null)
                {
                    foreach (RenamePreviewItem item in e.OldItems)
                    {
                        item.PropertyChanged -= Item_PropertyChanged;
                    }
                }

                if (e.NewItems != null)
                {
                    foreach (RenamePreviewItem item in e.NewItems)
                    {
                        item.PropertyChanged -= Item_PropertyChanged;
                        item.PropertyChanged += Item_PropertyChanged;
                    }
                }

                RefreshStatistics();
            };
        }

        public RenamePreviewViewModel(IEnumerable<RenamePreviewItem> items)
            : this()
        {
            LoadItems(items);
        }

        public void LoadItems(IEnumerable<RenamePreviewItem>? items)
        {
            foreach (RenamePreviewItem item in Items)
            {
                item.PropertyChanged -= Item_PropertyChanged;
            }

            Items.Clear();

            if (items != null)
            {
                foreach (RenamePreviewItem item in items)
                {
                    if (item.Status == StatusText.RenameCannot || item.Status == StatusText.RenameWillSkip)
                    {
                        item.IsSelected = false;
                    }

                    item.PropertyChanged -= Item_PropertyChanged;
                    item.PropertyChanged += Item_PropertyChanged;

                    Items.Add(item);
                }
            }

            Message = Items.Count == 0
                ? "没有可预览的改名项。"
                : $"已生成 {Items.Count} 个改名预览项。";

            RefreshStatistics();
        }

        public List<RenamePreviewItem> GetSelectedItems()
        {
            return Items
                .Where(x => x.IsSelected && CanItemRename(x))
                .ToList();
        }

        public void SelectAll()
        {
            foreach (RenamePreviewItem item in Items)
            {
                item.IsSelected = CanItemRename(item);
            }

            Message = "已选择所有可改名项。";
            RefreshStatistics();
        }

        public void SelectNone()
        {
            foreach (RenamePreviewItem item in Items)
            {
                item.IsSelected = false;
            }

            Message = "已取消全部选择。";
            RefreshStatistics();
        }

        public void InvertSelection()
        {
            foreach (RenamePreviewItem item in Items)
            {
                if (CanItemRename(item) || item.IsSelected)
                {
                    item.IsSelected = !item.IsSelected;
                }
            }

            Message = "已反选。";
            RefreshStatistics();
        }

        public void SelectCanRename()
        {
            foreach (RenamePreviewItem item in Items)
            {
                item.IsSelected = CanItemRename(item);
            }

            Message = "已仅选择可改名项。";
            RefreshStatistics();
        }

        public void Confirm()
        {
            ValidateSelectedItems();

            RefreshStatistics();

            List<RenamePreviewItem> selected = GetSelectedItems();

            if (selected.Count == 0)
            {
                Message = "没有可执行的改名项。请检查状态和错误信息。";
                return;
            }

            Message = $"确认改名 {selected.Count} 个文件。";
            DialogResult = true;
        }

        public void Cancel()
        {
            DialogResult = false;
        }

        private void ValidateSelectedItems()
        {
            foreach (RenamePreviewItem item in Items)
            {
                if (!item.IsSelected)
                {
                    continue;
                }

                item.SyncNewPathFromNewFileName();

                if (!CanItemRename(item))
                {
                    item.IsSelected = false;
                }
            }
        }

        private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(RenamePreviewItem.IsSelected)
                or nameof(RenamePreviewItem.Status)
                or nameof(RenamePreviewItem.NewPath)
                or nameof(RenamePreviewItem.NewFileName)
                or nameof(RenamePreviewItem.CanRename)
                or nameof(RenamePreviewItem.ErrorMessage))
            {
                RefreshStatistics();
            }
        }

        private static bool CanItemRename(RenamePreviewItem item)
        {
            if (item == null)
            {
                return false;
            }

            if (string.Equals(item.Status, StatusText.RenameCannot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(item.Status, StatusText.RenameWillSkip, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.OriginalPath))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(item.NewPath))
            {
                return false;
            }

            if (string.Equals(item.OriginalPath, item.NewPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private void RefreshStatistics()
        {
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(CanRenameCount));
            OnPropertyChanged(nameof(ConflictCount));
            OnPropertyChanged(nameof(SkipCount));
            OnPropertyChanged(nameof(HasExecutableItems));

            if (ConfirmCommand is RelayCommand relayCommand)
            {
                relayCommand.RaiseCanExecuteChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private sealed class RelayCommand : ICommand
        {
            private readonly Action<object?> _execute;
            private readonly Predicate<object?>? _canExecute;

            public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public bool CanExecute(object? parameter)
            {
                return _canExecute == null || _canExecute(parameter);
            }

            public void Execute(object? parameter)
            {
                _execute(parameter);
            }

            public event EventHandler? CanExecuteChanged;

            public void RaiseCanExecuteChanged()
            {
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
