using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Keys;
using GlamourerPreviewManager.Windows;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;
using System.Text.RegularExpressions;
using System.Reflection;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Chat;

namespace GlamourerPreviewManager;

public enum ResolutionStage
{
    None = 0,
    ReflectionSelection = 1,
    ReflectionDataNodes = 2,
    ButtonGuid = 3,
    IncognitoHexMatch = 4,
    NameMatchFallback = 5
}

public sealed class Plugin : IDalamudPlugin
{
    public static Plugin? Instance { get; private set; }

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;

    private const string CommandName = "/gpm";
    private const string AltCommandName = "/glampreview";
    private const string GalleryCommandName = "/gpmgallery";
    private const string AltGalleryCommandName = "/glampreviewgallery";
    private const string RouletteCommandName = "/gpmroulette";
    private const string AltRouletteCommandName = "/glampreviewroulette";

    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("GlamourerPreviewManager");
    private ConfigWindow ConfigWindow { get; init; }
    private GalleryWindow GalleryWindow { get; init; }
    private GalleryPromoWindow GalleryPromoWindow { get; init; }
    public RouletteWindow RouletteWindow { get; init; }
    public GlamourerPreviewWindow PreviewWindow { get; init; }
    public RediscoveryReviewWindow ReviewWindow { get; init; }
    public FileDialogManager FileDialogManager { get; } = new();
    internal ImGuiHookManager ImGuiHookManager { get; }
    public DesignManager DesignManager { get; }

    public Guid ActiveSelectedDesignId => activeSelectedDesignId;
    public bool IsReflectionInitialized => reflectionInitialized;
    public ResolutionStage CurrentResolutionStage => currentResolutionStage;
    public string CurrentResolutionSource => currentResolutionSource;

    public int? LastSeenRoll { get; private set; }
    private static readonly Regex RollRegex = new(
        @"(?:(?:rolls?|roll|würfelt|würfelst|obtient|obtenez)\s+(?:a\s+|eine\s+)?(?:🎲)?\s*|random!\s*[^\d]*)(\d+)", 
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ImGui hook states
    private Guid activeSelectedDesignId = Guid.Empty;
    private ResolutionStage currentResolutionStage = ResolutionStage.None;
    private string currentResolutionSource = "None";
    private int lastSeenDesignFrame = -1;
    private string currentWindowName = string.Empty;
    private readonly Stack<string> windowStack = new();
    private int lastStackFrame = -1;
    private bool isInGlamourerWindow = false;
    private readonly HashSet<string> seenButtonLabels = new();
    private readonly HashSet<string> seenSelectableLabels = new();

    // Cache dictionaries for performance optimization
    private readonly Dictionary<string, (string BustedPath, long LastWriteTicks)> bustedPathCache = new(StringComparer.OrdinalIgnoreCase);

    // High-performance reflection fields for Glamourer Selection resolution
    private Assembly? glamourerAssembly;
    private object? cachedGlamourerPluginInstance;
    private object? serviceManagerInstance;
    private object? cachedDesignFileSystemInstance;
    private MemberInfo? fileSystemSelectionMember;
    private object? cachedEphemeralConfigInstance;
    private PropertyInfo? selectedMainTabProp;
    private bool reflectionInitialized = false;
    private DateTime lastReflectionAttemptTime = DateTime.MinValue;
    private int reflectionErrorStreak = 0;

    // Per-frame memoization for zero frame overhead
    private int lastReflectedFrame = -1;
    private int reflectedThisFrameCount = 0;
    private Guid cachedReflectedGuid = Guid.Empty;

    // Screenshot states
    private bool isCapturingScreenshot = false;
    private int screenshotDelayFrame = -1;
    private bool lastSpaceDown = false;
    private bool lastEscapeDown = false;
    public System.Drawing.Bitmap? LastRawScreenshot { get; private set; }
    public Guid LastScreenshotDesignId { get; private set; } = Guid.Empty;
    private FileSystemWatcher? screenshotWatcher;
    private string? pendingScreenshotPath;

    // Deferred UI rendering states
    private int lastDrawnGpmFrame = -1;
    private int lastGlamourerWindowFrame = -1;
    private bool shouldDrawInjectedUI = false;
    private Guid deferredDesignId = Guid.Empty;
    private Guid lastFailedDesignId = Guid.Empty;

    #region Centralized Logging Helpers
    public static void LogVerbose(string message)
    {
        if (Instance?.Configuration.LogLevel >= GpmLogLevel.Verbose)
        {
            if (Instance?.Configuration.PromoteDebugLogsToInformation == true)
                Log.Information(message);
            else
                Log.Verbose(message);
        }
    }

    public static void LogDebug(string message)
    {
        if (Instance?.Configuration.LogLevel >= GpmLogLevel.Debug)
        {
            if (Instance?.Configuration.PromoteDebugLogsToInformation == true)
                Log.Information(message);
            else
                Log.Debug(message);
        }
    }

    public static void LogInfo(string message)
    {
        if (Instance?.Configuration.LogLevel >= GpmLogLevel.Information)
        {
            Log.Information(message);
        }
    }

    public static void LogWarn(string message)
    {
        if (Instance?.Configuration.LogLevel >= GpmLogLevel.Warning)
        {
            Log.Warning(message);
        }
    }

    public static void LogErr(string message, Exception? ex = null)
    {
        if (Instance?.Configuration.LogLevel >= GpmLogLevel.Error)
        {
            if (ex != null)
                Log.Error(ex, message);
            else
                Log.Error(message);
        }
    }
    #endregion

    public Plugin()
    {
        Instance = this;
        ClearTempCache();
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        DesignManager = new DesignManager(this);
        DesignManager.Initialize();

        ConfigWindow = new ConfigWindow(this);
        GalleryWindow = new GalleryWindow(this);
        GalleryPromoWindow = new GalleryPromoWindow(this);
        RouletteWindow = new RouletteWindow(this);
        PreviewWindow = new GlamourerPreviewWindow(this);
        ReviewWindow = new RediscoveryReviewWindow(this);
        ImGuiHookManager = new ImGuiHookManager(this);
        ImGuiHookManager.Initialize();

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(GalleryWindow);
        WindowSystem.AddWindow(GalleryPromoWindow);
        WindowSystem.AddWindow(RouletteWindow);
        WindowSystem.AddWindow(PreviewWindow);
        WindowSystem.AddWindow(ReviewWindow);

        var commandInfo = new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the configuration window. Use '/gpm gallery', '/gpm preview', or '/gpm review' to open Windows."
        };
        CommandManager.AddHandler(CommandName, commandInfo);
        CommandManager.AddHandler(AltCommandName, commandInfo);

        var galleryCommandInfo = new CommandInfo(OnGalleryCommand)
        {
            HelpMessage = "Open the Glamourer Preview Manager Gallery"
        };
        CommandManager.AddHandler(GalleryCommandName, galleryCommandInfo);
        CommandManager.AddHandler(AltGalleryCommandName, galleryCommandInfo);

        var rouletteCommandInfo = new CommandInfo(OnRouletteCommand)
        {
            HelpMessage = "Open the Glamourer Preview Manager Outfit Roulette"
        };
        CommandManager.AddHandler(RouletteCommandName, rouletteCommandInfo);
        CommandManager.AddHandler(AltRouletteCommandName, rouletteCommandInfo);

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw += DrawFileDialog;
        PluginInterface.UiBuilder.Draw += DrawScreenshotOverlay;
        PluginInterface.UiBuilder.Draw += CheckPerFrameLiveness;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;
        
        PluginInterface.UiBuilder.DisableGposeUiHide = true;

        // Auto-detect default game screenshot folder if empty
        if (string.IsNullOrEmpty(Configuration.GameScreenshotFolderPath))
        {
            try
            {
                var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "FINAL FANTASY XIV - A Realm Reborn", "screenshots");
                if (Directory.Exists(defaultPath))
                {
                    Configuration.GameScreenshotFolderPath = defaultPath;
                    Configuration.Save();
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to detect default FFXIV screenshot directory: {ex.Message}");
            }
        }
        ChatGui.ChatMessage += OnChatMessage;
        CheckFirstStartup();
    }

    public void Dispose()
    {
        if (Instance == this) Instance = null;
        ChatGui.ChatMessage -= OnChatMessage;
        ClientState.Login -= OnLogin;

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw -= DrawFileDialog;
        PluginInterface.UiBuilder.Draw -= DrawScreenshotOverlay;
        PluginInterface.UiBuilder.Draw -= CheckPerFrameLiveness;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;

        WindowSystem.RemoveAllWindows();
        ImGuiHookManager.Dispose();
        DesignManager.Dispose();
        ConfigWindow.Dispose();
        GalleryWindow.Dispose();
        GalleryPromoWindow.Dispose();
        RouletteWindow.Dispose();
        PreviewWindow.Dispose();
        ReviewWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(AltCommandName);
        CommandManager.RemoveHandler(GalleryCommandName);
        CommandManager.RemoveHandler(AltGalleryCommandName);
        CommandManager.RemoveHandler(RouletteCommandName);
        CommandManager.RemoveHandler(AltRouletteCommandName);
        LastRawScreenshot?.Dispose();
        screenshotWatcher?.Dispose();
        ClearTempCache();
    }

    private void OnCommand(string command, string args)
    {
        if (!string.IsNullOrWhiteSpace(args) && args.Trim().Equals("gallery", StringComparison.OrdinalIgnoreCase))
        {
            ToggleGalleryUi();
        }
        else if (!string.IsNullOrWhiteSpace(args) && args.Trim().Equals("roulette", StringComparison.OrdinalIgnoreCase))
        {
            ToggleRouletteUi();
        }
        else if (!string.IsNullOrWhiteSpace(args) && args.Trim().Equals("preview", StringComparison.OrdinalIgnoreCase))
        {
            TogglePreviewUi();
        }
        else if (!string.IsNullOrWhiteSpace(args) && (args.Trim().Equals("review", StringComparison.OrdinalIgnoreCase) || args.Trim().Equals("rediscover", StringComparison.OrdinalIgnoreCase)))
        {
            ToggleReviewUi();
        }
        else
        {
            ToggleConfigUi();
        }
    }

    private void OnGalleryCommand(string command, string args)
    {
        ToggleGalleryUi();
    }

    private void OnRouletteCommand(string command, string args)
    {
        ToggleRouletteUi();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleGalleryUi() => GalleryWindow.Toggle();
    public void ToggleRouletteUi() => RouletteWindow.Toggle();
    public void TogglePreviewUi() => PreviewWindow.Toggle();
    public void ToggleReviewUi() => ReviewWindow.Toggle();
    public void OpenReviewUi(RediscoveryResult result) => ReviewWindow.SetResult(result);
    private void DrawFileDialog() => FileDialogManager.Draw();

    public void OnBeginWindow(string name)
    {
        int currentFrame = (int)ImGui.GetFrameCount();
        if (currentFrame != lastStackFrame)
        {
            lastStackFrame = currentFrame;
            windowStack.Clear();
        }

        if (name != null)
        {
            windowStack.Push(name);
            currentWindowName = name;
            if (name.Contains("GlamourerMainWindow"))
            {
                lastGlamourerWindowFrame = currentFrame;
                isInGlamourerWindow = true;
            }
        }
    }

    public void OnEndWindow()
    {
        int currentFrame = (int)ImGui.GetFrameCount();
        if (currentFrame != lastStackFrame)
        {
            lastStackFrame = currentFrame;
            windowStack.Clear();
            currentWindowName = string.Empty;
            isInGlamourerWindow = false;
            return;
        }

        if (windowStack.Count > 0)
        {
            windowStack.Pop();
            currentWindowName = windowStack.Count > 0 ? windowStack.Peek() : string.Empty;
        }
        else
        {
            currentWindowName = string.Empty;
        }

        isInGlamourerWindow = windowStack.Any(w => w.Contains("GlamourerMainWindow"));
    }

    private bool IsInGlamourerWindow()
    {
        return isInGlamourerWindow || lastGlamourerWindowFrame == (int)ImGui.GetFrameCount();
    }

    private bool IsTooltipOrPopup()
    {
        if (string.IsNullOrEmpty(currentWindowName)) return false;
        return currentWindowName.IndexOf("Tooltip", StringComparison.OrdinalIgnoreCase) >= 0 ||
               currentWindowName.IndexOf("Popup", StringComparison.OrdinalIgnoreCase) >= 0 ||
               currentWindowName.IndexOf("Combo", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static readonly Regex GuidRegex = new Regex(@"[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}", RegexOptions.Compiled);

    private bool TryExtractGuid(string text, out Guid guid)
    {
        guid = Guid.Empty;
        var match = GuidRegex.Match(text);
        if (match.Success)
        {
            return Guid.TryParse(match.Value, out guid);
        }
        return false;
    }

    private static object? ExtractPropertyValueSafe(object? target, string propertyName)
    {
        if (target == null) return null;
        var type = target.GetType();

        // 1. Direct public property on public class
        if (type.IsPublic || type.IsNestedPublic)
        {
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                var p = t.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (p != null && p.GetMethod != null && p.GetMethod.IsPublic)
                {
                    try
                    {
                        var val = p.GetValue(target);
                        if (val != null) return val;
                    }
                    catch { }
                }
            }
        }

        // 2. Public interfaces (CRITICAL for internal classes like Luna.FileSystemData<T> implementing IFileSystemData)
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsPublic || iface.IsNestedPublic)
            {
                var p = iface.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (p != null)
                {
                    try
                    {
                        var val = p.GetValue(target);
                        if (val != null) return val;
                    }
                    catch { }
                }
            }
        }

        // 3. Any property or field as fallback
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            var p = t.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null)
            {
                try
                {
                    var val = p.GetValue(target);
                    if (val != null) return val;
                }
                catch { }
            }

            var f = t.GetField(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null)
            {
                try
                {
                    var val = f.GetValue(target);
                    if (val != null) return val;
                }
                catch { }
            }
        }

        return null;
    }

    private static object? GetMemberValue(MemberInfo? member, object? target)
    {
        if (member == null || target == null) return null;
        try
        {
            if (member is PropertyInfo prop) return prop.GetValue(target);
            if (member is FieldInfo field) return field.GetValue(target);
        }
        catch (Exception)
        {
            // If direct member access failed (e.g. MethodAccessException on internal class), try interface fallback
            if (member is PropertyInfo p)
            {
                return ExtractPropertyValueSafe(target, p.Name);
            }
        }
        return null;
    }

    public void InvalidateReflectionCache()
    {
        reflectionInitialized = false;
        glamourerAssembly = null;
        cachedGlamourerPluginInstance = null;
        serviceManagerInstance = null;
        cachedDesignFileSystemInstance = null;
        fileSystemSelectionMember = null;
        cachedEphemeralConfigInstance = null;
        selectedMainTabProp = null;
        lastReflectedFrame = -1;
        cachedReflectedGuid = Guid.Empty;
        reflectedThisFrameCount = 0;
        reflectionErrorStreak = 0;
        lastReflectionAttemptTime = DateTime.UtcNow; // Enforce timer cooldown before next reconnect attempt
    }

    public void ForceReinitializeReflection()
    {
        InvalidateReflectionCache();
        lastReflectionAttemptTime = DateTime.MinValue; // Bypass timer cooldown for user-initiated manual retry
        LogInfo("[GPM] Forcing reflection re-initialization...");
        InitializeReflection();
        if (reflectionInitialized)
        {
            LogInfo("[GPM] Reflection re-initialized successfully!");
            ChatGui.Print("[GPM] Reflection re-initialized successfully!");
        }
        else
        {
            LogWarn("[GPM] Reflection re-initialization attempted, but Glamourer was not yet ready.");
            ChatGui.Print("[GPM] Reflection re-initialization attempted, but Glamourer is not currently ready.");
        }
    }

    public void DumpStateToLog()
    {
        LogInfo("========== [GPM FULL STATE DIAGNOSTIC DUMP] ==========");
        LogInfo($"Plugin Version: {GetType().Assembly.GetName().Version}");
        LogInfo($"Current Frame: {ImGui.GetFrameCount()}");
        LogInfo($"Active Selected Design ID: {activeSelectedDesignId}");
        LogInfo($"Resolution Stage: {currentResolutionStage} (Source: {currentResolutionSource})");
        LogInfo($"In Glamourer Window: {IsInGlamourerWindow()} (Current Window: '{currentWindowName}')");
        LogInfo($"In Designs Tab: {IsInDesignsTab()}");
        LogInfo($"Reflection Initialized: {reflectionInitialized}");
        LogInfo($"Reflection Glamourer Assembly: {glamourerAssembly?.FullName ?? "null"}");
        LogInfo($"Reflection ServiceManager: {serviceManagerInstance?.GetType().FullName ?? "null"}");
        LogInfo($"Reflection DesignFileSystem: {cachedDesignFileSystemInstance?.GetType().FullName ?? "null"}");
        LogInfo($"Reflection SelectionMember: {fileSystemSelectionMember?.Name ?? "null"}");
        LogInfo($"Reflection EphemeralConfig: {cachedEphemeralConfigInstance?.GetType().FullName ?? "null"}");
        LogInfo($"Total Indexed Designs: {DesignManager.Designs.Count}");
        LogInfo($"Total Allocations: {DesignManager.Allocations.Count}");
        LogInfo($"Config Previews Folder: '{Configuration.PreviewsFolderPath}' (Exists: {Directory.Exists(Configuration.PreviewsFolderPath)})");
        LogInfo($"Config LogLevel: {Configuration.LogLevel}, PromoteToInfo: {Configuration.PromoteDebugLogsToInformation}");
        LogInfo("======================================================");
    }

    public void SetActiveDesignId(Guid newId, ResolutionStage stage, string sourceDetails)
    {
        if (newId == Guid.Empty) return;

        bool idChanged = activeSelectedDesignId != newId;

        // Stage hierarchy: Lower integer value = higher authority
        // Stage 1 (ReflectionSelection) > Stage 2 (ReflectionDataNodes) > Stage 3 (ButtonGuid) > Stage 4 (IncognitoHexMatch) > Stage 5 (NameMatchFallback)
        // If the design ID has NOT changed, never downgrade a higher authority stage to a lower authority stage
        if (!idChanged && currentResolutionStage != ResolutionStage.None && (int)stage > (int)currentResolutionStage)
        {
            return;
        }

        bool stageChanged = currentResolutionStage != stage;

        activeSelectedDesignId = newId;
        currentResolutionStage = stage;
        currentResolutionSource = sourceDetails;
        lastSeenDesignFrame = (int)ImGui.GetFrameCount();

        if (idChanged)
        {
            if (Configuration.LogSelectionChanges)
            {
                var design = DesignManager.GetDesignById(newId);
                var name = design?.Name ?? "Unknown";

                if (stage == ResolutionStage.NameMatchFallback)
                {
                    LogWarn($"[GPM] Active design changed to '{name}' [{newId}] via UNRELIABLE Name Match (Source: {sourceDetails}). Multiple designs may share this name!");
                }
                else
                {
                    LogInfo($"[GPM] Active design changed to {newId} ('{name}') via {stage} ({sourceDetails}).");
                }
            }
        }
        else if (stageChanged && Configuration.LogLevel >= GpmLogLevel.Verbose)
        {
            LogVerbose($"[GPM] Resolution stage for active design [{newId}] upgraded to {stage} ({sourceDetails}).");
        }
    }

    private object? GetLiveGlamourerPluginInstance()
    {
        try
        {
            var installedPluginsProp = PluginInterface.GetType().GetProperty("InstalledPlugins", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                       ?? typeof(IDalamudPluginInterface).GetProperty("InstalledPlugins");
            if (installedPluginsProp == null) return null;

            var installedPlugins = installedPluginsProp.GetValue(PluginInterface) as System.Collections.IEnumerable;
            if (installedPlugins == null) return null;

            foreach (var plugin in installedPlugins)
            {
                if (plugin == null) continue;

                var pt = plugin.GetType();
                var nameProp = pt.GetProperty("Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var name = nameProp?.GetValue(plugin) as string;

                var internalNameProp = pt.GetProperty("InternalName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var internalName = internalNameProp?.GetValue(plugin) as string;

                if (string.Equals(name, "Glamourer", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(internalName, "Glamourer", StringComparison.OrdinalIgnoreCase))
                {
                    // Check if plugin is loaded / enabled
                    var isLoadedProp = pt.GetProperty("IsLoaded", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (isLoadedProp != null && isLoadedProp.GetValue(plugin) is bool isLoaded && !isLoaded)
                    {
                        return null;
                    }

                    // In Dalamud, InstalledPlugins returns ExposedPlugin which wraps LocalPlugin in '<plugin>P'
                    object targetObj = plugin;
                    var localPluginField = pt.GetField("<plugin>P", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                           ?? pt.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                                .FirstOrDefault(f => f.FieldType.Name.Contains("LocalPlugin") || f.Name.Contains("plugin"));
                    if (localPluginField != null)
                    {
                        var inner = localPluginField.GetValue(plugin);
                        if (inner != null)
                        {
                            targetObj = inner;
                        }
                    }

                    var targetType = targetObj.GetType();
                    for (var curType = targetType; curType != null && curType != typeof(object); curType = curType.BaseType)
                    {
                        var instanceProp = curType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                           ?? curType.GetProperty("Plugin", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (instanceProp != null)
                        {
                            var inst = instanceProp.GetValue(targetObj);
                            if (inst != null) return inst;
                        }

                        var instanceField = curType.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                            ?? curType.GetField("plugin", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                            ?? curType.GetField("_instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                            ?? curType.GetField("_plugin", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (instanceField != null)
                        {
                            var inst = instanceField.GetValue(targetObj);
                            if (inst != null) return inst;
                        }
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private int lastLivenessCheckFrame = -1;

    public void CheckReflectionLiveness()
    {
        int currentFrame = (int)ImGui.GetFrameCount();
        // Rate-limit liveness check to at most once every 60 frames (~1.0s at 60fps)
        if (lastLivenessCheckFrame >= 0 && currentFrame - lastLivenessCheckFrame < 60)
        {
            return;
        }
        lastLivenessCheckFrame = currentFrame;

        var liveInstance = GetLiveGlamourerPluginInstance();

        if (reflectionInitialized)
        {
            // If marked initialized, verify the live Glamourer plugin instance hasn't been reloaded, disabled, or replaced
            if (liveInstance == null || !object.ReferenceEquals(liveInstance, cachedGlamourerPluginInstance))
            {
                LogInfo("[GPM] Detected Glamourer plugin reload, disable, or update. Invalidating stale reflection cache...");
                InvalidateReflectionCache();
                if (liveInstance != null)
                {
                    lastReflectionAttemptTime = DateTime.MinValue; // Immediate reconnect
                    InitializeReflection();
                }
            }
        }
        else
        {
            // If reflection is currently disconnected, attempt reconnection if Glamourer is now loaded
            if (liveInstance != null)
            {
                InitializeReflection();
            }
        }
    }

    private void CheckPerFrameLiveness() => CheckReflectionLiveness();

    private void InitializeReflection()
    {
        if (reflectionInitialized) return;

        // Rate-limit retry attempts to once every 2 seconds to prevent CPU overhead before Glamourer is ready
        if ((DateTime.UtcNow - lastReflectionAttemptTime).TotalSeconds < 2.0)
        {
            return;
        }
        lastReflectionAttemptTime = DateTime.UtcNow;

        try
        {
            var glamourerInstance = GetLiveGlamourerPluginInstance();
            if (glamourerInstance == null)
            {
                if (Configuration.LogLevel >= GpmLogLevel.Verbose)
                {
                    LogVerbose("[GPM] Could not find loaded Glamourer plugin instance in InstalledPlugins. Will retry...");
                }
                return;
            }

            for (var gt = glamourerInstance.GetType(); gt != null && gt != typeof(object); gt = gt.BaseType)
            {
                var servicesField = gt.GetField("_services", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                    ?? gt.GetField("services", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                    ?? gt.GetField("_serviceManager", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (servicesField != null)
                {
                    serviceManagerInstance = servicesField.GetValue(glamourerInstance);
                    if (serviceManagerInstance != null) break;
                }
            }

            if (serviceManagerInstance == null)
            {
                LogWarn("[GPM] Could not find _services field on Glamourer instance. Will retry...");
                return;
            }

            // Ensure glamourerAssembly is retrieved directly from the live plugin instance first
            glamourerAssembly = glamourerInstance.GetType().Assembly;
            var designFileSystemType = glamourerAssembly.GetType("Glamourer.Designs.DesignFileSystem")
                                       ?? AppDomain.CurrentDomain.GetAssemblies()
                                           .FirstOrDefault(a => a.GetName().Name == "Glamourer")?
                                           .GetType("Glamourer.Designs.DesignFileSystem");

            // Extract Microsoft.Extensions.DependencyInjection.ServiceProvider from Luna.ServiceManager.Provider
            var providerProp = serviceManagerInstance.GetType().GetProperty("Provider", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var innerProvider = providerProp?.GetValue(serviceManagerInstance) as IServiceProvider;

            // Multi-Tier Service Resolution for DesignFileSystem:
            // Tier 1: Invoke generic GetService<T>() on Luna.ServiceManager
            if (designFileSystemType != null)
            {
                var getServiceMethod = serviceManagerInstance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1);
                if (getServiceMethod != null)
                {
                    try
                    {
                        var genericMethod = getServiceMethod.MakeGenericMethod(designFileSystemType);
                        cachedDesignFileSystemInstance = genericMethod.Invoke(serviceManagerInstance, null);
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"[GPM] Generic GetService invocation failed: {ex.Message}");
                    }
                }
            }

            // Tier 2: Query Provider as IServiceProvider using designFileSystemType
            if (cachedDesignFileSystemInstance == null && innerProvider != null && designFileSystemType != null)
            {
                try
                {
                    cachedDesignFileSystemInstance = innerProvider.GetService(designFileSystemType);
                }
                catch { }
            }

            // Tier 3: Inspect Luna.ServiceManager's _collection (ServiceDescriptor list) to bypass cross-ALC type equality mismatches
            if (cachedDesignFileSystemInstance == null && innerProvider != null)
            {
                var collField = serviceManagerInstance.GetType().GetField("_collection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (collField?.GetValue(serviceManagerInstance) is System.Collections.IEnumerable coll)
                {
                    foreach (var item in coll)
                    {
                        if (item == null) continue;
                        var stProp = item.GetType().GetProperty("ServiceType");
                        if (stProp?.GetValue(item) is Type st && st.Name == "DesignFileSystem")
                        {
                            try
                            {
                                cachedDesignFileSystemInstance = innerProvider.GetService(st);
                                if (cachedDesignFileSystemInstance != null) break;
                            }
                            catch { }
                        }
                    }
                }
            }

            // Tier 4: Search ServiceManager._ownedObjects (which stores all instantiated IDisposable services)
            if (cachedDesignFileSystemInstance == null)
            {
                var ownedField = serviceManagerInstance.GetType().GetField("_ownedObjects", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (ownedField?.GetValue(serviceManagerInstance) is System.Collections.IEnumerable owned)
                {
                    foreach (var item in owned)
                    {
                        if (item != null && item.GetType().Name == "DesignFileSystem")
                        {
                            cachedDesignFileSystemInstance = item;
                            break;
                        }
                    }
                }
            }

            // Tier 5: Direct lookup through Glamourer instance fields/properties
            if (cachedDesignFileSystemInstance == null)
            {
                for (var gt = glamourerInstance.GetType(); gt != null && gt != typeof(object); gt = gt.BaseType)
                {
                    foreach (var field in gt.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (field.FieldType.Name == "DesignFileSystem")
                        {
                            cachedDesignFileSystemInstance = field.GetValue(glamourerInstance);
                            if (cachedDesignFileSystemInstance != null) break;
                        }
                    }
                    if (cachedDesignFileSystemInstance != null) break;
                }
            }

            if (cachedDesignFileSystemInstance == null)
            {
                LogWarn("[GPM] Could not resolve DesignFileSystem service from ServiceManager. Will retry...");
                return;
            }

            // BaseFileSystem.Selection can be either a Field or a Property in Luna hierarchy
            var fsType = cachedDesignFileSystemInstance.GetType();
            for (var ft = fsType; ft != null && ft != typeof(object); ft = ft.BaseType)
            {
                fileSystemSelectionMember = (MemberInfo?)ft.GetField("Selection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                            ?? ft.GetProperty("Selection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fileSystemSelectionMember != null) break;
            }

            if (fileSystemSelectionMember == null)
            {
                LogWarn("[GPM] Could not find Selection member on DesignFileSystem. Will retry...");
                return;
            }

            // Also resolve EphemeralConfig for tab state checking
            var ephemType = glamourerAssembly?.GetType("Glamourer.Config.EphemeralConfig");
            if (ephemType != null)
            {
                var getServiceMethod = serviceManagerInstance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1);
                if (getServiceMethod != null)
                {
                    try
                    {
                        cachedEphemeralConfigInstance = getServiceMethod.MakeGenericMethod(ephemType).Invoke(serviceManagerInstance, null);
                    }
                    catch { }
                }

                if (cachedEphemeralConfigInstance == null && innerProvider != null)
                {
                    try
                    {
                        cachedEphemeralConfigInstance = innerProvider.GetService(ephemType);
                    }
                    catch { }
                }

                if (cachedEphemeralConfigInstance != null)
                {
                    selectedMainTabProp = cachedEphemeralConfigInstance.GetType().GetProperty("SelectedMainTab", BindingFlags.Public | BindingFlags.Instance);
                }
            }

            reflectionInitialized = true;
            cachedGlamourerPluginInstance = glamourerInstance;
            reflectionErrorStreak = 0;
            LogInfo("[GPM] Glamourer selection reflection initialized successfully.");
        }
        catch (Exception ex)
        {
            if (ImGui.GetFrameCount() % 3600 == 0)
            {
                LogErr($"[GPM] Failed to initialize Glamourer selection reflection: {ex}");
            }
        }
    }

    public Guid GetActiveSelectedDesignIdReflection()
    {
        int currentFrame = (int)ImGui.GetFrameCount();
        if (currentFrame != lastReflectedFrame)
        {
            lastReflectedFrame = currentFrame;
            reflectedThisFrameCount = 0;
            cachedReflectedGuid = Guid.Empty;
        }

        if (cachedReflectedGuid != Guid.Empty)
        {
            return cachedReflectedGuid;
        }

        // Limit reflection queries to at most 2 attempts per frame to avoid CPU overhead on empty/unselected frames
        if (reflectedThisFrameCount >= 2)
        {
            return Guid.Empty;
        }
        reflectedThisFrameCount++;

        CheckReflectionLiveness();

        if (!reflectionInitialized || cachedDesignFileSystemInstance == null || fileSystemSelectionMember == null)
        {
            return Guid.Empty;
        }

        try
        {
            var selectionObj = GetMemberValue(fileSystemSelectionMember, cachedDesignFileSystemInstance);
            if (selectionObj == null)
            {
                // If selection object is unexpectedly null while inside Glamourer Designs tab, check if Glamourer was reloaded
                CheckReflectionLiveness();
                return Guid.Empty;
            }

            ResolutionStage stage = ResolutionStage.ReflectionSelection;
            string source = "FileSystemSelection.Selection";

            // 1. Extract leaf node from selectionObj (Luna.FileSystemSelection)
            // Property Selection returns IFileSystemData? when a single node is selected
            object? leafNode = ExtractPropertyValueSafe(selectionObj, "Selection");

            // 2. If single selection was null, check DataNodes / SelectedData / OrderedNodes
            if (leafNode == null)
            {
                var listVal = ExtractPropertyValueSafe(selectionObj, "DataNodes")
                              ?? ExtractPropertyValueSafe(selectionObj, "SelectedData")
                              ?? ExtractPropertyValueSafe(selectionObj, "OrderedNodes")
                              ?? ExtractPropertyValueSafe(selectionObj, "Selection");

                if (listVal is System.Collections.IList list && list.Count > 0)
                {
                    leafNode = list[0];
                    stage = ResolutionStage.ReflectionDataNodes;
                    source = "FileSystemSelection.DataNodes[0]";
                }
                else if (listVal is System.Collections.IEnumerable enumerable)
                {
                    foreach (var item in enumerable)
                    {
                        leafNode = item;
                        stage = ResolutionStage.ReflectionDataNodes;
                        source = "FileSystemSelection.DataNodes(IEnumerable)";
                        break;
                    }
                }
            }

            if (leafNode == null) return Guid.Empty;

            // 3. Extract Design object from leaf node (IFileSystemData.Value)
            // Notice: leafNode is Luna.FileSystemData<Design> (internal sealed class).
            // ExtractPropertyValueSafe safely dispatches through IFileSystemData.Value (public interface).
            object? designObj = ExtractPropertyValueSafe(leafNode, "Value") ?? leafNode;
            if (designObj == null) return Guid.Empty;

            // 4. Extract Guid identifier
            Guid foundGuid = Guid.Empty;
            var idVal = ExtractPropertyValueSafe(designObj, "Identifier")
                        ?? ExtractPropertyValueSafe(designObj, "Id")
                        ?? ExtractPropertyValueSafe(leafNode, "Identifier");

            if (idVal is Guid guid && guid != Guid.Empty)
            {
                foundGuid = guid;
            }
            else if (idVal != null && Guid.TryParse(idVal.ToString(), out var parsedGuid) && parsedGuid != Guid.Empty)
            {
                foundGuid = parsedGuid;
            }

            if (foundGuid != Guid.Empty)
            {
                // Ensure design exists in GPM (synthesize in-memory if disk index is missing or out of sync)
                var design = DesignManager.GetDesignById(foundGuid);
                if (design == null)
                {
                    string designName = "Unnamed Design";
                    try
                    {
                        var nameVal = ExtractPropertyValueSafe(designObj, "Name") ?? ExtractPropertyValueSafe(leafNode, "Name");
                        if (nameVal != null && !string.IsNullOrWhiteSpace(nameVal.ToString()))
                        {
                            designName = nameVal.ToString()!;
                        }
                    }
                    catch { }

                    design = DesignManager.RegisterSynthesizedDesign(foundGuid, designName);
                }

                cachedReflectedGuid = foundGuid;
                SetActiveDesignId(foundGuid, stage, source);
                reflectionErrorStreak = 0;
                return foundGuid;
            }
        }
        catch (Exception ex)
        {
            reflectionErrorStreak++;
            if (reflectionErrorStreak >= 5)
            {
                LogInfo("[GPM] Repeated reflection errors detected (Glamourer may have reloaded or updated). Invalidating reflection cache.");
                InvalidateReflectionCache();
            }
            else if (currentFrame % 1800 == 0)
            {
                LogWarn($"[GPM] Reflection error in GetActiveSelectedDesignIdReflection: {ex.Message}");
            }
        }

        return cachedReflectedGuid;
    }

    public bool IsInDesignsTab()
    {
        InitializeReflection();
        if (cachedEphemeralConfigInstance == null || selectedMainTabProp == null) return true;

        try
        {
            var tabVal = selectedMainTabProp.GetValue(cachedEphemeralConfigInstance);
            if (tabVal != null)
            {
                var str = tabVal.ToString();
                if (string.Equals(str, "Designs", StringComparison.OrdinalIgnoreCase))
                    return true;

                // Glamourer MainTabType enum: None = 0, Settings = 1, Designs = 2
                if (tabVal is int intVal && intVal == 2)
                    return true;
                if (int.TryParse(str, out var parsedInt) && parsedInt == 2)
                    return true;

                return false;
            }
        }
        catch { }

        return true;
    }

    private static bool IsHex(string str)
    {
        if (string.IsNullOrEmpty(str)) return false;
        foreach (char c in str)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }

    public void OnButtonDraw(string label)
    {
        if (string.IsNullOrEmpty(label)) return;
        if (label.Contains("##GPM_") || label.Contains("###GPM_") || label.StartsWith("GPM_")) return;

        if (Configuration.LogHookEvents && Configuration.LogLevel >= GpmLogLevel.Verbose)
        {
            LogVerbose($"[GPM Hook] Button: '{label}'");
        }

        // Capture design UUID from any buttons containing a GUID, regardless of window filter for safety
        if (TryExtractGuid(label, out var id))
        {
            if (DesignManager.GetDesignById(id) != null)
            {
                SetActiveDesignId(id, ResolutionStage.ButtonGuid, $"Button Label ({label})");
            }
        }

        // Hook "Apply to Yourself" or "Apply to yourself" to update reflection active design
        bool isApplyButton = label.Contains("Apply to Yourself", StringComparison.OrdinalIgnoreCase) || 
                             label.Contains("Apply to yourself", StringComparison.OrdinalIgnoreCase) ||
                             label.Contains("Apply to Self", StringComparison.OrdinalIgnoreCase);

        if (isApplyButton && IsInDesignsTab())
        {
            var reflectedGuid = GetActiveSelectedDesignIdReflection();
            if (reflectedGuid != Guid.Empty)
            {
                SetActiveDesignId(reflectedGuid, currentResolutionStage, $"ApplyButton Trigger ({label})");
            }
        }
    }

    public void OnButtonDrawAfter(string label)
    {
        if (string.IsNullOrEmpty(label)) return;
        if (label.Contains("##GPM_") || label.Contains("###GPM_") || label.StartsWith("GPM_")) return;

        if (!IsInGlamourerWindow()) return;
        if (!IsInDesignsTab()) return;

        bool isTargetButton = label.Contains("Export to Dat", StringComparison.OrdinalIgnoreCase) || 
                             label.Contains("Export to Clipboard", StringComparison.OrdinalIgnoreCase) ||
                             label.Contains("Apply Mod Associations", StringComparison.OrdinalIgnoreCase);

        if (isTargetButton)
        {
            int currentFrame = (int)ImGui.GetFrameCount();
            if (lastDrawnGpmFrame != currentFrame)
            {
                lastDrawnGpmFrame = currentFrame;

                var reflectedGuid = GetActiveSelectedDesignIdReflection();
                if (reflectedGuid != Guid.Empty)
                {
                    SetActiveDesignId(reflectedGuid, currentResolutionStage, $"TargetButton Trigger ({label})");
                }

                if (activeSelectedDesignId != Guid.Empty && DesignManager.GetDesignById(activeSelectedDesignId) != null)
                {
                    shouldDrawInjectedUI = true;
                    deferredDesignId = activeSelectedDesignId;
                }
            }
        }
    }

    public void CheckAndDrawDeferredUI()
    {
        if (shouldDrawInjectedUI)
        {
            if (IsTooltipOrPopup()) return;

            int currentFrame = (int)ImGui.GetFrameCount();
            if (currentFrame > lastDrawnGpmFrame)
            {
                shouldDrawInjectedUI = false;
                return;
            }

            shouldDrawInjectedUI = false;
            if (deferredDesignId != Guid.Empty && DesignManager.GetDesignById(deferredDesignId) != null)
            {
                DrawInjectedUI(deferredDesignId);
            }
        }
    }

    public void OnSelectableDraw(string label, bool selected)
    {
        if (string.IsNullOrEmpty(label)) return;
        if (label.Contains("##GPM_") || label.Contains("###GPM_")) return;

        if (!IsInGlamourerWindow()) return;
        if (!IsInDesignsTab()) return;

        if (selected)
        {
            // 1. Primary Authority: Query fast per-frame reflection cache
            var reflectedGuid = GetActiveSelectedDesignIdReflection();
            if (reflectedGuid != Guid.Empty)
            {
                SetActiveDesignId(reflectedGuid, currentResolutionStage, $"Selectable Refl ({label})");
                return;
            }

            // 2. Fallback: Try extracting GUID directly from label
            if (TryExtractGuid(label, out var id) && DesignManager.GetDesignById(id) != null)
            {
                SetActiveDesignId(id, ResolutionStage.ButtonGuid, $"Selectable GUID ({label})");
                return;
            }

            // 3. Fallback: Incognito 8-character hex match
            var cleanName = label;
            var hashIdx = label.IndexOf("##");
            if (hashIdx >= 0)
            {
                cleanName = label.Substring(0, hashIdx);
            }
            cleanName = cleanName.Trim();

            // If label is a path (e.g. "Folder/Subfolder/Design"), take the last segment
            var slashIdx = cleanName.LastIndexOf('/');
            if (slashIdx >= 0 && slashIdx < cleanName.Length - 1)
            {
                cleanName = cleanName.Substring(slashIdx + 1).Trim();
            }

            if (cleanName.Length == 8 && IsHex(cleanName))
            {
                var matchingDesign = DesignManager.Designs.FirstOrDefault(d => 
                    d.Identifier.ToString().StartsWith(cleanName, StringComparison.OrdinalIgnoreCase));
                if (matchingDesign != null)
                {
                    SetActiveDesignId(matchingDesign.Identifier, ResolutionStage.IncognitoHexMatch, $"Selectable Incognito Hex ({cleanName})");
                    return;
                }
            }

            // 4. Fallback: Design name match (handling Glamourer duplicate counters like "Name (2)")
            var matchingList = DesignManager.GetDesignsByName(cleanName);
            if (matchingList.Count == 0 && Regex.IsMatch(cleanName, @"\s\(\d+\)$"))
            {
                var baseName = Regex.Replace(cleanName, @"\s\(\d+\)$", "").Trim();
                matchingList = DesignManager.GetDesignsByName(baseName);
            }

            if (matchingList.Count == 1)
            {
                SetActiveDesignId(matchingList[0].Identifier, ResolutionStage.NameMatchFallback, $"Selectable Unique Name ('{cleanName}')");
            }
            else if (matchingList.Count > 1)
            {
                var targetDesign = matchingList.FirstOrDefault(d => d.Identifier == activeSelectedDesignId) ?? matchingList[0];
                SetActiveDesignId(targetDesign.Identifier, ResolutionStage.NameMatchFallback, $"Selectable Multiple Names ('{cleanName}', {matchingList.Count} matches)");
            }
        }
    }

    public void OnTreeNodeDraw(string label, bool selected, bool isLeaf)
    {
        if (string.IsNullOrEmpty(label)) return;
        if (label.Contains("##GPM_") || label.Contains("###GPM_")) return;

        if (!IsInGlamourerWindow()) return;
        if (!IsInDesignsTab()) return;

        if (selected && isLeaf)
        {
            // 1. Primary Authority: Query fast per-frame reflection cache
            var reflectedGuid = GetActiveSelectedDesignIdReflection();
            if (reflectedGuid != Guid.Empty)
            {
                SetActiveDesignId(reflectedGuid, currentResolutionStage, $"TreeNode Refl ({label})");
                return;
            }

            // 2. Fallback: Try extracting GUID directly from label
            if (TryExtractGuid(label, out var id) && DesignManager.GetDesignById(id) != null)
            {
                SetActiveDesignId(id, ResolutionStage.ButtonGuid, $"TreeNode GUID ({label})");
                return;
            }

            // 3. Fallback: Incognito 8-character hex match
            var cleanName = label;
            var hashIdx = label.IndexOf("##");
            if (hashIdx >= 0)
            {
                cleanName = label.Substring(0, hashIdx);
            }
            cleanName = cleanName.Trim();

            // If label is a path (e.g. "Folder/Subfolder/Design"), take the last segment
            var slashIdx = cleanName.LastIndexOf('/');
            if (slashIdx >= 0 && slashIdx < cleanName.Length - 1)
            {
                cleanName = cleanName.Substring(slashIdx + 1).Trim();
            }

            if (cleanName.Length == 8 && IsHex(cleanName))
            {
                var matchingDesign = DesignManager.Designs.FirstOrDefault(d => 
                    d.Identifier.ToString().StartsWith(cleanName, StringComparison.OrdinalIgnoreCase));
                if (matchingDesign != null)
                {
                    SetActiveDesignId(matchingDesign.Identifier, ResolutionStage.IncognitoHexMatch, $"TreeNode Incognito Hex ({cleanName})");
                    return;
                }
            }

            // 4. Fallback: Design name match (handling Glamourer duplicate counters like "Name (2)")
            var matchingList = DesignManager.GetDesignsByName(cleanName);
            if (matchingList.Count == 0 && Regex.IsMatch(cleanName, @"\s\(\d+\)$"))
            {
                var baseName = Regex.Replace(cleanName, @"\s\(\d+\)$", "").Trim();
                matchingList = DesignManager.GetDesignsByName(baseName);
            }

            if (matchingList.Count == 1)
            {
                SetActiveDesignId(matchingList[0].Identifier, ResolutionStage.NameMatchFallback, $"TreeNode Unique Name ('{cleanName}')");
            }
            else if (matchingList.Count > 1)
            {
                var targetDesign = matchingList.FirstOrDefault(d => d.Identifier == activeSelectedDesignId) ?? matchingList[0];
                SetActiveDesignId(targetDesign.Identifier, ResolutionStage.NameMatchFallback, $"TreeNode Multiple Names ('{cleanName}', {matchingList.Count} matches)");
            }
        }
    }

    public void DrawInjectedUI(Guid designId)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Glamourer Preview Image:");

        var previewsFolder = Configuration.PreviewsFolderPath;
        if (string.IsNullOrEmpty(previewsFolder) || !Directory.Exists(previewsFolder))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
            ImGui.TextUnformatted("Previews directory is not configured in settings!");
            ImGui.PopStyleColor();
            ImGui.Spacing();
            if (ImGui.Button("Configure Previews Directory##GPM_ConfigOpen"))
            {
                ToggleConfigUi();
            }
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            return;
        }

        var design = DesignManager.GetDesignById(designId);
        if (design == null)
        {
            if (lastFailedDesignId != designId)
            {
                lastFailedDesignId = designId;
                Log.Warning($"[GPM] Selected design {designId} could not be found in designs folder.");
            }

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
            ImGui.TextUnformatted("Design could not be found in designs directory.");
            ImGui.PopStyleColor();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            return;
        }
        else
        {
            lastFailedDesignId = Guid.Empty;
        }

        if (design.HasPreview)
        {
            // Load and display preview image
            var path = GetBustedImagePath(design.PreviewImagePath!);
            
            // Texture loading
            var texture = TextureProvider.GetFromFile(path).GetWrapOrDefault();
            if (texture != null)
            {
                var width = ImGui.GetContentRegionAvail().X;
                float aspect = 16f / 9f;
                if (texture.Width > 0 && texture.Height > 0)
                {
                    aspect = (float)texture.Width / texture.Height;
                }
                var scale = Configuration.PreviewImageSizePercent / 100f;
                var drawWidth = width * scale;
                var drawHeight = drawWidth / aspect;

                // Cap the height to a reasonable maximum so vertical/portrait images don't overflow the UI
                // ImGuiHelpers.GlobalScale handles High-DPI/4k scaling automatically
                float maxHeight = 350f * ImGuiHelpers.GlobalScale;
                if (drawHeight > maxHeight)
                {
                    drawHeight = maxHeight;
                    drawWidth = drawHeight * aspect;
                }

                var offsetX = (width - drawWidth) / 2f;
                if (offsetX > 0)
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);
                }

                ImGui.Image(texture.Handle, new Vector2(drawWidth, drawHeight));

                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted("Middle-click: Hold to zoom.");
                    ImGui.EndTooltip();
                }

                // Middle-click to zoom
                if (ImGui.IsItemHovered() && ImGui.IsMouseDown(ImGuiMouseButton.Middle))
                {
                    var winSize = ImGuiHelpers.MainViewport.WorkSize;
                    var imgSize = new Vector2(texture.Width, texture.Height) * Configuration.ZoomScale;

                    if (imgSize.X > winSize.X || imgSize.Y > winSize.Y)
                    {
                        var ratio = Math.Min(winSize.X / imgSize.X, winSize.Y / imgSize.Y);
                        imgSize *= ratio;
                    }

                    var min = new Vector2(winSize.X / 2 - imgSize.X / 2, winSize.Y / 2 - imgSize.Y / 2);
                    var max = new Vector2(winSize.X / 2 + imgSize.X / 2, winSize.Y / 2 + imgSize.Y / 2);

                    ImGui.GetForegroundDrawList().AddImage(texture.Handle, min, max);
                }

                ImGui.Spacing();
                
                // Align Delete button to the right of the area
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 0.7f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 0.3f, 0.3f, 0.9f));
                if (ImGui.Button($"Remove Preview Image##GPM_DelImg_{designId}"))
                {
                    if (DesignManager.Allocations.TryGetValue(designId, out var imgFile))
                    {
                        var imgPath = Path.Combine(previewsFolder, imgFile);
                        try
                        {
                            if (File.Exists(imgPath)) File.Delete(imgPath);
                        }
                        catch { }
                        
                        DesignManager.Allocations.Remove(designId);
                        DesignManager.SaveAllocations();
                    }
                    design.PreviewImagePath = null;
                }
                ImGui.PopStyleColor(2);
            }
            else if (File.Exists(path))
            {
                // File exists on disk and is currently loading asynchronously
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1f));
                ImGui.TextUnformatted("Loading preview image...");
                ImGui.PopStyleColor();
                ImGui.Spacing();
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.4f, 0.4f, 1f));
                ImGui.TextUnformatted("Failed to load preview image (file may be missing or corrupted).");
                ImGui.PopStyleColor();
                ImGui.Spacing();

                // Render import options side-by-side or stacked cleanly so they can replace/overwrite it
                var availWidth = ImGui.GetContentRegionAvail().X;
                var buttonWidth = (availWidth - ImGui.GetStyle().ItemSpacing.X * 2) / 3f;

                if (ImGui.Button($"Paste Clipboard##GPM_Paste_{designId}", new Vector2(buttonWidth, 30)))
                {
                    try
                    {
                        using var clipboardImage = ClipboardHelper.GetImageFromClipboard();
                        if (clipboardImage != null)
                        {
                            DesignManager.SaveImageDirect(designId, clipboardImage);
                            ChatGui.Print("Successfully pasted preview image from clipboard!");
                        }
                        else
                        {
                            ChatGui.PrintError("No image found in your clipboard!");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Failed to paste clipboard image: {ex}");
                    }
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Directly paste and crop an image from your clipboard.");

                ImGui.SameLine();
                if (ImGui.Button($"Browse File##GPM_Browse_{designId}", new Vector2(buttonWidth, 30)))
                {
                    FileDialogManager.OpenFileDialog(
                        "Select Preview Image", 
                        "Image Files{.png,.jpg,.jpeg,.webp,.bmp,.gif}", 
                        (success, path) =>
                        {
                            if (success)
                            {
                                DesignManager.UpdatePreviewImage(designId, path);
                                ChatGui.Print("Successfully attached preview image!");
                            }
                        });
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Browse your local files for a preview image.");

                ImGui.SameLine();
                if (ImGui.Button($"Screenshot##GPM_Screenshot_{designId}", new Vector2(buttonWidth, 30)))
                {
                    if (Configuration.AutoApplyOnScreenshot)
                    {
                        CommandManager.ProcessCommand($"/glamour apply {designId} | <me>");
                    }
                    SetScreenshotCaptureActive(true);
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Take a cropped screenshot from the center of the screen.");

                ImGui.Spacing();

                // Also provide "Remove Preview Reference" button to clean up
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 0.7f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1f, 0.3f, 0.3f, 0.9f));
                if (ImGui.Button($"Remove Preview Reference##GPM_DelImg_{designId}"))
                {
                    if (DesignManager.Allocations.TryGetValue(designId, out var imgFile))
                    {
                        var imgPath = Path.Combine(previewsFolder, imgFile);
                        try
                        {
                            if (File.Exists(imgPath)) File.Delete(imgPath);
                        }
                        catch { }
                        
                        DesignManager.Allocations.Remove(designId);
                        DesignManager.SaveAllocations();
                    }
                    design.PreviewImagePath = null;
                }
                ImGui.PopStyleColor(2);
            }
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.8f, 0.8f, 1f));
            ImGui.TextUnformatted("No preview image attached to this design.");
            ImGui.PopStyleColor();
            ImGui.Spacing();

            // Render import options side-by-side or stacked cleanly
            var availWidth = ImGui.GetContentRegionAvail().X;
            var buttonWidth = (availWidth - ImGui.GetStyle().ItemSpacing.X * 2) / 3f;

            if (ImGui.Button($"Paste Clipboard##GPM_Paste_{designId}", new Vector2(buttonWidth, 30)))
            {
                try
                {
                    using var clipboardImage = ClipboardHelper.GetImageFromClipboard();
                    if (clipboardImage != null)
                    {
                        DesignManager.SaveImageDirect(designId, clipboardImage);
                        ChatGui.Print("Successfully pasted preview image from clipboard!");
                    }
                    else
                    {
                        ChatGui.PrintError("No image found in your clipboard!");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to paste clipboard image: {ex}");
                }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Directly paste and crop an image from your clipboard.");

            ImGui.SameLine();
            if (ImGui.Button($"Browse File##GPM_Browse_{designId}", new Vector2(buttonWidth, 30)))
            {
                FileDialogManager.OpenFileDialog(
                    "Select Preview Image", 
                    "Image Files{.png,.jpg,.jpeg,.webp,.bmp,.gif}", 
                    (success, path) =>
                    {
                        if (success)
                        {
                            DesignManager.UpdatePreviewImage(designId, path);
                            ChatGui.Print("Successfully attached preview image!");
                        }
                    });
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Browse your local files for a preview image.");

            ImGui.SameLine();
            if (ImGui.Button($"Screenshot##GPM_Screenshot_{designId}", new Vector2(buttonWidth, 30)))
            {
                if (Configuration.AutoApplyOnScreenshot)
                {
                    CommandManager.ProcessCommand($"/glamour apply {designId} | <me>");
                }
                LastScreenshotDesignId = designId;
                SetScreenshotCaptureActive(true);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Take a cropped screenshot from the center of the screen.");
        }

        // Live diagnostic overlay below preview controls (visible if enabled or log level >= Debug)
        DrawDebugOverlay(design);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawDebugOverlay(DesignInfo design)
    {
        if (!Configuration.ShowDebugOverlayBelowPreview && Configuration.LogLevel < GpmLogLevel.Debug)
            return;

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.3f, 0.5f, 0.8f, 0.5f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 4f);

        float boxHeight = 44f * ImGuiHelpers.GlobalScale;
        if (ImGui.BeginChild("##GPM_DebugOverlay", new Vector2(0, boxHeight), true, ImGuiWindowFlags.NoScrollbar))
        {
            Vector4 stageColor = currentResolutionStage switch
            {
                ResolutionStage.ReflectionSelection => new Vector4(0.4f, 0.9f, 0.4f, 1f), // Green
                ResolutionStage.ReflectionDataNodes => new Vector4(0.6f, 0.9f, 0.4f, 1f), // Light green
                ResolutionStage.ButtonGuid => new Vector4(0.4f, 0.8f, 1f, 1f),          // Cyan
                ResolutionStage.IncognitoHexMatch => new Vector4(0.9f, 0.8f, 0.3f, 1f),    // Yellow
                ResolutionStage.NameMatchFallback => new Vector4(1f, 0.4f, 0.3f, 1f),    // Red/Orange warning
                _ => new Vector4(0.7f, 0.7f, 0.7f, 1f)
            };

            ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), "[GPM DEBUG]");
            ImGui.SameLine();
            ImGui.TextUnformatted("Design:");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), $"\"{design.Name}\"");
            ImGui.SameLine();
            var guidShort = design.Identifier.ToString();
            if (guidShort.Length >= 8) guidShort = guidShort.Substring(0, 8);
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), $"[{guidShort}]");

            ImGui.TextUnformatted("Stage:");
            ImGui.SameLine();
            ImGui.TextColored(stageColor, $"{currentResolutionStage}");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), $"({currentResolutionSource})");

            ImGui.SameLine();
            ImGui.TextUnformatted("| Refl:");
            ImGui.SameLine();
            if (reflectionInitialized)
            {
                ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.3f, 1f), "Connected");
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "Disconnected");
            }

            if (ImGui.IsWindowHovered())
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted($"Active Design GUID: {design.Identifier}");
                ImGui.TextUnformatted($"Design File System Folder: {(string.IsNullOrEmpty(design.FileSystemFolder) ? "(root)" : design.FileSystemFolder)}");
                ImGui.TextUnformatted($"Resolution Pipeline: Stage {(int)currentResolutionStage} / 5 ({currentResolutionStage})");
                ImGui.TextUnformatted($"Source Detail: {currentResolutionSource}");
                ImGui.TextUnformatted($"Reflection Status: {(reflectionInitialized ? "Active & Bound" : "Unbound / Retrying")}");
                ImGui.TextUnformatted($"Has Preview Image: {(design.HasPreview ? "Yes" : "No")}");
                if (design.HasPreview)
                {
                    ImGui.TextUnformatted($"Image Path: {design.PreviewImagePath}");
                }
                ImGui.TextUnformatted($"Total Designs In Memory: {DesignManager.Designs.Count}");
                if (currentResolutionStage == ResolutionStage.NameMatchFallback)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), "WARNING: Currently resolving by name match fallback!");
                    ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), "If multiple designs share this name, previews may become inaccurate.");
                }
                ImGui.EndTooltip();
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    private void DrawScreenshotOverlay()
    {
        if (!isCapturingScreenshot) return;

        if (!string.IsNullOrEmpty(pendingScreenshotPath))
        {
            var pathToProcess = pendingScreenshotPath;
            pendingScreenshotPath = null;
            SetScreenshotCaptureActive(false);
            ProcessAutoImportedScreenshot(pathToProcess);
            return;
        }

        if (screenshotDelayFrame > 0)
        {
            screenshotDelayFrame--;
            if (screenshotDelayFrame == 0)
            {
                DoCaptureScreenshot();
                SetScreenshotCaptureActive(false);
            }
            return;
        }

        var viewport = ImGuiHelpers.MainViewport;
        var pos = viewport.Pos;
        var size = viewport.Size;
        var center = pos + size / 2f;
        var drawList = ImGui.GetForegroundDrawList();

        float boxWidth = 500f;
        float boxHeight = 500f;

        if (Configuration.CropOption == CropAspect.Aspect16_9)
        {
            boxWidth = 640f;
            boxHeight = 360f;
        }
        else if (Configuration.CropOption == CropAspect.Aspect4_3)
        {
            boxWidth = 533f;
            boxHeight = 400f;
        }
        else if (Configuration.CropOption == CropAspect.Aspect9_16)
        {
            boxWidth = 360f;
            boxHeight = 640f;
        }
        else if (Configuration.CropOption == CropAspect.Aspect3_4)
        {
            boxWidth = 450f;
            boxHeight = 600f;
        }

        // Apply custom screenshot configurations
        boxWidth *= Configuration.ScreenshotScale;
        boxHeight *= Configuration.ScreenshotScale;
        center.X += Configuration.ScreenshotOffsetX;
        center.Y += Configuration.ScreenshotOffsetY;

        var min = new Vector2(center.X - boxWidth / 2f, center.Y - boxHeight / 2f);
        var max = new Vector2(center.X + boxWidth / 2f, center.Y + boxHeight / 2f);

        // Dim surrounding area
        drawList.AddRectFilled(pos, new Vector2(pos.X + size.X, min.Y), ImGui.GetColorU32(new Vector4(0, 0, 0, 0.4f)));
        drawList.AddRectFilled(new Vector2(pos.X, max.Y), pos + size, ImGui.GetColorU32(new Vector4(0, 0, 0, 0.4f)));
        drawList.AddRectFilled(new Vector2(pos.X, min.Y), new Vector2(min.X, max.Y), ImGui.GetColorU32(new Vector4(0, 0, 0, 0.4f)));
        drawList.AddRectFilled(new Vector2(max.X, min.Y), new Vector2(pos.X + size.X, max.Y), ImGui.GetColorU32(new Vector4(0, 0, 0, 0.4f)));

        // Draw glowing sky-blue crop border
        drawList.AddRect(min, max, ImGui.GetColorU32(new Vector4(0.3f, 0.8f, 1f, 0.9f)), 0f, ImDrawFlags.None, 2f);

        // Draw HUD message badge
        var text = Configuration.AutoImportFromWatchedFolder && !string.IsNullOrEmpty(Configuration.GameScreenshotFolderPath)
            ? "Screenshot Mode - [Space] Capture Screen | Or take a Game/ReShade screenshot to auto-crop! | [Esc] Cancel"
            : "Screenshot Mode - [Space] Capture Screen | [Esc] Cancel";
        var textSize = ImGui.CalcTextSize(text);
        var textPos = new Vector2(center.X - textSize.X / 2f, max.Y + 20f);

        drawList.AddRectFilled(textPos - new Vector2(10, 5), textPos + textSize + new Vector2(10, 5), ImGui.GetColorU32(new Vector4(0.1f, 0.1f, 0.1f, 0.8f)), 4f);
        drawList.AddText(textPos, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), text);

        // Capture keyboard events
        bool spaceDown = KeyState[VirtualKey.SPACE];
        bool escapeDown = KeyState[VirtualKey.ESCAPE];

        bool spacePressed = spaceDown && !lastSpaceDown;
        bool escapePressed = escapeDown && !lastEscapeDown;

        lastSpaceDown = spaceDown;
        lastEscapeDown = escapeDown;

        if (spacePressed)
        {
            screenshotDelayFrame = 2; // Count down 2 frames then trigger capture
        }
        else if (escapePressed)
        {
            SetScreenshotCaptureActive(false);
        }
    }

    public void SetScreenshotCaptureActive(bool active)
    {
        if (isCapturingScreenshot == active) return;

        isCapturingScreenshot = active;
        if (active)
        {
            screenshotDelayFrame = -1;
            UpdateScreenshotWatcher();
        }
        else
        {
            StopScreenshotWatcher();
        }
    }

    public void StopScreenshotWatcher()
    {
        try
        {
            if (screenshotWatcher != null)
            {
                screenshotWatcher.EnableRaisingEvents = false;
                screenshotWatcher.Created -= OnScreenshotCreated;
                screenshotWatcher.Dispose();
                screenshotWatcher = null;
                Log.Information("GPM screenshot watcher stopped and disposed.");
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Error stopping screenshot watcher: {ex.Message}");
        }
    }

    private void DoCaptureScreenshot()
    {
        try
        {
            var viewport = ImGuiHelpers.MainViewport;
            var pos = viewport.Pos;
            var size = viewport.Size;
            var center = pos + size / 2f;

            float boxWidth = 500f;
            float boxHeight = 500f;

            if (Configuration.CropOption == CropAspect.Aspect16_9)
            {
                boxWidth = 640f;
                boxHeight = 360f;
            }
            else if (Configuration.CropOption == CropAspect.Aspect4_3)
            {
                boxWidth = 533f;
                boxHeight = 400f;
            }
            else if (Configuration.CropOption == CropAspect.Aspect9_16)
            {
                boxWidth = 360f;
                boxHeight = 640f;
            }
            else if (Configuration.CropOption == CropAspect.Aspect3_4)
            {
                boxWidth = 450f;
                boxHeight = 600f;
            }

            // Apply custom screenshot configurations
            boxWidth *= Configuration.ScreenshotScale;
            boxHeight *= Configuration.ScreenshotScale;
            center.X += Configuration.ScreenshotOffsetX;
            center.Y += Configuration.ScreenshotOffsetY;

            int startX = (int)(center.X - boxWidth / 2f);
            int startY = (int)(center.Y - boxHeight / 2f);
            int w = (int)boxWidth;
            int h = (int)boxHeight;

            using (var bmp = new System.Drawing.Bitmap(w, h))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(startX, startY, 0, 0, new System.Drawing.Size(w, h));
                }

                var targetId = LastScreenshotDesignId != Guid.Empty ? LastScreenshotDesignId : activeSelectedDesignId;
                if (targetId != Guid.Empty)
                {
                    DesignManager.SaveImageDirect(targetId, bmp);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to take screenshot: {ex}");
            ChatGui.PrintError("Failed to capture screenshot. Make sure the game is in Windowed or Borderless Windowed mode.");
        }
    }

    public void CropAndScaleImage(string sourcePath, string targetPath, CropAspect cropOption)
    {
        using (var originalImage = System.Drawing.Image.FromFile(sourcePath))
        {
            SaveImageFromBitmap(originalImage, targetPath, cropOption);
        }
    }

    public void SaveImageFromBitmap(System.Drawing.Image originalImage, string targetPath, CropAspect cropOption)
    {
        int targetWidth, targetHeight;
        bool shouldCrop = true;

        switch (cropOption)
        {
            case CropAspect.Aspect16_9:
                targetWidth = 800;
                targetHeight = 450;
                break;
            case CropAspect.Aspect1_1:
                targetWidth = 600;
                targetHeight = 600;
                break;
            case CropAspect.Aspect4_3:
                targetWidth = 800;
                targetHeight = 600;
                break;
            case CropAspect.Aspect9_16:
                targetWidth = 450;
                targetHeight = 800;
                break;
            case CropAspect.Aspect3_4:
                targetWidth = 600;
                targetHeight = 800;
                break;
            case CropAspect.NoCrop:
            default:
                shouldCrop = false;
                targetWidth = originalImage.Width;
                targetHeight = originalImage.Height;
                int maxSize = 1024;
                if (targetWidth > maxSize || targetHeight > maxSize)
                {
                    float aspect = (float)targetWidth / targetHeight;
                    if (aspect > 1f)
                    {
                        targetWidth = maxSize;
                        targetHeight = (int)(maxSize / aspect);
                    }
                    else
                    {
                        targetHeight = maxSize;
                        targetWidth = (int)(maxSize * aspect);
                    }
                }
                break;
        }

        int cropWidth = originalImage.Width;
        int cropHeight = originalImage.Height;
        int cropX = 0;
        int cropY = 0;

        if (shouldCrop)
        {
            float targetAspect = (float)targetWidth / targetHeight;
            float sourceAspect = (float)originalImage.Width / originalImage.Height;

            if (sourceAspect > targetAspect)
            {
                cropWidth = (int)(originalImage.Height * targetAspect);
                cropX = (originalImage.Width - cropWidth) / 2;
            }
            else
            {
                cropHeight = (int)(originalImage.Width / targetAspect);
                cropY = (originalImage.Height - cropHeight) / 2;
            }
        }

        using (var bitmap = new System.Drawing.Bitmap(targetWidth, targetHeight))
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

            g.DrawImage(originalImage,
                new System.Drawing.Rectangle(0, 0, targetWidth, targetHeight),
                new System.Drawing.Rectangle(cropX, cropY, cropWidth, cropHeight),
                System.Drawing.GraphicsUnit.Pixel);

            var dir = Path.GetDirectoryName(targetPath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            bitmap.Save(targetPath, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    private void CheckFirstStartup()
    {
        if (string.IsNullOrEmpty(Configuration.PreviewsFolderPath) || !Configuration.HasSeenGalleryNotification)
        {
            if (ClientState.IsLoggedIn)
            {
                TriggerStartupPopups();
            }
            else
            {
                ClientState.Login += OnLogin;
            }
        }
    }

    private void OnLogin()
    {
        ClientState.Login -= OnLogin;
        TriggerStartupPopups();
    }

    private void TriggerStartupPopups()
    {
        if (string.IsNullOrEmpty(Configuration.PreviewsFolderPath))
        {
            NotifyFirstStartup();
        }
        else if (!Configuration.HasSeenGalleryNotification)
        {
            GalleryPromoWindow.IsOpen = true;
        }
    }

    private void NotifyFirstStartup()
    {
        ChatGui.Print("[Glamourer Preview Manager] Welcome! Please configure your Previews Storage Directory in the settings window.");
        ConfigWindow.IsOpen = true;
    }

    public List<DesignInfo> GetActiveRoulettePool()
    {
        var allWithPreview = Configuration.RouletteIncludeWithoutPreviews
            ? DesignManager.Designs.ToList()
            : DesignManager.Designs.Where(d => d.HasPreview).ToList();
        
        // Filter out by excluded folders
        if (Configuration.RouletteExcludedFolders != null && Configuration.RouletteExcludedFolders.Count > 0)
        {
            allWithPreview = allWithPreview.Where(d => 
                !Configuration.RouletteExcludedFolders.Contains(d.FileSystemFolder ?? string.Empty)
            ).ToList();
        }
        
        // Filter out by excluded individual designs
        if (Configuration.RouletteExcludedPool != null && Configuration.RouletteExcludedPool.Count > 0)
        {
            allWithPreview = allWithPreview.Where(d => 
                !Configuration.RouletteExcludedPool.Contains(d.Identifier)
            ).ToList();
        }
        
        return allWithPreview;
    }

    /// <summary>
    /// Creates a cache-busted copy of the image file in the system temp directory if it has changed,
    /// resolving caching issues in Dalamud's TextureProvider while keeping directories clean.
    /// </summary>
    public string GetBustedImagePath(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return path;

        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(path).Ticks;

            // Return cached busted path immediately if it's already resolved and valid
            if (bustedPathCache.TryGetValue(path, out var cached) && cached.LastWriteTicks == lastWrite)
            {
                return cached.BustedPath;
            }

            var cacheDir = Path.Combine(Path.GetTempPath(), "GlamourerPreviewManagerCache");
            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
            }

            var fileDir = Path.GetDirectoryName(path) ?? string.Empty;
            uint pathHash = 2166136261;
            foreach (char c in fileDir)
            {
                pathHash = (pathHash ^ c) * 16777619;
            }
            var pathHashStr = (pathHash & 0xFFFF).ToString("x4");

            var nameWithoutExt = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var cachePath = Path.Combine(cacheDir, $"gpm_{pathHashStr}_{nameWithoutExt}_{lastWrite}{ext}");

            if (!File.Exists(cachePath))
            {
                // Clean up previous cache-busted copies for this specific file in the temp directory
                var searchPattern = $"gpm_{pathHashStr}_{nameWithoutExt}_*{ext}";
                foreach (var oldFile in Directory.GetFiles(cacheDir, searchPattern))
                {
                    try { File.Delete(oldFile); } catch { }
                }

                // Copy the updated file to the new cache-buster path
                File.Copy(path, cachePath, true);
            }

            bustedPathCache[path] = (cachePath, lastWrite);
            return cachePath;
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to create cache-busted image copy in temp: {ex.Message}");
            return path;
        }
    }

    /// <summary>
    /// Clears all files in the central temporary cache directory to free up disk space.
    /// </summary>
    public void ClearTempCache()
    {
        try
        {
            bustedPathCache.Clear();
            var cacheDir = Path.Combine(Path.GetTempPath(), "GlamourerPreviewManagerCache");
            if (Directory.Exists(cacheDir))
            {
                foreach (var file in Directory.GetFiles(cacheDir))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to clear temporary cache: {ex.Message}");
        }
    }

    public void UpdateScreenshotWatcher()
    {
        try
        {
            screenshotWatcher?.Dispose();
            screenshotWatcher = null;

            var path = Configuration.GameScreenshotFolderPath;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path) || !Configuration.AutoImportFromWatchedFolder)
            {
                return;
            }

            screenshotWatcher = new FileSystemWatcher
            {
                Path = path,
                Filter = "*.*",
                EnableRaisingEvents = true
            };

            screenshotWatcher.Created += OnScreenshotCreated;
            Log.Information($"GPM screenshot watcher initialized for directory: {path}");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to initialize screenshot watcher: {ex.Message}");
        }
    }

    private void OnScreenshotCreated(object sender, FileSystemEventArgs e)
    {
        if (!isCapturingScreenshot || activeSelectedDesignId == Guid.Empty) return;

        var ext = Path.GetExtension(e.FullPath).ToLower();
        if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".webp" && ext != ".bmp" && ext != ".gif")
        {
            return;
        }

        // Wait for game or ReShade to finish writing the file
        Task.Run(async () =>
        {
            string filePath = e.FullPath;
            bool accessible = false;
            int attempts = 30; // Wait up to 3 seconds
            while (attempts > 0)
            {
                try
                {
                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        accessible = true;
                    }
                    break;
                }
                catch (IOException)
                {
                    attempts--;
                    await Task.Delay(100);
                }
                catch (Exception)
                {
                    break;
                }
            }

            if (accessible)
            {
                pendingScreenshotPath = filePath;
            }
            else
            {
                Log.Warning($"Could not access newly created screenshot at {filePath} after multiple retries.");
            }
        });
    }

    private void ProcessAutoImportedScreenshot(string filePath)
    {
        try
        {
            if (activeSelectedDesignId == Guid.Empty) return;

            var viewport = ImGuiHelpers.MainViewport;
            var pos = viewport.Pos;
            var size = viewport.Size;
            var center = pos + size / 2f;

            float boxWidth = 500f;
            float boxHeight = 500f;

            if (Configuration.CropOption == CropAspect.Aspect16_9)
            {
                boxWidth = 640f;
                boxHeight = 360f;
            }
            else if (Configuration.CropOption == CropAspect.Aspect4_3)
            {
                boxWidth = 533f;
                boxHeight = 400f;
            }
            else if (Configuration.CropOption == CropAspect.Aspect9_16)
            {
                boxWidth = 360f;
                boxHeight = 640f;
            }
            else if (Configuration.CropOption == CropAspect.Aspect3_4)
            {
                boxWidth = 450f;
                boxHeight = 600f;
            }

            // Apply custom screenshot configurations
            boxWidth *= Configuration.ScreenshotScale;
            boxHeight *= Configuration.ScreenshotScale;
            center.X += Configuration.ScreenshotOffsetX;
            center.Y += Configuration.ScreenshotOffsetY;

            float relativeX = (center.X - boxWidth / 2f) - pos.X;
            float relativeY = (center.Y - boxHeight / 2f) - pos.Y;

            using (var originalImg = System.Drawing.Image.FromFile(filePath))
            {
                double scaleX = (double)originalImg.Width / size.X;
                double scaleY = (double)originalImg.Height / size.Y;

                int cropX = (int)Math.Round(relativeX * scaleX);
                int cropY = (int)Math.Round(relativeY * scaleY);
                int cropW = (int)Math.Round(boxWidth * scaleX);
                int cropH = (int)Math.Round(boxHeight * scaleY);

                cropX = Math.Max(0, Math.Min(cropX, originalImg.Width - 1));
                cropY = Math.Max(0, Math.Min(cropY, originalImg.Height - 1));
                cropW = Math.Max(1, Math.Min(cropW, originalImg.Width - cropX));
                cropH = Math.Max(1, Math.Min(cropH, originalImg.Height - cropY));

                using (var croppedBmp = new System.Drawing.Bitmap(cropW, cropH))
                {
                    using (var g = System.Drawing.Graphics.FromImage(croppedBmp))
                    {
                        g.DrawImage(originalImg, 
                            new System.Drawing.Rectangle(0, 0, cropW, cropH), 
                            new System.Drawing.Rectangle(cropX, cropY, cropW, cropH), 
                            System.Drawing.GraphicsUnit.Pixel);
                    }

                    var targetId = LastScreenshotDesignId != Guid.Empty ? LastScreenshotDesignId : activeSelectedDesignId;
                    if (targetId != Guid.Empty)
                    {
                        DesignManager.SaveImageDirect(targetId, croppedBmp);
                        ChatGui.Print($"[GPM] Cropped and imported screenshot from: {Path.GetFileName(filePath)}");
                    }
                }
            }

            if (Configuration.AutoDeleteWatchedScreenshot)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        Log.Information($"Deleted original watched screenshot: {filePath}");
                    }
                }
                catch (Exception deleteEx)
                {
                    Log.Warning($"Failed to delete original watched screenshot {filePath}: {deleteEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to process auto-imported screenshot: {ex}");
            ChatGui.PrintError("Failed to crop the imported screenshot. See logs for details.");
        }
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            var text = message.Message.TextValue;
            if (string.IsNullOrEmpty(text)) return;

            // Performance pre-filter: quickly discard non-roll messages before running regex
            if (!text.Contains("roll", StringComparison.OrdinalIgnoreCase) && 
                !text.Contains("würfel", StringComparison.OrdinalIgnoreCase) && 
                !text.Contains("obti", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("random", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var match = RollRegex.Match(text);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var roll))
            {
                LastSeenRoll = roll;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Failed to parse chat message for roll: {ex.Message}");
        }
    }
}
