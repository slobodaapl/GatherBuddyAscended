using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.FcMesh.Protocol;

namespace GatherBuddy.FcMesh.Fulfillment;

public sealed record FcListDemand(
    Guid ListId,
    uint ItemId,
    FcItemQuality Quality,
    int Quantity)
{
    public FcQuantityKey Key => new(ItemId, Quality);
}

public sealed record FcWorkerSupply(
    string WorkerId,
    FcItemQuantityMap HeldInventory,
    IReadOnlySet<Guid> EligibleListIds);

public sealed record FcContribution(
    Guid ListId,
    string SourceId,
    bool IsChest,
    int Quantity,
    FcQuantityKey Key);

public sealed class FcFulfillmentMatchResult
{
    public FcFulfillmentMatchResult(
        IReadOnlyList<FcListDemand> demands,
        IReadOnlyList<FcContribution> contributions)
    {
        Demands = demands;
        Contributions = contributions;
        var matchedByList = demands
            .Select(demand => demand.ListId)
            .Distinct()
            .ToDictionary(listId => listId, _ => FcItemQuantityMap.Empty);
        foreach (var group in contributions.GroupBy(contribution => contribution.ListId))
        {
            var values = group
                .GroupBy(contribution => contribution.Key)
                .ToDictionary(item => item.Key, item => checked(item.Sum(contribution => contribution.Quantity)));
            matchedByList[group.Key] = FcItemQuantityMap.FromDictionary(values);
        }
        MatchedByList = matchedByList;
        RemainingByList = demands
            .GroupBy(demand => demand.ListId)
            .ToDictionary(
                group => group.Key,
                group => new FcItemQuantityMap(group
                    .GroupBy(demand => demand.Key)
                    .Select(demand => new ItemQuantityEntry(
                        demand.Key.ItemId,
                        demand.Key.Quality,
                        checked(demand.Sum(value => value.Quantity))))
                    .Where(entry => entry.Quantity > 0))
                    .SubtractClamped(matchedByList.GetValueOrDefault(group.Key) ?? FcItemQuantityMap.Empty));
        RemainingDemand = new FcItemQuantityMap(demands
            .GroupBy(demand => demand.Key)
            .SelectMany(group =>
            {
                var supplied = contributions
                    .Where(contribution => contribution.Key == group.Key)
                    .Sum(contribution => contribution.Quantity);
                var total = group.Sum(demand => demand.Quantity);
                var remaining = Math.Max(0, total - supplied);
                return remaining == 0
                    ? Array.Empty<ItemQuantityEntry>()
                    : [new ItemQuantityEntry(group.Key.ItemId, group.Key.Quality, remaining)];
            })
            .ToArray());
        TotalDemand = demands.Sum(demand => (long)demand.Quantity);
        TotalMatched = contributions.Sum(contribution => (long)contribution.Quantity);
    }

    public IReadOnlyList<FcListDemand> Demands { get; }
    public IReadOnlyList<FcContribution> Contributions { get; }
    public IReadOnlyDictionary<Guid, FcItemQuantityMap> MatchedByList { get; }
    public IReadOnlyDictionary<Guid, FcItemQuantityMap> RemainingByList { get; }
    public FcItemQuantityMap RemainingDemand { get; }
    public FcItemQuantityMap AggregateRemainingDemand => RemainingDemand;
    public long TotalDemand { get; }
    public long TotalMatched { get; }
}

public static class FcFulfillmentMatcher
{
    public static FcFulfillmentMatchResult Match(
        IEnumerable<FcListDemand> demands,
        FcItemQuantityMap chest,
        IEnumerable<FcWorkerSupply> workerSupplies)
    {
        if (demands is null)
            throw new ArgumentNullException(nameof(demands));
        if (chest is null)
            throw new ArgumentNullException(nameof(chest));
        if (workerSupplies is null)
            throw new ArgumentNullException(nameof(workerSupplies));

        var normalizedDemands = demands
            .Where(demand => demand.Quantity > 0)
            .OrderBy(demand => demand.Key)
            .ThenBy(demand => demand.ListId)
            .ToArray();
        var workers = workerSupplies
            .OrderBy(worker => worker.WorkerId, StringComparer.Ordinal)
            .ToArray();
        var contributions = new List<FcContribution>();

        foreach (var demandGroup in normalizedDemands.GroupBy(demand => demand.Key).OrderBy(group => group.Key))
        {
            var groupDemands = demandGroup.ToArray();
            var sources = new List<Source>();
            var chestQuantity = chest.Get(demandGroup.Key);
            if (chestQuantity > 0)
                sources.Add(new Source("chest", true, chestQuantity, null));
            foreach (var worker in workers)
            {
                var quantity = worker.HeldInventory.Get(demandGroup.Key);
                if (quantity > 0)
                    sources.Add(new Source(worker.WorkerId, false, quantity, worker.EligibleListIds));
            }

            if (sources.Count == 0)
                continue;

            var flow = BuildAndRunFlow(groupDemands, sources);
            foreach (var result in flow)
            {
                if (result.Quantity <= 0)
                    continue;
                if (result.Quantity > int.MaxValue)
                    throw new OverflowException("Contribution quantity exceeds protocol int range.");
                contributions.Add(new FcContribution(
                    result.ListId,
                    result.SourceId,
                    result.IsChest,
                    (int)result.Quantity,
                    demandGroup.Key));
            }
        }

        return new FcFulfillmentMatchResult(normalizedDemands, contributions);
    }

    private static IReadOnlyList<FlowContribution> BuildAndRunFlow(
        IReadOnlyList<FcListDemand> demands,
        IReadOnlyList<Source> sources)
    {
        var sourceNode = 0;
        var firstSupply = 1;
        var firstDemand = firstSupply + sources.Count;
        var sinkNode = firstDemand + demands.Count;
        var graph = new Dinic(sinkNode + 1);
        var edges = new List<(Source Source, Guid ListId, Dinic.Edge Edge)>();

        for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
        {
            var source = sources[sourceIndex];
            graph.AddEdge(sourceNode, firstSupply + sourceIndex, source.Quantity);
            for (var demandIndex = 0; demandIndex < demands.Count; demandIndex++)
            {
                var demand = demands[demandIndex];
                if (source.IsChest || source.EligibleListIds!.Contains(demand.ListId))
                {
                    var edge = graph.AddEdge(firstSupply + sourceIndex, firstDemand + demandIndex, int.MaxValue);
                    edges.Add((source, demand.ListId, edge));
                }
            }
        }

        for (var demandIndex = 0; demandIndex < demands.Count; demandIndex++)
            graph.AddEdge(firstDemand + demandIndex, sinkNode, demands[demandIndex].Quantity);

        graph.MaxFlow(sourceNode, sinkNode);
        return edges
            .Select(edge => new FlowContribution(edge.Source.Id, edge.Source.IsChest, edge.ListId, edge.Edge.Flow))
            .Where(contribution => contribution.Quantity > 0)
            .ToArray();
    }

    private sealed record Source(string Id, bool IsChest, int Quantity, IReadOnlySet<Guid>? EligibleListIds);
    private sealed record FlowContribution(string SourceId, bool IsChest, Guid ListId, long Quantity);

    private sealed class Dinic
    {
        private readonly List<Edge>[] _graph;

        public Dinic(int count)
        {
            _graph = Enumerable.Range(0, count).Select(_ => new List<Edge>()).ToArray();
        }

        public Edge AddEdge(int from, int to, long capacity)
        {
            var forward = new Edge(to, capacity);
            var reverse = new Edge(from, 0);
            forward.Reverse = reverse;
            reverse.Reverse = forward;
            _graph[from].Add(forward);
            _graph[to].Add(reverse);
            return forward;
        }

        public long MaxFlow(int source, int sink)
        {
            long total = 0;
            while (true)
            {
                var level = Enumerable.Repeat(-1, _graph.Length).ToArray();
                var queue = new Queue<int>();
                level[source] = 0;
                queue.Enqueue(source);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    foreach (var edge in _graph[current])
                    {
                        if (edge.Residual <= 0 || level[edge.To] >= 0)
                            continue;
                        level[edge.To] = level[current] + 1;
                        queue.Enqueue(edge.To);
                    }
                }
                if (level[sink] < 0)
                    return total;

                var next = new int[_graph.Length];
                while (true)
                {
                    var pushed = Send(source, sink, long.MaxValue, level, next);
                    if (pushed == 0)
                        break;
                    total = checked(total + pushed);
                }
            }
        }

        private long Send(int node, int sink, long amount, int[] level, int[] next)
        {
            if (node == sink)
                return amount;
            for (; next[node] < _graph[node].Count; next[node]++)
            {
                var edge = _graph[node][next[node]];
                if (edge.Residual <= 0 || level[edge.To] != level[node] + 1)
                    continue;
                var pushed = Send(edge.To, sink, Math.Min(amount, edge.Residual), level, next);
                if (pushed == 0)
                    continue;
                edge.Flow = checked(edge.Flow + pushed);
                edge.Reverse.Flow = checked(edge.Reverse.Flow - pushed);
                return pushed;
            }
            return 0;
        }

        internal sealed class Edge
        {
            public Edge(int to, long capacity)
            {
                To = to;
                Capacity = capacity;
            }

            public int To { get; }
            public long Capacity { get; }
            public long Flow { get; set; }
            public Edge Reverse { get; set; } = null!;
            public long Residual => Capacity - Flow;
        }
    }
}
