using System;
using System.Runtime.InteropServices;
using System.Drawing;

namespace GlamourerPreviewManager;

public static class ClipboardHelper
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    private const uint CF_BITMAP = 2;

    /// <summary>
    /// Checks if there is an image in the clipboard.
    /// </summary>
    public static bool IsImageInClipboard()
    {
        return IsClipboardFormatAvailable(CF_BITMAP);
    }

    /// <summary>
    /// Gets the image from the clipboard and returns it as a System.Drawing.Image, or null if not available.
    /// </summary>
    public static Image? GetImageFromClipboard()
    {
        if (!IsClipboardFormatAvailable(CF_BITMAP))
            return null;

        if (!OpenClipboard(IntPtr.Zero))
            return null;

        try
        {
            IntPtr handle = GetClipboardData(CF_BITMAP);
            if (handle != IntPtr.Zero)
            {
                // Create a copy of the image from the Win32 HBITMAP handle
                return Image.FromHbitmap(handle);
            }
        }
        catch (Exception)
        {
            // Ignore exceptions and return null
        }
        finally
        {
            CloseClipboard();
        }

        return null;
    }
}
