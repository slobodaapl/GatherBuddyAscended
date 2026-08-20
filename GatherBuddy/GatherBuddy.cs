using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using GatherBuddy.Alarms;
using GatherBuddy.Config;
using GatherBuddy.CustomInfo;
using GatherBuddy.Data;
using GatherBuddy.Enums;
using GatherBuddy.FishTimer;
using GatherBuddy.GatherHelper;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Crafting;
using GatherBuddy.Crafting.Acquisition;
using GatherBuddy.Gui;
using GatherBuddy.Marketboard;
using GatherBuddy.Plugin;
using GatherBuddy.SeFunctions;
using GatherBuddy.Spearfishing;
using GatherBuddy.Weather;
using SigScannerWrapper = GatherBuddy.SeFunctions.SigScannerWrapper;
using ElliLib;
using ElliLib.Classes;
using ElliLib.Log;
using GatherBuddy.AutoGather;
using GatherBuddy.FcMesh.Chest;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Native;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Publication;
using GatherBuddy.FcMesh.Sessions;
using GatherBuddy.FcMesh.State;
using Dalamud.IoC;
using Dalamud.Game.ClientState.Objects.SubKinds;
using ElliCon.Core;

namespace GatherBuddy;

public partial class GatherBuddy : IDalamudPlugin
{
    public const string InternalName = "GatherBuddyAscended";

    public string Name
        => InternalName;

    public static string Version = string.Empty;

    public static Configuration  Config   { get; private set; } = null!;
    public static GameData       GameData { get; private set; } = null!;
    public static Logger         Log      { get; private set; } = null!;
    public static ClientLanguage Language { get; private set; } = ClientLanguage.English;
    public static SeTime         Time     { get; private set; } = null!;
#if DEBUG
    public static bool DebugMode { get; private set; } = true;
#else
    public static bool DebugMode { get; private set; } = false;
#endif

    public static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMilliseconds(1500),
    };

    private static readonly TimeSpan FrameworkDispatchTimeout = TimeSpan.FromSeconds(5);

    public static WeatherManager        WeatherManager  { get; private set; } = null!;
    public static UptimeManager         UptimeManager   { get; private set; } = null!;
    public static FishLog               FishLog         { get; private set; } = null!;
    public static EventFramework        EventFramework  { get; private set; } = null!;
    public static CurrentBait           CurrentBait     { get; private set; } = null!;
    public static SeTugType             TugType         { get; private set; } = null!;
    public static WaymarkManager        WaymarkManager  { get; private set; } = null!;
    public static AutoGather.AutoGather AutoGather      { get; private set; } = null!;
    public static AutoHookIntegration.BiteTimerService BiteTimerService { get; private set; } = null!;
    public static AutoGather.Collectables.CollectableManager CollectableManager { get; private set; } = null!;
    public static Crafting.CraftingListManager CraftingListManager { get; private set; } = null!;
    public static NativeLiveAcquisitionEnvironment? LiveAcquisitionEnvironment { get; private set; }
    public static LiveAcquisitionExecutor? LiveAcquisitionExecutor { get; private set; }
    public static Crafting.RaphaelSolveCoordinator RaphaelSolveCoordinator { get; private set; } = null!;
    public static Crafting.RecipeBrowserSettings RecipeBrowserSettings { get; private set; } = null!;
    public static Crafting.ArtisanIpcShim? ArtisanShim { get; private set; }
    public static FcChestFeasibilityProbe? FcChestProbe { get; private set; }
    public static FcMeshNativeCoordinator? FcMeshNative { get; private set; }
    public static FcPublishedListService? FcPublishedLists { get; private set; }
    public static FcWorkerSessionService? FcWorkerSessions { get; private set; }
    public static FcChestPublicationService? FcChestPublication { get; private set; }
    /// <summary>
    /// Developer-only in-memory FC world. It never reaches native mesh,
    /// physical chest interaction, travel, or player inventory.
    /// </summary>
    public static FcSyntheticFulfillmentDriver? FcSyntheticFulfillment { get; private set; }
    internal static Crafting.NativeRecipeCraftingUi? NativeRecipeCraftingUi { get; private set; }
    public static Gui.CraftingStatusWindow? CraftingStatusWindow { get; private set; }
    public static Gui.VulcanWindow? VulcanWindow { get; private set; }
    public static Gui.CraftingMaterialsWindow? CraftingMaterialsWindow { get; private set; }
    public static Gui.CraftingTreeWindow? CraftingTreeWindow { get; private set; }
    public static Gui.VendorBuyListWindow? VendorBuyListWindow { get; private set; }
    public static Gui.MarketplaceBuyListWindow? MarketplaceBuyListWindow { get; private set; }
    public static Gui.CollectablesWindow? CollectablesWindow { get; private set; }
    internal static Gui.NativeItemTooltipBridge? NativeItemTooltipBridge { get; private set; }
    public static ControllerSupportManager?      ControllerSupport      { get; private set; }
    public static MarketboardService?             MarketboardService     { get; private set; }
    public static MarketplaceBuyListManager?      MarketplaceBuyListManager { get; private set; }
    public static Vulcan.Vendors.VendorNavigator  VendorNavigator        { get; private set; } = null!;
    public static Vulcan.Vendors.VendorPurchaseManager VendorPurchaseManager { get; private set; } = null!;
    public static Vulcan.Vendors.VendorBuyListManager VendorBuyListManager { get; private set; } = null!;

    internal readonly GatherGroup.GatherGroupManager GatherGroupManager;
    internal readonly LocationManager                LocationManager;
    internal readonly AlarmManager                   AlarmManager;
    internal readonly GatherWindowManager            GatherWindowManager;
    internal readonly AutoGatherListsManager         AutoGatherListsManager;
    internal readonly WindowSystem                   WindowSystem;
    internal readonly Interface                      Interface;
    internal readonly Executor                       Executor;
    internal readonly ContextMenu                    ContextMenu;
    internal readonly FishRecorder                   FishRecorder;
    internal VulcanWindow?                           _vulcanWindow;
    internal Gui.CraftingStatusWindow?               _craftingStatusWindow;
    internal Gui.CraftingMaterialsWindow?            _craftingMaterialsWindow;
    internal Gui.CraftingTreeWindow?                 _craftingTreeWindow;
    internal Gui.VendorBuyListWindow?                _vendorBuyListWindow;
    internal Gui.MarketplaceBuyListWindow?           _marketplaceBuyListWindow;
    internal Gui.CollectablesWindow?                 _collectablesWindow;
    private bool _disposeStarted;
    private bool _disposeCompleted;
    private static FcGameVersionProvider? _fcGameVersionProvider;
    private string? _fcMeshStorageDirectory;
    private string? _fcMeshRuntimeScope;
    private long _fcMeshRuntimeGeneration;
    private Task<FcMeshRuntimeServices?>? _fcMeshServicesTask;

    internal readonly GatherBuddyIpc Ipc;
    //    internal readonly WotsitIpc Wotsit;

    public GatherBuddy(IDalamudPluginInterface pluginInterface)
    {
        try
        {
            Dalamud.Initialize(pluginInterface);
            _fcGameVersionProvider = new FcGameVersionProvider(
                Dalamud.PluginInterface.GetType().Assembly.Location);
            Icons.Init(Dalamud.GameData, Dalamud.Textures);
            Log     = new Logger();
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
            var rebornMigration = GatherBuddyRebornMigration.Prepare(pluginInterface, Log);
            Backup.CreateAutomaticBackup(Log, pluginInterface.ConfigDirectory, GatherBuddyBackupFiles());
            Config   = Configuration.Load();
            if (rebornMigration.AppliedThisStartup)
            {
                Config.RaphaelSolverConfig.SolverMode = VulcanSolverMode.Donatello;
                Config.Save();
            }
            Language = Dalamud.ClientState.ClientLanguage;
            GameData = new GameData(Dalamud.GameData, Log, WorldData.WorldLocationsByNodeId, "fish_overrides.json");
            Time     = new SeTime();

            WaymarkManager = new WaymarkManager();

            WeatherManager         = new WeatherManager(GameData);
            UptimeManager          = new UptimeManager(GameData);
            var sigScannerWrapper  = new SigScannerWrapper(Dalamud.Interop);
            try { FishLog = new FishLog(sigScannerWrapper, Dalamud.GameData); }
            catch (Exception e) { Log.Warning($"Failed to initialize FishLog: {e.Message}"); FishLog = null!; }
            EventFramework         = new EventFramework();
            CurrentBait           = new CurrentBait();
            try { TugType = new SeTugType(sigScannerWrapper); }
            catch (Exception e) { Log.Warning($"Failed to initialize TugType: {e.Message}"); TugType = null!; }
            Executor               = new Executor(this);
            ContextMenu            = new ContextMenu(this, Dalamud.ContextMenu, Executor);
            GatherGroupManager     = GatherGroup.GatherGroupManager.Load();
            LocationManager        = LocationManager.Load();
            AlarmManager           = AlarmManager.Load();
            AutoGatherListsManager = AutoGatherListsManager.Load();
            GatherWindowManager    = GatherWindowManager.Load(AlarmManager);
            AlarmManager.ForceEnable();
            CraftingListManager   = new Crafting.CraftingListManager();
            MarketboardService    = new MarketboardService();
            MarketplaceBuyListManager = new MarketplaceBuyListManager(Config);
            RaphaelSolveCoordinator = new Crafting.RaphaelSolveCoordinator(Config.RaphaelSolverConfig);
            RecipeBrowserSettings = new Crafting.RecipeBrowserSettings();
            RecipeBrowserSettings.Load();
            VendorNavigator = new Vulcan.Vendors.VendorNavigator();
            VendorPurchaseManager = new Vulcan.Vendors.VendorPurchaseManager();
            VendorBuyListManager = new Vulcan.Vendors.VendorBuyListManager();
            var liveVendorPurchase = new LiveVendorPurchaseAdapter(VendorPurchaseManager);
            LiveAcquisitionEnvironment = new NativeLiveAcquisitionEnvironment(liveVendorPurchase.PurchaseAsync);
            LiveAcquisitionExecutor = null;
            CraftingGameInterop.Initialize();
            CraftingGatherBridge.Initialize(this);
            CraftingGameInterop.CraftFinished += (recipe, cancelled) => CraftingGatherBridge.OnCraftFinished(recipe, cancelled);
            
            Task.Run(() =>
            {
                try
                {
                    Crafting.RepairNPCHelper.PopulateRepairNPCs();
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to populate repair NPCs: {ex.Message}");
                }
            });

            InitializeCommands();

            FishRecorder = new FishRecorder(Dalamud.Interop);
            FishRecorder.Enable();
            BiteTimerService = new AutoHookIntegration.BiteTimerService(pluginInterface.ConfigDirectory.FullName);
            AutoGather   = new AutoGather.AutoGather(this);
            CollectableManager = new AutoGather.Collectables.CollectableManager(Dalamud.Framework, Dalamud.Conditions, Config);
            FcChestProbe = new FcChestFeasibilityProbe(Dalamud.Framework);
            FcSyntheticFulfillment = new FcSyntheticFulfillmentDriver();
            _fcMeshStorageDirectory = Path.Combine(pluginInterface.ConfigDirectory.FullName, "fcmesh");
            global::GatherBuddy.AutoGather.Collectables.CollectableInventoryHelper.InitializeAsync();
            CraftingGatherBridge.BindCollectableManager(CollectableManager);
            ArtisanShim = new Crafting.ArtisanIpcShim(pluginInterface);
            NativeRecipeCraftingUi = new Crafting.NativeRecipeCraftingUi();
            WindowSystem = new WindowSystem(Name);
            Interface    = new Interface(this);
            _vulcanWindow = new VulcanWindow();
            VulcanWindow = _vulcanWindow;
            _craftingStatusWindow = new Gui.CraftingStatusWindow();
            CraftingStatusWindow = _craftingStatusWindow;
            _craftingMaterialsWindow = new Gui.CraftingMaterialsWindow(AutoGatherListsManager);
            CraftingMaterialsWindow = _craftingMaterialsWindow;
            _craftingTreeWindow = new Gui.CraftingTreeWindow();
            CraftingTreeWindow = _craftingTreeWindow;
            _vendorBuyListWindow = new Gui.VendorBuyListWindow();
            VendorBuyListWindow = _vendorBuyListWindow;
            _marketplaceBuyListWindow = new Gui.MarketplaceBuyListWindow();
            MarketplaceBuyListWindow = _marketplaceBuyListWindow;
            _collectablesWindow = new Gui.CollectablesWindow();
            CollectablesWindow = _collectablesWindow;
            NativeItemTooltipBridge = new Gui.NativeItemTooltipBridge();
            WindowSystem.AddWindow(Interface);
            WindowSystem.AddWindow(new GatherWindow(this));
            WindowSystem.AddWindow(new FishTimerWindow(FishRecorder));
            WindowSystem.AddWindow(new SpearfishingHelper(GameData));
            WindowSystem.AddWindow(_vulcanWindow);
            WindowSystem.AddWindow(_craftingStatusWindow);
            WindowSystem.AddWindow(_craftingMaterialsWindow);
            WindowSystem.AddWindow(_craftingTreeWindow);
            WindowSystem.AddWindow(_vendorBuyListWindow);
            WindowSystem.AddWindow(_marketplaceBuyListWindow);
            WindowSystem.AddWindow(_collectablesWindow);
            if (rebornMigration.ShouldPrompt)
                WindowSystem.AddWindow(new GatherBuddyRebornMigrationWindow(rebornMigration));
            Dalamud.PluginInterface.UiBuilder.Draw         += DrawUi;
            Dalamud.PluginInterface.UiBuilder.OpenConfigUi += Interface.Toggle;
            Dalamud.PluginInterface.UiBuilder.OpenMainUi   += Interface.Toggle;
            Dalamud.Framework.Update                       += Update;

            try
            {
                ControllerSupport = new ControllerSupportManager(
                    Dalamud.GamepadState,
                    Dalamud.Interop,
                    null,
                    Dalamud.Log
                );
                ControllerSupport.EnableInputBlocking();
                
                // Register both windows as managed by ElliCon
                ControllerSupport.RegisterBlockingWindow("Vulcan - Crafting###VulcanWindow");
                ControllerSupport.RegisterBlockingWindow("Crafting Status###GatherBuddyCraftingStatus");
                ControllerSupport.RegisterBlockingWindow(Gui.VendorBuyListWindow.WindowId);
                ControllerSupport.RegisterBlockingWindow(Gui.CollectablesWindow.WindowId);
                
                // Start in normal mode (blocks everything when windows are focused)
                ControllerSupport.SetBlockingMode(true, true, true);
            }
            catch (Exception e)
            {
                Log.Warning($"Failed to initialize ElliCon controller support: {e.Message}");
            }

            Ipc = new GatherBuddyIpc(this);
            CheckForOGGB();
            //Wotsit = new WotsitIpc();
        }
        catch
        {
            ((IDisposable)this).Dispose();
            throw;
        }
    }

    private void DrawUi()
    {
        NativeItemTooltipBridge?.BeginImGuiFrame();
        try
        {
            WindowSystem.Draw();
        }
        finally
        {
            NativeItemTooltipBridge?.EndImGuiFrame();
        }
    }

    private void CheckForOGGB()
    {
        var plugins = Dalamud.PluginInterface.InstalledPlugins;
        foreach (var plugin in plugins)
        {
            if (plugin.Name == "GatherBuddy" && plugin.IsLoaded)
            {
                Log.Error("First Party GatherBuddy detected. Please uninstall it to use this version.");
                Communicator.PrintError(
                    "[GatherBuddy Ascended] First Party GatherBuddy detected. Please uninstall it and restart your game to use this version.");
                break;
            }
        }
    }

    private static async Task<AcquisitionPlanningResult?> ReplanLiveAcquisitionAsync(
        CancellationToken cancellationToken)
    {
        var plan = CraftingGatherBridge.GetActiveExecutionPlan();
        if (plan == null)
            return null;

        // A stale live market listing invalidates the complete source plan.
        // The normal builder queues a cache refresh and reports loading; wait
        // for that refresh before allowing the executor to choose again.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evaluation = await EvaluateAcquisitionOnFrameworkThreadAsync(plan, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!evaluation.IsLoading)
                return evaluation.Planning;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    internal static LiveAcquisitionExecutor? CreateLiveAcquisitionExecutor(
        LiveAcquisitionOptions options,
        Func<CancellationToken, Task<AcquisitionPlanningResult?>>? replan = null,
        Func<uint, CancellationToken, Task>? invalidateMarketData = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var environment = LiveAcquisitionEnvironment;
        if (environment == null)
            return null;

        if (LiveAcquisitionExecutor is { IsRunning: true })
            return null;

        LiveAcquisitionExecutor?.Dispose();
        LiveAcquisitionExecutor = new LiveAcquisitionExecutor(
            environment,
            options,
            replan ?? ReplanLiveAcquisitionAsync,
            invalidateMarketData ?? InvalidateAcquisitionMarketDataOnFrameworkThreadAsync);
        return LiveAcquisitionExecutor;
    }

    internal static void ReleaseLiveAcquisitionExecutor(LiveAcquisitionExecutor? executor)
    {
        if (executor == null)
            return;

        executor.Dispose();
        if (ReferenceEquals(LiveAcquisitionExecutor, executor))
            LiveAcquisitionExecutor = null;
    }

    internal static async Task<T> RunOnFrameworkThreadAsync<T>(
        Func<T> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();
        if (Dalamud.Framework == null)
            throw new InvalidOperationException("Dalamud framework is unavailable.");
        if (Dalamud.Framework.IsInFrameworkUpdateThread)
            return callback();

        var dispatch = new FrameworkDispatchGate<T>();
        void Run(IFramework _)
        {
            if (!dispatch.TryClaim(cancellationToken))
            {
                Dalamud.Framework.Update -= Run;
                return;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                dispatch.TryComplete(callback(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                dispatch.TryCancel(cancellationToken);
            }
            catch (Exception ex)
            {
                dispatch.TryFail(ex);
            }
            finally
            {
                Dalamud.Framework.Update -= Run;
            }
        }

        Dalamud.Framework.Update += Run;
        using var registration = cancellationToken.Register(() =>
        {
            if (dispatch.TryCancel(cancellationToken))
                Dalamud.Framework.Update -= Run;
        });

        var completed = await Task.WhenAny(dispatch.Completion, Task.Delay(FrameworkDispatchTimeout)).ConfigureAwait(false);
        if (completed == dispatch.Completion)
            return await dispatch.Completion.ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        dispatch.TryCancel();
        Dalamud.Framework.Update -= Run;
        throw new TimeoutException("Dalamud framework callback did not run before the dispatch timeout.");
    }

    internal static string? CurrentFcCharacterScope()
    {
        try
        {
            if (!Dalamud.PlayerState.IsLoaded || Dalamud.PlayerState.ContentId == 0)
                return null;
            return Dalamud.PlayerState.ContentId.ToString("X16", CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    internal static CharacterIdentity? CurrentFcCharacterIdentity()
    {
        try
        {
            var scope = CurrentFcCharacterScope();
            var player = Dalamud.Objects.LocalPlayer;
            if (scope is null || player is null)
                return null;
            return new CharacterIdentity(
                scope,
                player.Name.ToString(),
                player.HomeWorld.Value.Name.ToString());
        }
        catch
        {
            return null;
        }
    }

    internal static string? CurrentFcGameVersion()
        // Provider reads the loaded Dalamud hook's host-owned SupportedGameVer
        // metadata off the framework thread; null keeps publication read-only.
        => _fcGameVersionProvider?.CurrentVersion;

    /// <summary>
    /// Binds the managed/native FC mesh runtime to the current character. The
    /// identity check and all lifecycle transitions happen on the framework
    /// thread. A new native handle is created for each character so no queued
    /// command can cross an author scope boundary.
    /// </summary>
    private void UpdateFcMeshRuntime()
    {
        var scope = CurrentFcCharacterScope();
        if (scope is null)
        {
            if (FcMeshNative is not null || FcPublishedLists is not null || FcWorkerSessions is not null)
                DisposeFcMeshRuntime();
            return;
        }

        if (FcMeshNative is null || !string.Equals(_fcMeshRuntimeScope, scope, StringComparison.Ordinal))
        {
            DisposeFcMeshRuntime();
            if (string.IsNullOrWhiteSpace(_fcMeshStorageDirectory))
                return;
            try
            {
                var coordinator = new FcMeshNativeCoordinator(new FcMeshNativePInvokeApi());
                var result = coordinator.Start(
                    new FcNativeConfiguration(_fcMeshStorageDirectory),
                    Encoding.UTF8.GetBytes(scope));
                if (!result.Succeeded)
                {
                    coordinator.Dispose();
                    Log.Warning($"Failed to initialize FC mesh service for character scope: {result.ErrorCode}.");
                    return;
                }
                FcMeshNative = coordinator;
                _fcMeshRuntimeScope = scope;
            }
            catch (Exception exception)
            {
                Log.Warning($"Failed to initialize FC mesh service: {exception.Message}");
                return;
            }
        }

        FcMeshNative?.Tick(TimeSpan.FromMilliseconds(2));
        TryInstallFcMeshServices();
    }

    private void TryInstallFcMeshServices()
    {
        if (FcMeshNative is null
            || string.IsNullOrWhiteSpace(_fcMeshStorageDirectory)
            || string.IsNullOrWhiteSpace(FcMeshNative.LocalAuthorId)
            || FcPublishedLists is not null)
            return;

        if (_fcMeshServicesTask is { IsCompleted: true } completed)
        {
            _fcMeshServicesTask = null;
            FcMeshRuntimeServices? services = null;
            try { services = completed.GetAwaiter().GetResult(); }
            catch (Exception exception) { Log.Warning($"Failed to load FC mesh state: {exception.Message}"); }
            if (services is not null)
            {
                if (services.Generation == _fcMeshRuntimeGeneration
                    && services.Native == FcMeshNative
                    && string.Equals(services.Scope, _fcMeshRuntimeScope, StringComparison.Ordinal))
                {
                    FcPublishedLists = services.PublishedLists;
                    FcChestPublication = services.ChestPublication;
                    FcWorkerSessions = services.WorkerSessions;
                    services = null;
                }
                services?.Dispose();
            }
        }

        if (_fcMeshServicesTask is null)
        {
            var coordinator = FcMeshNative;
            var scope = _fcMeshRuntimeScope;
            var storage = _fcMeshStorageDirectory;
            var generation = _fcMeshRuntimeGeneration;
            if (coordinator is not null
                && scope is { Length: > 0 }
                && storage is { Length: > 0 })
            {
                var selectedScope = scope;
                var selectedStorage = storage;
                _fcMeshServicesTask = Task.Run<FcMeshRuntimeServices?>(() =>
                    CreateFcMeshServices(coordinator, selectedScope, selectedStorage, generation));
            }
        }
    }

    private FcMeshRuntimeServices CreateFcMeshServices(
        FcMeshNativeCoordinator coordinator,
        string scope,
        string storage,
        long generation)
    {
        var fcPublicationState = new FcFilePublicationStateStore(storage);
        var fcPublicationTransport = new FcMeshNativePublicationTransport(coordinator);
        FcPublishedListService? publishedLists = null;
        FcChestPublicationService? chestPublication = null;
        FcWorkerSessionService? workerSessions = null;
        try
        {
            publishedLists = new FcPublishedListService(
                fcPublicationState,
                fcPublicationTransport,
                () => scope,
                () => coordinator.LocalAuthorId,
                () => new FcCompatibilityContext(
                    FcPublishedListMapper.CurrentPlannerSemanticsVersion,
                    CurrentFcGameVersion() ?? string.Empty),
                localListExists: identity => CraftingListManager.GetListByID(identity.ListId) is { } local
                    && local.CreatedAt.ToUniversalTime() == identity.CreatedAtUtc);
            chestPublication = new FcChestPublicationService(
                fcPublicationState,
                fcPublicationTransport,
                new FcCompleteChestReader(new FcChestSnapshotReader()),
                () => scope,
                () => coordinator.LocalAuthorId);
            workerSessions = new FcWorkerSessionService(
                new FcFileWorkerSessionStateStore(storage),
                fcPublicationTransport,
                () => scope,
                () => coordinator.LocalAuthorId,
                () => new FcCompatibilityContext(
                    FcPublishedListMapper.CurrentPlannerSemanticsVersion,
                    CurrentFcGameVersion() ?? string.Empty),
                () => publishedLists.PublicLists,
                CurrentFcCharacterIdentity,
                () => CurrentFcGameVersion() ?? string.Empty,
                FcSystemClock.Instance,
                physicalSnapshotProvider: physicalClosure =>
                    FcWorkerPhysicalInventorySnapshot.Capture(physicalClosure));
            return new FcMeshRuntimeServices(
                coordinator,
                scope,
                generation,
                publishedLists,
                chestPublication,
                workerSessions);
        }
        catch
        {
            workerSessions?.Dispose();
            chestPublication?.Dispose();
            publishedLists?.Dispose();
            throw;
        }
    }

    private void DisposeFcMeshRuntime()
    {
        _fcMeshRuntimeGeneration++;
        var servicesTask = _fcMeshServicesTask;
        _fcMeshServicesTask = null;
        if (servicesTask is not null)
        {
            _ = servicesTask.ContinueWith(
                completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion)
                        completed.Result?.Dispose();
                    else
                        _ = completed.Exception;
                },
                TaskScheduler.Default);
        }
        FcWorkerSessions?.Dispose();
        FcWorkerSessions = null;
        FcChestPublication?.Dispose();
        FcChestPublication = null;
        FcPublishedLists?.Dispose();
        FcPublishedLists = null;
        FcMeshNative?.Dispose();
        FcMeshNative = null;
        _fcMeshRuntimeScope = null;
    }

    private sealed class FcMeshRuntimeServices : IDisposable
    {
        public FcMeshRuntimeServices(
            FcMeshNativeCoordinator native,
            string scope,
            long generation,
            FcPublishedListService publishedLists,
            FcChestPublicationService chestPublication,
            FcWorkerSessionService workerSessions)
        {
            Native = native;
            Scope = scope;
            Generation = generation;
            PublishedLists = publishedLists;
            ChestPublication = chestPublication;
            WorkerSessions = workerSessions;
        }

        public FcMeshNativeCoordinator Native { get; }
        public string Scope { get; }
        public long Generation { get; }
        public FcPublishedListService PublishedLists { get; }
        public FcChestPublicationService ChestPublication { get; }
        public FcWorkerSessionService WorkerSessions { get; }

        public void Dispose()
        {
            WorkerSessions.Dispose();
            ChestPublication.Dispose();
            PublishedLists.Dispose();
        }
    }

    public static FcNativeCallResult ConfigureFcMeshCharacterAuthor()
    {
        if (FcMeshNative is null)
            return new FcNativeCallResult((uint)FcNativeErrorCode.InvalidHandle, 0, 0);
        var scope = CurrentFcCharacterScope();
        if (scope is null)
            return new FcNativeCallResult((uint)FcNativeErrorCode.InvalidState, 0, 0);
        return FcMeshNative.SetCharacterAuthor(Encoding.UTF8.GetBytes(scope));
    }

    internal static async Task InvalidateMarketplaceMarketDataOnFrameworkThreadAsync(
        uint itemId,
        bool currentWorldOnly,
        CancellationToken cancellationToken)
    {
        await RunOnFrameworkThreadAsync(() =>
        {
            var service = MarketboardService;
            if (service == null)
                return true;
            var scope = currentWorldOnly ? service.GetCurrentWorld() : service.GetDataCenter();
            if (itemId != 0)
                service.ForceRefresh(itemId, scope);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CraftingAcquisitionService.Evaluation> EvaluateAcquisitionOnFrameworkThreadAsync(
        CraftingExecutionPlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dalamud.Framework.IsInFrameworkUpdateThread)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plan.RefreshFromCurrentInventory();
            cancellationToken.ThrowIfCancellationRequested();
            var evaluation = CraftingAcquisitionService.Evaluate(plan);
            cancellationToken.ThrowIfCancellationRequested();
            return evaluation;
        }

        var dispatch = new FrameworkDispatchGate<CraftingAcquisitionService.Evaluation>();
        void Evaluate(IFramework _)
        {
            if (!dispatch.TryClaim(cancellationToken))
            {
                Dalamud.Framework.Update -= Evaluate;
                return;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                plan.RefreshFromCurrentInventory();
                cancellationToken.ThrowIfCancellationRequested();
                var evaluation = CraftingAcquisitionService.Evaluate(plan);
                dispatch.TryComplete(evaluation, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                dispatch.TryCancel(cancellationToken);
            }
            catch (Exception ex)
            {
                dispatch.TryFail(ex);
            }
            finally
            {
                Dalamud.Framework.Update -= Evaluate;
            }
        }

        Dalamud.Framework.Update += Evaluate;
        using var registration = cancellationToken.Register(() =>
        {
            if (dispatch.TryCancel(cancellationToken))
                Dalamud.Framework.Update -= Evaluate;
        });

        var timeout = Task.Delay(FrameworkDispatchTimeout);
        var completed = await Task.WhenAny(dispatch.Completion, timeout).ConfigureAwait(false);
        if (completed == dispatch.Completion)
            return await dispatch.Completion.ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        dispatch.TryCancel();
        Dalamud.Framework.Update -= Evaluate;
        throw new TimeoutException(
            "Dalamud framework callback did not run before the acquisition dispatch timeout.");
    }

    private static async Task InvalidateAcquisitionMarketDataOnFrameworkThreadAsync(
        uint itemId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dalamud.Framework == null)
            throw new InvalidOperationException("Dalamud framework is unavailable for market-data invalidation.");

        if (Dalamud.Framework.IsInFrameworkUpdateThread)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CraftingGatherBridge.InvalidateAcquisitionMarketData(itemId);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        var dispatch = new FrameworkDispatchGate<bool>();
        void Invalidate(IFramework _)
        {
            if (!dispatch.TryClaim(cancellationToken))
            {
                Dalamud.Framework.Update -= Invalidate;
                return;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                CraftingGatherBridge.InvalidateAcquisitionMarketData(itemId);
                cancellationToken.ThrowIfCancellationRequested();
                dispatch.TryComplete(true, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                dispatch.TryCancel(cancellationToken);
            }
            catch (Exception ex)
            {
                dispatch.TryFail(ex);
            }
            finally
            {
                Dalamud.Framework.Update -= Invalidate;
            }
        }

        Dalamud.Framework.Update += Invalidate;
        using var registration = cancellationToken.Register(() =>
        {
            if (dispatch.TryCancel(cancellationToken))
                Dalamud.Framework.Update -= Invalidate;
        });

        var timeout = Task.Delay(FrameworkDispatchTimeout);
        var completed = await Task.WhenAny(dispatch.Completion, timeout).ConfigureAwait(false);
        if (completed == dispatch.Completion)
        {
            await dispatch.Completion.ConfigureAwait(false);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        dispatch.TryCancel();
        Dalamud.Framework.Update -= Invalidate;
        throw new TimeoutException(
            "Dalamud framework callback did not run before market-data invalidation timeout.");
    }

    private int      LastObjectsLength;
    private DateTime LastObjectsScan = DateTime.Now;

    private unsafe void Update(IFramework framework)
    {
        Config.SaveIfDirty();
        try
        {
            UpdateFcMeshRuntime();
            FcChestPublication?.ProcessFrameworkCommands();
            FcPublishedLists?.ReconcileAuthoritativeState();
            FcWorkerSessions?.ReconcileAuthoritativeState();
            FcWorkerSessions?.Tick();
        }
        catch (Exception exception)
        {
            Log.Error($"Error while running FC mesh update: {exception.Message}");
        }
        var prev = LastObjectsLength;
        LastObjectsLength = Dalamud.Objects.Length;
        //Scan objects every 5 secons or when the number of objects change
        if (prev != LastObjectsLength || (DateTime.Now - LastObjectsScan).TotalSeconds >= 5)
        {
            LastObjectsScan = DateTime.Now;

            foreach (var obj in Dalamud.Objects)
            {
                // Add gathering node locations
                if (obj.ObjectKind == ObjectKind.GatheringPoint)
                {
                    WorldData.AddLocation(obj.BaseId, obj.Position);
                }
                // Detect other players gathering and add their positions as offsets
                else if (obj is IPlayerCharacter player)
                {
                    var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player.Address;
                    if (character == null) continue;

                    // Only add offsets if player is gathering and is not flying
                    // (I've seen glitches where the flying character would gather, let's exclude those)
                    if (character->Mode == FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.Gathering
                        && character->MoveController.MovementState != FFXIVClientStructs.FFXIV.Client.Game.Character.MovementStateOptions.Flying)
                    {
                        var target = player.TargetObject;
                        if (target != null && target.ObjectKind == ObjectKind.GatheringPoint)
                        {
                            AutoOffsets.AddOffset(target.BaseId, target.Position, player.Position);
                        }
                    }
                }
            }
        }

        try
        {
            var samplingExpertConditions = ExpertConditionSampler.Update();
            if (!samplingExpertConditions)
            {
                CraftingGameInterop.Update();
                CraftingGatherBridge.Update();
                ArtisanShim?.Update();
            }
            VendorNavigator.Update();
            VendorPurchaseManager.Update();
            VendorBuyListManager.Update();
            MarketplaceBuyListManager?.Update();
        }
        catch (Exception e)
        {
            Log.Error($"Error while running crafting update: {e}");
        }

        try
        {
            AutoGather.DoAutoGather();
        }
        catch (Exception e)
        {
            Log.Error($"Error while running auto gather: {e}");
        }
    }

    void IDisposable.Dispose()
    {
        if (_disposeStarted)
            return;
        _disposeStarted = true;

        if (Dalamud.Framework == null)
        {
            // Partial construction can fail before Dalamud exposes the
            // framework service. There is no safe callback target in that
            // path; still run bridge cleanup before the final best-effort
            // disposal.
            _ = ShutdownBridgeThenDisposeAsync();
            return;
        }

        if (Dalamud.Framework.IsInFrameworkUpdateThread)
        {
            _ = ShutdownBridgeThenDisposeAsync();
            return;
        }

        Dalamud.Framework.Update += ShutdownBridgeDeferredOnFramework;
    }

    private void ShutdownBridgeDeferredOnFramework(IFramework framework)
    {
        Dalamud.Framework.Update -= ShutdownBridgeDeferredOnFramework;
        _ = ShutdownBridgeThenDisposeAsync();
    }

    private async Task ShutdownBridgeThenDisposeAsync()
    {
        try
        {
            // CraftingGatherBridge stops the queue and awaits its acquisition
            // drain. Only after this completes may vendor, market, or native
            // acquisition dependencies be disposed.
            await CraftingGatherBridge.ShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Error($"[GatherBuddy] Crafting queue cleanup failed during shutdown: {ex.Message}");
        }

        // Native Dalamud/game objects must be released on the framework thread.
        // The bridge drain is intentionally awaited off-thread, then the final
        // disposal is marshalled back through the framework update event.
        if (Dalamud.Framework == null)
        {
            DisposeCore();
            return;
        }

        if (Dalamud.Framework.IsInFrameworkUpdateThread)
        {
            DisposeCore();
            return;
        }

        Dalamud.Framework.Update += DisposeDeferredOnFramework;
    }

    private void DisposeDeferredOnFramework(IFramework framework)
    {
        Dalamud.Framework.Update -= DisposeDeferredOnFramework;
        DisposeCore();
    }

    private void DisposeCore()
    {
        if (_disposeCompleted)
            return;
        _disposeCompleted = true;
        Config?.SaveIfDirty(force: true);
        MarketboardService?.Dispose();
        if (RaphaelSolveCoordinator != null
            && !RaphaelSolveCoordinator.CancelAllPendingSolvesAndWait(TimeSpan.FromSeconds(5)))
            Log.Error("[RaphaelSolveCoordinator] Native solver work did not stop within five seconds during plugin shutdown");
        RaphaelSolveCoordinator?.Save();
        if (Dalamud.Framework != null)
            Dalamud.Framework.Update -= Update;
        if (WindowSystem != null)
            Dalamud.PluginInterface.UiBuilder.Draw -= DrawUi;
        if (Interface != null)
        {
            Dalamud.PluginInterface.UiBuilder.OpenConfigUi -= Interface.Toggle;
            Dalamud.PluginInterface.UiBuilder.OpenMainUi -= Interface.Toggle;
        }
        NativeRecipeCraftingUi?.Dispose();
        NativeRecipeCraftingUi = null;
        ArtisanShim?.Dispose();
        ArtisanShim = null;
        ExpertConditionSampler.Dispose();
        CraftingGameInterop.Dispose();
        FcChestProbe?.Dispose();
        FcChestProbe = null;
        DisposeFcMeshRuntime();
        _fcGameVersionProvider = null;
        FcSyntheticFulfillment = null;
        FishRecorder?.Dispose();
        ContextMenu?.Dispose();
        UptimeManager?.Dispose();
        AutoGather?.Dispose();
        CollectableManager?.Dispose();
        VendorBuyListManager?.Dispose();
        VendorPurchaseManager?.Dispose();
        MarketplaceBuyListManager?.Dispose();
        LiveAcquisitionExecutor?.Dispose();
        LiveAcquisitionExecutor = null;
        LiveAcquisitionEnvironment = null;
        _marketplaceBuyListWindow?.Dispose();
        MarketplaceBuyListManager = null;
        MarketplaceBuyListWindow = null;
        ControllerSupport?.Dispose();
        Ipc?.Dispose();
        NativeItemTooltipBridge?.Dispose();
        NativeItemTooltipBridge = null;
        //Wotsit?.Dispose();
        Interface?.Dispose();
        WindowSystem?.RemoveAllWindows();
        DisposeCommands();
        Time?.Dispose();
        HttpClient?.Dispose();
        Plugin.EzIPC.Dispose();
    }

    // Collect all relevant files for GatherBuddy configuration
    private static IReadOnlyList<FileInfo> GatherBuddyBackupFiles()
    {
        var list = Directory.Exists(Dalamud.PluginInterface.GetPluginConfigDirectory())
            ? Dalamud.PluginInterface.ConfigDirectory.EnumerateFiles("*.*").ToList()
            : new List<FileInfo>();
        list.Add(Dalamud.PluginInterface.ConfigFile);
        return list;
    }
}
