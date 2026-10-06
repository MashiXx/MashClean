using CleanBoost.Core;
using CleanBoost.Engine.Nodes;

namespace CleanBoost.Tests.Engine;

internal static class NodeFactory
{
    /// <summary>Gốc không tồn tại: các test Node Tree không đụng ổ đĩa.</summary>
    public const string Base = @"C:\cb-test-nonexistent";

    public static Node Leaf(string path, long size = 100, string? category = "userCaches", SafetyLevel safety = SafetyLevel.Safe, string? rule = null)
    {
        var n = Node.ForPath(Base + path, isDirectory: true);
        n.Size = new ByteCount(size);
        n.ItemCount = 1;
        n.Safety = safety;
        n.RuleID = rule;
        n.Category = category;
        n.Removal = RemovalStrategy.Delete;
        return n;
    }

    public static Node Group(string title, IEnumerable<Node> children, string? category = null) =>
        Node.Group(new LocalizedText(title, title), "folder", category, children);
}

/// <summary>Gộp trùng Node Tree (mục 6.3), chỉ mục, tổng dung lượng và chặn mục lớn bất thường.</summary>
public class NodeTreeDedupeTests
{
    private static Node Leaf(string path, long size = 100, string? category = "userCaches", SafetyLevel safety = SafetyLevel.Safe, string? rule = null) =>
        NodeFactory.Leaf(path, size, category, safety, rule);

    private static Node Group(string title, params Node[] children) => NodeFactory.Group(title, children);

    [Fact]
    public void SamePathKeepsHigherPriorityCategory()
    {
        var cache = Leaf(@"\AppData\Local\Vendor", category: "userCaches", rule: "user.caches.all");
        var leftover = Leaf(@"\AppData\Local\Vendor", category: "appLeftovers", rule: "app.vendor.leftovers");
        var tree = NodeTree.Merge([Group("Cache", cache), Group("Leftovers", leftover)]);
        var only = Assert.Single(tree.AllLeaves);
        Assert.Equal("appLeftovers", only.Category);
        Assert.Single(tree.Roots); // nhóm rỗng sau gộp bị bỏ
    }

    [Fact]
    public void SamePathComparisonIgnoresCaseAndSyntax()
    {
        var a = Leaf(@"\AppData\Local\Vendor", rule: "first");
        var b = Leaf(@"\appdata\LOCAL\.\x\..\vendor\", rule: "second");
        var result = NodeTree.Deduplicate([Group("G", a, b)], NodeTree.DefaultCategoryPriority);
        Assert.Equal(new[] { "first" }, result.SelectMany(r => r.RemovableLeaves()).Select(l => l.RuleID));
    }

    [Fact]
    public void CategoryInheritedFromGroup()
    {
        var a = Leaf(@"\x", category: null);
        var b = Leaf(@"\x", category: "devToolCaches");
        var result = NodeTree.Deduplicate(
            [NodeFactory.Group("Logs", [a], "userLogs"), NodeFactory.Group("Dev", [b], "devToolCaches")],
            NodeTree.DefaultCategoryPriority);
        Assert.Equal(new[] { "devToolCaches" }, result.SelectMany(r => r.RemovableLeaves()).Select(l => l.Category));
    }

    [Fact]
    public void ParentWinsOverChild()
    {
        var parent = Leaf(@"\Caches\Google", size: 1000);
        var child = Leaf(@"\Caches\Google\Chrome\Default\Cache", size: 600, category: "appLeftovers");
        var other = Leaf(@"\Caches\GoogleUpdater", size: 50);
        var tree = NodeTree.Merge([Group("Cache", parent, other), Group("Chrome", child)]);
        var paths = tree.AllLeaves.Select(l => l.Path!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { NodeFactory.Base + @"\Caches\Google", NodeFactory.Base + @"\Caches\GoogleUpdater" }, paths);
        Assert.Equal(1050, tree.TotalSize.Bytes);
    }

    [Theory]
    [InlineData(@"\Cache X")]   // khoảng trắng < '\'
    [InlineData(@"\Cache-old")] // '-' < '\'
    [InlineData(@"\Cache2")]    // chữ số < '\'
    [InlineData(@"\CacheZ")]    // chữ hoa < '\'
    [InlineData(@"\Cache_x")]   // '_' > '\'
    public void ChildIsDroppedEvenWhenASiblingSharesThePrefix(string sibling)
    {
        var parent = Leaf(@"\Cache", size: 1000);
        var siblingNode = Leaf(sibling, size: 10);
        var child = Leaf(@"\Cache\Sub", size: 600);
        var tree = NodeTree.Merge([Group("G", parent, siblingNode, child)]);
        Assert.DoesNotContain(tree.AllLeaves, l => l.Id == child.Id);
        Assert.Equal(1010, tree.TotalSize.Bytes);
    }

    [Fact]
    public void ChildBeforeParentInInputStillDeduped()
    {
        var child = Leaf(@"\a\b\c");
        var parent = Leaf(@"\a\b");
        var tree = NodeTree.Merge([Group("G", child, parent)]);
        Assert.Equal(new[] { NodeFactory.Base + @"\a\b" }, tree.AllLeaves.Select(l => l.Path));
    }

    [Fact]
    public void NoDuplicatesReturnsInputUnchanged()
    {
        List<Node> roots = [Group("G", Leaf(@"\a"), Leaf(@"\b"))];
        var result = NodeTree.Deduplicate(roots, NodeTree.DefaultCategoryPriority);
        Assert.Same(roots, result);
        Assert.Equal(2, result[0].Children.Count);
    }

    [Fact]
    public void VirtualNodesWithoutPathAreKept()
    {
        var docker = Node.ForVirtual(new DockerPruneItem());
        docker.Size = new ByteCount(10);
        docker.Category = "devToolCaches";
        docker.Removal = RemovalStrategy.Custom(RemoverIDs.Docker);
        var tree = NodeTree.Merge([Group("Dev", docker, Leaf(@"\a"))]);
        Assert.Equal(2, tree.AllLeaves.Count());
    }

    [Fact]
    public void LargeCacheIsDowngradedToReview()
    {
        var huge = Leaf(@"\huge", size: ByteCount.Gigabytes(60).Bytes, category: "userCaches");
        var hugeLog = Leaf(@"\hugeLog", size: ByteCount.Gigabytes(60).Bytes, category: "userLogs");
        var normal = Leaf(@"\normal", size: ByteCount.Gigabytes(1).Bytes, category: "browserCaches");
        var tree = NodeTree.Merge([Group("G", huge, hugeLog, normal)]);
        var byName = tree.AllLeaves.ToDictionary(l => Path.GetFileName(l.Path!));
        Assert.Equal(SafetyLevel.Review, byName["huge"].Safety);
        Assert.Contains(Loc.T("Lớn bất thường"), byName["huge"].Badges);
        Assert.Equal(SafetyLevel.Safe, byName["hugeLog"].Safety);
        Assert.Equal(SafetyLevel.Safe, byName["normal"].Safety);
    }

    [Fact]
    public void LargeRiskyCacheStaysRisky()
    {
        var risky = Leaf(@"\risky", size: ByteCount.Gigabytes(60).Bytes, safety: SafetyLevel.Risky);
        NodeTree.Merge([Group("G", risky)]);
        Assert.Equal(SafetyLevel.Risky, risky.Safety);
        Assert.Empty(risky.Badges);
    }

    [Fact]
    public void IndexAndAggregates()
    {
        var a = Leaf(@"\a", size: 10);
        var b = Leaf(@"\b", size: 20);
        var inner = Group("Inner", b);
        var root = NodeFactory.Group("Root", [a, inner]);
        var tree = new NodeTree([root]);
        Assert.Equal(30, tree.TotalSize.Bytes);
        Assert.Equal(2, tree.Leaves(root.Id).Count());
        Assert.Equal(new[] { inner.Id, root.Id }, tree.Ancestors(b.Id));
        Assert.Equal(20, tree.Node(inner.Id)?.Size.Bytes);
        Assert.Equal(20, tree.Size(n => n.Size.Bytes > 15).Bytes);
        Assert.Equal(2, root.ItemCount);
        Assert.Null(tree.Node(Guid.NewGuid()));
        Assert.True(NodeTree.Empty.IsEmpty);
    }

    [Fact]
    public void ApplicationNodeAggregatesChildren()
    {
        var app = Node.Application("vendor.app", @"C:\Program Files\Vendor", "Vendor", [Leaf(@"\x", 5), Leaf(@"\y", 7, safety: SafetyLevel.Review)]);
        Assert.Equal(12, app.Size.Bytes);
        Assert.Equal(SafetyLevel.Safe, app.Safety);
        Assert.True(app.IsContainer);
        Assert.Equal(@"C:\Program Files\Vendor", app.Url);
    }
}

/// <summary>Trạng thái chọn tách khỏi cây, cộng dồn tăng dần (mục 6.2).</summary>
public class SelectionStateTests
{
    private readonly Node _a = NodeFactory.Leaf(@"\a", 10, safety: SafetyLevel.Safe);
    private readonly Node _b = NodeFactory.Leaf(@"\b", 20, safety: SafetyLevel.Review);
    private readonly Node _c = NodeFactory.Leaf(@"\c", 40, safety: SafetyLevel.Risky);
    private readonly Node _d = NodeFactory.Leaf(@"\d", 80, safety: SafetyLevel.Safe);
    private readonly NodeTree _tree;
    private readonly Guid _rootID;
    private readonly Guid _innerID;

    public SelectionStateTests()
    {
        var inner = NodeFactory.Group("Inner", [_b, _c]);
        var root = NodeFactory.Group("Root", [_a, inner, _d]);
        _tree = new NodeTree([root]);
        _rootID = root.Id;
        _innerID = inner.Id;
    }

    [Fact]
    public void DefaultsSelectOnlySafe()
    {
        var s = SelectionState.Defaults(_tree);
        Assert.True(s.IsSelected(_a.Id) && s.IsSelected(_d.Id));
        Assert.False(s.IsSelected(_b.Id) || s.IsSelected(_c.Id));
        Assert.Equal(90, s.SelectedBytes.Bytes);
        Assert.Equal(2, s.SelectedCount);
        Assert.Equal(CheckState.Mixed, s.State(_rootID, _tree));
        Assert.Equal(CheckState.Off, s.State(_innerID, _tree));
    }

    [Fact]
    public void DefaultsWithPredicate() => Assert.Equal(80, SelectionState.Defaults(_tree, n => n.Size.Bytes > 50).SelectedBytes.Bytes);

    [Fact]
    public void IncrementalTotalsAndMixedState()
    {
        var s = new SelectionState();
        s.Set(_innerID, true, _tree);
        Assert.Equal(60, s.SelectedBytes.Bytes);
        Assert.Equal(CheckState.On, s.State(_innerID, _tree));
        Assert.Equal(CheckState.Mixed, s.State(_rootID, _tree));
        s.Set(_b.Id, false, _tree);
        Assert.Equal(40, s.SelectedBytes.Bytes);
        Assert.Equal(CheckState.Mixed, s.State(_innerID, _tree));
        s.Set(_c.Id, true, _tree); // chọn lại cùng giá trị không cộng hai lần
        Assert.Equal(40, s.SelectedBytes.Bytes);
        Assert.Equal(1, s.SelectedCount);
    }

    [Fact]
    public void Toggle()
    {
        var s = SelectionState.Defaults(_tree);
        s.Toggle(_rootID, _tree); // mixed → chọn hết
        Assert.Equal(CheckState.On, s.State(_rootID, _tree));
        Assert.Equal(150, s.SelectedBytes.Bytes);
        s.Toggle(_rootID, _tree); // on → bỏ hết
        Assert.Equal(CheckState.Off, s.State(_rootID, _tree));
        Assert.Equal(0, s.SelectedBytes.Bytes);
        Assert.Equal(0, s.SelectedCount);
    }

    [Fact]
    public void SelectAllSetManyAndClear()
    {
        var s = new SelectionState();
        var changes = 0;
        s.Changed += () => changes++;
        s.SelectAll(_tree);
        Assert.Equal(4, s.SelectedLeaves(_tree).Count);
        Assert.Equal(CheckState.On, s.State(_a.Id, _tree));
        s.Clear();
        Assert.Empty(s.SelectedIDs);
        Assert.Equal(0, s.SelectedBytes.Bytes);
        s.SetMany([_a.Id, _c.Id], true, _tree);
        Assert.Equal(50, s.SelectedBytes.Bytes);
        Assert.Equal(new[] { _a.Id, _c.Id }, s.SelectedLeaves(_tree).Select(n => n.Id));
        Assert.Equal(3, changes);
    }
}
