using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace GlamourerPreviewManager.Windows;

public class GlamourerPreviewWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public GlamourerPreviewWindow(Plugin plugin) : base("Glamourer Preview###GPM_PreviewWindow")
    {
        Size = new Vector2(380, 480);
        SizeCondition = ImGuiCond.FirstUseEver;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300, 250),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        Flags = ImGuiWindowFlags.NoCollapse;
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var activeDesignId = plugin.GetActiveSelectedDesignIdReflection();

        if (activeDesignId == Guid.Empty)
        {
            // Fallback to last seen design id if active selection reflection returned empty
            activeDesignId = plugin.ActiveSelectedDesignId;
        }

        if (activeDesignId == Guid.Empty)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Glamourer Preview Manager");
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextWrapped("Select a design in Glamourer's Designs tab to view and manage its preview image.");
            ImGui.Spacing();

            if (ImGui.Button("Open Outfit Gallery##GPM_OpenGallery", new Vector2(-1, 30)))
            {
                plugin.ToggleGalleryUi();
            }
            if (ImGui.Button("Open Outfit Roulette##GPM_OpenRoulette", new Vector2(-1, 30)))
            {
                plugin.ToggleRouletteUi();
            }
            if (ImGui.Button("Open Settings##GPM_OpenSettings", new Vector2(-1, 30)))
            {
                plugin.ToggleConfigUi();
            }
            return;
        }

        var design = plugin.DesignManager.GetDesignById(activeDesignId);
        if (design != null)
        {
            ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), $"Design: {design.Name}");
            if (!string.IsNullOrEmpty(design.FileSystemFolder))
            {
                ImGui.TextDisabled($"Folder: {design.FileSystemFolder}");
            }
        }
        else
        {
            ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), $"Design ID: {activeDesignId}");
        }

        plugin.DrawInjectedUI(activeDesignId);
    }
}
