using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GatherBuddy.Config;
using GatherBuddy.Crafting.Acquisition;

namespace GatherBuddy.Marketboard;

/// <summary>
/// Owns persisted marketplace lists and transient craft-owned managed lists.
/// </summary>
public sealed class MarketplaceBuyListManager
{
    private readonly Configuration _config;
    private LiveAcquisitionExecutor? _executor;
    private Task<LiveAcquisitionResult>? _execution;
    private DateTime _nextEvaluation = DateTime.MinValue;
    private bool _evaluationDirty = true;
    private Guid? _runningListId;
    private bool _startWhenReady;

    public AcquisitionPlanningInputBuilder.BuildResult? Snapshot { get; private set; }
    public AcquisitionPlanningResult? Planning { get; private set; }
    public LiveAcquisitionResult? LastResult { get; private set; }
    public string StatusText { get; private set; } = string.Empty;

    public bool IsRunning
        => _execution is { IsCompleted: false };

    public bool IsBusy
        => _execution != null;

    public bool IsEstimateReady
        => !_evaluationDirty && Snapshot?.IsReady == true && Planning != null;

    public LiveAcquisitionStage Stage
        => _executor?.Stage ?? LiveAcquisitionStage.Idle;

    public Guid? RunningListId
        => _runningListId;

    public MarketplaceBuyListManager(Configuration config)
    {
        _config = config;
        EnsureState();
    }

    public IReadOnlyList<MarketplaceBuyListDefinition> Lists
        => _config.MarketplaceBuyLists;

    public MarketplaceBuyListDefinition? ActiveList
        => Lists.FirstOrDefault(list => list.Id == _config.ActiveMarketplaceBuyListId);

    public Guid ActiveListId => _config.ActiveMarketplaceBuyListId;

    public MarketplaceBuyListDefinition CreateList(string name = "Marketplace List", bool select = true)
    {
        var list = new MarketplaceBuyListDefinition
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Marketplace List" : name.Trim(),
        };
        _config.MarketplaceBuyLists.Add(list);
        if (select)
            _config.ActiveMarketplaceBuyListId = list.Id;
        Save();
        if (select)
            InvalidateEvaluation();
        return list;
    }

    public MarketplaceBuyListDefinition CreateManagedList(string name = "Craft acquisition")
        => new()
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Craft acquisition" : name.Trim(),
            IsManaged = true,
        };

    /// <summary>
    /// Creates the read-only marketplace view for one craft execution. The
    /// plan already excludes final outputs and usable craft/gather paths, so a
    /// managed list cannot accidentally turn into a persistent shopping list.
    /// </summary>
    public MarketplaceBuyListDefinition CreateManagedList(
        AcquisitionPlanningResult planning,
        LiveAcquisitionOptions? options = null,
        string name = "Craft acquisition")
    {
        ArgumentNullException.ThrowIfNull(planning);
        var list = CreateManagedList(name);
        var selected = planning.SelectedPlan;
        if (selected == null)
            return list;

        options ??= new LiveAcquisitionOptions();
        list.PreferHQ = options.PreferHQ;
        list.PreferVendors = options.PreferVendors;
        list.PreferMarketForSpecialCurrency = options.PreferMarketForSpecialCurrency;
        list.CurrentWorldOnly = options.CurrentWorldOnly;
        list.MaximumGilSpend = options.MaximumGilSpend;
        foreach (var group in selected.Transactions
                     .Where(transaction => transaction.SourceKind == AcquisitionSourceKind.Market)
                     .GroupBy(transaction => transaction.ItemId))
        {
            var first = group.First();
            AddItemInternal(list, first.ItemId, first.ItemName, 0, group.Sum(transaction => transaction.Quantity));
        }
        return list;
    }

    public bool IsManaged(Guid id)
        => Find(id)?.IsManaged == true;

    public bool SelectList(Guid id)
    {
        if (IsBusy)
            return false;
        if (_config.MarketplaceBuyLists.All(list => list.Id != id))
            return false;
        if (_config.ActiveMarketplaceBuyListId == id)
            return true;
        _config.ActiveMarketplaceBuyListId = id;
        Save();
        InvalidateEvaluation();
        return true;
    }

    public bool RenameList(Guid id, string name)
    {
        if (IsBusy)
            return false;
        var list = Find(id);
        if (list == null || list.IsManaged || string.IsNullOrWhiteSpace(name))
            return false;
        list.Name = name.Trim();
        Save();
        return true;
    }

    public bool DeleteList(Guid id)
    {
        if (IsBusy || _config.MarketplaceBuyLists.Count <= 1)
            return false;
        var index = _config.MarketplaceBuyLists.FindIndex(list => list.Id == id);
        if (index < 0)
            return false;

        _config.MarketplaceBuyLists.RemoveAt(index);
        if (_config.ActiveMarketplaceBuyListId == id)
            _config.ActiveMarketplaceBuyListId = _config.MarketplaceBuyLists[Math.Min(index, _config.MarketplaceBuyLists.Count - 1)].Id;
        Save();
        InvalidateEvaluation();
        return true;
    }

    public bool AddItem(Guid id, uint itemId, string itemName, uint iconId, int quantity)
    {
        if (IsBusy || itemId == 0 || quantity <= 0)
            return false;
        var list = Find(id);
        if (list == null || list.IsManaged)
            return false;

        AddItem(list, itemId, itemName, iconId, quantity);
        Save();
        InvalidateEvaluation();
        return true;
    }

    public bool AddItem(MarketplaceBuyListDefinition list, uint itemId, string itemName, uint iconId, int quantity)
    {
        if (itemId == 0 || quantity <= 0 || list == null || (IsBusy && !list.IsManaged))
            return false;
        if (list.IsManaged)
        {
            AddItemInternal(list, itemId, itemName, iconId, quantity);
            return true;
        }

        if (!Lists.Any(candidate => candidate.Id == list.Id))
            return false;
        AddItemInternal(list, itemId, itemName, iconId, quantity);
        Save();
        InvalidateEvaluation();
        return true;
    }

    public bool SetTarget(Guid id, uint itemId, int quantity)
    {
        if (IsBusy)
            return false;
        var list = Find(id);
        if (list == null || list.IsManaged)
            return false;

        var entry = list.Entries.FirstOrDefault(candidate => candidate.ItemId == itemId);
        if (quantity <= 0)
        {
            if (entry == null)
                return false;
            list.Entries.Remove(entry);
        }
        else if (entry == null)
        {
            list.Entries.Add(new MarketplaceBuyListEntry { ItemId = itemId, TargetQuantity = quantity });
        }
        else
        {
            entry.TargetQuantity = quantity;
        }
        Save();
        InvalidateEvaluation();
        return true;
    }

    public bool RemoveItem(Guid id, uint itemId)
        => SetTarget(id, itemId, 0);

    public bool UpdateSettings(Guid id, bool? preferHq = null, bool? currentWorldOnly = null,
        bool? preferVendors = null, bool? preferMarketForSpecialCurrency = null,
        long? maximumGilSpend = null, bool clearMaximumGilSpend = false)
    {
        if (IsBusy)
            return false;
        var list = Find(id);
        if (list == null || list.IsManaged)
            return false;
        if (preferHq.HasValue) list.PreferHQ = preferHq.Value;
        if (currentWorldOnly.HasValue) list.CurrentWorldOnly = currentWorldOnly.Value;
        if (preferVendors.HasValue) list.PreferVendors = preferVendors.Value;
        if (preferMarketForSpecialCurrency.HasValue)
            list.PreferMarketForSpecialCurrency = preferMarketForSpecialCurrency.Value;
        if (clearMaximumGilSpend)
            list.MaximumGilSpend = null;
        else if (maximumGilSpend.HasValue)
            list.MaximumGilSpend = Math.Max(0, maximumGilSpend.Value);
        Save();
        InvalidateEvaluation();
        return true;
    }

    public void Update()
    {
        if (_execution is { IsCompleted: true })
        {
            try
            {
                LastResult = _execution.GetAwaiter().GetResult();
                StatusText = LastResult.Message;
                GatherBuddy.Log.Information(
                    $"[MarketplaceBuyListManager] Marketplace list finished: status={LastResult.Status}, failure={LastResult.FailureKind}, gil={LastResult.GilSpent:N0}, message={LastResult.Message}");
            }
            catch (Exception ex)
            {
                StatusText = $"Marketplace list failed: {ex.Message}";
                GatherBuddy.Log.Error($"[MarketplaceBuyListManager] Marketplace list execution failed: {ex}");
            }

            if (_executor != null)
                _executor.Diagnostic -= OnExecutorDiagnostic;
            GatherBuddy.ReleaseLiveAcquisitionExecutor(_executor);
            _executor = null;
            _execution = null;
            _runningListId = null;
            InvalidateEvaluation();
        }

        if (Snapshot != null && DateTime.UtcNow >= _nextEvaluation)
            _evaluationDirty = true;
        if (IsBusy || !_evaluationDirty || DateTime.UtcNow < _nextEvaluation)
            return;

        Evaluate(ActiveList);
        if (_startWhenReady && IsEstimateReady)
        {
            _startWhenReady = false;
            StartReady(ActiveList!);
        }
        else if (_startWhenReady && Snapshot is { IsLoading: false, IsReady: false })
        {
            _startWhenReady = false;
        }
        _nextEvaluation = DateTime.UtcNow + (Snapshot == null || Snapshot.IsLoading
            ? TimeSpan.FromMilliseconds(500)
            : TimeSpan.FromSeconds(5));
    }

    public void RefreshEstimate()
        => InvalidateEvaluation();

    public bool Start()
    {
        var list = ActiveList;
        if (list == null)
        {
            StatusText = "No marketplace list is available.";
            return false;
        }
        if (IsBusy)
        {
            StatusText = "A marketplace list is already running.";
            return false;
        }
        if (list.Entries.Count == 0)
        {
            StatusText = "Marketplace list is empty.";
            return false;
        }
        if (_evaluationDirty || Snapshot == null || Snapshot.IsLoading)
        {
            _startWhenReady = true;
            StatusText = "Starting when the acquisition estimate is ready...";
            GatherBuddy.Log.Information($"[MarketplaceBuyListManager] Queued marketplace list '{list.Name}' until acquisition estimate is ready.");
            return true;
        }
        if (Planning == null || !Snapshot.IsReady)
        {
            StatusText = Snapshot.ErrorReason;
            return false;
        }

        return StartReady(list);
    }

    private bool StartReady(MarketplaceBuyListDefinition list)
    {
        if (Planning == null || !Planning.IsSuccess)
        {
            StatusText = Planning?.Blockers.FirstOrDefault()?.Reason
                ?? $"Marketplace list cannot start ({Planning?.Status.ToString() ?? "no plan"}).";
            GatherBuddy.Log.Warning($"[MarketplaceBuyListManager] Start rejected for '{list.Name}': {StatusText}");
            return false;
        }
        if (Planning.SelectedPlan?.Transactions.Count is not > 0)
        {
            StatusText = "Nothing needs to be purchased.";
            return false;
        }

        var options = new LiveAcquisitionOptions
        {
            CurrentWorldOnly = list.CurrentWorldOnly,
            PreferHQ = list.PreferHQ,
            PreferVendors = list.PreferVendors,
            PreferMarketForSpecialCurrency = list.PreferMarketForSpecialCurrency,
            MaximumGilSpend = list.MaximumGilSpend,
        };
        var executor = GatherBuddy.CreateLiveAcquisitionExecutor(
            options,
            cancellationToken => ReplanAsync(list.Id, cancellationToken),
            (itemId, cancellationToken) => GatherBuddy.InvalidateMarketplaceMarketDataOnFrameworkThreadAsync(
                itemId,
                list.CurrentWorldOnly,
                cancellationToken));
        if (executor == null)
        {
            StatusText = "Another acquisition run is already active.";
            GatherBuddy.Log.Warning($"[MarketplaceBuyListManager] Could not start '{list.Name}': another acquisition run is active.");
            return false;
        }

        LastResult = null;
        _executor = executor;
        executor.Diagnostic += OnExecutorDiagnostic;
        _runningListId = list.Id;
        StatusText = $"Starting marketplace list '{list.Name}'...";
        GatherBuddy.Log.Information($"[MarketplaceBuyListManager] Starting marketplace list '{list.Name}' with {Planning.SelectedPlan.Transactions.Count} transaction(s).");
        try
        {
            _execution = executor.ExecuteAsync(Planning);
        }
        catch (Exception ex)
        {
            executor.Diagnostic -= OnExecutorDiagnostic;
            GatherBuddy.ReleaseLiveAcquisitionExecutor(executor);
            _executor = null;
            _runningListId = null;
            StatusText = $"Could not start marketplace list: {ex.Message}";
            GatherBuddy.Log.Error($"[MarketplaceBuyListManager] Could not start '{list.Name}': {ex}");
            return false;
        }
        return true;
    }

    private void OnExecutorDiagnostic(LiveAcquisitionDiagnostic diagnostic)
    {
        StatusText = diagnostic.Message;
        GatherBuddy.Log.Information(
            $"[MarketplaceBuyListManager] {diagnostic.Stage}: {diagnostic.Message}");
    }

    public void Stop()
    {
        if (!IsBusy)
        {
            _startWhenReady = false;
            return;
        }
        _executor?.Cancel();
        _startWhenReady = false;
        StatusText = "Marketplace list stopping...";
    }

    public void Clear()
    {
        var list = ActiveList;
        if (list == null || list.IsManaged || IsBusy)
            return;
        if (list.Entries.Count == 0)
            return;
        list.Entries.Clear();
        Save();
        StatusText = $"Cleared marketplace list '{list.Name}'.";
        InvalidateEvaluation();
    }

    public void Dispose()
    {
        _executor?.Cancel();
        if (_executor != null)
            _executor.Diagnostic -= OnExecutorDiagnostic;
        GatherBuddy.ReleaseLiveAcquisitionExecutor(_executor);
        _executor = null;
        _execution = null;
    }

    private void Evaluate(MarketplaceBuyListDefinition? list)
    {
        _evaluationDirty = false;
        if (list == null)
        {
            Snapshot = null;
            Planning = null;
            StatusText = "No marketplace list is available.";
            return;
        }

        var settings = BuildSettings(list);
        var targets = list.Entries
            .Select(entry => (entry.ItemId, entry.ItemName, entry.IconId, entry.TargetQuantity))
            .ToArray();
        Snapshot = AcquisitionPlanningInputBuilder.BuildMarketplaceTargets(targets, settings);
        if (!Snapshot.IsReady)
        {
            Planning = null;
            StatusText = Snapshot.IsLoading ? Snapshot.LoadingReason : Snapshot.ErrorReason;
            return;
        }

        Planning = AcquisitionPlanner.Plan(Snapshot.Input, settings);
        StatusText = Planning.IsSuccess
            ? list.Entries.Count == 0
                ? "List is empty."
                : Planning.SelectedPlan?.Transactions.Count is > 0
                    ? "Estimate ready."
                    : "All targets already satisfied."
            : Planning.Blockers.FirstOrDefault()?.Reason
                ?? $"Marketplace list cannot be planned ({Planning.Status}).";
    }

    private async Task<AcquisitionPlanningResult?> ReplanAsync(Guid listId, CancellationToken cancellationToken)
    {
        return await GatherBuddy.RunOnFrameworkThreadAsync(() =>
        {
            var list = Find(listId);
            if (list == null)
                return null;
            Evaluate(list);
            return Snapshot?.IsReady == true ? Planning : null;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static AcquisitionPlanningSettings BuildSettings(MarketplaceBuyListDefinition list)
        => new()
        {
            AutoPurchaseBlockedDependencies = true,
            CurrentWorldOnly = list.CurrentWorldOnly,
            PreferHQ = list.PreferHQ,
            PreferVendors = list.PreferVendors,
            PreferMarketForSpecialCurrency = list.PreferMarketForSpecialCurrency,
            MaximumGilSpend = list.MaximumGilSpend,
        };

    private void InvalidateEvaluation()
    {
        _evaluationDirty = true;
        _nextEvaluation = DateTime.MinValue;
    }

    public MarketplaceBuyListDefinition? Find(Guid id)
        => Lists.FirstOrDefault(list => list.Id == id);

    public void EnsureState()
    {
        _config.MarketplaceBuyLists ??= new List<MarketplaceBuyListDefinition>();
        if (_config.MarketplaceBuyLists.Count == 0)
            _config.MarketplaceBuyLists.Add(new MarketplaceBuyListDefinition { Name = "Default" });

        var active = _config.MarketplaceBuyLists.FirstOrDefault(list => list.Id == _config.ActiveMarketplaceBuyListId);
        if (active == null)
            _config.ActiveMarketplaceBuyListId = _config.MarketplaceBuyLists[0].Id;

        foreach (var list in _config.MarketplaceBuyLists)
        {
            list.Entries ??= new List<MarketplaceBuyListEntry>();
            list.Name = string.IsNullOrWhiteSpace(list.Name) ? "Marketplace List" : list.Name;
            list.Entries.RemoveAll(entry => entry == null || entry.ItemId == 0 || entry.TargetQuantity <= 0);
        }
    }

    private static void AddItemInternal(MarketplaceBuyListDefinition list, uint itemId, string itemName, uint iconId, int quantity)
    {
        var entry = list.Entries.FirstOrDefault(candidate => candidate.ItemId == itemId);
        if (entry == null)
        {
            list.Entries.Add(new MarketplaceBuyListEntry
            {
                ItemId = itemId,
                ItemName = itemName,
                IconId = iconId,
                TargetQuantity = quantity,
            });
            return;
        }

        entry.TargetQuantity = checked(entry.TargetQuantity + quantity);
        if (!string.IsNullOrWhiteSpace(itemName)) entry.ItemName = itemName;
        if (iconId > 0) entry.IconId = iconId;
    }

    private void Save() => _config.Save();
}
