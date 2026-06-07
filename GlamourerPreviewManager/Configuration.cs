using Dalamud.Configuration;
using System;

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
    public bool AutoSyncSelection { get; set; } = true;
    public int PreviewImageSizePercent { get; set; } = 100;
    
    // Screenshot settings
    public float ScreenshotScale { get; set; } = 1.0f;
    public int ScreenshotOffsetX { get; set; } = 0;
    public int ScreenshotOffsetY { get; set; } = 0;
    public bool AutoApplyOnScreenshot { get; set; } = false;
    public float ZoomScale { get; set; } = 1.0f;

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
