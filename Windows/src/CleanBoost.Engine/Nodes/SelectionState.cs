using CleanBoost.Core;

namespace CleanBoost.Engine.Nodes;

/// <summary>Trạng thái chọn của node cha tính từ con.</summary>
public enum CheckState { On, Off, Mixed }

/// <summary>
/// Trạng thái chọn lưu tách khỏi cây (mục 6.2). Dung lượng "sẽ giải phóng" được tính tăng dần:
/// chỉ cộng trừ phần thay đổi, không duyệt lại cả cây.
/// </summary>
public sealed class SelectionState
{
    private readonly HashSet<Guid> _selected = [];
    /// <summary>Số lá đang chọn dưới mỗi node chứa (cập nhật tăng dần dọc đường tới gốc).</summary>
    private readonly Dictionary<Guid, int> _selectedLeafCount = [];

    public ByteCount SelectedBytes { get; private set; }
    public int SelectedCount { get; private set; }

    /// <summary>Phát ra sau mỗi lần thay đổi (để giao diện cập nhật).</summary>
    public event Action? Changed;

    /// <summary>Lựa chọn mặc định: node <c>safe</c> được chọn sẵn, còn lại không (mục 6.2).</summary>
    public static SelectionState Defaults(NodeTree tree, Func<Node, bool>? predicate = null)
    {
        var s = new SelectionState();
        foreach (var leaf in tree.AllLeaves)
        {
            if (leaf.Safety == SafetyLevel.Safe && (predicate?.Invoke(leaf) ?? true)) s.SetLeaf(leaf, true, tree);
        }
        return s;
    }

    public bool IsSelected(Guid id) => _selected.Contains(id);

    public IReadOnlyCollection<Guid> SelectedIDs => _selected;

    public CheckState State(Guid id, NodeTree tree)
    {
        var total = tree.LeafIDs.TryGetValue(id, out var leaves) ? leaves.Length : 0;
        if (total == 0) return CheckState.Off;
        int selected;
        if (tree.Node(id) is { IsContainer: true }) selected = _selectedLeafCount.GetValueOrDefault(id);
        else selected = IsSelected(id) ? 1 : 0;
        if (selected == 0) return CheckState.Off;
        return selected == total ? CheckState.On : CheckState.Mixed;
    }

    /// <summary>Chọn hoặc bỏ chọn một node; với node cha áp cho toàn bộ node con.</summary>
    public void Set(Guid id, bool on, NodeTree tree)
    {
        foreach (var leaf in tree.Leaves(id)) SetLeaf(leaf, on, tree);
        Changed?.Invoke();
    }

    /// <summary>Đảo trạng thái: off/mixed → chọn hết, on → bỏ hết.</summary>
    public void Toggle(Guid id, NodeTree tree) => Set(id, State(id, tree) != CheckState.On, tree);

    public void SelectAll(NodeTree tree)
    {
        foreach (var leaf in tree.AllLeaves) SetLeaf(leaf, true, tree);
        Changed?.Invoke();
    }

    public void SetMany(IEnumerable<Guid> ids, bool on, NodeTree tree)
    {
        foreach (var id in ids)
        {
            foreach (var leaf in tree.Leaves(id)) SetLeaf(leaf, on, tree);
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        _selected.Clear();
        _selectedLeafCount.Clear();
        SelectedBytes = ByteCount.Zero;
        SelectedCount = 0;
        Changed?.Invoke();
    }

    private void SetLeaf(Node leaf, bool on, NodeTree tree)
    {
        var was = _selected.Contains(leaf.Id);
        if (was == on) return;
        if (on) _selected.Add(leaf.Id);
        else _selected.Remove(leaf.Id);
        var delta = on ? 1 : -1;
        SelectedCount += delta;
        SelectedBytes = on ? SelectedBytes + leaf.Size : SelectedBytes - leaf.Size;
        foreach (var a in tree.Ancestors(leaf.Id)) _selectedLeafCount[a] = _selectedLeafCount.GetValueOrDefault(a) + delta;
    }

    /// <summary>Các lá đang chọn (theo thứ tự trong cây).</summary>
    public List<Node> SelectedLeaves(NodeTree tree) => tree.AllLeaves.Where(l => IsSelected(l.Id)).ToList();
}
