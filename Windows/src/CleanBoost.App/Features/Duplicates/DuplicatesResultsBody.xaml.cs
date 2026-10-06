using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CleanBoost.App.Features.LargeOldFiles;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Nodes;
using CleanBoost.Features.Duplicates;

namespace CleanBoost.App.Features.Duplicates;

/// <summary>Một nhóm ở cột trái: checkbox ba trạng thái, biểu tượng, tên, "5 bản · Mỗi bản 27,5 MB", dung lượng lãng phí.</summary>
public sealed class DuplicateGroupRow(Node group, Func<Node, CheckState> state) : INotifyPropertyChanged
{
    public Node Group { get; } = group;
    public event PropertyChangedEventHandler? PropertyChanged;

    private Node? First => Group.Children.FirstOrDefault();
    public string Title => First?.Title ?? Group.Title;
    public string Subtitle => Loc.F("%lld bản · %@", Group.Children.Count, Group.Reason.Resolved);
    public string WastedText { get; } = DuplicateSelection.Wasted(group).Formatted;
    public ImageSource? Icon => ShellIcons.For(First?.Path, isDirectory: false, large: true);
    public bool? IsChecked => CheckStates.ToNullable(state(Group));

    public void RefreshCheck() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
}

/// <summary>Một bản trong nhóm: checkbox, biểu tượng, tên + nhãn "Nên giữ", thư mục chứa, ngày sửa.</summary>
public sealed class DuplicateFileRow(Node file, Func<Guid, bool> isSelected) : INotifyPropertyChanged
{
    public Node File { get; } = file;
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path => File.Path ?? "";
    public string Title => File.Title;
    public string FolderText { get; } = (System.IO.Path.GetDirectoryName(file.Path ?? "") ?? "").AbbreviatingHome();
    public string DateText { get; } = FileDates.MediumWithTime(file.LastAccessUtc);
    public Visibility KeepVisibility => DuplicateSelection.IsKeep(File) ? Visibility.Visible : Visibility.Collapsed;
    public string KeepLabel => DuplicateBadges.Keep;
    public Visibility LinkedVisibility => DuplicateSelection.IsLinked(File) ? Visibility.Visible : Visibility.Collapsed;
    public string LinkedTooltip => DuplicateBadges.HardLink;
    public ImageSource? Icon => ShellIcons.For(File.Path, isDirectory: false, large: true);
    public bool? IsChecked => isSelected(File.Id);

    public void RefreshCheck() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
}

/// <summary>
/// Thân màn kết quả File trùng lặp (DuplicatesResultsView của bản Mac): trái là các nhóm (sắp theo dung lượng lãng phí),
/// phải là chi tiết nhóm đang xem với ảnh xem trước và từng bản; chân trang có "Chọn tự động" và "Chuyển vào Thùng rác".
/// </summary>
public partial class DuplicatesResultsBody : UserControl
{
    private readonly DuplicatesViewModel _vm;
    private readonly SelectionFooter _footer;
    private List<DuplicateGroupRow> _groups = [];
    private List<DuplicateFileRow> _files = [];
    private SelectionState? _observedSelection;
    private int _thumbnailVersion;

    public DuplicatesResultsBody(DuplicatesViewModel vm, string cleanTitle)
    {
        InitializeComponent();
        _vm = vm;
        ThumbPlaceholder.Text = Icons.Glyph("doc");
        FileList.ContextMenu = FileActions.Menu(() => FileList.SelectedItems.Cast<DuplicateFileRow>().Select(r => r.Path).ToList());
        GroupList.ContextMenu = FileActions.Menu(() => (GroupList.SelectedItem as DuplicateGroupRow)?.Group.Children.FirstOrDefault()?.Path is { } p ? [p] : []);

        _footer = new SelectionFooter(cleanTitle, vm.DryRun);
        var auto = _footer.AddButton(Loc.T("Chọn tự động"), Loc.T("Chọn mọi bản trừ bản nên giữ trong mỗi nhóm"));
        auto.Click += (_, _) => _vm.AutoSelect();
        _footer.Deselect.Click += (_, _) => _vm.Selection.Clear();
        _footer.Primary.Click += async (_, _) => await _vm.RequestCleanAsync(Window.GetWindow(this));
        FooterHost.Content = _footer;

        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(DuplicatesViewModel.Selection):
                    ObserveSelection();
                    break;
                case nameof(DuplicatesViewModel.SortedGroups):
                    Reload();
                    break;
                case nameof(DuplicatesViewModel.FocusedGroup):
                    ShowFocused();
                    break;
            }
        };
        ObserveSelection();
        Reload();
    }

    // MARK: Dữ liệu

    private void Reload()
    {
        _groups = _vm.SortedGroups.Select(g => new DuplicateGroupRow(g, n => _vm.Selection.State(n.Id, _vm.Tree))).ToList();
        GroupList.ItemsSource = _groups;
        ShowFocused();
    }

    private void ShowFocused()
    {
        var focused = _vm.FocusedGroup;
        var row = _groups.FirstOrDefault(r => r.Group.Id == focused?.Id);
        if (!ReferenceEquals(GroupList.SelectedItem, row)) GroupList.SelectedItem = row;
        if (row is not null) GroupList.ScrollIntoView(row);
        if (focused is null)
        {
            DetailTitle.Text = DetailSubtitle.Text = DetailWasted.Text = "";
            FileList.ItemsSource = null;
            Thumb.Source = null;
            return;
        }
        var first = focused.Children.FirstOrDefault();
        DetailTitle.Text = first?.Title ?? focused.Title;
        DetailSubtitle.Text = Loc.F("%lld bản giống hệt nhau · %@", focused.Children.Count, focused.Reason.Resolved);
        DetailWasted.Text = Loc.F("Có thể giải phóng %@ nếu chỉ giữ một bản", DuplicateSelection.Wasted(focused).Formatted);
        _files = focused.Children.Select(c => new DuplicateFileRow(c, id => _vm.Selection.IsSelected(id))).ToList();
        FileList.ItemsSource = _files;
        LoadThumbnail(first?.Path);
    }

    private async void LoadThumbnail(string? path)
    {
        var version = ++_thumbnailVersion;
        Thumb.Source = null;
        ThumbPlaceholder.Visibility = Visibility.Visible;
        if (string.IsNullOrEmpty(path)) return;
        var image = await ShellThumbnail.LoadAsync(path, 256);
        if (version != _thumbnailVersion || image is null) return;
        Thumb.Source = image;
        ThumbPlaceholder.Visibility = Visibility.Collapsed;
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
        foreach (var g in _groups) g.RefreshCheck();
        foreach (var f in _files) f.RefreshCheck();
        _footer.Update(_vm.Selection.SelectedBytes, _vm.Selection.SelectedCount);
    }

    private void OnGroupFocused(object sender, SelectionChangedEventArgs e)
    {
        if (GroupList.SelectedItem is DuplicateGroupRow row && !ReferenceEquals(_vm.FocusedGroup, row.Group)) _vm.FocusedGroup = row.Group;
    }

    private void OnGroupCheck(object? sender, EventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DuplicateGroupRow row) _vm.Selection.Toggle(row.Group.Id, _vm.Tree);
    }

    private void OnFileCheck(object? sender, EventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DuplicateFileRow row) _vm.Selection.Toggle(row.File.Id, _vm.Tree);
    }

    private void OnGroupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || GroupList.SelectedItem is not DuplicateGroupRow row) return;
        _vm.Selection.Toggle(row.Group.Id, _vm.Tree);
        e.Handled = true;
    }

    private void OnFileKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || FileList.SelectedItems.Count == 0) return;
        var rows = FileList.SelectedItems.Cast<DuplicateFileRow>().ToList();
        var allOn = rows.All(r => r.IsChecked == true);
        _vm.Selection.SetMany(rows.Select(r => r.File.Id), !allOn, _vm.Tree);
        e.Handled = true;
    }

    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is DuplicateFileRow row) FileActions.Reveal(row.Path);
    }
}
