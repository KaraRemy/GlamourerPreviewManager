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
    public string? PreviewImagePath { get; set; }
    public bool HasPreview => !string.IsNullOrEmpty(PreviewImagePath);
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

            // Clean up allocations that belong to designs that no longer exist
            var currentIds = scannedDesigns.Select(d => d.Identifier).ToHashSet();
            var keysToRemove = Allocations.Keys.Where(k => !currentIds.Contains(k)).ToList();
            foreach (var key in keysToRemove)
            {
                // Delete associated image file from previews folder
                if (!string.IsNullOrEmpty(previewsFolder) && Directory.Exists(previewsFolder))
                {
                    var imgFile = Allocations[key];
                    var imgPath = Path.Combine(previewsFolder, imgFile);
                    try
                    {
                        if (File.Exists(imgPath)) File.Delete(imgPath);
                    }
                    catch { }
                }
                Allocations.Remove(key);
                allocationsChanged = true;
            }

            Designs = scannedDesigns.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();

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
                    Description = obj.Value<string>("Description") ?? string.Empty
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
                            var safeName = GetSafeFilename(designInfo.Name);
                            var newImgFile = $"{safeName}.png";
                            var newImgPath = Path.Combine(previewsFolder, newImgFile);

                            // Resolve name conflict if needed
                            int counter = 1;
                            while (File.Exists(newImgPath) && newImgPath != oldImgPath)
                            {
                                newImgFile = $"{safeName} ({counter}).png";
                                newImgPath = Path.Combine(previewsFolder, newImgFile);
                                counter++;
                            }

                            try
                            {
                                if (File.Exists(oldImgPath))
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

                // Replace in list
                var index = Designs.IndexOf(existing);
                Designs[index] = designInfo;
            }
            else
            {
                // New design! Add it
                Designs.Add(designInfo);
            }

            Designs = Designs.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
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
                
                var previewsFolder = plugin.Configuration.PreviewsFolderPath;
                if (!string.IsNullOrEmpty(previewsFolder) && Directory.Exists(previewsFolder))
                {
                    if (Allocations.TryGetValue(id, out var imgFile))
                    {
                        var imgPath = Path.Combine(previewsFolder, imgFile);
                        try
                        {
                            if (File.Exists(imgPath)) File.Delete(imgPath);
                        }
                        catch { }
                        
                        Allocations.Remove(id);
                        SaveAllocations();
                    }
                }
            }
        }
    }

    public void UpdatePreviewImage(Guid id, string sourceImagePath)
    {
        var previewsFolder = plugin.Configuration.PreviewsFolderPath;
        if (string.IsNullOrEmpty(previewsFolder) || !Directory.Exists(previewsFolder)) return;

        lock (scanLock)
        {
            var design = Designs.FirstOrDefault(d => d.Identifier == id);
            if (design == null) return;

            var safeName = GetSafeFilename(design.Name);
            var destFile = $"{safeName}.png";
            var destPath = Path.Combine(previewsFolder, destFile);

            // Handle name conflicts
            int counter = 1;
            while (File.Exists(destPath))
            {
                destFile = $"{safeName} ({counter}).png";
                destPath = Path.Combine(previewsFolder, destFile);
                counter++;
            }

            // If there was an old image, try to delete it
            if (Allocations.TryGetValue(id, out var oldFile))
            {
                var oldPath = Path.Combine(previewsFolder, oldFile);
                try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }
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

            var safeName = GetSafeFilename(design.Name);
            var destFile = $"{safeName}.png";
            var destPath = Path.Combine(previewsFolder, destFile);

            // Handle name conflicts
            int counter = 1;
            while (File.Exists(destPath))
            {
                destFile = $"{safeName} ({counter}).png";
                destPath = Path.Combine(previewsFolder, destFile);
                counter++;
            }

            // If there was an old image, try to delete it
            if (Allocations.TryGetValue(id, out var oldFile))
            {
                var oldPath = Path.Combine(previewsFolder, oldFile);
                try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }
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

    public (int allocatedCount, int totalFilesCount) RediscoverPreviews()
    {
        var previewsFolder = plugin.Configuration.PreviewsFolderPath;
        if (string.IsNullOrEmpty(previewsFolder) || !Directory.Exists(previewsFolder))
        {
            return (0, 0);
        }

        lock (scanLock)
        {
            var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".png", ".jpg", ".jpeg", ".webp", ".bmp"
            };

            var files = Directory.GetFiles(previewsFolder)
                .Where(f => supportedExtensions.Contains(Path.GetExtension(f)))
                .ToList();

            int totalFiles = files.Count;
            int allocatedCount = 0;

            var safeNameToDesign = new Dictionary<string, List<DesignInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var design in Designs)
            {
                var safeName = GetSafeFilename(design.Name);
                if (!safeNameToDesign.TryGetValue(safeName, out var list))
                {
                    list = new List<DesignInfo>();
                    safeNameToDesign[safeName] = list;
                }
                list.Add(design);
            }

            foreach (var file in files)
            {
                var fileNameWithExt = Path.GetFileName(file);
                var fileNameNoExt = Path.GetFileNameWithoutExtension(file);

                // Strip copy counter suffix, e.g. " (1)" -> ""
                var cleanedName = Regex.Replace(fileNameNoExt, @"\s\(\d+\)$", "");

                if (safeNameToDesign.TryGetValue(cleanedName, out var matchingDesigns))
                {
                    var targetDesign = matchingDesigns.FirstOrDefault(d => 
                        !Allocations.ContainsKey(d.Identifier) || 
                        !File.Exists(Path.Combine(previewsFolder, Allocations[d.Identifier])));
                    
                    if (targetDesign == null)
                    {
                        targetDesign = matchingDesigns.First();
                    }

                    Allocations[targetDesign.Identifier] = fileNameWithExt;
                    targetDesign.PreviewImagePath = file;
                    allocatedCount++;
                }
            }

            if (allocatedCount > 0)
            {
                SaveAllocations();
                ScanDesigns();
            }

            return (allocatedCount, totalFiles);
        }
    }

    private string GetSafeFilename(string name)
    {
        var invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
        var invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);
        return Regex.Replace(name, invalidRegStr, "_");
    }
}
