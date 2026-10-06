using CleanBoost.Core;

namespace CleanBoost.Engine.Scanning;

public sealed class ScanGraphException(string message) : Exception(message);

/// <summary>
/// DAG các task (mục 5.1). Task trùng id bị bỏ (giữ cái đầu) để nhiều feature cùng thêm <c>installedApps</c>.
/// Kiểm tra không có chu trình bằng thuật toán Kahn khi tạo.
/// </summary>
public sealed class ScanGraph
{
    public IReadOnlyList<IScanTask> Tasks { get; }
    public IReadOnlyList<string> TopologicalOrder { get; }
    public IReadOnlyDictionary<string, List<string>> Dependents { get; }
    private readonly Dictionary<string, IScanTask> _byId;

    public ScanGraph(IEnumerable<IScanTask> tasks)
    {
        var unique = new List<IScanTask>();
        _byId = new Dictionary<string, IScanTask>();
        foreach (var t in tasks)
        {
            if (_byId.TryAdd(t.Id, t)) unique.Add(t);
        }
        Tasks = unique;

        var dependents = new Dictionary<string, List<string>>();
        var indegree = unique.ToDictionary(t => t.Id, _ => 0);
        foreach (var t in unique)
        {
            foreach (var d in t.Dependencies)
            {
                if (!_byId.ContainsKey(d)) throw new ScanGraphException(Loc.F("Task %@ phụ thuộc task không tồn tại %@", t.Id, d));
                if (!dependents.TryGetValue(d, out var list)) dependents[d] = list = [];
                list.Add(t.Id);
                indegree[t.Id]++;
            }
        }
        Dependents = dependents;

        var order = new List<string>();
        var queue = new Queue<string>(unique.Where(t => indegree[t.Id] == 0).Select(t => t.Id));
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            order.Add(id);
            foreach (var dep in dependents.GetValueOrDefault(id) ?? [])
            {
                if (--indegree[dep] == 0) queue.Enqueue(dep);
            }
        }
        if (order.Count != unique.Count)
        {
            var cycle = unique.Select(t => t.Id).Where(id => !order.Contains(id));
            throw new ScanGraphException(Loc.F("Graph quét có chu trình: %@", string.Join(", ", cycle)));
        }
        TopologicalOrder = order;
    }

    public IScanTask? Task(string id) => _byId.GetValueOrDefault(id);

    public HashSet<string> TransitiveDependents(string id)
    {
        var result = new HashSet<string>();
        var stack = new Stack<string>(Dependents.GetValueOrDefault(id) ?? []);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (result.Add(d))
            {
                foreach (var x in Dependents.GetValueOrDefault(d) ?? []) stack.Push(x);
            }
        }
        return result;
    }

    public HashSet<string> TransitiveDependencies(string id)
    {
        var result = new HashSet<string>();
        var stack = new Stack<string>(Task(id)?.Dependencies ?? []);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (result.Add(d))
            {
                foreach (var x in Task(d)?.Dependencies ?? []) stack.Push(x);
            }
        }
        return result;
    }
}
