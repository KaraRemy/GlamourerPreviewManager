using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace GlamourerPreviewManager;

public enum CropAspect
{
    NoCrop,
    Aspect16_9,
    Aspect1_1,
    Aspect4_3,
    Aspect9_16,
    Aspect3_4
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public string PreviewsFolderPath { get; set; } = string.Empty;
    public CropAspect CropOption { get; set; } = CropAspect.Aspect9_16;
    public int PreviewImageSizePercent { get; set; } = 100;
    
    // Screenshot settings
    public float ScreenshotScale { get; set; } = 1.0f;
    public int ScreenshotOffsetX { get; set; } = 0;
    public int ScreenshotOffsetY { get; set; } = 0;
    public bool AutoApplyOnScreenshot { get; set; } = false;
    public float ZoomScale { get; set; } = 1.0f;

    public bool GalleryShowOnlyWithPreviews { get; set; } = true;
    public float GalleryCardWidth { get; set; } = 150f;
    public bool HasSeenGalleryNotification { get; set; } = false;
    public CropAspect GalleryCardAspect { get; set; } = CropAspect.Aspect9_16;
    public bool GalleryCardContainImage { get; set; } = false;

    // Screenshot watcher settings
    public string GameScreenshotFolderPath { get; set; } = string.Empty;
    public bool AutoImportFromWatchedFolder { get; set; } = true;
    public bool AutoDeleteWatchedScreenshot { get; set; } = false;

    // Roulette settings
    public bool RouletteConfirmBeforeApply { get; set; } = true;
    public bool RouletteIncludeWithoutPreviews { get; set; } = false;
    public List<Guid> RouletteExcludedPool { get; set; } = new();
    public List<string> RouletteExcludedFolders { get; set; } = new();

    // Retention and deletion policies
    public bool AutoDeletePreviewsOnDesignDeletion { get; set; } = false;

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
