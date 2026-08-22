using System;
using System.Numerics;
using GatherBuddy.Plugin;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.SeFunctions;

namespace GatherBuddy.FcMesh.Chest;

/// <summary>
/// Framework-thread housing route using the installed Lifestream command
/// boundary. Arrival is not inferred from IPC completion: territory and a
/// unique targetable stable Company Chest object must both be observable.
/// Registration and routing use the installed typed current-plot/address IPC;
/// arrival still requires territory and a unique targetable stable Company
/// Chest object, with ambiguous/missing object cases failing closed.
/// </summary>
public sealed class FcLifestreamHousingRouteAdapter : IFcChestRouteAdapter
{
    private readonly IFcChestObjectResolver _resolver;
    private readonly FcPublicChestNavigator _publicNavigator;
    private FcEstateChestLocationRecord? _location;
    private FcPublicChestDestination? _publicDestination;
    private DateTime _deadlineUtc;
    private bool _navigationRequested;
    private bool _openRequested;
    private DateTime _lastOpenAttemptUtc;
    private bool _housingCommandStarted;

    public FcLifestreamHousingRouteAdapter(
        IFcChestObjectResolver resolver,
        FcPublicChestNavigator? publicNavigator = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _publicNavigator = publicNavigator ?? new FcPublicChestNavigator();
    }

    public bool IsAvailable => Lifestream.Enabled;
    public FcChestRouteStatus Status { get; private set; } = FcChestRouteStatus.Unavailable;
    public string LastError { get; private set; } = string.Empty;

    public bool TryStart(FcEstateChestLocationRecord location, out string error)
    {
        _publicDestination = null;
        _publicNavigator.Stop();
        if (location is null)
        {
            error = "FC housing location is unavailable.";
            LastError = error;
            Status = FcChestRouteStatus.Failed;
            return false;
        }
        if (!IsAvailable)
        {
            error = "Lifestream is unavailable; FC housing route is waiting.";
            LastError = error;
            Status = FcChestRouteStatus.Unavailable;
            return false;
        }
        if (string.IsNullOrWhiteSpace(location.Housing.HousingDistrict))
        {
            error = "Registered FC location has no housing district; route is blocked.";
            LastError = error;
            Status = FcChestRouteStatus.Failed;
            return false;
        }
        var player = Dalamud.Objects.LocalPlayer;
        if (player is null)
        {
            error = "Current player is unavailable; FC housing route is waiting.";
            LastError = error;
            Status = FcChestRouteStatus.Failed;
            return false;
        }
        var currentWorld = player.CurrentWorld.Value.Name.ToString();
        if (!string.Equals(currentWorld, location.Housing.World, StringComparison.Ordinal))
        {
            if (!Lifestream.TryTpAndChangeWorld(
                    location.Housing.World,
                    false,
                    string.Empty,
                    true,
                    null,
                    null,
                    null,
                    out error))
            {
                LastError = error;
                Status = FcChestRouteStatus.Failed;
                return false;
            }
            _housingCommandStarted = false;
        }
        else if (!Lifestream.TryEnterHousing(
                 location.Housing.World,
                 location.Housing.HousingDistrict,
                 location.Housing.Ward,
                 location.Housing.Plot,
                 location.Housing.IsSubdivision,
                 out error))
        {
            LastError = error;
            Status = FcChestRouteStatus.Failed;
            return false;
        }
        else
        {
            _housingCommandStarted = true;
        }

        _location = location;
        _deadlineUtc = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        _navigationRequested = false;
        _openRequested = false;
        _lastOpenAttemptUtc = default;
        LastError = string.Empty;
        Status = FcChestRouteStatus.Traveling;
        return true;
    }

    public bool TryStartPublic(FcPublicChestDestination destination, out string error)
    {
        _location = null;
        _publicDestination = null;
        _navigationRequested = false;
        _openRequested = false;
        _lastOpenAttemptUtc = default;
        _housingCommandStarted = false;
        if (destination is null)
        {
            error = "Public Company Chest destination is unavailable.";
            LastError = error;
            Status = FcChestRouteStatus.Failed;
            return false;
        }
        if (!IsAvailable)
        {
            error = "Lifestream is unavailable; public Company Chest route is waiting.";
            LastError = error;
            Status = FcChestRouteStatus.Unavailable;
            return false;
        }
        if (!_publicNavigator.TryStart(destination, out error))
        {
            LastError = error;
            Status = FcChestRouteStatus.Failed;
            return false;
        }
        _publicDestination = destination;
        _deadlineUtc = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        LastError = string.Empty;
        Status = FcChestRouteStatus.Traveling;
        return true;
    }

    public void Tick()
    {
        if (Status != FcChestRouteStatus.Traveling || _location is not { } location)
        {
            if (Status != FcChestRouteStatus.Traveling || _publicDestination is null)
                return;
            _publicNavigator.Tick();
            if (_publicNavigator.State == FcPublicChestNavigationState.AtDestination)
                Status = FcChestRouteStatus.AtDestination;
            else if (_publicNavigator.State == FcPublicChestNavigationState.Blocked)
            {
                LastError = _publicNavigator.LastError;
                Status = FcChestRouteStatus.Failed;
            }
            return;
        }
        if (DateTime.UtcNow >= _deadlineUtc)
        {
            LastError = "Lifestream housing route did not resolve a live Company Chest before timeout.";
            Status = FcChestRouteStatus.Failed;
            return;
        }
        if (!Lifestream.Enabled)
        {
            LastError = "Lifestream became unavailable while routing to the FC estate.";
            Status = FcChestRouteStatus.Failed;
            return;
        }
        try
        {
            if (Lifestream.IsBusy())
                return;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            Status = FcChestRouteStatus.Failed;
            return;
        }

        if (!_housingCommandStarted)
        {
            var player = Dalamud.Objects.LocalPlayer;
            if (player is null
                || !string.Equals(player.CurrentWorld.Value.Name.ToString(), location.Housing.World, StringComparison.Ordinal))
                return;
            if (!Lifestream.TryEnterHousing(
                    location.Housing.World,
                    location.Housing.HousingDistrict,
                    location.Housing.Ward,
                    location.Housing.Plot,
                    location.Housing.IsSubdivision,
                    out var housingError))
            {
                LastError = housingError;
                Status = FcChestRouteStatus.Failed;
                return;
            }
            _housingCommandStarted = true;
            return;
        }

        if (Dalamud.ClientState.TerritoryType != location.Environment.TerritoryId)
            return;

        if (!Lifestream.TryGetCurrentPlotInfo(
                out var residentialKind,
                out var zeroBasedWard,
                out var zeroBasedPlot,
                out var plotError))
        {
            LastError = plotError;
            return;
        }
        if (!MatchesHousingPlot(
                location.Housing,
                residentialKind,
                zeroBasedWard,
                zeroBasedPlot))
            return;

        if (!HousingManager.TryGetCurrentFreeCompanyEstate(
                out _,
                out _,
                out _,
                out var currentDivision,
                out var identityError))
        {
            LastError = identityError;
            Status = FcChestRouteStatus.Failed;
            return;
        }
        if ((currentDivision == 2) != location.Housing.IsSubdivision)
        {
            LastError = "Current housing division does not match the registered FC estate route.";
            Status = FcChestRouteStatus.Failed;
            return;
        }

        var resolved = _resolver is FcDalamudChestObjectResolver live
            ? live.ResolveStable(location.Chest, location.Environment.TerritoryId)
            : ResolveWithoutStableBinding(location);
        if (resolved.Succeeded)
        {
            var player = Dalamud.Objects.LocalPlayer;
            if (player is null)
                return;
            var distance = Vector3.Distance(player.Position, resolved.Object!.Value.Position);
            if (distance > 3f)
            {
                if (!VNavmesh.Enabled)
                {
                    LastError = "vnavmesh is unavailable; live Company Chest navigation is blocked.";
                    Status = FcChestRouteStatus.Failed;
                    return;
                }
                try
                {
                    if (!_navigationRequested || !VNavmesh.SimpleMove.PathfindInProgress())
                    {
                        if (!VNavmesh.SimpleMove.PathfindAndMoveCloseTo(resolved.Object.Value.Position, false, 2f))
                        {
                            LastError = "vnavmesh rejected navigation to the live Company Chest position.";
                            Status = FcChestRouteStatus.Failed;
                            return;
                        }
                        _navigationRequested = true;
                    }
                }
                catch (Exception exception)
                {
                    LastError = exception.Message;
                    Status = FcChestRouteStatus.Failed;
                }
                return;
            }
            _navigationRequested = false;
            Status = FcChestRouteStatus.AtDestination;
            return;
        }
        if (resolved.Status is FcChestObjectResolutionStatus.Ambiguous
            or FcChestObjectResolutionStatus.NotTargetable)
        {
            LastError = resolved.Message;
            Status = FcChestRouteStatus.Failed;
        }
    }

    public bool TryOpenChest(FcEstateChestLocationRecord location, out string error)
    {
        if (_location is null || Status != FcChestRouteStatus.AtDestination)
        {
            error = "FC route has not reached the live Company Chest.";
            return false;
        }
        if (_openRequested && DateTime.UtcNow - _lastOpenAttemptUtc < TimeSpan.FromSeconds(2))
        {
            error = string.Empty;
            return true;
        }
        if (_resolver is not FcDalamudChestObjectResolver live)
        {
            error = "Production chest interaction requires the live object-table resolver.";
            return false;
        }
        if (!live.TryOpenStable(location.Chest, location.Environment.TerritoryId, out error))
            return false;
        _openRequested = true;
        _lastOpenAttemptUtc = DateTime.UtcNow;
        return true;
    }

    public bool TryOpenPublic(FcPublicChestDestination destination, out string error)
    {
        if (_publicDestination is null
            || !string.Equals(_publicDestination.Key, destination?.Key, StringComparison.Ordinal))
        {
            error = "Public Company Chest route destination is not active.";
            return false;
        }
        return _publicNavigator.TryOpen(out error);
    }

    public void Stop()
    {
        if (Status == FcChestRouteStatus.Traveling && Lifestream.Enabled)
        {
            try { _ = Lifestream.TryAbort(out _); }
            catch { }
        }
        _location = null;
        _publicDestination = null;
        _publicNavigator.Stop();
        _navigationRequested = false;
        _openRequested = false;
        _lastOpenAttemptUtc = default;
        _housingCommandStarted = false;
        Status = FcChestRouteStatus.Unavailable;
    }

    private FcChestObjectResolution ResolveWithoutStableBinding(FcEstateChestLocationRecord location)
        => new(
            FcChestObjectResolutionStatus.InvalidSearch,
            null,
            "Production route requires the live stable Company Chest resolver.");

    private static bool MatchesHousingPlot(
        FcHousingAddress address,
        int residentialKind,
        int zeroBasedWard,
        int zeroBasedPlot)
    {
        var expectedKind = address.HousingDistrict switch
        {
            "Lavender Beds" => 2,
            "Mist" => 8,
            "Goblet" => 9,
            "Empyreum" => 70,
            "Shirogane" => 111,
            _ => 0,
        };
        return expectedKind != 0
            && residentialKind == expectedKind
            && zeroBasedWard is >= 0 and <= 29
            && zeroBasedPlot is >= 0 and <= 59
            && (uint)zeroBasedWard + 1u == address.Ward
            && (uint)zeroBasedPlot + 1u == address.Plot;
    }
}
