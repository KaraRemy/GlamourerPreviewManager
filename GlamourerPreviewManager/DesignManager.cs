using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace GlamourerPreviewManager;

public class DesignInfo
{
    public Guid Identifier { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string FileSystemFolder { get; set; } = string.Empty;
    public string? PreviewImagePath { get; set; }
    public bool HasPreview => !string.IsNullOrEmpty(PreviewImagePath);
}

public class UnallocatedImageEntry
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string CleanedName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public List<DesignInfo> CandidateDesigns { get; set; } = new();
    public bool IsAmbiguous => CandidateDesigns.Count > 1;
}

public class RediscoveryResult
{
    public int TotalFilesCount { get; set; }
    public int AllocatedCount { get; set; }
    public List<UnallocatedImageEntry> UnallocatedImages { get; set; } = new();
    public List<DesignInfo> DesignsWithoutPreview { get; set; } = new();
    public bool HasPendingReviews => UnallocatedImages.Count > 0;
}

public class DesignManager : IDisposable
{
    private readonly Plugin plugin;
    private FileSystemWatcher? designsWatcher;
    private readonly object scanLock = new();
    private bool isScanning = false;
    
    // In-memory list of designs and their mapped preview files
    public List<DesignInfo> Designs { get; private set; } = new();
    
    // Allocation map: UUID -> Image filename (relative to previews folder)
    public Dictionary<Guid, string> Allocations { get; private set; } = new();

    // Fast O(1) lookup maps
    public Dictionary<Guid, DesignInfo> DesignsById { get; private set; } = new();
    public Dictionary<string, List<DesignInfo>> DesignsByName { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public DesignManager(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Initialize()
    {
        LoadAllocations();
        ScanDesigns();
        SetupWatcher();
    }

    public void Dispose()
    {
        designsWatcher?.Dispose();
        designsWatcher = null;
    }

    public string GetDesignsDirectory()
    {
        try
        {
            var configDir = Plugin.PluginInterface.ConfigDirectory.FullName;
            var parentDir = Path.GetDirectoryName(configDir);
            if (!string.IsNullOrEmpty(parentDir))
            {
                var glamourerDir = Path.Combine(parentDir, "Glamourer", "designs");
                if (Directory.Exists(glamourerDir))
                {
                    return glamourerDir;
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[GPM] Failed to resolve sibling designs directory: {ex.Message}");
        }

        // Fallback to default AppData path
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "XIVLauncher", "pluginConfigs", "Glamourer", "designs");
    }

    private void SetupWatcher()
    {
        var dir = GetDesignsDirectory();
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { return; }
        }

        designsWatcher = new FileSystemWatcher(dir, "*.json")
        {
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };

        designsWatcher.Created += (s, e) => Task.Run(() => OnDesignFileChanged(e.FullPath));
        designsWatcher.Changed += (s, e) => Task.Run(() => OnDesignFileChanged(e.FullPath));
        designsWatcher.Deleted += (s, e) => Task.Run(() => OnDesignFileDeleted(e.FullPath));
    }

    private string GetAllocationFilePath()
    {
        var configDir = Plugin.PluginInterface.ConfigDirectory;
        if (!configDir.Exists)
        {
            try
            {
                configDir.Create();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Failed to create config directory: {ex.Message}");
            }
        }
        return Path.Combine(configDir.FullName, "allocation.json");
    }

    private void LoadAllocations()
    {
        lock (scanLock)
        {
            Allocations.Clear();
            var previewsFolder = plugin.Configuration.PreviewsFolderPath;
            var allocationFile = GetAllocationFilePath();
            var oldAllocationFile = string.IsNullOrEmpty(previewsFolder) ? "" : Path.Combine(previewsFolder, "allocation.json");

            bool migrated = false;
            string targetLoadFile = allocationFile;

            if (!File.Exists(allocationFile) && !string.IsNullOrEmpty(oldAllocationFile) && File.Exists(oldAllocationFile))
            {
                targetLoadFile = oldAllocationFile;
                migrated = true;
            }

            if (File.Exists(targetLoadFile))
            {
                try
                {
                    var text = File.ReadAllText(targetLoadFile);
                    var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(text);
                    if (dict != null)
                    {
                        foreach (var kvp in dict)
                        {
                            if (Guid.TryParse(kvp.Key, out var id))
                            {
                                Allocations[id] = kvp.Value;
                            }
                        }
                    }

                    if (migrated)
                    {
                        // Save to the new location immediately
                        SaveAllocations();
                        // Try to delete the old file to clean up the preview folder
                        try
                        {
                            File.Delete(oldAllocationFile);
                            Plugin.Log.Information("Migrated allocation.json to config directory and deleted the old file from previews folder.");
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.Warning($"Failed to delete old allocation.json: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"Failed to load GPM allocations file: {ex.Message}");
                }
            }
        }
    }

    public void SaveAllocations()
    {
        lock (scanLock)
        {
            var allocationFile = GetAllocationFilePath();
            try
            {
                var dict = Allocations.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value);
                var text = JsonConvert.SerializeObject(dict, Formatting.Indented);
                File.WriteAllText(allocationFile, text);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Failed to save GPM allocations file: {ex.Message}");
            }
        }
    }

    public void ScanDesigns()
    {
        lock (scanLock)
        {
            if (isScanning) return;
            isScanning = true;
        }

        try
        {
            var dir = GetDesignsDirectory();
            if (!Directory.Exists(dir))
            {
                Designs = new List<DesignInfo>();
                return;
            }

            var files = Directory.GetFiles(dir, "*.json");
            var scannedDesigns = new List<DesignInfo>();
            var previewsFolder = plugin.Configuration.PreviewsFolderPath;
            bool allocationsChanged = false;

            foreach (var file in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (!Guid.TryParse(fileName, out var id)) continue;

                var designInfo = ParseDesignFile(file, id);
                if (designInfo == null) continue;

                // Bind preview image
                if (!string.IsNullOrEmpty(previewsFolder) && Directory.Exists(previewsFolder))
                {
                    if (Allocations.TryGetValue(id, out var imgFile))
                    {
                        var imgPath = Path.Combine(previewsFolder, imgFile);
                        if (File.Exists(imgPath))
                        {
                            designInfo.PreviewImagePath = imgPath;
                        }
                        else
                        {
                            // File vanished, clean it up
                            Allocations.Remove(id);
                            allocationsChanged = true;
                        }
                    }
                }

                scannedDesigns.Add(designInfo);
            }

            // Clean up allocations that belong to designs that no longer exist in Glamourer
            var currentIds = scannedDesigns.Select(d => d.Identifier).ToHashSet();
            var keysToRemove = Allocations.Keys.Where(k => !currentIds.Contains(k)).ToList();
            foreach (var key in keysToRemove)
            {
                if (plugin.Configuration.AutoDeletePreviewsOnDesignDeletion && Allocations.TryGetValue(key, out var imgFile))
                {
                    var imgPath = Path.Combine(previewsFolder, imgFile);
                    bool isShared = Allocations.Any(kvp => kvp.Key != key && string.Equals(kvp.Value, imgFile, StringComparison.OrdinalIgnoreCase));
                    if (!isShared && File.Exists(imgPath))
                    {
                        try { File.Delete(imgPath); } catch { }
                    }
                }
                Allocations.Remove(key);
                allocationsChanged = true;
            }

            Designs = scannedDesigns.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            RebuildLookupDictionaries();

            if (allocationsChanged)
            {
                SaveAllocations();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error scanning Glamourer designs: {ex.Message}");
        }
        finally
        {
            lock (scanLock)
            {
                isScanning = false;
            }
        }
    }

    private DesignInfo? ParseDesignFile(string path, Guid id)
    {
        try
        {
            var text = File.ReadAllText(path);
            var obj = JsonConvert.DeserializeObject<JObject>(text);
            if (obj != null)
            {
                return new DesignInfo
                {
                    Identifier = id,
                    Name = obj.Value<string>("Name") ?? "Unnamed Design",
                    Description = obj.Value<string>("Description") ?? string.Empty,
                    FileSystemFolder = obj.Value<string>("FileSystemFolder") ?? string.Empty
                };
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"Failed to parse design file {path}: {ex.Message}");
        }
        return null;
    }

    private void OnDesignFileChanged(string fullPath)
    {
        // Give Glamourer a tiny fraction of time to finish writing the file
        System.Threading.Thread.Sleep(100);
        
        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        if (!Guid.TryParse(fileName, out var id)) return;

        lock (scanLock)
        {
            var designInfo = ParseDesignFile(fullPath, id);
            if (designInfo == null) return;

            var existing = Designs.FirstOrDefault(d => d.Identifier == id);
            var previewsFolder = plugin.Configuration.PreviewsFolderPath;

            if (existing != null)
            {
                // Check if name has changed
                if (existing.Name != designInfo.Name)
                {
                    Plugin.Log.Information($"Design renamed from '{existing.Name}' to '{designInfo.Name}'");
                    
                    if (!string.IsNullOrEmpty(previewsFolder) && Directory.Exists(previewsFolder))
                    {
                        if (Allocations.TryGetValue(id, out var oldImgFile))
                        {
                            var oldImgPath = Path.Combine(previewsFolder, oldImgFile);
                            var newImgFile = GeneratePreviewFilename(designInfo);
                            var newImgPath = Path.Combine(previewsFolder, newImgFile);

                            try
                            {
                                if (File.Exists(oldImgPath) && oldImgPath != newImgPath)
                                {
                                    File.Move(oldImgPath, newImgPath);
                                    designInfo.PreviewImagePath = newImgPath;
                                }
                            }
                            catch (Exception ex)
                            {
                                Plugin.Log.Error($"Failed to rename image file during design rename: {ex.Message}");
                            }

                            Allocations[id] = newImgFile;
                            SaveAllocations();
                        }
                    }
                }
                else
                {
                    // Retain preview path
                    designInfo.PreviewImagePath = existing.PreviewImagePath;
                }

                var index = Designs.IndexOf(existing);
                Designs[index] = designInfo;
            }
            else
            {
                // New design
                if (!string.IsNullOrEmpty(previewsFolder) && Directory.Exists(previewsFolder))
                {
                    if (Allocations.TryGetValue(id, out var imgFile))
                    {
                        var imgPath = Path.Combine(previewsFolder, imgFile);
                        if (File.Exists(imgPath))
                        {
                            designInfo.PreviewImagePath = imgPath;
                        }
                    }
                }

                Designs.Add(designInfo);
            }

            Designs = Designs.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
            RebuildLookupDictionaries();
        }
    }

    private void OnDesignFileDeleted(string fullPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        if (!Guid.TryParse(fileName, out var id)) return;

        lock (scanLock)
        {
            var existing = Designs.FirstOrDefault(d => d.Identifier == id);
            if (existing != null)
            {
                Designs.Remove(existing);

                // Check if preview image file should be automatically deleted
                if (plugin.Configuration.AutoDeletePreviewsOnDesignDeletion && Allocations.TryGetValue(id, out var imgFile))
                {
                    var previewsFolder = plugin.Configuration.PreviewsFolderPath;
                    if (!string.IsNullOrEmpty(previewsFolder) && Directory.Exists(previewsFolder))
                    {
                        var imgPath = Path.Combine(previewsFolder, imgFile);
                        bool isShared = Allocations.Any(kvp => kvp.Key != id && string.Equals(kvp.Value, imgFile, StringComparison.OrdinalIgnoreCase));
                        if (!isShared && File.Exists(imgPath))
                        {
                            try { File.Delete(imgPath); } catch { }
                        }
                    }
                }

                if (Allocations.ContainsKey(id))
                {
                    Allocations.Remove(id);
                    SaveAllocations();
                }

                RebuildLookupDictionaries();
            }
        }
    }

    public string GeneratePreviewFilename(DesignInfo design)
    {
        var safeName = GetSafeFilename(design.Name);
        var shortGuid = design.Identifier.ToString()[..8].ToLowerInvariant();
        var previewsFolder = plugin.Configuration.PreviewsFolderPath;

        // Check if multiple designs in Glamourer share this exact name
        bool hasDuplicateName = false;
        if (DesignsByName.TryGetValue(design.Name, out var list) && list.Count > 1)
        {
            hasDuplicateName = true;
        }

        // If duplicate name, always use deterministic bracketed short GUID tag
        if (hasDuplicateName)
        {
            return $"{safeName} [{shortGuid}].png";
        }

        // If unique name, check if default filename is already allocated to another design
        var defaultFilename = $"{safeName}.png";
        if (Directory.Exists(previewsFolder))
        {
            var defaultPath = Path.Combine(previewsFolder, defaultFilename);
            if (File.Exists(defaultPath))
            {
                var allocatedId = Allocations.FirstOrDefault(kvp => string.Equals(kvp.Value, defaultFilename, StringComparison.OrdinalIgnoreCase)).Key;
                if (allocatedId != Guid.Empty && allocatedId != design.Identifier)
                {
                    return $"{safeName} [{shortGuid}].png";
                }
            }
        }

        return defaultFilename;
    }

    public void UpdatePreviewImage(Guid id, string sourceImagePath)
    {
        var previewsFolder = plugin.Configuration.PreviewsFolderPath;
        if (string.IsNullOrEmpty(previewsFolder) || !Directory.Exists(previewsFolder)) return;

        lock (scanLock)
        {
            var design = Designs.FirstOrDefault(d => d.Identifier == id);
            if (design == null) return;

            var destFile = GeneratePreviewFilename(design);
            var destPath = Path.Combine(previewsFolder, destFile);

            // If there was an old image with a different name, clean it up only if no other design uses it
            if (Allocations.TryGetValue(id, out var oldFile) && !string.Equals(oldFile, destFile, StringComparison.OrdinalIgnoreCase))
            {
                bool isShared = Allocations.Any(kvp => kvp.Key != id && string.Equals(kvp.Value, oldFile, StringComparison.OrdinalIgnoreCase));
                if (!isShared)
                {
                    var oldPath = Path.Combine(previewsFolder, oldFile);
                    try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }
                }
            }

            try
            {
                // Process and save to previews folder
                plugin.CropAndScaleImage(sourceImagePath, destPath, plugin.Configuration.CropOption);
                
                Allocations[id] = destFile;
                design.PreviewImagePath = destPath;
                SaveAllocations();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Failed to copy and scale preview image for design {design.Name}: {ex.Message}");
            }
        }
    }

    public void SaveImageDirect(Guid id, System.Drawing.Image image)
    {
        var previewsFolder = plugin.Configuration.PreviewsFolderPath;
        if (string.IsNullOrEmpty(previewsFolder) || !Directory.Exists(previewsFolder)) return;

        lock (scanLock)
        {
            var design = Designs.FirstOrDefault(d => d.Identifier == id);
            if (design == null) return;

            var destFile = GeneratePreviewFilename(design);
            var destPath = Path.Combine(previewsFolder, destFile);

            // If there was an old image with a different name, clean it up only if no other design uses it
            if (Allocations.TryGetValue(id, out var oldFile) && !string.Equals(oldFile, destFile, StringComparison.OrdinalIgnoreCase))
            {
                bool isShared = Allocations.Any(kvp => kvp.Key != id && string.Equals(kvp.Value, oldFile, StringComparison.OrdinalIgnoreCase));
                if (!isShared)
                {
                    var oldPath = Path.Combine(previewsFolder, oldFile);
                    try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }
                }
            }

            try
            {
                plugin.SaveImageFromBitmap(image, destPath, plugin.Configuration.CropOption);
                
                Allocations[id] = destFile;
                design.PreviewImagePath = destPath;
                SaveAllocations();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Failed to save preview image for design {design.Name}: {ex.Message}");
            }
        }
    }

    public void OnPreviewsFolderChanged()
    {
        LoadAllocations();
        ScanDesigns();
    }

    public RediscoveryResult RediscoverPreviews()
    {
        var result = new RediscoveryResult();
        var previewsFolder = plugin.Configuration.PreviewsFolderPath;
        if (string.IsNullOrEmpty(previewsFolder) || !Directory.Exists(previewsFolder))
        {
            return result;
        }

        // Rescan Glamourer designs from disk first so in-memory cache is 100% up to date
        ScanDesigns();

        lock (scanLock)
        {
            var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".png", ".jpg", ".jpeg", ".webp", ".bmp"
            };

            var allDiskFiles = Directory.GetFiles(previewsFolder)
                .Where(f => supportedExtensions.Contains(Path.GetExtension(f)))
                .ToList();

            result.TotalFilesCount = allDiskFiles.Count;

            // 1. Identify all files that are ALREADY validly allocated to active existing designs
            var alreadyAllocatedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in Allocations)
            {
                if (DesignsById.ContainsKey(kvp.Key))
                {
                    var fullImgPath = Path.Combine(previewsFolder, kvp.Value);
                    if (File.Exists(fullImgPath))
                    {
                        alreadyAllocatedFiles.Add(kvp.Value);
                    }
                }
            }

            result.AllocatedCount = alreadyAllocatedFiles.Count;

            // 2. Identify remaining unallocated disk files
            var unallocatedDiskFiles = allDiskFiles
                .Where(f => !alreadyAllocatedFiles.Contains(Path.GetFileName(f)))
                .ToList();

            // 3. Identify designs that are missing a preview image
            var unallocatedDesigns = Designs
                .Where(d => !Allocations.ContainsKey(d.Identifier) || 
                            !File.Exists(Path.Combine(previewsFolder, Allocations[d.Identifier])))
                .ToList();

            var safeNameToUnallocatedDesigns = new Dictionary<string, List<DesignInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var design in unallocatedDesigns)
            {
                var safeName = GetSafeFilename(design.Name);
                if (!safeNameToUnallocatedDesigns.TryGetValue(safeName, out var list))
                {
                    list = new List<DesignInfo>();
                    safeNameToUnallocatedDesigns[safeName] = list;
                }
                list.Add(design);
            }

            var unallocatedAfterPass1 = new List<string>();

            // PASS 1: Tag / GUID Match on unallocated disk files
            foreach (var file in unallocatedDiskFiles)
            {
                var fileNameNoExt = Path.GetFileNameWithoutExtension(file);
                var fileNameWithExt = Path.GetFileName(file);
                bool matched = false;

                // Check for full GUID in filename
                var fullGuidMatch = Regex.Match(fileNameNoExt, @"([a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12})");
                if (fullGuidMatch.Success && Guid.TryParse(fullGuidMatch.Value, out var parsedGuid))
                {
                    var targetDesign = unallocatedDesigns.FirstOrDefault(d => d.Identifier == parsedGuid);
                    if (targetDesign != null)
                    {
                        Allocations[targetDesign.Identifier] = fileNameWithExt;
                        targetDesign.PreviewImagePath = file;
                        unallocatedDesigns.Remove(targetDesign);
                        result.AllocatedCount++;
                        matched = true;
                    }
                }

                // Check for 8-character bracketed hex tag, e.g. [d8f4e2a1] or (d8f4e2a1) or {d8f4e2a1}
                if (!matched)
                {
                    var tagMatch = Regex.Match(fileNameNoExt, @"[\[\(\{]([a-fA-F0-9]{8})[\]\}\)]");
                    if (tagMatch.Success)
                    {
                        var shortHex = tagMatch.Groups[1].Value;
                        var targetDesign = unallocatedDesigns.FirstOrDefault(d => 
                            d.Identifier.ToString().StartsWith(shortHex, StringComparison.OrdinalIgnoreCase));
                        if (targetDesign != null)
                        {
                            Allocations[targetDesign.Identifier] = fileNameWithExt;
                            targetDesign.PreviewImagePath = file;
                            unallocatedDesigns.Remove(targetDesign);
                            result.AllocatedCount++;
                            matched = true;
                        }
                    }
                }

                if (!matched)
                {
                    unallocatedAfterPass1.Add(file);
                }
            }

            // Rebuild unallocated design name map after Pass 1
            safeNameToUnallocatedDesigns.Clear();
            foreach (var design in unallocatedDesigns)
            {
                var safeName = GetSafeFilename(design.Name);
                if (!safeNameToUnallocatedDesigns.TryGetValue(safeName, out var list))
                {
                    list = new List<DesignInfo>();
                    safeNameToUnallocatedDesigns[safeName] = list;
                }
                list.Add(design);
            }

            // PASS 2: Non-ambiguous unique name match on untagged disk files
            var pass3Files = new List<string>();
            foreach (var file in unallocatedAfterPass1)
            {
                var fileNameWithExt = Path.GetFileName(file);
                var fileNameNoExt = Path.GetFileNameWithoutExtension(file);
                var cleanedName = GetCleanedDesignNameFromFilename(fileNameNoExt);
                bool hasExplicitTag = Regex.IsMatch(fileNameNoExt, @"[\[\(\{][a-fA-F0-9\-]{8,36}[\]\}\)]");

                if (!hasExplicitTag && safeNameToUnallocatedDesigns.TryGetValue(cleanedName, out var matchingDesigns) && matchingDesigns.Count == 1)
                {
                    var targetDesign = matchingDesigns[0];
                    Allocations[targetDesign.Identifier] = fileNameWithExt;
                    targetDesign.PreviewImagePath = file;
                    matchingDesigns.Clear();
                    unallocatedDesigns.Remove(targetDesign);
                    result.AllocatedCount++;
                }
                else
                {
                    pass3Files.Add(file);
                }
            }

            // PASS 3: Truly Unallocated, Ambiguous, and Orphan Files for Manual Review
            foreach (var file in pass3Files)
            {
                var fileNameWithExt = Path.GetFileName(file);
                var fileNameNoExt = Path.GetFileNameWithoutExtension(file);
                var cleanedName = GetCleanedDesignNameFromFilename(fileNameNoExt);

                var entry = new UnallocatedImageEntry
                {
                    FilePath = file,
                    FileName = fileNameWithExt,
                    CleanedName = cleanedName,
                    FileSizeBytes = new FileInfo(file).Length
                };

                if (safeNameToUnallocatedDesigns.TryGetValue(cleanedName, out var matchingDesigns))
                {
                    entry.CandidateDesigns = matchingDesigns.ToList();
                }

                result.UnallocatedImages.Add(entry);
            }

            // Record designs that still have no preview image
            result.DesignsWithoutPreview = Designs.Where(d => !d.HasPreview).ToList();

            if (result.AllocatedCount > alreadyAllocatedFiles.Count)
            {
                SaveAllocations();
                ScanDesigns();
            }

            return result;
        }
    }

    public bool AssignUnallocatedImage(string sourceFilePath, Guid targetDesignId)
    {
        var previewsFolder = plugin.Configuration.PreviewsFolderPath;
        if (string.IsNullOrEmpty(previewsFolder) || !Directory.Exists(previewsFolder)) return false;

        lock (scanLock)
        {
            var design = Designs.FirstOrDefault(d => d.Identifier == targetDesignId);
            if (design == null || !File.Exists(sourceFilePath)) return false;

            var targetFileName = GeneratePreviewFilename(design);
            var targetFilePath = Path.Combine(previewsFolder, targetFileName);

            try
            {
                if (!string.Equals(sourceFilePath, targetFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(targetFilePath))
                    {
                        File.Delete(targetFilePath);
                    }
                    File.Move(sourceFilePath, targetFilePath);
                }

                Allocations[targetDesignId] = targetFileName;
                design.PreviewImagePath = targetFilePath;
                SaveAllocations();
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Failed to assign unallocated image '{sourceFilePath}' to design {design.Name}: {ex.Message}");
                return false;
            }
        }
    }

    public bool DeleteUnallocatedImage(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                return true;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Failed to delete unallocated image '{filePath}': {ex.Message}");
        }
        return false;
    }

    public string GetCleanedDesignNameFromFilename(string filenameNoExt)
    {
        // Strip bracketed GUID or hex tags e.g. [71599ba7] or (71599ba7) or {71599ba7} or full GUIDs
        var name = Regex.Replace(filenameNoExt, @"\s*[\[\(\{][a-fA-F0-9\-]{8,36}[\]\}\)]$", "");
        // Strip copy counters e.g. " (1)"
        name = Regex.Replace(name, @"\s\(\d+\)$", "");
        return name.Trim();
    }

    private string GetSafeFilename(string name)
    {
        var invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
        var invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);
        return Regex.Replace(name, invalidRegStr, "_");
    }

    private void RebuildLookupDictionaries()
    {
        DesignsById.Clear();
        DesignsByName.Clear();
        foreach (var design in Designs)
        {
            DesignsById[design.Identifier] = design;
            if (!DesignsByName.TryGetValue(design.Name, out var list))
            {
                list = new List<DesignInfo>();
                DesignsByName[design.Name] = list;
            }
            list.Add(design);
        }
    }

    public DesignInfo? GetDesignById(Guid id)
    {
        lock (scanLock)
        {
            return DesignsById.TryGetValue(id, out var design) ? design : null;
        }
    }

    public DesignInfo? GetDesignByName(string name)
    {
        lock (scanLock)
        {
            if (DesignsByName.TryGetValue(name, out var list) && list.Count > 0)
            {
                return list[0];
            }
            return null;
        }
    }

    public IReadOnlyList<DesignInfo> GetDesignsByName(string name)
    {
        lock (scanLock)
        {
            return DesignsByName.TryGetValue(name, out var list) ? list : Array.Empty<DesignInfo>();
        }
    }
}
