using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Crafting;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Publication;
using GatherBuddy.FcMesh.Sessions;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Capabilities;

public sealed class FcCapabilityService : IDisposable
{
    private readonly FcCapabilityPublicationService _publication;
    private readonly IFcCapabilityAssessmentProvider _assessor;
    private readonly Func<WorkerSessionRecord?> _sessionProvider;
    private readonly Func<FcCompatibilityContext> _compatibilityProvider;
    private readonly Func<string?> _worldFingerprintProvider;
    private readonly Func<uint, uint> _recipeJobResolver;
    private readonly IFcClock _clock;
    private readonly FcLivenessOptions _livenessOptions;
    private readonly Dictionary<FcCapabilityCacheKey, FcCapabilityAssessment> _cache = new();
    private readonly Dictionary<(uint RecipeId, bool IsPrecraft), string> _lastEnvironment = new();
    private Guid _lastSessionId;
    private ulong _lastSessionGeneration;
    private string? _lastWorldFingerprint;
    private string? _lastGameVersion;
    private string? _lastPlannerFingerprint;
    private bool _requiresReassessment;
    private Guid _reassessmentRequestId;
    private ulong _reassessmentRevision;
    private bool _disposed;

    public event Action? CapabilityInvalidated;

    public FcCapabilityService(
        FcCapabilityPublicationService publication,
        Func<WorkerSessionRecord?> sessionProvider,
        Func<FcCompatibilityContext> compatibilityProvider,
        Func<string?> worldFingerprintProvider,
        Func<uint, uint> recipeJobResolver,
        IFcCapabilityAssessmentProvider? assessor = null,
        IFcClock? clock = null,
        FcLivenessOptions? livenessOptions = null)
    {
        _publication = publication ?? throw new ArgumentNullException(nameof(publication));
        _sessionProvider = sessionProvider ?? throw new ArgumentNullException(nameof(sessionProvider));
        _compatibilityProvider = compatibilityProvider ?? throw new ArgumentNullException(nameof(compatibilityProvider));
        _worldFingerprintProvider = worldFingerprintProvider ?? throw new ArgumentNullException(nameof(worldFingerprintProvider));
        _recipeJobResolver = recipeJobResolver ?? throw new ArgumentNullException(nameof(recipeJobResolver));
        _assessor = assessor ?? new FcLiveCapabilityAssessmentProvider(_recipeJobResolver);
        _clock = clock ?? FcSystemClock.Instance;
        _livenessOptions = livenessOptions ?? FcLivenessOptions.Default;
    }

    public FcCapabilityPublicationService Publication => _publication;
    public CapabilityRequestRecord? CurrentRequest => _publication.CurrentRequest;
    public string? CurrentWorldFingerprint => _worldFingerprintProvider();
    public IReadOnlyList<CapabilityResponseRecord> PublishedResponses => _publication.Responses;
    public FcCapabilityPublicationDiagnostics Diagnostics => _publication.Diagnostics;

    public IReadOnlyCollection<FcCapabilityAssessment> CachedAssessments
    {
        get => _cache.Values.ToArray();
    }

    /// <summary>
    /// Rebuilds a recovery proof from non-authoritative ticket identity. A
    /// missing/expired/mismatched request remains blocked; callers must not
    /// turn that failure into a private-list recovery.
    /// </summary>
    public bool TryCreateRecoveryExecutionProof(
        FcCapabilityRecoveryIdentity identity,
        out FcCapabilityExecutionProof? proof,
        out string reason)
    {
        proof = null;
        reason = string.Empty;
        if (identity is null || !identity.IsValid)
        {
            reason = "FC recovery has no valid worker session identity.";
            return false;
        }

        var session = _sessionProvider();
        if (session is null
            || session.State is not (FcWorkerState.Active or FcWorkerState.Waiting)
            || session.SessionId != identity.SessionId
            || session.SessionGeneration != identity.SessionGeneration)
        {
            reason = "FC recovery worker session changed or is not active.";
            return false;
        }
        var selection = session.Selection;
        if (selection is null
            || (!selection.AllPublishedLists
                && !(selection.ListIds ?? Array.Empty<Guid>())
                    .OrderBy(value => value)
                    .SequenceEqual(identity.Lists)))
        {
            reason = "FC recovery list scope changed with the worker session.";
            return false;
        }

        if (identity.RequestId == Guid.Empty && identity.RequiresHq)
        {
            reason = "FC recovery lost the HQ capability request identity; NQ fallback is forbidden.";
            return false;
        }
        if (identity.RequestId == Guid.Empty)
            return TryCreateNqExecutionProof(out proof, out reason);

        var request = _publication.CurrentRequest;
        if (request is null || request.RequestId != identity.RequestId)
        {
            reason = "FC recovery capability request is missing or changed.";
            return false;
        }
        if (!TryPreflightRequest(request, out reason))
            return false;
        return TryCreateExecutionProof(request, out proof, out reason);
    }

    public void Tick()
    {
        if (_disposed)
            return;
        var session = _sessionProvider();
        if (session is null
            || session.State is not (FcWorkerState.Active or FcWorkerState.Waiting)
            || session.Header is null
            || session.SessionId == Guid.Empty
            || session.SessionGeneration == 0)
            return;

        if (_lastSessionId != session.SessionId || _lastSessionGeneration != session.SessionGeneration)
        {
            _cache.Clear();
            _lastEnvironment.Clear();
            _lastSessionId = session.SessionId;
            _lastSessionGeneration = session.SessionGeneration;
            RequireReassessment();
        }

        var world = _publication.WorldStore;
        if (world is null)
            return;
        var compatibility = _compatibilityProvider();
        var worldFingerprint = _worldFingerprintProvider();
        if (!compatibility.IsValid || string.IsNullOrWhiteSpace(worldFingerprint))
            return;

        foreach (var request in world.CapabilityRequests.Values
                     .Where(value => !_publication.IsForkedRequest(value))
                     .Where(value => value.ExpiresAt.PhysicalUnixMs > _clock.UnixMilliseconds)
                     .Where(value => string.Equals(value.WorldFingerprint, worldFingerprint, StringComparison.Ordinal))
                     .Where(value => string.IsNullOrWhiteSpace(value.GameVersion)
                         || string.Equals(value.GameVersion, compatibility.GameVersion, StringComparison.Ordinal))
                     .Where(value => string.IsNullOrWhiteSpace(value.PlannerFingerprint)
                         || string.Equals(value.PlannerFingerprint,
                             FcCapabilityFingerprints.Planner(compatibility.PlannerSemanticsVersion, compatibility.GameVersion),
                             StringComparison.Ordinal)))
        {
            var assessments = AssessRequest(request, session, compatibility, worldFingerprint);
            if (assessments.Count == 0)
                continue;
            var current = _publication.Responses
                .Where(response => response.RequestId == request.RequestId)
                .OrderByDescending(response => response.Header.Revision)
                .FirstOrDefault();
            if (current is not null
                && current.ExpiresAt.PhysicalUnixMs > _clock.UnixMilliseconds
                && (!_requiresReassessment
                    || (_reassessmentRevision > 0
                        && current.Header.Revision >= _reassessmentRevision
                        && (_reassessmentRequestId == Guid.Empty
                            || _reassessmentRequestId == request.RequestId)))
                && FcWorldProjection.IsCapabilityValid(
                    request,
                    current,
                    session,
                    world.GetWorkerHlc(session.Header.OwnerAuthorId)
                        ?? new FcHlcTimestamp(_clock.UnixMilliseconds, 0, session.Header.OwnerAuthorId),
                    worldFingerprint,
                    _clock,
                    _livenessOptions)
                && MatchesAssessments(current, assessments))
            {
                if (_requiresReassessment)
                {
                    _requiresReassessment = false;
                    _reassessmentRequestId = Guid.Empty;
                    _reassessmentRevision = 0;
                }
                continue;
            }
            NotifyCapabilityInvalidated();
            var publication = _publication.PublishResponse(request, assessments);
            if (publication.Accepted)
            {
                _requiresReassessment = true;
                _reassessmentRequestId = publication.RequestId;
                _reassessmentRevision = Math.Max(_reassessmentRevision, publication.Revision);
            }
        }
    }

    public IReadOnlyList<FcCapabilityAssessment> AssessRequest(
        CapabilityRequestRecord request,
        WorkerSessionRecord session,
        FcCompatibilityContext? compatibility = null,
        string? worldFingerprint = null,
        bool useCache = true)
    {
        if (request is null || session is null)
            return Array.Empty<FcCapabilityAssessment>();
        var resolvedCompatibility = compatibility ?? _compatibilityProvider();
        var resolvedWorld = worldFingerprint ?? _worldFingerprintProvider();
        if (!resolvedCompatibility.IsValid || string.IsNullOrWhiteSpace(resolvedWorld))
            return Array.Empty<FcCapabilityAssessment>();
        if (!string.IsNullOrWhiteSpace(request.WorldFingerprint)
            && !string.Equals(request.WorldFingerprint, resolvedWorld, StringComparison.Ordinal))
            return Array.Empty<FcCapabilityAssessment>();
        var planner = FcCapabilityFingerprints.Planner(
            resolvedCompatibility.PlannerSemanticsVersion,
            resolvedCompatibility.GameVersion);
        if (!string.IsNullOrWhiteSpace(request.GameVersion)
            && !string.Equals(request.GameVersion, resolvedCompatibility.GameVersion, StringComparison.Ordinal))
            return Array.Empty<FcCapabilityAssessment>();
        if (!string.IsNullOrWhiteSpace(request.PlannerFingerprint)
            && !string.Equals(request.PlannerFingerprint, planner, StringComparison.Ordinal))
            return Array.Empty<FcCapabilityAssessment>();
        var contextChanged = _lastWorldFingerprint is not null
            && (!string.Equals(_lastWorldFingerprint, resolvedWorld, StringComparison.Ordinal)
            || !string.Equals(_lastGameVersion, resolvedCompatibility.GameVersion, StringComparison.Ordinal)
            || !string.Equals(_lastPlannerFingerprint, planner, StringComparison.Ordinal));
        if (contextChanged)
        {
            _cache.Clear();
            RequireReassessment();
        }
        _lastWorldFingerprint = resolvedWorld;
        _lastGameVersion = resolvedCompatibility.GameVersion;
        _lastPlannerFingerprint = planner;
        var requested = request.Recipes ?? Array.Empty<RequiredCraftCapability>();
        var results = new List<FcCapabilityAssessment>(requested.Length);
        foreach (var capability in requested.OrderBy(value => value.RecipeId))
        {
            var context = new FcCapabilityAssessmentContext(
                request,
                capability,
                session,
                resolvedCompatibility.GameVersion,
                planner,
                resolvedWorld);
            var environment = _assessor.EnvironmentFingerprint(context);
            var environmentKey = (capability.RecipeId, capability.IsPrecraft);
            if (_lastEnvironment.TryGetValue(environmentKey, out var previousEnvironment)
                && !string.Equals(previousEnvironment, environment, StringComparison.Ordinal))
            {
                foreach (var staleKey in _cache.Keys
                             .Where(key => key.RecipeId == capability.RecipeId
                                 && key.IsPrecraft == capability.IsPrecraft)
                             .ToArray())
                    _cache.Remove(staleKey);
                RequireReassessment(request.RequestId);
            }
            _lastEnvironment[environmentKey] = environment;
            var key = new FcCapabilityCacheKey(
                capability.RecipeId,
                capability.IsPrecraft,
                FcCapabilityFingerprints.Quality(capability.EffectiveQualityPolicy),
                resolvedWorld,
                resolvedCompatibility.GameVersion,
                planner,
                environment,
                environment);
            if (useCache && _cache.TryGetValue(key, out var cached))
            {
                results.Add(cached);
                continue;
            }
            var assessment = FcCapabilityAssessor.SelectBest(
                context,
                _assessor.Assess(context),
                _recipeJobResolver);
            _cache[key] = assessment;
            results.Add(assessment);
        }
        return results;
    }

    /// <summary>
    /// Reassesses every capability immediately before a local craft starts.
    /// This path deliberately bypasses the cache: cached state is useful for
    /// publication cadence, never as the final craft admission proof.
    /// </summary>
    public bool TryPreflightRequest(
        CapabilityRequestRecord request,
        out string reason)
    {
        reason = string.Empty;
        if (request is null)
        {
            reason = "Capability request is unavailable.";
            return false;
        }
        var session = _sessionProvider();
        var compatibility = _compatibilityProvider();
        var worldFingerprint = _worldFingerprintProvider();
        if (session is null
            || session.State is not (FcWorkerState.Active or FcWorkerState.Waiting)
            || session.Header is null
            || session.SessionId == Guid.Empty
            || session.SessionGeneration == 0)
        {
            reason = "The local subscribed worker session is not active.";
            return false;
        }
        if (!compatibility.IsValid || string.IsNullOrWhiteSpace(worldFingerprint)
            || !string.Equals(request.WorldFingerprint, worldFingerprint, StringComparison.Ordinal))
        {
            reason = "Capability request belongs to an incompatible world or game context.";
            return false;
        }
        if (request.ExpiresAt.PhysicalUnixMs <= _clock.UnixMilliseconds)
        {
            reason = "Capability request has expired.";
            return false;
        }
        if (_requiresReassessment
            && (_reassessmentRequestId == Guid.Empty || _reassessmentRequestId == request.RequestId))
        {
            reason = "Capability reassessment is pending after a local capability change.";
            return false;
        }
        var response = _publication.Responses
            .Where(value => value.Header is not null
                && value.RequestId == request.RequestId
                && string.Equals(value.Header.OwnerAuthorId, session.Header.OwnerAuthorId, StringComparison.Ordinal))
            .OrderByDescending(value => value.Header.Revision)
            .FirstOrDefault();
        if (response is null)
        {
            reason = "A local capability response is not published yet.";
            return false;
        }
        if (!HasExactCapabilitySet(request.Recipes, response.RequestedRecipes))
        {
            reason = "The local response does not cover the exact current capability request.";
            return false;
        }
        var responderHlc = _publication.WorldStore?.GetWorkerHlc(session.Header.OwnerAuthorId)
            ?? new FcHlcTimestamp(_clock.UnixMilliseconds, 0, session.Header.OwnerAuthorId);
        if (!FcWorldProjection.IsCapabilityValid(
                request,
                response,
                session,
                responderHlc,
                worldFingerprint,
                _clock,
                _livenessOptions))
        {
            reason = "The local capability response is stale, expired, or no longer compatible.";
            RequireReassessment(request.RequestId);
            return false;
        }
        var assessments = AssessRequest(
            request,
            session,
            compatibility,
            worldFingerprint,
            useCache: false);
        if (!MatchesAssessments(response, assessments))
        {
            reason = "Current gear, planner, solver, or Raphael assessment differs from the published response.";
            RequireReassessment(request.RequestId);
            return false;
        }
        foreach (var capability in request.Recipes ?? Array.Empty<RequiredCraftCapability>())
        {
            if (!TryPreflight(
                    request,
                    response,
                    capability,
                    out reason))
            {
                RequireReassessment(request.RequestId);
                return false;
            }
        }
        return true;
    }

    public bool TryCreateExecutionProof(
        CapabilityRequestRecord request,
        out FcCapabilityExecutionProof? proof,
        out string reason)
    {
        proof = null;
        reason = string.Empty;
        if (request is null)
        {
            reason = "Capability request is unavailable.";
            return false;
        }
        var session = _sessionProvider();
        var compatibility = _compatibilityProvider();
        var worldFingerprint = _worldFingerprintProvider();
        if (session is null
            || session.Header is null
            || session.State is not (FcWorkerState.Active or FcWorkerState.Waiting)
            || session.SessionId == Guid.Empty
            || session.SessionGeneration == 0
            || !compatibility.IsValid
            || string.IsNullOrWhiteSpace(worldFingerprint))
        {
            reason = "A compatible active worker session is unavailable for capability proof creation.";
            return false;
        }
        var response = _publication.Responses
            .Where(value => value.Header is not null
                && value.RequestId == request.RequestId
                && string.Equals(value.Header.OwnerAuthorId, session.Header.OwnerAuthorId, StringComparison.Ordinal))
            .OrderByDescending(value => value.Header.Revision)
            .FirstOrDefault();
        if (response is null)
        {
            reason = "No local response exists for the current capability request.";
            return false;
        }
        foreach (var capability in request.Recipes ?? Array.Empty<RequiredCraftCapability>())
        {
            if (EvaluateEligibility(
                    request,
                    response,
                    capability.RecipeId,
                    capability.IsPrecraft,
                    capability.EffectiveQualityPolicy)
                != FcCapabilityEligibility.EligibleGuaranteed)
            {
                reason = "The local response is not currently guaranteed for every requested HQ capability.";
                return false;
            }
        }
        proof = new FcCapabilityExecutionProof(
            request.RequestId,
            session.SessionId,
            session.SessionGeneration,
            worldFingerprint,
            compatibility.GameVersion,
            FcCapabilityFingerprints.Planner(
                compatibility.PlannerSemanticsVersion,
                compatibility.GameVersion),
            response.GearsetFingerprint,
            response.SolverFingerprint,
            FcCapabilityEligibility.EligibleGuaranteed)
        {
            Request = request,
        };
        return true;
    }

    public bool TryCreateNqExecutionProof(
        out FcCapabilityExecutionProof? proof,
        out string reason)
    {
        proof = null;
        reason = string.Empty;
        var session = _sessionProvider();
        var compatibility = _compatibilityProvider();
        var worldFingerprint = _worldFingerprintProvider();
        if (session is null
            || session.Header is null
            || session.State is not (FcWorkerState.Active or FcWorkerState.Waiting)
            || session.SessionId == Guid.Empty
            || session.SessionGeneration == 0
            || !compatibility.IsValid
            || string.IsNullOrWhiteSpace(worldFingerprint))
        {
            reason = "A compatible active worker session is unavailable for NQ capability proof creation.";
            return false;
        }
        proof = new FcCapabilityExecutionProof(
            Guid.Empty,
            session.SessionId,
            session.SessionGeneration,
            worldFingerprint,
            compatibility.GameVersion,
            FcCapabilityFingerprints.Planner(
                compatibility.PlannerSemanticsVersion,
                compatibility.GameVersion),
            string.Empty,
            string.Empty,
            FcCapabilityEligibility.NqAllowed);
        return true;
    }

    /// <summary>
    /// Actual queue-entry admission. The plan proof is not trusted as a
    /// cached quality result: HQ proofs trigger a fresh request/response,
    /// gear, solver, and Raphael assessment immediately before side effects.
    /// </summary>
    public bool TryPreflightExecution(
        FcCapabilityExecutionProof? proof,
        out string reason)
    {
        reason = string.Empty;
        if (proof is null)
        {
            reason = "FC execution has no capability admission proof.";
            return false;
        }
        var session = _sessionProvider();
        var compatibility = _compatibilityProvider();
        var worldFingerprint = _worldFingerprintProvider();
        if (session is null
            || session.Header is null
            || session.State is not (FcWorkerState.Active or FcWorkerState.Waiting)
            || session.SessionId != proof.SessionId
            || session.SessionGeneration != proof.SessionGeneration
            || !string.Equals(worldFingerprint, proof.WorldFingerprint, StringComparison.Ordinal)
            || !compatibility.IsValid
            || !string.Equals(compatibility.GameVersion, proof.GameVersion, StringComparison.Ordinal)
            || !string.Equals(
                FcCapabilityFingerprints.Planner(
                    compatibility.PlannerSemanticsVersion,
                    compatibility.GameVersion),
                proof.PlannerFingerprint,
                StringComparison.Ordinal))
        {
            reason = "FC execution capability proof no longer matches the active worker or compatibility context.";
            RequireReassessment(proof.RequestId);
            return false;
        }
        if (proof.Eligibility == FcCapabilityEligibility.NqAllowed
            && proof.RequestId == Guid.Empty)
            return true;
        if (proof.Eligibility != FcCapabilityEligibility.EligibleGuaranteed
            || proof.RequestId == Guid.Empty)
        {
            reason = "FC execution capability proof is not an eligible HQ guarantee.";
            RequireReassessment(proof.RequestId);
            return false;
        }
        var request = _publication.CurrentRequest;
        if (request is null
            || request.Header is null
            || request.RequestId != proof.RequestId
            || (proof.Request is not null
                && (proof.Request.Header is null
                    || proof.Request.Header.Revision != request.Header.Revision
                    || !string.Equals(proof.Request.WorldFingerprint, request.WorldFingerprint, StringComparison.Ordinal))))
        {
            reason = "The FC capability request changed after the execution plan was built.";
            RequireReassessment(proof.RequestId);
            return false;
        }
        if (!TryPreflightRequest(request, out reason))
            return false;
        var response = _publication.Responses
            .Where(value => value.Header is not null
                && value.RequestId == request.RequestId
                && string.Equals(value.Header.OwnerAuthorId, session.Header.OwnerAuthorId, StringComparison.Ordinal))
            .OrderByDescending(value => value.Header.Revision)
            .FirstOrDefault();
        if (response is null
            || !string.Equals(response.GearsetFingerprint, proof.GearsetFingerprint, StringComparison.Ordinal)
            || !string.Equals(response.SolverFingerprint, proof.SolverFingerprint, StringComparison.Ordinal))
        {
            reason = "The capability response fingerprints changed after the execution plan was built.";
            RequireReassessment(request.RequestId);
            return false;
        }
        return true;
    }

    public FcCapabilityEligibility EvaluateEligibility(
        CapabilityRequestRecord request,
        CapabilityResponseRecord? response,
        uint recipeId,
        bool isPrecraft,
        FcQualityPolicy policy)
    {
        if (policy is null || request is null)
            return FcCapabilityEligibility.Ineligible;
        var session = _sessionProvider();
        var compatibility = _compatibilityProvider();
        var worldFingerprint = _worldFingerprintProvider();
        if (!compatibility.IsValid || string.IsNullOrWhiteSpace(worldFingerprint)
            || session is null
            || session.State is not (FcWorkerState.Active or FcWorkerState.Waiting)
            || session.Header is null
            || session.SessionId == Guid.Empty
            || session.SessionGeneration == 0)
            return FcCapabilityEligibility.CapabilityPending;
        if (response is null)
            return FcCapabilityEligibility.CapabilityPending;
        if (request.ExpiresAt.PhysicalUnixMs <= _clock.UnixMilliseconds
            || response.ExpiresAt.PhysicalUnixMs <= _clock.UnixMilliseconds
            || !string.Equals(request.WorldFingerprint, worldFingerprint, StringComparison.Ordinal)
            || !string.Equals(response.WorldFingerprint, worldFingerprint, StringComparison.Ordinal)
            || response.Header is null
            || !string.Equals(response.Header.OwnerAuthorId, session.Header.OwnerAuthorId, StringComparison.Ordinal)
            || response.SessionId != session.SessionId
            || response.SessionGeneration != session.SessionGeneration
            || !string.Equals(response.GameVersion, compatibility.GameVersion, StringComparison.Ordinal)
            || !string.Equals(response.PlannerFingerprint,
                FcCapabilityFingerprints.Planner(compatibility.PlannerSemanticsVersion, compatibility.GameVersion),
                StringComparison.Ordinal)
            || response.Results is null)
            return FcCapabilityEligibility.CapabilityPending;
        if (!HasExactCapabilitySet(request.Recipes, response.RequestedRecipes))
            return FcCapabilityEligibility.CapabilityPending;
        var responderHlc = _publication.WorldStore?.GetWorkerHlc(session.Header.OwnerAuthorId)
            ?? new FcHlcTimestamp(_clock.UnixMilliseconds, 0, session.Header.OwnerAuthorId);
        if (!FcWorldProjection.IsCapabilityValid(
                request,
                response,
                session,
                responderHlc,
                worldFingerprint,
                _clock,
                _livenessOptions))
            return FcCapabilityEligibility.CapabilityPending;
        var result = response.Results.FirstOrDefault(value => value.RecipeId == recipeId);
        if (result is null)
            return FcCapabilityEligibility.CapabilityPending;
        var requiredJob = _recipeJobResolver(recipeId);
        if (requiredJob == 0 || result.SelectedJobId != requiredJob)
            return FcCapabilityEligibility.Ineligible;
        if (!result.CanCraft)
            return FcCapabilityEligibility.Ineligible;
        var requiresHq = policy.Rules.Any(rule => rule.Quality == FcItemQuality.Hq);
        if (!requiresHq)
            return FcCapabilityEligibility.NqAllowed;
        return result.GuaranteesRequiredQuality
            && result.Assessment == FcRaphaelAssessmentOutcome.FullQuality
            ? FcCapabilityEligibility.EligibleGuaranteed
            : FcCapabilityEligibility.Ineligible;
    }

    public bool TryPreflight(
        CapabilityRequestRecord request,
        CapabilityResponseRecord response,
        RequiredCraftCapability capability,
        out string reason)
    {
        reason = string.Empty;
        var eligibility = EvaluateEligibility(
            request,
            response,
            capability.RecipeId,
            capability.IsPrecraft,
            capability.EffectiveQualityPolicy);
        if (eligibility is FcCapabilityEligibility.EligibleGuaranteed or FcCapabilityEligibility.NqAllowed)
            return true;
        reason = eligibility switch
        {
            FcCapabilityEligibility.CapabilityPending => "Fresh local capability response is pending or stale.",
            FcCapabilityEligibility.Ineligible => "The current local Raphael assessment does not guarantee the requested quality.",
            _ => "The current capability response is not eligible.",
        };
        return false;
    }

    public void InvalidateAll()
    {
        _cache.Clear();
        _lastEnvironment.Clear();
        _lastWorldFingerprint = null;
        _lastGameVersion = null;
        _lastPlannerFingerprint = null;
        RequireReassessment();
    }

    public void InvalidateWorld(string worldFingerprint)
    {
        var invalidated = false;
        foreach (var key in _cache.Keys.Where(key => !string.Equals(key.WorldFingerprint, worldFingerprint, StringComparison.Ordinal)).ToArray())
        {
            _cache.Remove(key);
            invalidated = true;
        }
        if (invalidated)
            RequireReassessment();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cache.Clear();
        _lastEnvironment.Clear();
        _publication.Dispose();
    }

    private static bool MatchesAssessments(
        CapabilityResponseRecord response,
        IReadOnlyList<FcCapabilityAssessment> assessments)
        => response is not null
            && response.Results is not null
            && response.Results.Length == assessments.Count
            && string.Equals(response.GearsetFingerprint, AggregateFingerprint("gear", assessments.Select(value => value.GearsetFingerprint)), StringComparison.Ordinal)
            && string.Equals(response.SolverFingerprint, AggregateFingerprint("solver", assessments.Select(value => value.SolverFingerprint)), StringComparison.Ordinal)
            && response.Results.OrderBy(value => value.RecipeId).Zip(
                assessments.OrderBy(value => value.RecipeId),
                (result, assessment) => result.RecipeId == assessment.RecipeId
                    && result.CanCraft == assessment.CanCraft
                    && result.SelectedJobId == assessment.SelectedJobId
                    && result.GuaranteesRequiredQuality == assessment.GuaranteesRequiredQuality
                    && result.Assessment == assessment.ToWireResult().Assessment)
                .All(value => value);

    private void NotifyCapabilityInvalidated()
    {
        try
        {
            CapabilityInvalidated?.Invoke();
        }
        catch
        {
            // A diagnostics/replan subscriber cannot weaken capability
            // invalidation or interrupt publication/assessment cadence.
        }
    }

    private void RequireReassessment(Guid requestId = default)
    {
        _requiresReassessment = true;
        if (requestId != Guid.Empty)
            _reassessmentRequestId = requestId;
        NotifyCapabilityInvalidated();
    }

    private static bool HasExactCapabilitySet(
        IReadOnlyList<RequiredCraftCapability>? requested,
        IReadOnlyList<RequiredCraftCapability>? responseRequested)
    {
        if (requested is null || responseRequested is null || requested.Count != responseRequested.Count)
            return false;
        var expected = requested.ToDictionary(value => value.RecipeId);
        foreach (var actual in responseRequested)
        {
            if (!expected.TryGetValue(actual.RecipeId, out var value)
                || value.IsPrecraft != actual.IsPrecraft
                || value.EffectiveQualityPolicy is null
                || actual.EffectiveQualityPolicy is null
                || FcCanonical.Hash(value.EffectiveQualityPolicy)
                    != FcCanonical.Hash(actual.EffectiveQualityPolicy))
                return false;
        }
        return true;
    }

    private static string AggregateFingerprint(string kind, IEnumerable<string> values)
    {
        var joined = string.Join("|", values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .OrderBy(value => value, StringComparer.Ordinal));
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(kind + "-v1|" + joined)))
            .ToLowerInvariant();
    }
}
