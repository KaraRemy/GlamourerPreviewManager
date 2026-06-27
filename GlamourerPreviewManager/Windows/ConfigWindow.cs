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
        if (ImGui.Button("Rediscover Previews##GPM_Rediscover"))
        {
            var (allocated, total) = plugin.DesignManager.RediscoverPreviews();
            Plugin.ChatGui.Print($"[Glamourer Preview Manager] {allocated} out of {total} previews were allocated successfully.");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Attempt to map existing image files in the previews storage folder to designs by matching filenames.");

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.7f, 0.2f, 1f));
        ImGui.TextWrapped("Important Notice:\n" +
                          "- Please select a dedicated, empty folder to store previews.\n" +
                          "- Do NOT choose a folder inside your Penumbra mod directory, FFXIV game directory, or the synchronizer/Mare sync-ram folders.\n" +
                          "- Preview images will be named according to design names, and GPM will automatically rename or delete them when designs are updated or removed.");
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
}
