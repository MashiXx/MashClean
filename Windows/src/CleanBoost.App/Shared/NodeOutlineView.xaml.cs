using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CleanBoost.Core;
using CleanBoost.Engine.Nodes;

namespace CleanBoost.App.Shared;

/// <summary>
/// Cây kết quả (NodeOutlineView của bản Mac, vốn bọc NSOutlineView cho cây lớn): trải phẳng thành danh sách ảo hoá,
/// mỗi dòng có checkbox, biểu tượng thật của file, tên, chú thích, nhãn mức an toàn và cột dung lượng thẳng hàng.
/// Nháy đúp mở File Explorer tại mục; menu chuột phải: hiện trong Explorer, sao chép đường dẫn, không bao giờ đề xuất.
/// </summary>
public partial class NodeOutlineView : UserControl
{
    public static readonly DependencyProperty TreeProperty = DependencyProperty.Register(
        nameof(Tree), typeof(NodeTree), typeof(NodeOutlineView), new PropertyMetadata(NodeTree.Empty, (d, _) => ((NodeOutlineView)d).Rebuild()));

    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(
        nameof(Selection), typeof(SelectionState), typeof(NodeOutlineView), new PropertyMetadata(null, OnSelectionChanged));

    public static readonly DependencyProperty RootsProperty = DependencyProperty.Register(
        nameof(Roots), typeof(IEnumerable<Node>), typeof(NodeOutlineView), new PropertyMetadata(null, (d, _) => ((NodeOutlineView)d).Rebuild()));

    public static readonly DependencyProperty ShowCheckboxesProperty = DependencyProperty.Register(
        nameof(ShowCheckboxes), typeof(bool), typeof(NodeOutlineView), new PropertyMetadata(true));

    public NodeTree Tree { get => (NodeTree)GetValue(TreeProperty); set => SetValue(TreeProperty, value); }
    public SelectionState? Selection { get => (SelectionState?)GetValue(SelectionProperty); set => SetValue(SelectionProperty, value); }
    /// <summary>Node hiển thị ở cấp trên cùng (mặc định là gốc của cây).</summary>
    public IEnumerable<Node>? Roots { get => (IEnumerable<Node>?)GetValue(RootsProperty); set => SetValue(RootsProperty, value); }
    public bool ShowCheckboxes { get => (bool)GetValue(ShowCheckboxesProperty); set => SetValue(ShowCheckboxesProperty, value); }

    /// <summary>"Không bao giờ đề xuất mục này" — null thì ẩn mục menu.</summary>
    public Action<Node>? OnIgnore { get; set; }

    /// <summary>Đổi trạng thái chọn của một node (mặc định đảo trong <see cref="Selection"/>).</summary>
    public Action<Node>? OnToggle { get; set; }

    private readonly ObservableCollection<OutlineRow> _rows = [];

    public NodeOutlineView()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
        RevealItem.Header = Loc.T("Hiện trong File Explorer");
        CopyItem.Header = Loc.T("Sao chép đường dẫn");
        IgnoreItem.Header = Loc.T("Không bao giờ đề xuất mục này");
    }

    private static void OnSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (NodeOutlineView)d;
        if (e.OldValue is SelectionState old) old.Changed -= view.RefreshChecks;
        if (e.NewValue is SelectionState s) s.Changed += view.RefreshChecks;
        view.RefreshChecks();
    }

    private CheckState StateOf(Node node) => Selection?.State(node.Id, Tree) ?? CheckState.Off;

    private void Rebuild()
    {
        _rows.Clear();
        var roots = (Roots ?? Tree.Roots).ToList();
        foreach (var r in roots) _rows.Add(new OutlineRow(r, 0, StateOf));
        // Một gốc duy nhất thì mở sẵn.
        if (_rows.Count == 1 && _rows[0].HasChildren) Expand(_rows[0]);
    }

    private void RefreshChecks()
    {
        foreach (var r in _rows) r.RefreshCheck();
    }

    private void Expand(OutlineRow row)
    {
        if (row.IsExpanded || !row.HasChildren) return;
        row.IsExpanded = true;
        var index = _rows.IndexOf(row);
        var insert = index + 1;
        foreach (var child in row.Node.Children.OrderByDescending(c => c.Size.Bytes))
        {
            _rows.Insert(insert++, new OutlineRow(child, row.Depth + 1, StateOf));
        }
    }

    private void Collapse(OutlineRow row)
    {
        if (!row.IsExpanded) return;
        row.IsExpanded = false;
        var index = _rows.IndexOf(row) + 1;
        while (index < _rows.Count && _rows[index].Depth > row.Depth) _rows.RemoveAt(index);
    }

    private void OnExpanderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OutlineRow row) return;
        if (row.IsExpanded) Collapse(row);
        else Expand(row);
        e.Handled = true;
    }

    private void OnCheckClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OutlineRow row) return;
        if (OnToggle is { } toggle) toggle(row.Node);
        else Selection?.Toggle(row.Node.Id, Tree);
        // Checkbox hiển thị theo Selection, không theo trạng thái nội bộ của nó.
        row.RefreshCheck();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (List.SelectedItem is not OutlineRow row) return;
        switch (e.Key)
        {
            case Key.Space:
                foreach (var r in List.SelectedItems.Cast<OutlineRow>().ToList())
                {
                    if (OnToggle is { } toggle) toggle(r.Node);
                    else Selection?.Toggle(r.Node.Id, Tree);
                }
                e.Handled = true;
                break;
            case Key.Right:
                Expand(row);
                e.Handled = true;
                break;
            case Key.Left:
                Collapse(row);
                e.Handled = true;
                break;
        }
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not OutlineRow row) return;
        if (row.HasChildren && row.Node.IsContainer)
        {
            if (row.IsExpanded) Collapse(row);
            else Expand(row);
            return;
        }
        Reveal(row.Node.Url);
    }

    private OutlineRow? ContextRow => List.SelectedItem as OutlineRow;

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var hasPath = ContextRow?.Node.Url is not null;
        RevealItem.IsEnabled = hasPath;
        CopyItem.IsEnabled = hasPath;
        var canIgnore = OnIgnore is not null && hasPath;
        IgnoreItem.Visibility = canIgnore ? Visibility.Visible : Visibility.Collapsed;
        IgnoreSeparator.Visibility = IgnoreItem.Visibility;
    }

    private void OnReveal(object sender, RoutedEventArgs e) => Reveal(ContextRow?.Node.Url);

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        var paths = List.SelectedItems.Cast<OutlineRow>().Select(r => r.Node.Url).Where(u => u is not null).ToList();
        if (paths.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, paths));
    }

    private void OnIgnoreClick(object sender, RoutedEventArgs e)
    {
        if (OnIgnore is not { } ignore) return;
        foreach (var r in List.SelectedItems.Cast<OutlineRow>().ToList())
        {
            if (r.Node.Url is not null) ignore(r.Node);
        }
    }

    /// <summary>Mở File Explorer và chọn mục.</summary>
    public static void Reveal(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
            else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.UI, "Không mở được Explorer: " + ex.Message);
        }
    }
}
