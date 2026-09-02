using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace GlamourerPreviewManager.Windows;

public class RediscoveryReviewWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    public RediscoveryResult? CurrentResult { get; set; }

    private string searchFilter = string.Empty;
    private readonly Dictionary<string, Guid> selectedDesignForFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> dismissedFiles = new(StringComparer.OrdinalIgnoreCase);
    private string? fileToDeleteConfirm = null;

    public RediscoveryReviewWindow(Plugin plugin) : base("Preview Rediscovery & Manual Review###GPM_RediscoveryReviewWindow")
    {
        Size = new Vector2(760, 600);
        SizeCondition = ImGuiCond.FirstUseEver;

        Position = new Vector2(200, 100);
        PositionCondition = ImGuiCond.FirstUseEver;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        Flags = ImGuiWindowFlags.NoCollapse;
        this.plugin = plugin;
    }

    public void Dispose() { }

    public void SetResult(RediscoveryResult result)
    {
        CurrentResult = result;
        dismissedFiles.Clear();
        selectedDesignForFile.Clear();
        fileToDeleteConfirm = null;
        IsOpen = true;
    }

    public override void Draw()
    {
        if (CurrentResult == null)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted("No rediscovery scan has been performed yet.");
            ImGui.Spacing();
            if (ImGui.Button("Run Rediscover Previews Now##GPM_RunRediscoverFromReview", new Vector2(250, 32)))
            {
                var result = plugin.DesignManager.RediscoverPreviews();
                SetResult(result);
            }
            return;
        }

        // Header Summary Cards
        DrawSummaryHeader();

        ImGui.Separator();
        ImGui.Spacing();

        var activeUnallocated = CurrentResult.UnallocatedImages
            .Where(e => !dismissedFiles.Contains(e.FilePath))
            .ToList();

        if (activeUnallocated.Count == 0)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 0.9f, 0.4f, 1f));
            ImGui.TextUnformatted("✔ All preview images are currently mapped and allocated to designs!");
            ImGui.PopStyleColor();
            ImGui.Spacing();
            ImGui.TextUnformatted("There are no unassigned or ambiguous preview images requiring review.");
            ImGui.Spacing();
            if (ImGui.Button("Scan Again##GPM_RescanReview", new Vector2(150, 30)))
            {
                var result = plugin.DesignManager.RediscoverPreviews();
                SetResult(result);
            }
            return;
        }

        // Controls bar
        ImGui.SetNextItemWidth(300 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##ReviewSearchFilter", "Filter by filename or design...", ref searchFilter, 100);

        ImGui.SameLine();
        if (ImGui.Button("Auto-Assign Best Guesses##GPM_AutoAssignGuesses"))
        {
            AutoAssignBestGuesses(activeUnallocated);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Automatically assign images that have candidate designs with non-conflicting names.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Rescan Folder##GPM_RescanBtn"))
        {
            var result = plugin.DesignManager.RediscoverPreviews();
            SetResult(result);
        }

        ImGui.Spacing();

        // Scrollable list of review cards
        var filteredList = activeUnallocated;
        if (!string.IsNullOrWhiteSpace(searchFilter))
        {
            filteredList = activeUnallocated.Where(e => 
                e.FileName.Contains(searchFilter, StringComparison.OrdinalIgnoreCase) ||
                e.CandidateDesigns.Any(d => d.Name.Contains(searchFilter, StringComparison.OrdinalIgnoreCase))
            ).ToList();
        }

        using var child = ImRaii.Child("##ReviewCardList", new Vector2(0, -40), true);
        if (child)
        {
            for (int i = 0; i < filteredList.Count; i++)
            {
                DrawReviewCard(filteredList[i], i);
            }
        }

        // Bottom Action Bar
        ImGui.Spacing();
        if (ImGui.Button("Close Review Window##GPM_CloseReview", new Vector2(200, 30)))
        {
            IsOpen = false;
        }
    }

    private void DrawSummaryHeader()
    {
        if (CurrentResult == null) return;

        var activeCount = CurrentResult.UnallocatedImages.Count(e => !dismissedFiles.Contains(e.FilePath));
        var ambiguousCount = CurrentResult.UnallocatedImages.Count(e => !dismissedFiles.Contains(e.FilePath) && e.IsAmbiguous);
        var orphanCount = activeCount - ambiguousCount;

        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), "Preview Discovery Summary");
        ImGui.Spacing();

        ImGui.TextUnformatted($"Total Disk Files: {CurrentResult.TotalFilesCount}  |");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.4f, 1f), $"Auto-Allocated: {CurrentResult.AllocatedCount}  |");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), $"Pending Review: {activeCount} ({ambiguousCount} Ambiguous, {orphanCount} Orphan)  |");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), $"Designs Missing Previews: {CurrentResult.DesignsWithoutPreview.Count}");
    }

    private void DrawReviewCard(UnallocatedImageEntry entry, int index)
    {
        ImGui.PushID($"Card_{index}_{entry.FileName}");

        var cardWidth = ImGui.GetContentRegionAvail().X;
        using (var cardChild = ImRaii.Child($"##CardFrame_{index}", new Vector2(cardWidth, 140 * ImGuiHelpers.GlobalScale), true))
        {
            if (cardChild)
            {
                // Left column: Image Thumbnail
                float thumbSize = 120 * ImGuiHelpers.GlobalScale;
                var texture = Plugin.TextureProvider.GetFromFile(plugin.GetBustedImagePath(entry.FilePath)).GetWrapOrDefault();

                if (texture != null)
                {
                    float aspect = 1f;
                    if (texture.Width > 0 && texture.Height > 0)
                    {
                        aspect = (float)texture.Width / texture.Height;
                    }
                    float drawWidth = thumbSize;
                    float drawHeight = thumbSize / aspect;
                    if (drawHeight > thumbSize)
                    {
                        drawHeight = thumbSize;
                        drawWidth = drawHeight * aspect;
                    }

                    ImGui.Image(texture.Handle, new Vector2(drawWidth, drawHeight));

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.Image(texture.Handle, new Vector2(drawWidth * 2.5f, drawHeight * 2.5f));
                        ImGui.EndTooltip();
                    }
                }
                else
                {
                    ImGui.Dummy(new Vector2(thumbSize, thumbSize));
                }

                ImGui.SameLine();

                // Right column: Details & Selection UI
                ImGui.BeginGroup();

                // Filename & Status Badge
                ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), entry.FileName);
                ImGui.SameLine();
                ImGui.TextDisabled($"({entry.FileSizeBytes / 1024} KB)");

                if (entry.IsAmbiguous)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.7f, 0.2f, 1f));
                    ImGui.TextUnformatted($"⚠️ Ambiguous Name: {entry.CandidateDesigns.Count} designs share the name '{entry.CleanedName}'");
                    ImGui.PopStyleColor();
                }
                else if (entry.CandidateDesigns.Count == 0)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.4f, 0.4f, 1f));
                    ImGui.TextUnformatted("📁 Orphan File: No active Glamourer design found with this name");
                    ImGui.PopStyleColor();
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.3f, 0.8f, 1f, 1f), $"Potential Match: '{entry.CandidateDesigns[0].Name}'");
                }

                ImGui.Spacing();

                // Target Design Dropdown Selector
                ImGui.TextUnformatted("Assign to:");
                ImGui.SameLine();

                // Build candidate and unassigned designs list
                var unassignedCandidateDesigns = entry.CandidateDesigns.Where(d => !d.HasPreview).ToList();
                var otherUnassignedDesigns = plugin.DesignManager.Designs
                    .Where(d => !d.HasPreview && !unassignedCandidateDesigns.Any(c => c.Identifier == d.Identifier))
                    .OrderBy(d => d.Name)
                    .ToList();
                var alreadyAllocatedDesigns = plugin.DesignManager.Designs
                    .Where(d => d.HasPreview && !entry.CandidateDesigns.Any(c => c.Identifier == d.Identifier))
                    .OrderBy(d => d.Name)
                    .ToList();

                var allEligibleDesigns = new List<DesignInfo>();
                allEligibleDesigns.AddRange(unassignedCandidateDesigns);
                allEligibleDesigns.AddRange(otherUnassignedDesigns);
                allEligibleDesigns.AddRange(entry.CandidateDesigns.Where(d => d.HasPreview));
                allEligibleDesigns.AddRange(alreadyAllocatedDesigns);

                if (!selectedDesignForFile.TryGetValue(entry.FilePath, out var currentSelectedGuid))
                {
                    if (unassignedCandidateDesigns.Count > 0)
                    {
                        currentSelectedGuid = unassignedCandidateDesigns[0].Identifier;
                        selectedDesignForFile[entry.FilePath] = currentSelectedGuid;
                    }
                    else if (otherUnassignedDesigns.Count > 0)
                    {
                        currentSelectedGuid = otherUnassignedDesigns[0].Identifier;
                        selectedDesignForFile[entry.FilePath] = currentSelectedGuid;
                    }
                    else if (allEligibleDesigns.Count > 0)
                    {
                        currentSelectedGuid = allEligibleDesigns[0].Identifier;
                        selectedDesignForFile[entry.FilePath] = currentSelectedGuid;
                    }
                }

                var selectedDesign = allEligibleDesigns.FirstOrDefault(d => d.Identifier == currentSelectedGuid);
                var previewLabel = selectedDesign != null ? $"{selectedDesign.Name} [{selectedDesign.Identifier.ToString()[..8]}]" : "-- Select Design --";

                ImGui.SetNextItemWidth(260 * ImGuiHelpers.GlobalScale);
                if (ImGui.BeginCombo("##TargetDesignCombo", previewLabel))
                {
                    if (unassignedCandidateDesigns.Count > 0)
                    {
                        ImGui.TextDisabled("--- Candidate Name Matches ---");
                        foreach (var candidate in unassignedCandidateDesigns)
                        {
                            bool isSelected = candidate.Identifier == currentSelectedGuid;
                            var folderTag = !string.IsNullOrEmpty(candidate.FileSystemFolder) ? $" ({candidate.FileSystemFolder})" : "";
                            if (ImGui.Selectable($"{candidate.Name} [{candidate.Identifier.ToString()[..8]}]{folderTag}##Cand_{candidate.Identifier}", isSelected))
                            {
                                selectedDesignForFile[entry.FilePath] = candidate.Identifier;
                            }
                        }
                        ImGui.Separator();
                    }

                    if (otherUnassignedDesigns.Count > 0)
                    {
                        ImGui.TextDisabled("--- Other Designs Missing Previews ---");
                        foreach (var other in otherUnassignedDesigns)
                        {
                            bool isSelected = other.Identifier == currentSelectedGuid;
                            var folderTag = !string.IsNullOrEmpty(other.FileSystemFolder) ? $" ({other.FileSystemFolder})" : "";
                            if (ImGui.Selectable($"{other.Name} [{other.Identifier.ToString()[..8]}]{folderTag}##Unass_{other.Identifier}", isSelected))
                            {
                                selectedDesignForFile[entry.FilePath] = other.Identifier;
                            }
                        }
                        ImGui.Separator();
                    }

                    if (alreadyAllocatedDesigns.Count > 0)
                    {
                        ImGui.TextDisabled("--- Designs With Existing Preview (Will Overwrite) ---");
                        foreach (var allocated in alreadyAllocatedDesigns)
                        {
                            bool isSelected = allocated.Identifier == currentSelectedGuid;
                            var folderTag = !string.IsNullOrEmpty(allocated.FileSystemFolder) ? $" ({allocated.FileSystemFolder})" : "";
                            if (ImGui.Selectable($"{allocated.Name} [{allocated.Identifier.ToString()[..8]}]{folderTag} [Has Image]##Alloc_{allocated.Identifier}", isSelected))
                            {
                                selectedDesignForFile[entry.FilePath] = allocated.Identifier;
                            }
                        }
                    }

                    ImGui.EndCombo();
                }

                ImGui.SameLine();

                // Assign Button
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.6f, 0.25f, 0.8f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.8f, 0.35f, 1f));
                if (ImGui.Button("Assign Preview##AssignBtn", new Vector2(120 * ImGuiHelpers.GlobalScale, 0)))
                {
                    if (selectedDesign != null)
                    {
                        if (plugin.DesignManager.AssignUnallocatedImage(entry.FilePath, selectedDesign.Identifier))
                        {
                            dismissedFiles.Add(entry.FilePath);
                            Plugin.ChatGui.Print($"[GPM] Assigned '{entry.FileName}' to '{selectedDesign.Name}'!");
                        }
                    }
                }
                ImGui.PopStyleColor(2);

                ImGui.SameLine();

                // Delete Button
                if (fileToDeleteConfirm == entry.FilePath)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.1f, 0.1f, 0.9f));
                    if (ImGui.Button("Confirm Delete##ConfirmDel", new Vector2(110 * ImGuiHelpers.GlobalScale, 0)))
                    {
                        if (plugin.DesignManager.DeleteUnallocatedImage(entry.FilePath))
                        {
                            dismissedFiles.Add(entry.FilePath);
                            fileToDeleteConfirm = null;
                            Plugin.ChatGui.Print($"[GPM] Deleted orphan image file '{entry.FileName}'.");
                        }
                    }
                    ImGui.PopStyleColor();

                    ImGui.SameLine();
                    if (ImGui.Button("Cancel##CancelDel"))
                    {
                        fileToDeleteConfirm = null;
                    }
                }
                else
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.5f, 0.2f, 0.2f, 0.7f));
                    if (ImGui.Button("Delete File##DelBtn"))
                    {
                        fileToDeleteConfirm = entry.FilePath;
                    }
                    ImGui.PopStyleColor();
                }

                ImGui.SameLine();

                // Dismiss Button
                if (ImGui.Button("Dismiss##DismissBtn"))
                {
                    dismissedFiles.Add(entry.FilePath);
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Ignore this file for the current review session without deleting it.");
                }

                ImGui.EndGroup();
            }
        }

        ImGui.PopID();
        ImGui.Spacing();
    }

    private void AutoAssignBestGuesses(List<UnallocatedImageEntry> entries)
    {
        int assignedCount = 0;
        foreach (var entry in entries)
        {
            if (entry.CandidateDesigns.Count == 1)
            {
                var candidate = entry.CandidateDesigns[0];
                if (plugin.DesignManager.AssignUnallocatedImage(entry.FilePath, candidate.Identifier))
                {
                    dismissedFiles.Add(entry.FilePath);
                    assignedCount++;
                }
            }
        }

        if (assignedCount > 0)
        {
            Plugin.ChatGui.Print($"[GPM] Auto-assigned {assignedCount} images to their matching designs!");
        }
        else
        {
            Plugin.ChatGui.Print("[GPM] No unambiguous single-candidate matches were available for auto-assignment.");
        }
    }

    private static class ImRaii
    {
        public readonly struct ChildToken : IDisposable
        {
            private readonly bool open;
            public ChildToken(bool open) => this.open = open;
            public static implicit operator bool(ChildToken token) => token.open;
            public void Dispose()
            {
                if (open) ImGui.EndChild();
            }
        }

        public static ChildToken Child(string strId, Vector2 size, bool border = false, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
        {
            return new ChildToken(ImGui.BeginChild(strId, size, border, flags));
        }
    }
}
