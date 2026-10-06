using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Nodes;
using CleanBoost.Features.LargeOldFiles;

namespace CleanBoost.App.Features.LargeOldFiles;

/// <summary>Một dòng trong bảng File lớn &amp; cũ.</summary>
public sealed class LargeOldRow(LargeOldItem item, Func<Guid, bool> isSelected) : INotifyPropertyChanged
{
    private static readonly Brush OldBrush = Theme.Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));

    public LargeOldItem Item { get; } = item;
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path => Item.Path;
    public string Name => Item.Name;
    public string FolderText { get; } = item.Folder.AbbreviatingHome();
    public string SizeText => Item.Size.Formatted;
    public string DateText { get; } = FileDates.Medium(item.LastUsedUtc);
    public Brush DateBrush => Item.IsOld ? OldBrush : Brushes.White;
    public string KindText => Item.KindTitle;
    public Visibility HiddenVisibility => Item.IsHidden ? Visibility.Visible : Visibility.Collapsed;
    public string HiddenLabel => LargeOldBadges.Hidden;
    public ImageSource? Icon => ShellIcons.For(Item.Path, isDirectory: false);
    public bool IsChecked => isSelected(Item.Id);

    public void RefreshCheck() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
}

/// <summary>
/// Thân màn kết quả File lớn &amp; cũ (LargeOldResultsView của bản Mac): thanh lọc (loại, dung lượng, thời gian, thư mục, tên),
/// bảng sắp xếp được theo cột, chân trang chọn tay và "Chuyển vào Thùng rác". Không có gì được chọn sẵn.
/// </summary>
public partial class LargeOldResultsBody : UserControl
{
    private enum SortColumn { Name, Folder, Size, LastUsed, Kind }

    private readonly LargeOldFilesViewModel _vm;
    private readonly SelectionFooter _footer;
    private readonly Dictionary<Guid, LargeOldRow> _rowCache = [];
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private List<LargeOldRow> _visible = [];
    private SortColumn _sort = SortColumn.Size;
    private bool _descending = true;
    private bool _suppressFilter;
    private SelectionState? _observedSelection;
    private ScrollViewer? _scroller;

    /// <summary>Gọi sau mỗi lần danh sách hiện thay đổi (để màn hình cập nhật dòng phụ đề).</summary>
    public event Action? VisibleChanged;

    public int VisibleCount => _visible.Count;

    public LargeOldResultsBody(LargeOldFilesViewModel vm, string cleanTitle)
    {
        InitializeComponent();
        _vm = vm;
        SearchPlaceholder.Text = Loc.T("Tìm theo tên");
        NoMatchText.Text = Loc.T("Không có file nào khớp bộ lọc");
        Table.ContextMenu = FileActions.Menu(() => Table.SelectedItems.Cast<LargeOldRow>().Select(r => r.Path).ToList());

        _suppressFilter = true;
        foreach (var range in Enum.GetValues<LargeOldFilter.SizeRange>())
            SizeCombo.Items.Add(new ComboBoxItem { Content = LargeOldFilter.Title(range), Tag = range });
        foreach (var range in Enum.GetValues<LargeOldFilter.AgeRange>())
            AgeCombo.Items.Add(new ComboBoxItem { Content = LargeOldFilter.Title(range), Tag = range });
        SizeCombo.SelectedIndex = 0;
        AgeCombo.SelectedIndex = 0;
        _suppressFilter = false;

        _footer = new SelectionFooter(cleanTitle, vm.DryRun);
        var selectVisible = _footer.AddButton(Loc.T("Chọn tất cả đang hiện"));
        selectVisible.Click += (_, _) => _vm.Selection.SetMany(_visible.Select(r => r.Item.Id), true, _vm.Tree);
        _footer.Deselect.Click += (_, _) => _vm.Selection.Clear();
        _footer.Primary.Click += async (_, _) =>
        {
            if (_vm.PrepareCleanCommand.CanExecute(null)) await _vm.PrepareCleanAsync();
        };
        FooterHost.Content = _footer;

        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _vm.Filter.Search = SearchBox.Text.Trim();
            Refresh();
        };
        Loaded += (_, _) => HookScroller();
        _vm.ItemsChanged += Reload;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_vm.Selection)) ObserveSelection();
        };
        ObserveSelection();
        UpdateHeaders();
        UpdateKindText();
        Reload();
    }

    // MARK: Dữ liệu

    /// <summary>Kết quả quét mới: dựng lại danh sách thư mục cho bộ lọc, giữ lựa chọn lọc nếu còn hợp lệ.</summary>
    private void Reload()
    {
        _rowCache.Clear();
        var previous = _vm.Filter.Folder;
        _suppressFilter = true;
        FolderCombo.Items.Clear();
        FolderCombo.Items.Add(new ComboBoxItem { Content = Loc.T("Mọi thư mục"), Tag = null });
        foreach (var folder in LargeOldFilter.FolderOptions(_vm.Items, _vm.Feature.Home, _vm.Roots.Snapshot))
            FolderCombo.Items.Add(new ComboBoxItem { Content = folder.AbbreviatingHome(), Tag = folder, ToolTip = folder });
        var match = FolderCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => previous is not null && previous.Equals(i.Tag as string, StringComparison.OrdinalIgnoreCase));
        FolderCombo.SelectedItem = match ?? FolderCombo.Items[0];
        _vm.Filter.Folder = match?.Tag as string;
        _suppressFilter = false;
        Refresh();
    }

    private void Refresh()
    {
        var now = DateTime.UtcNow;
        var items = _vm.Filter.Apply(_vm.Items, now);
        IEnumerable<LargeOldItem> sorted = _sort switch
        {
            SortColumn.Name => Order(items, i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            SortColumn.Folder => Order(items, i => i.Folder, StringComparer.CurrentCultureIgnoreCase),
            SortColumn.LastUsed => Order(items, i => i.LastUsedSortKey, Comparer<DateTime>.Default),
            SortColumn.Kind => Order(items, i => i.KindTitle, StringComparer.CurrentCultureIgnoreCase),
            _ => Order(items, i => i.Size.Bytes, Comparer<long>.Default),
        };
        _visible = sorted.Select(RowFor).ToList();
        Table.ItemsSource = _visible;
        NoMatchText.Visibility = _visible.Count == 0 && _vm.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        VisibleChanged?.Invoke();
    }

    private IOrderedEnumerable<LargeOldItem> Order<TKey>(IEnumerable<LargeOldItem> items, Func<LargeOldItem, TKey> key, IComparer<TKey> comparer)
    {
        var first = _descending ? items.OrderByDescending(key, comparer) : items.OrderBy(key, comparer);
        return first.ThenByDescending(i => i.Size.Bytes);
    }

    private LargeOldRow RowFor(LargeOldItem item)
    {
        if (!_rowCache.TryGetValue(item.Id, out var row))
        {
            row = new LargeOldRow(item, id => _vm.Selection.IsSelected(id));
            _rowCache[item.Id] = row;
        }
        return row;
    }

    // MARK: Chọn

    private void ObserveSelection()
    {
        if (_observedSelection is not null) _observedSelection.Changed -= OnSelectionChanged;
        _observedSelection = _vm.Selection;
        _observedSelection.Changed += OnSelectionChanged;
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        foreach (var row in _rowCache.Values) row.RefreshCheck();
        _footer.Update(_vm.Selection.SelectedBytes, _vm.Selection.SelectedCount);
    }

    private void OnRowCheck(object? sender, EventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LargeOldRow row) _vm.Selection.Toggle(row.Item.Id, _vm.Tree);
    }

    private void OnTableKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Table.SelectedItems.Count == 0) return;
        var rows = Table.SelectedItems.Cast<LargeOldRow>().ToList();
        var allOn = rows.All(r => r.IsChecked);
        _vm.Selection.SetMany(rows.Select(r => r.Item.Id), !allOn, _vm.Tree);
        e.Handled = true;
    }

    private void OnTableDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is LargeOldRow row) FileActions.Reveal(row.Path);
    }

    // MARK: Lọc

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilter) return;
        if (SizeCombo.SelectedItem is ComboBoxItem { Tag: LargeOldFilter.SizeRange size }) _vm.Filter.Size = size;
        if (AgeCombo.SelectedItem is ComboBoxItem { Tag: LargeOldFilter.AgeRange age }) _vm.Filter.Age = age;
        if (FolderCombo.SelectedItem is ComboBoxItem folder) _vm.Filter.Folder = folder.Tag as string;
        Refresh();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    /// <summary>Menu loại file: "Mọi loại" bỏ lọc, mỗi loại bật/tắt (có dấu tích).</summary>
    private void OnKindMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = KindButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var all = new MenuItem { Header = Loc.T("Mọi loại"), IsCheckable = true, IsChecked = _vm.Filter.Kinds.Count == 0 };
        all.Click += (_, _) =>
        {
            _vm.Filter.Kinds.Clear();
            UpdateKindText();
            Refresh();
        };
        menu.Items.Add(all);
        menu.Items.Add(new Separator());
        foreach (var kind in FileKinds.All)
        {
            var item = new MenuItem { Header = kind.Title(), IsCheckable = true, IsChecked = _vm.Filter.Kinds.Contains(kind), StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                if (!_vm.Filter.Kinds.Remove(kind)) _vm.Filter.Kinds.Add(kind);
                item.IsChecked = _vm.Filter.Kinds.Contains(kind);
                all.IsChecked = _vm.Filter.Kinds.Count == 0;
                UpdateKindText();
                Refresh();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void UpdateKindText()
    {
        var kinds = _vm.Filter.Kinds;
        KindText.Text = kinds.Count == 0 ? Loc.T("Mọi loại") : string.Join(", ", kinds.Select(k => k.Title()).Order(StringComparer.CurrentCulture));
        KindButton.ToolTip = kinds.Count > 1 ? KindText.Text : null;
    }

    // MARK: Sắp xếp

    private void OnSortName(object sender, RoutedEventArgs e) => SortBy(SortColumn.Name);
    private void OnSortFolder(object sender, RoutedEventArgs e) => SortBy(SortColumn.Folder);
    private void OnSortSize(object sender, RoutedEventArgs e) => SortBy(SortColumn.Size);
    private void OnSortDate(object sender, RoutedEventArgs e) => SortBy(SortColumn.LastUsed);
    private void OnSortKind(object sender, RoutedEventArgs e) => SortBy(SortColumn.Kind);

    /// <summary>Bấm lại cột đang sắp thì đảo chiều; cột mới: chữ tăng dần, dung lượng/ngày giảm dần.</summary>
    private void SortBy(SortColumn column)
    {
        if (_sort == column) _descending = !_descending;
        else
        {
            _sort = column;
            _descending = column is SortColumn.Size or SortColumn.LastUsed;
        }
        UpdateHeaders();
        Refresh();
    }

    private void UpdateHeaders()
    {
        Header(NameHeader, Loc.T("Tên"), SortColumn.Name);
        Header(FolderHeader, Loc.T("Thư mục"), SortColumn.Folder);
        Header(SizeHeader, Loc.T("Dung lượng"), SortColumn.Size);
        Header(DateHeader, Loc.T("Lần dùng cuối"), SortColumn.LastUsed);
        Header(KindHeader, Loc.T("Loại"), SortColumn.Kind);
    }

    private void Header(Button button, string title, SortColumn column)
    {
        var active = _sort == column;
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        stack.Children.Add(new TextBlock { Text = title, Foreground = active ? Brushes.White : FileListUi.Res("SecondaryText") });
        if (active) stack.Children.Add(new Icon { Symbol = _descending ? "chevron.down" : "chevron.up", Size = 9, Margin = new Thickness(6, 1, 0, 0), Foreground = Brushes.White });
        button.Content = stack;
    }

    // MARK: Căn tiêu đề cột theo thanh cuộn

    private void HookScroller()
    {
        if (_scroller is not null) return;
        Table.ApplyTemplate();
        _scroller = Table.Template.FindName("Scroller", Table) as ScrollViewer;
        if (_scroller is null) return;
        _scroller.ScrollChanged += (_, _) => AlignHeader();
        AlignHeader();
    }

    private void AlignHeader()
    {
        if (_scroller is null) return;
        // Thanh cuộn mảnh rộng 8 (Styles.xaml): khi hiện, cột của dòng hẹp lại đúng bấy nhiêu.
        HeaderGrid.Margin = new Thickness(0, 0, _scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible ? 8 : 0, 0);
    }
}
