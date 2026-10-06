using System.ComponentModel;
using System.Windows.Media;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Nodes;

namespace CleanBoost.App.Shared;

/// <summary>Một dòng của cây kết quả đã trải phẳng (một node ở một độ sâu).</summary>
public sealed class OutlineRow : INotifyPropertyChanged
{
    private readonly Func<Node, CheckState> _state;
    private bool _isExpanded;

    public Node Node { get; }
    public int Depth { get; }

    public OutlineRow(Node node, int depth, Func<Node, CheckState> state)
    {
        Node = node;
        Depth = depth;
        _state = state;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public double Indent => Depth * 16;
    public bool HasChildren => Node.Children.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    /// <summary>Trạng thái checkbox: true = chọn, false = không, null = một phần.</summary>
    public bool? IsChecked => _state(Node) switch
    {
        CheckState.On => true,
        CheckState.Off => false,
        _ => null,
    };

    public void RefreshCheck() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));

    public string Title => Node.Title;
    public string SizeText => Node.Size.Formatted;

    /// <summary>Nhãn phụ: badge nối " · ", nếu không có thì là lý do.</summary>
    public string Detail => Node.Badges.Count > 0 ? string.Join(" · ", Node.Badges) : Node.Reason.Resolved;

    /// <summary>Nhãn mức an toàn: ẩn với mục an toàn và với nhóm.</summary>
    public string? SafetyLabel => Node.IsContainer || Node.Safety == SafetyLevel.Safe ? null : Node.Safety.LocalizedTitle();
    public Brush SafetyBrush => Theme.SafetyBrush(Node.Safety);

    public ImageSource? FileIcon => Node.Kind is NodeKind.File or NodeKind.Directory ? ShellIcons.For(Node.Path, Node.Kind == NodeKind.Directory) : null;
    public string Glyph => Icons.Glyph(Node.Icon ?? (Node.Kind == NodeKind.Application ? "app" : Node.Kind == NodeKind.Virtual ? "gearshape" : "folder"));
    public bool HasFileIcon => FileIcon is not null;

    public string Tooltip
    {
        get
        {
            var lines = new List<string>();
            if (Node.Url is { } u) lines.Add(u);
            if (!Node.Reason.IsEmpty) lines.Add(Node.Reason.Resolved);
            if (Node.RuleID is { } r) lines.Add("rule: " + r);
            return lines.Count == 0 ? Node.Title : string.Join("\n", lines);
        }
    }
}
