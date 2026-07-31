using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;

namespace GlamourerPreviewManager.Windows;

public class RouletteWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private Guid selectedDesignId = Guid.Empty;
    private string friendRollInput = string.Empty;
    private readonly Random random = new();
    private string selectionMessage = string.Empty;

    public RouletteWindow(Plugin plugin) : base("Glamourer Outfit Roulette###GPM_Roulette")
    {
        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(400, 580);
        SizeCondition = ImGuiCond.FirstUseEver;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(350, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        this.configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var pool = plugin.GetActiveRoulettePool();

        ImGui.Spacing();
        
        // Center-align Title
        var title = "Spin the Roulette";
        var titleWidth = ImGui.CalcTextSize(title).X;
        ImGui.SetCursorPosX((ImGui.GetWindowSize().X - titleWidth) / 2f);
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), title);
        
        ImGui.Separator();
        ImGui.Spacing();

        // Center-align Active Pool Count
        var poolText = $"Outfits in Active Pool: {pool.Count}";
        var poolTextWidth = ImGui.CalcTextSize(poolText).X;
        ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - poolTextWidth) / 2f + ImGui.GetCursorPosX());
        ImGui.TextUnformatted(poolText);
        
        ImGui.Spacing();

        // 1. Roll / Spin Button (Centered with fixed size)
        var spinBtnWidth = 250f * ImGuiHelpers.GlobalScale;
        var spinBtnHeight = 35f * ImGuiHelpers.GlobalScale;
        ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - spinBtnWidth) / 2f + ImGui.GetCursorPosX());
        if (ImGui.Button("Spin Roulette!##GPM_RouletteSpin", new Vector2(spinBtnWidth, spinBtnHeight)))
        {
            if (pool.Count > 0)
            {
                var index = random.Next(pool.Count);
                var chosen = pool[index];
                selectedDesignId = chosen.Identifier;
                selectionMessage = $"Rolled random outfit: '{chosen.Name}'";

                if (!configuration.RouletteConfirmBeforeApply)
                {
                    Plugin.CommandManager.ProcessCommand($"/glamour apply {chosen.Identifier} | <me>");
                    Plugin.ChatGui.Print($"[GPM] Roulette applied design '{chosen.Name}' to yourself.");
                }
            }
            else
            {
                selectionMessage = "Error: Roulette pool is empty! Check your folder filters below.";
            }
        }

        ImGui.Spacing();

        // 2. Friends Roll Field (Centered Row)
        var labelWidth = ImGui.CalcTextSize("Friend's Roll:").X;
        var inputWidth = 120f * ImGuiHelpers.GlobalScale;
        var iconBtnWidth = ImGui.GetFrameHeight();
        var applyBtnWidth = ImGui.CalcTextSize("Apply Roll").X + ImGui.GetStyle().FramePadding.X * 2f;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var totalRowWidth = labelWidth + spacing + inputWidth + spacing + iconBtnWidth + spacing + applyBtnWidth;
        var rowOffset = (ImGui.GetContentRegionAvail().X - totalRowWidth) / 2f;
        
        if (rowOffset > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + rowOffset);
        
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Friend's Roll:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(inputWidth);
        ImGui.InputText("##GPM_FriendRoll", ref friendRollInput, 10);
        ImGui.SameLine();

        ImGui.PushFont(UiBuilder.IconFont);
        var getRollClicked = ImGui.Button($"{FontAwesomeIcon.History.ToIconString()}##GPM_GetLastRoll");
        ImGui.PopFont();
        if (getRollClicked)
        {
            if (plugin.LastSeenRoll.HasValue)
            {
                friendRollInput = plugin.LastSeenRoll.Value.ToString();
            }
            else
            {
                Plugin.ChatGui.Print("[GPM] No recent /random or /dice roll detected in chat yet.");
            }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(plugin.LastSeenRoll.HasValue 
            ? $"Insert last seen roll from chat: {plugin.LastSeenRoll.Value}" 
            : "No /random or /dice roll seen in chat yet.");

        ImGui.SameLine();
        if (ImGui.Button("Apply Roll##GPM_ApplyRoll"))
        {
            if (int.TryParse(friendRollInput.Trim(), out var roll))
            {
                if (pool.Count > 0)
                {
                    var index = Math.Abs(roll) % pool.Count;
                    var chosen = pool[index];
                    selectedDesignId = chosen.Identifier;
                    selectionMessage = $"Applied Roll {roll} % {pool.Count} = Index {index}. Chosen: '{chosen.Name}'";

                    if (!configuration.RouletteConfirmBeforeApply)
                    {
                        Plugin.CommandManager.ProcessCommand($"/glamour apply {chosen.Identifier} | <me>");
                        Plugin.ChatGui.Print($"[GPM] Roulette applied design '{chosen.Name}' to yourself.");
                    }
                }
                else
                {
                    selectionMessage = "Error: Roulette pool is empty!";
                }
            }
            else
            {
                selectionMessage = "Error: Enter a valid number.";
            }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Enter a number rolled by a friend (e.g. from /random) to determine the outfit. Uses modulo mapping.");

        ImGui.Spacing();
        
        // Center-align Checkbox
        var checkboxText = "Confirm before applying outfit";
        var checkboxWidth = ImGui.CalcTextSize(checkboxText).X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
        var cbOffset = (ImGui.GetContentRegionAvail().X - checkboxWidth) / 2f;
        if (cbOffset > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + cbOffset);
        
        var confirm = configuration.RouletteConfirmBeforeApply;
        if (ImGui.Checkbox($"{checkboxText}##RouletteConfirmInWindow", ref confirm))
        {
            configuration.RouletteConfirmBeforeApply = confirm;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 3. Selection details display
        if (selectedDesignId != Guid.Empty && plugin.DesignManager.DesignsById.TryGetValue(selectedDesignId, out var design))
        {
            // Center Selected Outfit text
            var selPrefix = "Selected Outfit: ";
            var combinedWidth = ImGui.CalcTextSize(selPrefix).X + ImGui.CalcTextSize(design.Name).X;
            ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - combinedWidth) / 2f + ImGui.GetCursorPosX());
            
            ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), selPrefix);
            ImGui.SameLine();
            ImGui.TextUnformatted(design.Name);

            if (!string.IsNullOrEmpty(design.FileSystemFolder))
            {
                var folderText = $"Folder: {design.FileSystemFolder}";
                var folderTextWidth = ImGui.CalcTextSize(folderText).X;
                ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - folderTextWidth) / 2f + ImGui.GetCursorPosX());
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), folderText);
            }

            if (!string.IsNullOrEmpty(design.Description))
            {
                var descWidth = Math.Min(ImGui.CalcTextSize(design.Description).X, ImGui.GetContentRegionAvail().X - 20f);
                ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - descWidth) / 2f + ImGui.GetCursorPosX());
                ImGui.TextWrapped(design.Description);
            }

            ImGui.Spacing();

            // Center Selection Message
            if (!string.IsNullOrEmpty(selectionMessage))
            {
                var msgWidth = ImGui.CalcTextSize(selectionMessage).X;
                ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - msgWidth) / 2f + ImGui.GetCursorPosX());
                ImGui.TextColored(new Vector4(0.2f, 0.8f, 0.2f, 1f), selectionMessage);
                ImGui.Spacing();
            }

            // Center Buttons
            if (configuration.RouletteConfirmBeforeApply)
            {
                var applyWidth = 150f * ImGuiHelpers.GlobalScale;
                var clearWidth = 100f * ImGuiHelpers.GlobalScale;
                var btnSpacing = ImGui.GetStyle().ItemSpacing.X;
                var btnsTotalWidth = applyWidth + btnSpacing + clearWidth;
                var btnsOffset = (ImGui.GetContentRegionAvail().X - btnsTotalWidth) / 2f;
                
                if (btnsOffset > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + btnsOffset);
                
                if (ImGui.Button("Apply Outfit##GPM_RouletteApply", new Vector2(applyWidth, 30f)))
                {
                    Plugin.CommandManager.ProcessCommand($"/glamour apply {design.Identifier} | <me>");
                    Plugin.ChatGui.Print($"[GPM] Applied design '{design.Name}' to yourself.");
                }
                ImGui.SameLine();
                if (ImGui.Button("Clear##GPM_RouletteClear", new Vector2(clearWidth, 30f)))
                {
                    selectedDesignId = Guid.Empty;
                    selectionMessage = string.Empty;
                }
            }
            else
            {
                var reapplyText = "Re-apply Outfit";
                var reapplyWidth = ImGui.CalcTextSize(reapplyText).X + ImGui.GetStyle().FramePadding.X * 2f;
                var reapplyOffset = (ImGui.GetContentRegionAvail().X - reapplyWidth) / 2f;
                
                if (reapplyOffset > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + reapplyOffset);
                if (ImGui.Button($"{reapplyText}##GPM_RouletteReapply"))
                {
                    Plugin.CommandManager.ProcessCommand($"/glamour apply {design.Identifier} | <me>");
                }
            }

            ImGui.Spacing();

            // Center Image
            if (design.HasPreview)
            {
                var path = plugin.GetBustedImagePath(design.PreviewImagePath!);
                var texture = Plugin.TextureProvider.GetFromFile(path).GetWrapOrDefault();
                if (texture != null)
                {
                    var width = ImGui.GetContentRegionAvail().X;
                    var ratio = (float)texture.Height / texture.Width;
                    var height = width * ratio;

                    if (height > 250f * ImGuiHelpers.GlobalScale)
                    {
                        height = 250f * ImGuiHelpers.GlobalScale;
                        width = height / ratio;
                    }

                    var imgOffset = (ImGui.GetContentRegionAvail().X - width) / 2f;
                    if (imgOffset > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + imgOffset);
                    ImGui.Image(texture.Handle, new Vector2(width, height));
                }
            }
        }
        else if (!string.IsNullOrEmpty(selectionMessage))
        {
            var errWidth = ImGui.CalcTextSize(selectionMessage).X;
            ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - errWidth) / 2f + ImGui.GetCursorPosX());
            ImGui.TextColored(new Vector4(0.8f, 0.3f, 0.3f, 1f), selectionMessage);
        }
        else
        {
            var defText = "Spin the roulette or apply a roll to select an outfit!";
            var defTextWidth = ImGui.CalcTextSize(defText).X;
            ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - defTextWidth) / 2f + ImGui.GetCursorPosX());
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), defText);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // 4. Expandable Pool Configuration
        if (ImGui.CollapsingHeader("Pool Configuration (Folder Filters)##GPM_RouletteFolders"))
        {
            var folders = plugin.DesignManager.Designs
                .Where(d => d.HasPreview)
                .Select(d => d.FileSystemFolder ?? string.Empty)
                .Distinct()
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (folders.Count > 0)
            {
                ImGui.TextUnformatted("Choose folders to include in the roulette pool:");
                ImGui.Spacing();

                if (ImGui.Button("Include All##GPM_IncAllFolders"))
                {
                    configuration.RouletteExcludedFolders.Clear();
                    configuration.Save();
                }
                ImGui.SameLine();
                if (ImGui.Button("Exclude All##GPM_ExcAllFolders"))
                {
                    configuration.RouletteExcludedFolders.Clear();
                    configuration.RouletteExcludedFolders.AddRange(folders);
                    configuration.Save();
                }

                ImGui.Spacing();

                using (var child = Dalamud.Interface.Utility.Raii.ImRaii.Child("RouletteFolderList", new Vector2(-1, 140f * ImGuiHelpers.GlobalScale), true))
                {
                    if (child.Success)
                    {
                        foreach (var folder in folders)
                        {
                            var displayName = string.IsNullOrEmpty(folder) ? "[Root / Uncategorized]" : folder;
                            var isExcluded = configuration.RouletteExcludedFolders.Contains(folder);
                            var isIncluded = !isExcluded;

                            if (ImGui.Checkbox($"{displayName}##RouletteFolder_{folder}", ref isIncluded))
                            {
                                if (isIncluded)
                                {
                                    configuration.RouletteExcludedFolders.Remove(folder);
                                }
                                else
                                {
                                    if (!configuration.RouletteExcludedFolders.Contains(folder))
                                        configuration.RouletteExcludedFolders.Add(folder);
                                }
                                configuration.Save();
                            }
                        }
                    }
                }
            }
            else
            {
                ImGui.TextUnformatted("No designs with previews found in your library.");
            }
        }
        ImGui.Spacing();
    }
}
