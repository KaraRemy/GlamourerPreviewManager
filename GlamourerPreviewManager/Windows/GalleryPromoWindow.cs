using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface;
using Dalamud.Interface.Utility;

namespace GlamourerPreviewManager.Windows;

public class GalleryPromoWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

    public GalleryPromoWindow(Plugin plugin) : base("New Feature: Glamourer Preview Gallery!###GPM_GalleryPromo")
    {
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.AlwaysAutoResize;
        
        Size = new Vector2(460, 260);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        this.configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        // Add a beautiful header section with an icon
        ImGui.Spacing();
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), FontAwesomeIcon.Images.ToIconString());
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Glamourer Design Preview Gallery");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(
            "We have added a brand new, visually rich Preview Gallery! " +
            "You can now browse all of your Glamourer designs in a responsive visual grid complete with customizable aspects, " +
            "collapsible category folders (mirroring Glamourer's filesystem structure), and instant search filtering."
        );

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1.0f), "Key Features:");
        ImGui.BulletText("Apply designs instantly with a quick double-click on any card.");
        ImGui.BulletText("Launch the Gallery anytime with the command: /gpmgallery or /gpm gallery");
        ImGui.BulletText("Crop & scale options directly configured in GPM settings.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Right-align buttons or display side-by-side
        var availWidth = ImGui.GetContentRegionAvail().X;
        var buttonWidth = 140f * ImGuiHelpers.GlobalScale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        
        // Push buttons to the right
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availWidth - (buttonWidth * 2 + spacing));

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.6f, 0.8f, 0.7f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.8f, 1f, 0.9f));
        if (ImGui.Button("Open Gallery Now", new Vector2(buttonWidth, 30f * ImGuiHelpers.GlobalScale)))
        {
            DismissPromo(openGallery: true);
        }
        ImGui.PopStyleColor(2);

        ImGui.SameLine();
        if (ImGui.Button("Got it!", new Vector2(buttonWidth, 30f * ImGuiHelpers.GlobalScale)))
        {
            DismissPromo(openGallery: false);
        }
    }

    private void DismissPromo(bool openGallery)
    {
        configuration.HasSeenGalleryNotification = true;
        configuration.Save();
        IsOpen = false;

        if (openGallery)
        {
            plugin.ToggleGalleryUi();
        }
    }
}
