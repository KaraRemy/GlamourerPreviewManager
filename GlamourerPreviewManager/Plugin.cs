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

namespace GlamourerPreviewManager;

public sealed class Plugin : IDalamudPlugin
{
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

    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("GlamourerPreviewManager");
    private ConfigWindow ConfigWindow { get; init; }
    public FileDialogManager FileDialogManager { get; } = new();
    internal ImGuiHookManager ImGuiHookManager { get; }
    public DesignManager DesignManager { get; }

    // ImGui hook states
    private Guid activeSelectedDesignId = Guid.Empty;
    private int lastSeenDesignFrame = -1;
    private string currentWindowName = string.Empty;
    private readonly HashSet<string> seenButtonLabels = new();
    private readonly HashSet<string> seenSelectableLabels = new();

    // Reflection fields for Glamourer Selection resolution
    private Assembly? glamourerAssembly;
    private object? serviceManagerInstance;
    private Type? designFileSystemType;
    private PropertyInfo? fileSystemSelectionProp;
    private PropertyInfo? selectorSelectionProp;
    private PropertyInfo? leafNodeValueProp;
    private PropertyInfo? designIdentifierProp;
    private bool reflectionInitialized = false;
    private bool reflectionFailed = false;

    // Screenshot states
    private bool isCapturingScreenshot = false;
    private int screenshotDelayFrame = -1;
    private bool lastSpaceDown = false;
    private bool lastEscapeDown = false;

    // Deferred UI rendering states
    private int lastDrawnGpmFrame = -1;
    private int lastGlamourerWindowFrame = -1;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        DesignManager = new DesignManager(this);
        DesignManager.Initialize();

        ConfigWindow = new ConfigWindow(this);
        ImGuiHookManager = new ImGuiHookManager(this);
        ImGuiHookManager.Initialize();

        WindowSystem.AddWindow(ConfigWindow);

        var commandInfo = new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Glamourer Preview Manager Config"
        };
        CommandManager.AddHandler(CommandName, commandInfo);
        CommandManager.AddHandler(AltCommandName, commandInfo);

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw += DrawFileDialog;
        PluginInterface.UiBuilder.Draw += DrawScreenshotOverlay;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUi;
        
        PluginInterface.UiBuilder.DisableGposeUiHide = true;

        CheckFirstStartup();
    }

    public void Dispose()
    {
        ClientState.Login -= OnLogin;

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw -= DrawFileDialog;
        PluginInterface.UiBuilder.Draw -= DrawScreenshotOverlay;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUi;

        WindowSystem.RemoveAllWindows();
        ImGuiHookManager.Dispose();
        DesignManager.Dispose();
        ConfigWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(AltCommandName);
    }

    private void OnCommand(string command, string args)
    {
        ToggleConfigUi();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    private void DrawFileDialog() => FileDialogManager.Draw();

    public void OnBeginWindow(string name)
    {
        if (name != null)
        {
            currentWindowName = name;
            if (name.Contains("GlamourerMainWindow"))
            {
                lastGlamourerWindowFrame = (int)ImGui.GetFrameCount();
            }
        }
    }

    public void OnEndWindow()
    {
        // No-op, we just track currentWindowName in OnBeginWindow
    }

    private bool IsInGlamourerWindow()
    {
        return lastGlamourerWindowFrame == (int)ImGui.GetFrameCount() || currentWindowName.Contains("GlamourerMainWindow");
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

    private void InitializeReflection()
    {
        if (reflectionInitialized || reflectionFailed) return;

        try
        {
            glamourerAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Glamourer");
            if (glamourerAssembly == null)
            {
                reflectionFailed = true;
                return;
            }

            var installedPluginsProp = PluginInterface.GetType().GetProperty("InstalledPlugins", BindingFlags.Public | BindingFlags.Instance);
            if (installedPluginsProp == null)
            {
                reflectionFailed = true;
                return;
            }

            var installedPlugins = installedPluginsProp.GetValue(PluginInterface) as System.Collections.IEnumerable;
            if (installedPlugins == null)
            {
                reflectionFailed = true;
                return;
            }

            object? glamourerInstance = null;
            foreach (var plugin in installedPlugins)
            {
                var nameProp = plugin.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)
                               ?? plugin.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var name = nameProp?.GetValue(plugin) as string;
                if (string.Equals(name, "Glamourer", StringComparison.OrdinalIgnoreCase))
                {
                    var instanceProp = plugin.GetType().GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                       ?? plugin.GetType().GetProperty("Plugin", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    glamourerInstance = instanceProp?.GetValue(plugin)
                                        ?? plugin.GetType().GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(plugin)
                                        ?? plugin.GetType().GetField("plugin", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(plugin);
                    break;
                }
            }

            if (glamourerInstance == null)
            {
                reflectionFailed = true;
                return;
            }

            var servicesField = glamourerInstance.GetType().GetField("_services", BindingFlags.NonPublic | BindingFlags.Instance);
            serviceManagerInstance = servicesField?.GetValue(glamourerInstance);
            if (serviceManagerInstance == null)
            {
                reflectionFailed = true;
                return;
            }

            designFileSystemType = glamourerAssembly.GetType("Glamourer.Designs.DesignFileSystem");
            if (designFileSystemType == null)
            {
                reflectionFailed = true;
                return;
            }

            fileSystemSelectionProp = designFileSystemType.GetProperty("Selection", BindingFlags.Public | BindingFlags.Instance);
            if (fileSystemSelectionProp == null)
            {
                reflectionFailed = true;
                return;
            }

            reflectionInitialized = true;
            Log.Information("[GPM] Glamourer selection reflection initialized successfully.");
        }
        catch (Exception ex)
        {
            Log.Error($"[GPM] Failed to initialize Glamourer selection reflection: {ex}");
            reflectionFailed = true;
        }
    }

    private Guid GetActiveSelectedDesignIdReflection()
    {
        InitializeReflection();
        if (!reflectionInitialized || serviceManagerInstance == null || designFileSystemType == null) return Guid.Empty;

        try
        {
            object? fileSystem = null;
            if (serviceManagerInstance is IServiceProvider provider)
            {
                fileSystem = provider.GetService(designFileSystemType);
            }
            else
            {
                var getServiceMethod = serviceManagerInstance.GetType().GetMethod("GetService", new Type[] { typeof(Type) });
                if (getServiceMethod != null)
                {
                    fileSystem = getServiceMethod.Invoke(serviceManagerInstance, new[] { designFileSystemType });
                }
            }

            if (fileSystem == null) return Guid.Empty;

            var selectionObj = fileSystemSelectionProp?.GetValue(fileSystem);
            if (selectionObj == null) return Guid.Empty;

            if (selectorSelectionProp == null)
            {
                selectorSelectionProp = selectionObj.GetType().GetProperty("Selection", BindingFlags.Public | BindingFlags.Instance);
            }
            var selectionInnerObj = selectorSelectionProp?.GetValue(selectionObj);
            if (selectionInnerObj == null) return Guid.Empty;

            if (leafNodeValueProp == null)
            {
                leafNodeValueProp = selectionInnerObj.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            }
            var designObj = leafNodeValueProp?.GetValue(selectionInnerObj);
            if (designObj == null) return Guid.Empty;

            if (designIdentifierProp == null)
            {
                designIdentifierProp = designObj.GetType().GetProperty("Identifier", BindingFlags.Public | BindingFlags.Instance)
                                       ?? designObj.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
            }

            if (designIdentifierProp != null)
            {
                var guidVal = designIdentifierProp.GetValue(designObj);
                if (guidVal is Guid guid)
                {
                    return guid;
                }
            }
        }
        catch (Exception ex)
        {
            if (ImGui.GetFrameCount() % 3600 == 0)
            {
                Log.Error($"[GPM] Reflection error in GetActiveSelectedDesignIdReflection: {ex}");
            }
        }
        return Guid.Empty;
    }

    public void OnButtonDraw(string label)
    {
        // Capture design UUID from any buttons containing a GUID, regardless of window filter for safety
        if (TryExtractGuid(label, out var id))
        {
            activeSelectedDesignId = id;
            lastSeenDesignFrame = (int)ImGui.GetFrameCount();
        }

        // Hook "Apply to Yourself" or "Apply to yourself" to update reflection active design
        bool isApplyButton = label.Contains("Apply to Yourself", StringComparison.OrdinalIgnoreCase) || 
                             label.Contains("Apply to yourself", StringComparison.OrdinalIgnoreCase) ||
                             label.Contains("Apply to Self", StringComparison.OrdinalIgnoreCase);

        if (isApplyButton)
        {
            var reflectedGuid = GetActiveSelectedDesignIdReflection();
            if (reflectedGuid != Guid.Empty)
            {
                activeSelectedDesignId = reflectedGuid;
                lastSeenDesignFrame = (int)ImGui.GetFrameCount();
            }
        }
    }

    public void OnButtonDrawAfter(string label)
    {
        if (!IsInGlamourerWindow()) return;

        bool isLastButton = label.Contains("Export to Dat", StringComparison.OrdinalIgnoreCase) || 
                            label.Contains("Export to Clipboard", StringComparison.OrdinalIgnoreCase);

        if (isLastButton)
        {
            int currentFrame = (int)ImGui.GetFrameCount();
            if (lastDrawnGpmFrame != currentFrame)
            {
                lastDrawnGpmFrame = currentFrame;
                var reflectedGuid = GetActiveSelectedDesignIdReflection();
                if (reflectedGuid != Guid.Empty)
                {
                    activeSelectedDesignId = reflectedGuid;
                    lastSeenDesignFrame = (int)ImGui.GetFrameCount();
                }

                if (activeSelectedDesignId != Guid.Empty)
                {
                    DrawInjectedUI(activeSelectedDesignId);
                }
            }
        }
    }

    public void OnSelectableDraw(string label, bool selected)
    {
        if (!IsInGlamourerWindow()) return;

        if (selected)
        {
            if (TryExtractGuid(label, out var id))
            {
                activeSelectedDesignId = id;
                lastSeenDesignFrame = (int)ImGui.GetFrameCount();
            }
            else
            {
                var cleanName = label;
                var hashIdx = label.IndexOf("##");
                if (hashIdx >= 0)
                {
                    cleanName = label.Substring(0, hashIdx);
                }

                var design = DesignManager.Designs.FirstOrDefault(d => string.Equals(d.Name, cleanName, StringComparison.OrdinalIgnoreCase));
                if (design != null)
                {
                    activeSelectedDesignId = design.Identifier;
                    lastSeenDesignFrame = (int)ImGui.GetFrameCount();
                }
            }
        }
    }

    public void OnTreeNodeDraw(string label, bool selected, bool isLeaf)
    {
        if (!IsInGlamourerWindow()) return;

        if (selected && isLeaf)
        {
            if (TryExtractGuid(label, out var id))
            {
                activeSelectedDesignId = id;
                lastSeenDesignFrame = (int)ImGui.GetFrameCount();
            }
            else
            {
                var cleanName = label;
                var hashIdx = label.IndexOf("##");
                if (hashIdx >= 0)
                {
                    cleanName = label.Substring(0, hashIdx);
                }

                var design = DesignManager.Designs.FirstOrDefault(d => string.Equals(d.Name, cleanName, StringComparison.OrdinalIgnoreCase));
                if (design != null)
                {
                    activeSelectedDesignId = design.Identifier;
                    lastSeenDesignFrame = (int)ImGui.GetFrameCount();
                }
            }
        }
    }

    private void DrawInjectedUI(Guid designId)
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

        var design = DesignManager.Designs.FirstOrDefault(d => d.Identifier == designId);
        if (design == null)
        {
            // If the design is not in our list yet, wait or trigger a quick re-scan
            ImGui.TextUnformatted("Loading design info...");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            return;
        }

        if (design.HasPreview)
        {
            // Load and display preview image
            var path = design.PreviewImagePath!;
            
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
                        design.PreviewImagePath = null;
                    }
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
                isCapturingScreenshot = true;
                screenshotDelayFrame = -1;
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Take a cropped screenshot from the center of the screen.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawScreenshotOverlay()
    {
        if (!isCapturingScreenshot) return;

        if (screenshotDelayFrame > 0)
        {
            screenshotDelayFrame--;
            if (screenshotDelayFrame == 0)
            {
                DoCaptureScreenshot();
                isCapturingScreenshot = false;
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
        var text = "Screenshot Mode - [Space] Capture | [Esc] Cancel";
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
            isCapturingScreenshot = false;
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

                if (activeSelectedDesignId != Guid.Empty)
                {
                    DesignManager.SaveImageDirect(activeSelectedDesignId, bmp);
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
        if (string.IsNullOrEmpty(Configuration.PreviewsFolderPath))
        {
            if (ClientState.IsLoggedIn)
            {
                NotifyFirstStartup();
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
        NotifyFirstStartup();
    }

    private void NotifyFirstStartup()
    {
        ChatGui.Print("[Glamourer Preview Manager] Welcome! Please configure your Previews Storage Directory in the settings window.");
        ConfigWindow.IsOpen = true;
    }
}
