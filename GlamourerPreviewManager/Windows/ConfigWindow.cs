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
            MinimumSize = new Vector2(450, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        this.configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw() { }

    public override void Draw()
    {
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
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Display & Crop Options");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Preview Image Size:");
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
        ImGui.TextUnformatted("Screenshot/Import Crop Method:");
        
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

        ImGui.Spacing();
        var autoSync = configuration.AutoSyncSelection;
        if (ImGui.Checkbox("Automatically sync selected design in main window", ref autoSync))
        {
            configuration.AutoSyncSelection = autoSync;
            configuration.Save();
        }

        ImGui.Spacing();
        var autoApply = configuration.AutoApplyOnScreenshot;
        if (ImGui.Checkbox("Automatically apply design to yourself when taking screenshot", ref autoApply))
        {
            configuration.AutoApplyOnScreenshot = autoApply;
            configuration.Save();
        }

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
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Support & Community");
        if (ImGui.Button("Join Support Discord"))
        {
            Dalamud.Utility.Util.OpenLink("https://discord.gg/PvxW4mXaWp");
        }
    }
}
