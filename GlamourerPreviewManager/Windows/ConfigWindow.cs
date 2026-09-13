using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace GlamourerPreviewManager.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin) : base("Glamourer Preview Manager Settings###GPM_Config")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(450, 680);
        SizeCondition = ImGuiCond.FirstUseEver;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(450, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        this.configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw() { }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("GPM_ConfigTabBar"))
        {
            if (ImGui.BeginTabItem("Navigation##GPM_NavigationTab"))
            {
                DrawNavigationTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Previews & Storage##GPM_StorageTab"))
            {
                DrawStorageTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Display & UI##GPM_DisplayTab"))
            {
                DrawDisplayTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Screenshot Capture##GPM_ScreenshotTab"))
            {
                DrawScreenshotTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Debug & Logging##GPM_DebugTab"))
            {
                DrawDebugTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Information##GPM_InfoTab"))
            {
                DrawInfoTab();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawStorageTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Storage Folder Configuration");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Previews Storage Directory:");
        
        var folderPath = configuration.PreviewsFolderPath;
        ImGui.SetNextItemWidth(310f);
        if (ImGui.InputText("##FolderPath", ref folderPath, 500))
        {
            configuration.PreviewsFolderPath = folderPath;
            configuration.Save();
            plugin.DesignManager.OnPreviewsFolderChanged();
        }
        ImGui.SameLine();
        if (ImGui.Button("Browse##GPM_BrowseFolder"))
        {
            plugin.FileDialogManager.OpenFolderDialog("Select Previews Folder", (success, path) =>
            {
                if (success && Directory.Exists(path))
                {
                    configuration.PreviewsFolderPath = path;
                    configuration.Save();
                    plugin.DesignManager.OnPreviewsFolderChanged();
                }
            });
        }

        ImGui.Spacing();
        if (ImGui.Button("Rediscover Previews##GPM_Rediscover", new Vector2(200, 30)))
        {
            var result = plugin.DesignManager.RediscoverPreviews();
            if (result.HasPendingReviews)
            {
                Plugin.ChatGui.Print($"[Glamourer Preview Manager] Allocated {result.AllocatedCount} previews. {result.UnallocatedImages.Count} files require review.");
                plugin.OpenReviewUi(result);
            }
            else
            {
                Plugin.ChatGui.Print($"[Glamourer Preview Manager] All {result.AllocatedCount} previews were allocated successfully!");
            }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Attempt to map existing image files in the previews storage folder to designs by matching filenames.");

        ImGui.SameLine();
        if (ImGui.Button("Open Review & Unassigned Images##GPM_OpenReviewBtn", new Vector2(320, 30)))
        {
            var result = plugin.DesignManager.RediscoverPreviews();
            plugin.OpenReviewUi(result);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Open the interactive review window to inspect ambiguous or unassigned preview image files.");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Retention & Deletion Policy");
        ImGui.Separator();
        ImGui.Spacing();

        var autoDelPreviews = configuration.AutoDeletePreviewsOnDesignDeletion;
        if (ImGui.Checkbox("Auto-delete preview images on design deletion##AutoDelPreviews", ref autoDelPreviews))
        {
            configuration.AutoDeletePreviewsOnDesignDeletion = autoDelPreviews;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("When enabled, deleting a design in Glamourer will also permanently delete its preview image file from your previews folder.\nWhen disabled (default), image files remain safely in the folder as unassigned images for you to review or reassign.");
        }

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.7f, 0.2f, 1f));
        ImGui.TextWrapped("Important Notice:\n" +
                          "- Please select a dedicated, empty folder to store previews.\n" +
                          "- Do NOT choose a folder inside your Penumbra mod directory, FFXIV game directory, or the synchronizer/Mare sync-ram folders.\n" +
                          "- Preview images will be named according to design names, and GPM will safely tag duplicate names with design IDs.");
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    private void DrawDisplayTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Display & Image Viewer Options");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Preview Image Size (in Glamourer window):");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"{configuration.PreviewImageSizePercent}%");

        var sizePercent = configuration.PreviewImageSizePercent;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderInt("##ImageSizeSlider", ref sizePercent, 10, 100, "%d%%"))
        {
            configuration.PreviewImageSizePercent = sizePercent;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Middle-Click Zoom Scale:");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"{configuration.ZoomScale:F2}x");

        var zoomScale = configuration.ZoomScale;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat("##ZoomScaleSlider", ref zoomScale, 0.5f, 5.0f, "%.2fx"))
        {
            configuration.ZoomScale = zoomScale;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Gallery Card Aspect Ratio:");
        
        var cropNames = new[] { 
            "No Crop (Preserve Aspect)", 
            "16:9 Aspect Ratio", 
            "1:1 Aspect Ratio (Square)", 
            "4:3 Aspect Ratio",
            "9:16 Aspect Ratio (Vertical/Portrait)",
            "3:4 Aspect Ratio (Vertical)"
        };
        int cardAspectIndex = (int)configuration.GalleryCardAspect;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##GalleryAspectCombo", ref cardAspectIndex, cropNames, cropNames.Length))
        {
            configuration.GalleryCardAspect = (CropAspect)cardAspectIndex;
            configuration.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Controls the shape of the design cards drawn inside the gallery grid.");

        ImGui.Spacing();
        var containImage = configuration.GalleryCardContainImage;
        if (ImGui.Checkbox("Fit full image without cropping (Contain)", ref containImage))
        {
            configuration.GalleryCardContainImage = containImage;
            configuration.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("If checked, the entire preview image is scaled to fit inside the card without any cropping (adding margins on the sides or top/bottom where needed).");
        ImGui.Spacing();
    }

    private void DrawScreenshotTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "General Capture Settings");
        ImGui.Separator();
        ImGui.Spacing();

        var autoApply = configuration.AutoApplyOnScreenshot;
        if (ImGui.Checkbox("Automatically apply design to yourself when taking screenshot", ref autoApply))
        {
            configuration.AutoApplyOnScreenshot = autoApply;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Screenshot Capture Crop Ratio:");
        var cropNames = new[] { 
            "No Crop (Preserve Aspect)", 
            "16:9 Aspect Ratio", 
            "1:1 Aspect Ratio (Square)", 
            "4:3 Aspect Ratio",
            "9:16 Aspect Ratio (Vertical/Portrait)",
            "3:4 Aspect Ratio (Vertical)"
        };
        int cropIndex = (int)configuration.CropOption;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##CropCombo", ref cropIndex, cropNames, cropNames.Length))
        {
            configuration.CropOption = (CropAspect)cropIndex;
            configuration.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Controls the aspect ratio of the screenshot overlay crop box and the cropping applied to new imports.");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Screenshot Calibration (4k / DPI)");
        ImGui.Separator();
        ImGui.Spacing();

        var screenshotScale = configuration.ScreenshotScale;
        ImGui.SetNextItemWidth(310f);
        if (ImGui.SliderFloat("Box Scale##GPM_BoxScale", ref screenshotScale, 0.5f, 3.0f, "%.2fx"))
        {
            configuration.ScreenshotScale = screenshotScale;
            configuration.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Scale the size of the capture box (e.g. set to 2.0x for 4k / 200% scaling).");

        var offsetX = configuration.ScreenshotOffsetX;
        ImGui.SetNextItemWidth(310f);
        if (ImGui.SliderInt("Offset X##GPM_OffsetX", ref offsetX, -1000, 1000))
        {
            configuration.ScreenshotOffsetX = offsetX;
            configuration.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Horizontal screen center offset.");

        var offsetY = configuration.ScreenshotOffsetY;
        ImGui.SetNextItemWidth(310f);
        if (ImGui.SliderInt("Offset Y##GPM_OffsetY", ref offsetY, -1000, 1000))
        {
            configuration.ScreenshotOffsetY = offsetY;
            configuration.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Vertical screen center offset.");

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Use External Screenshots");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Watched Game/ReShade Screenshot Folder:");
        var screenshotFolder = configuration.GameScreenshotFolderPath;
        ImGui.SetNextItemWidth(310f);
        if (ImGui.InputText("##ScreenshotFolder", ref screenshotFolder, 500))
        {
            configuration.GameScreenshotFolderPath = screenshotFolder;
            configuration.Save();
            plugin.UpdateScreenshotWatcher();
        }
        ImGui.SameLine();
        if (ImGui.Button("Browse##GPM_BrowseScreenshotFolder"))
        {
            plugin.FileDialogManager.OpenFolderDialog("Select Screenshot Folder", (success, path) =>
            {
                if (success && Directory.Exists(path))
                {
                    configuration.GameScreenshotFolderPath = path;
                    configuration.Save();
                    plugin.UpdateScreenshotWatcher();
                }
            });
        }
        
        var autoImport = configuration.AutoImportFromWatchedFolder;
        if (ImGui.Checkbox("Auto-crop & import screenshots from watched folder", ref autoImport))
        {
            configuration.AutoImportFromWatchedFolder = autoImport;
            configuration.Save();
            plugin.UpdateScreenshotWatcher();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("When in GPM screenshot capture mode, taking a native FFXIV or ReShade screenshot will automatically crop and import it into GPM. This bypasses GDI capture to support perfect HDR tone-mapping and ReShade shaders.");

        var autoDelete = configuration.AutoDeleteWatchedScreenshot;
        if (ImGui.Checkbox("Auto-delete original screenshots after import", ref autoDelete))
        {
            configuration.AutoDeleteWatchedScreenshot = autoDelete;
            configuration.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("If checked, the original uncropped screenshot taken by the game or ReShade will be deleted from the watched folder automatically after GPM crops and imports it.");
        ImGui.Spacing();
    }

    private void DrawInfoTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Glamourer Preview Manager Information");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(
            "Glamourer Preview Manager (GPM) is a Dalamud plugin designed to bring " +
            "customizable preview images to FFXIV's Glamourer plugin. It links " +
            "custom screenshots and external images directly to your designs."
        );

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "Tips:");
        
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextWrapped("Open the gallery grid by typing /gpmgallery in chat.");

        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextWrapped("Middle-click images in the editor or cards in the gallery to view full-sized previews.");

        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextWrapped("Use the External Screenshots feature to use ReShade or other screen capture tools - or to mitigate HDR tone mapping issues.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Support & Community");
        ImGui.Spacing();
        if (ImGui.Button("Join Support Discord"))
        {
            Dalamud.Utility.Util.OpenLink("https://discord.gg/PvxW4mXaWp");
        }
        ImGui.SameLine();
        if (ImGui.Button("Support on Ko-fi"))
        {
            Dalamud.Utility.Util.OpenLink("https://ko-fi.com/kararemy");
        }
        ImGui.Spacing();
    }

    private void DrawNavigationTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "GPM Navigation Hub");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(
            "Welcome to Glamourer Preview Manager! Use this navigation hub to quickly open the primary user interface windows."
        );

        ImGui.Spacing();
        ImGui.Spacing();

        if (ImGui.Button("Open Preview Gallery##GPM_NavGallery", new Vector2(-1, 40f)))
        {
            plugin.ToggleGalleryUi();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Browse all Glamourer designs in a collapsible category grid and double-click cards to apply them.");

        ImGui.Spacing();

        if (ImGui.Button("Open Outfit Roulette##GPM_NavRoulette", new Vector2(-1, 40f)))
        {
            plugin.ToggleRouletteUi();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Spin the roulette to pick a random design, or enter a number rolled by a friend.");
        
        ImGui.Spacing();
    }

    private void DrawDebugTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Debugging, Reflection & Diagnostics");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(
            "Configure logging verbosity, inspect active selection state, and troubleshoot connection to Glamourer."
        );
        ImGui.Spacing();

        // 1. Log Level selector
        ImGui.TextUnformatted("Plugin Log Level (/xllog):");
        var currentLogLevel = configuration.LogLevel;
        var logLevelNames = Enum.GetNames<GpmLogLevel>();
        int selectedIndex = (int)currentLogLevel;

        ImGui.SetNextItemWidth(200f);
        if (ImGui.Combo("##GPM_LogLevelCombo", ref selectedIndex, logLevelNames, logLevelNames.Length))
        {
            configuration.LogLevel = (GpmLogLevel)selectedIndex;
            configuration.Save();
            Plugin.LogInfo($"[GPM] Log level set to {configuration.LogLevel}");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Controls the minimum severity of messages sent to Dalamud's /xllog window.");

        ImGui.Spacing();

        // Promote Debug to Info toggle
        var promoteInfo = configuration.PromoteDebugLogsToInformation;
        if (ImGui.Checkbox("Promote Debug logs to Information level##GPM_PromoteLogs", ref promoteInfo))
        {
            configuration.PromoteDebugLogsToInformation = promoteInfo;
            configuration.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Dalamud's /xllog filters out Debug logs by default unless global Debug mode is on in Dalamud settings. Checking this forwards GPM Debug messages as Information so you can easily see them in /xllog without changing global Dalamud settings.");

        // Show Debug Overlay toggle
        var showOverlay = configuration.ShowDebugOverlayBelowPreview;
        if (ImGui.Checkbox("Show live diagnostic overlay below preview in Glamourer##GPM_ShowOverlay", ref showOverlay))
        {
            configuration.ShowDebugOverlayBelowPreview = showOverlay;
            configuration.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Displays a diagnostic bar directly beneath preview images in Glamourer showing the currently resolved design, resolution stage, and reflection health.");

        // Log selection changes toggle
        var logSelection = configuration.LogSelectionChanges;
        if (ImGui.Checkbox("Log active design selection changes to /xllog##GPM_LogSelection", ref logSelection))
        {
            configuration.LogSelectionChanges = logSelection;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 2. Live Diagnostics readout
        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "Current Reflection & Selection Status:");
        ImGui.Spacing();

        ImGui.BulletText($"Reflection Initialized: {(plugin.IsReflectionInitialized ? "Connected" : "Disconnected / Retrying")}");
        ImGui.BulletText($"Current Resolution Stage: {plugin.CurrentResolutionStage}");
        ImGui.BulletText($"Last Resolution Source: {plugin.CurrentResolutionSource}");
        ImGui.BulletText($"Active Selected Design ID: {(plugin.ActiveSelectedDesignId == Guid.Empty ? "None" : plugin.ActiveSelectedDesignId.ToString())}");
        var activeDesign = plugin.DesignManager.GetDesignById(plugin.ActiveSelectedDesignId);
        ImGui.BulletText($"Active Design Name: {(activeDesign != null ? $"\"{activeDesign.Name}\"" : "(Not found)")}");
        ImGui.BulletText($"Total Indexed Designs: {plugin.DesignManager.Designs.Count}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 3. Actions / Troubleshooting Buttons
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Troubleshooting Actions");
        ImGui.Spacing();

        if (ImGui.Button("Open /xllog Log Window##GPM_OpenXllog", new Vector2(-1, 30f)))
        {
            Plugin.CommandManager.ProcessCommand("/xllog");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Executes /xllog to open the Dalamud log viewer window.");

        ImGui.Spacing();

        if (ImGui.Button("Dump Full State to /xllog##GPM_DumpState", new Vector2(-1, 30f)))
        {
            plugin.DumpStateToLog();
            Plugin.ChatGui.Print("[GPM] Diagnostic state dumped to /xllog.");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Writes a complete diagnostic dump of GPM and Glamourer reflection state into /xllog.");

        ImGui.Spacing();

        if (ImGui.Button("Force Re-bind Glamourer Reflection##GPM_ForceRebind", new Vector2(-1, 30f)))
        {
            plugin.ForceReinitializeReflection();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clears the reflection cache and immediately searches for Glamourer's ServiceManager and DesignFileSystem. Use this if Glamourer was reloaded, updated, or toggled in Dalamud.");

        ImGui.Spacing();

        if (ImGui.Button("Force Rescan Designs Directory##GPM_ForceRescan", new Vector2(-1, 30f)))
        {
            plugin.DesignManager.ScanDesigns();
            Plugin.ChatGui.Print($"[GPM] Rescanned designs directory. Total designs found: {plugin.DesignManager.Designs.Count}");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Forces an immediate disk rescan of all Glamourer designs.");

        ImGui.Spacing();
    }
}
