using System;
using System.Numerics;
using GatherBuddy.Plugin;

namespace GatherBuddy.FcMesh.Chest;

public enum FcPublicChestNavigationState : byte
{
    Idle,
    Traveling,
    Navigating,
    AtDestination,
    Blocked,
}

public sealed record FcPublicChestNavigationDiagnostics(
    FcPublicChestNavigationState State,
    FcPublicChestDestination? Destination,
    FcChestObjectResolutionStatus ObjectStatus,
    string LastError);

/// <summary>
/// Framework-thread route for the immutable public destination catalog. A
/// catalog anchor is used only to search the current object table. Every
/// navigation request is rebuilt from the resolved live object's Position;
/// no catalog coordinate is ever passed to vnavmesh.
/// </summary>
public sealed class FcPublicChestNavigator
{
    private readonly FcPublicChestDestinationResolver _resolver;
    private FcPublicChestDestination? _destination;
    private FcChestObjectSearch _search;
    private DateTime _deadlineUtc;
    private DateTime _lastOpenAttemptUtc;
    private bool _navigationRequested;
    private bool _openRequested;

    public FcPublicChestNavigator(FcPublicChestDestinationResolver? resolver = null)
        => _resolver = resolver ?? new FcPublicChestDestinationResolver();

    public FcPublicChestNavigationState State { get; private set; } = FcPublicChestNavigationState.Idle;
    public FcChestObjectResolutionStatus ObjectStatus { get; private set; } = FcChestObjectResolutionStatus.InvalidSearch;
    public string LastError { get; private set; } = string.Empty;
    public FcPublicChestDestination? Destination => _destination;

    public FcPublicChestNavigationDiagnostics Diagnostics
        => new(State, _destination, ObjectStatus, LastError);

    public bool TryStart(FcPublicChestDestination destination, out string error)
    {
        Stop();
        if (destination is null)
        {
            error = "Public Company Chest destination is unavailable.";
            return Fail(error);
        }
        if (!Lifestream.Enabled)
        {
            error = "Lifestream is unavailable; public Company Chest routing is blocked.";
            return Fail(error);
        }

        var search = _resolver.BuildSearch(destination);
        if (!search.Succeeded || !search.Search.IsValid)
        {
            error = search.Error.Length == 0
                ? "Public Company Chest stable object resolution is unavailable."
                : search.Error;
            return Fail(error);
        }

        try
        {
            if (Dalamud.ClientState.TerritoryType != search.Search.TerritoryId)
            {
                if (Lifestream.IsBusy())
                {
                    error = "Lifestream is already busy; public Company Chest routing is waiting.";
                    return Fail(error);
                }
                if (!Lifestream.AethernetTeleport(destination.AethernetShard))
                {
                    error = "Lifestream rejected the public Company Chest aethernet route.";
                    return Fail(error);
                }
            }
        }
        catch (Exception exception)
        {
            error = $"Public Company Chest route request failed: {exception.Message}";
            return Fail(error);
        }

        _destination = destination;
        _search = search.Search;
        _deadlineUtc = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        _navigationRequested = false;
        _openRequested = false;
        _lastOpenAttemptUtc = default;
        LastError = string.Empty;
        ObjectStatus = FcChestObjectResolutionStatus.Missing;
        State = FcPublicChestNavigationState.Traveling;
        error = string.Empty;
        return true;
    }

    public void Tick()
    {
        if (State is not (FcPublicChestNavigationState.Traveling
            or FcPublicChestNavigationState.Navigating)
            || _destination is null)
            return;
        if (DateTime.UtcNow >= _deadlineUtc)
        {
            Fail("Public Company Chest route did not resolve a live object before timeout.");
            return;
        }

        try
        {
            if (Lifestream.IsBusy())
                return;
        }
        catch (Exception exception)
        {
            Fail($"Public Company Chest route status failed: {exception.Message}");
            return;
        }

        if (Dalamud.ClientState.TerritoryType != _search.TerritoryId)
            return;

        var resolved = _resolver.ResolveLive(_destination);
        ObjectStatus = resolved.Status;
        if (!resolved.Succeeded)
        {
            if (resolved.Status is FcChestObjectResolutionStatus.Ambiguous
                or FcChestObjectResolutionStatus.NotTargetable
                or FcChestObjectResolutionStatus.WrongTerritory
                or FcChestObjectResolutionStatus.InvalidSearch)
                Fail(resolved.Message);
            return;
        }

        var player = Dalamud.Objects.LocalPlayer;
        if (player is null)
            return;
        var target = resolved.Object!.Value.Position;
        if (Vector3.Distance(player.Position, target) > 3f)
        {
            if (!VNavmesh.Enabled)
            {
                Fail("vnavmesh is unavailable; public Company Chest navigation is blocked.");
                return;
            }
            try
            {
                if (!_navigationRequested
                    || (!VNavmesh.Path.IsRunning() && !VNavmesh.SimpleMove.PathfindInProgress()))
                {
                    if (!VNavmesh.SimpleMove.PathfindAndMoveCloseTo(target, false, 2f))
                    {
                        Fail("vnavmesh rejected navigation to the live public Company Chest position.");
                        return;
                    }
                    _navigationRequested = true;
                    State = FcPublicChestNavigationState.Navigating;
                }
            }
            catch (Exception exception)
            {
                Fail($"Public Company Chest navigation failed: {exception.Message}");
            }
            return;
        }

        _navigationRequested = false;
        State = FcPublicChestNavigationState.AtDestination;
    }

    public bool TryOpen(out string error)
    {
        if (State != FcPublicChestNavigationState.AtDestination || _destination is null)
        {
            error = "Public Company Chest route has not reached a live target.";
            return false;
        }
        if (_openRequested && DateTime.UtcNow - _lastOpenAttemptUtc < TimeSpan.FromSeconds(2))
        {
            error = string.Empty;
            return true;
        }
        if (!_resolver.TryOpenLive(_destination, out error))
            return false;
        _openRequested = true;
        _lastOpenAttemptUtc = DateTime.UtcNow;
        return true;
    }

    public void Stop()
    {
        if (State is FcPublicChestNavigationState.Traveling
            or FcPublicChestNavigationState.Navigating)
        {
            try
            {
                if (VNavmesh.Enabled)
                    VNavmesh.Path.Stop?.Invoke();
            }
            catch
            {
                // Stop is best effort; it must not convert route containment
                // into a physical chest action.
            }
            try
            {
                if (Lifestream.Enabled && Lifestream.IsBusy())
                    _ = Lifestream.TryAbort(out _);
            }
            catch
            {
            }
        }

        _destination = null;
        _search = default;
        _navigationRequested = false;
        _openRequested = false;
        _lastOpenAttemptUtc = default;
        ObjectStatus = FcChestObjectResolutionStatus.InvalidSearch;
        LastError = string.Empty;
        State = FcPublicChestNavigationState.Idle;
    }

    private bool Fail(string error)
    {
        LastError = error;
        State = FcPublicChestNavigationState.Blocked;
        return false;
    }
}
