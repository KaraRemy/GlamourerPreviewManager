using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;

namespace GlamourerPreviewManager;

internal class ImGuiHookManager : IDisposable
{
    private readonly Plugin plugin;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte ButtonDelegate(IntPtr label, Vector2 size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte BeginDelegate(IntPtr name, IntPtr p_open, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EndDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte SelectableDelegate(IntPtr label, byte selected, int flags, Vector2 size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte SelectablePtrDelegate(IntPtr label, IntPtr p_selected, int flags, Vector2 size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte ButtonExDelegate(IntPtr label, Vector2 size, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte TreeNodeExStrDelegate(IntPtr label, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte TreeNodeExStrStrDelegate(IntPtr str_id, int flags, IntPtr fmt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte TreeNodeExPtrDelegate(IntPtr ptr_id, int flags, IntPtr fmt);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte TableNextColumnDelegate();

    private Hook<ButtonDelegate>? buttonHook;
    private Hook<ButtonExDelegate>? buttonExHook;
    private Hook<BeginDelegate>? beginHook;
    private Hook<EndDelegate>? endHook;
    private Hook<SelectableDelegate>? selectableHook;
    private Hook<SelectablePtrDelegate>? selectablePtrHook;
    private Hook<TreeNodeExStrDelegate>? treeNodeExStrHook;
    private Hook<TreeNodeExStrStrDelegate>? treeNodeExStrStrHook;
    private Hook<TreeNodeExPtrDelegate>? treeNodeExPtrHook;
    private Hook<TableNextColumnDelegate>? tableNextColumnHook;

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    public ImGuiHookManager(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Initialize()
    {
        try
        {
            var moduleName = "cimgui.dll";
            var moduleHandle = GetModuleHandle(moduleName);
            if (moduleHandle == IntPtr.Zero)
            {
                var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                    .FirstOrDefault(m => m.ModuleName.Contains("cimgui", StringComparison.OrdinalIgnoreCase));
                if (module != null)
                {
                    moduleHandle = module.BaseAddress;
                }
            }

            if (moduleHandle == IntPtr.Zero)
            {
                Plugin.ChatGui.PrintError("[GPM] Could not find cimgui module handle for hooking.");
                return;
            }

            var igButtonAddr = GetProcAddress(moduleHandle, "igButton");
            var igButtonExAddr = GetProcAddress(moduleHandle, "igButtonEx");
            var igBeginAddr = GetProcAddress(moduleHandle, "igBegin");
            var igEndAddr = GetProcAddress(moduleHandle, "igEnd");
            var igSelectableAddr = GetProcAddress(moduleHandle, "igSelectable_Bool");
            var igSelectablePtrAddr = GetProcAddress(moduleHandle, "igSelectable_BoolPtr");
            var igTreeNodeExStrAddr = GetProcAddress(moduleHandle, "igTreeNodeEx_Str");
            var igTreeNodeExStrStrAddr = GetProcAddress(moduleHandle, "igTreeNodeEx_StrStr");
            var igTreeNodeExPtrAddr = GetProcAddress(moduleHandle, "igTreeNodeEx_Ptr");

            if (igButtonAddr == IntPtr.Zero) Plugin.ChatGui.PrintError("[GPM] Failed to resolve address for igButton.");
            if (igButtonExAddr == IntPtr.Zero) Plugin.ChatGui.PrintError("[GPM] Failed to resolve address for igButtonEx.");
            if (igBeginAddr == IntPtr.Zero) Plugin.ChatGui.PrintError("[GPM] Failed to resolve address for igBegin.");
            if (igEndAddr == IntPtr.Zero) Plugin.ChatGui.PrintError("[GPM] Failed to resolve address for igEnd.");
            if (igSelectableAddr == IntPtr.Zero) Plugin.ChatGui.PrintError("[GPM] Failed to resolve address for igSelectable_Bool.");
            if (igSelectablePtrAddr == IntPtr.Zero) Plugin.ChatGui.PrintError("[GPM] Failed to resolve address for igSelectable_BoolPtr.");

            if (igButtonAddr != IntPtr.Zero)
            {
                buttonHook = Plugin.GameInteropProvider.HookFromAddress<ButtonDelegate>(igButtonAddr, ButtonDetour);
                buttonHook.Enable();
            }

            if (igButtonExAddr != IntPtr.Zero)
            {
                buttonExHook = Plugin.GameInteropProvider.HookFromAddress<ButtonExDelegate>(igButtonExAddr, ButtonExDetour);
                buttonExHook.Enable();
            }

            if (igBeginAddr != IntPtr.Zero)
            {
                beginHook = Plugin.GameInteropProvider.HookFromAddress<BeginDelegate>(igBeginAddr, BeginDetour);
                beginHook.Enable();
            }

            if (igEndAddr != IntPtr.Zero)
            {
                endHook = Plugin.GameInteropProvider.HookFromAddress<EndDelegate>(igEndAddr, EndDetour);
                endHook.Enable();
            }

            if (igSelectableAddr != IntPtr.Zero)
            {
                selectableHook = Plugin.GameInteropProvider.HookFromAddress<SelectableDelegate>(igSelectableAddr, SelectableDetour);
                selectableHook.Enable();
            }

            if (igSelectablePtrAddr != IntPtr.Zero)
            {
                selectablePtrHook = Plugin.GameInteropProvider.HookFromAddress<SelectablePtrDelegate>(igSelectablePtrAddr, SelectablePtrDetour);
                selectablePtrHook.Enable();
            }

            if (igTreeNodeExStrAddr != IntPtr.Zero)
            {
                treeNodeExStrHook = Plugin.GameInteropProvider.HookFromAddress<TreeNodeExStrDelegate>(igTreeNodeExStrAddr, TreeNodeExStrDetour);
                treeNodeExStrHook.Enable();
            }

            if (igTreeNodeExStrStrAddr != IntPtr.Zero)
            {
                treeNodeExStrStrHook = Plugin.GameInteropProvider.HookFromAddress<TreeNodeExStrStrDelegate>(igTreeNodeExStrStrAddr, TreeNodeExStrStrDetour);
                treeNodeExStrStrHook.Enable();
            }

            if (igTreeNodeExPtrAddr != IntPtr.Zero)
            {
                treeNodeExPtrHook = Plugin.GameInteropProvider.HookFromAddress<TreeNodeExPtrDelegate>(igTreeNodeExPtrAddr, TreeNodeExPtrDetour);
                treeNodeExPtrHook.Enable();
            }

            var igTableNextColumnAddr = GetProcAddress(moduleHandle, "igTableNextColumn");
            if (igTableNextColumnAddr != IntPtr.Zero)
            {
                tableNextColumnHook = Plugin.GameInteropProvider.HookFromAddress<TableNextColumnDelegate>(igTableNextColumnAddr, TableNextColumnDetour);
                tableNextColumnHook.Enable();
            }
        }
        catch (Exception ex)
        {
            Plugin.ChatGui.PrintError($"[GPM] Failed to initialize ImGui native hooks: {ex.Message}");
        }
    }

    public void Dispose()
    {
        buttonHook?.Dispose();
        buttonHook = null;
        buttonExHook?.Dispose();
        buttonExHook = null;
        beginHook?.Dispose();
        beginHook = null;
        endHook?.Dispose();
        endHook = null;
        selectableHook?.Dispose();
        selectableHook = null;
        selectablePtrHook?.Dispose();
        selectablePtrHook = null;
        treeNodeExStrHook?.Dispose();
        treeNodeExStrHook = null;
        treeNodeExStrStrHook?.Dispose();
        treeNodeExStrStrHook = null;
        treeNodeExPtrHook?.Dispose();
        treeNodeExPtrHook = null;
        tableNextColumnHook?.Dispose();
        tableNextColumnHook = null;
    }

    private byte ButtonDetour(IntPtr labelPtr, Vector2 size)
    {
        var label = GetUtf8String(labelPtr);

        try
        {
            if (!string.IsNullOrEmpty(label))
            {
                plugin.OnButtonDraw(label);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error in ButtonDetour: {ex}");
        }

        var result = buttonHook != null ? buttonHook.Original(labelPtr, size) : (byte)0;

        try
        {
            if (!string.IsNullOrEmpty(label))
            {
                plugin.OnButtonDrawAfter(label);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error in ButtonDetourAfter: {ex}");
        }

        return result;
    }

    private byte ButtonExDetour(IntPtr labelPtr, Vector2 size, int flags)
    {
        var label = GetUtf8String(labelPtr);

        try
        {
            if (!string.IsNullOrEmpty(label))
            {
                plugin.OnButtonDraw(label);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error in ButtonExDetour: {ex}");
        }

        var result = buttonExHook != null ? buttonExHook.Original(labelPtr, size, flags) : (byte)0;

        try
        {
            if (!string.IsNullOrEmpty(label))
            {
                plugin.OnButtonDrawAfter(label);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error in ButtonExDetourAfter: {ex}");
        }

        return result;
    }

    private byte BeginDetour(IntPtr namePtr, IntPtr p_open, int flags)
    {
        var name = GetUtf8String(namePtr);
        try
        {
            plugin.OnBeginWindow(name);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error in BeginDetour: {ex}");
        }
        return beginHook != null ? beginHook.Original(namePtr, p_open, flags) : (byte)0;
    }

    private void EndDetour()
    {
        endHook?.Original();
        try
        {
            plugin.OnEndWindow();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error in EndDetour: {ex}");
        }
    }

    private byte SelectableDetour(IntPtr labelPtr, byte selected, int flags, Vector2 size)
    {
        var result = selectableHook != null ? selectableHook.Original(labelPtr, selected, flags, size) : (byte)0;
        try
        {
            var label = GetUtf8String(labelPtr);
            if (!string.IsNullOrEmpty(label))
            {
                bool isSelected = (selected != 0) || (result != 0);
                plugin.OnSelectableDraw(label, isSelected);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error in SelectableDetour: {ex}");
        }

        return result;
    }

    private byte SelectablePtrDetour(IntPtr labelPtr, IntPtr p_selected, int flags, Vector2 size)
    {
        var result = selectablePtrHook != null ? selectablePtrHook.Original(labelPtr, p_selected, flags, size) : (byte)0;
        try
        {
            var label = GetUtf8String(labelPtr);
            if (!string.IsNullOrEmpty(label))
            {
                bool selected = p_selected != IntPtr.Zero && Marshal.ReadByte(p_selected) != 0;
                bool isSelected = selected || (result != 0);
                plugin.OnSelectableDraw(label, isSelected);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error in SelectablePtrDetour: {ex}");
        }

        return result;
    }

    private byte TreeNodeExStrDetour(IntPtr labelPtr, int flags)
    {
        var result = treeNodeExStrHook != null ? treeNodeExStrHook.Original(labelPtr, flags) : (byte)0;
        try
        {
            var label = GetUtf8String(labelPtr);
            if (!string.IsNullOrEmpty(label))
            {
                bool selected = (flags & 1) != 0;
                bool isLeaf = (flags & 256) != 0;
                plugin.OnTreeNodeDraw(label, selected, isLeaf);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error in TreeNodeExStrDetour: {ex}");
        }
        return result;
    }

    private byte TreeNodeExStrStrDetour(IntPtr strIdPtr, int flags, IntPtr fmtPtr)
    {
        var result = treeNodeExStrStrHook != null ? treeNodeExStrStrHook.Original(strIdPtr, flags, fmtPtr) : (byte)0;
        try
        {
            var label = GetUtf8String(fmtPtr);
            if (!string.IsNullOrEmpty(label))
            {
                bool selected = (flags & 1) != 0;
                bool isLeaf = (flags & 256) != 0;
                plugin.OnTreeNodeDraw(label, selected, isLeaf);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error in TreeNodeExStrStrDetour: {ex}");
        }
        return result;
    }

    private byte TreeNodeExPtrDetour(IntPtr ptrId, int flags, IntPtr fmtPtr)
    {
        var result = treeNodeExPtrHook != null ? treeNodeExPtrHook.Original(ptrId, flags, fmtPtr) : (byte)0;
        try
        {
            var label = GetUtf8String(fmtPtr);
            if (!string.IsNullOrEmpty(label))
            {
                bool selected = (flags & 1) != 0;
                bool isLeaf = (flags & 256) != 0;
                plugin.OnTreeNodeDraw(label, selected, isLeaf);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error in TreeNodeExPtrDetour: {ex}");
        }
        return result;
    }

    private byte TableNextColumnDetour()
    {
        try
        {
            plugin.CheckAndDrawDeferredUI();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Error in TableNextColumnDetour: {ex}");
        }
        return tableNextColumnHook != null ? tableNextColumnHook.Original() : (byte)0;
    }

    private static string GetUtf8String(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return string.Empty;
        int len = 0;
        while (Marshal.ReadByte(ptr, len) != 0) len++;
        byte[] buffer = new byte[len];
        Marshal.Copy(ptr, buffer, 0, len);
        return System.Text.Encoding.UTF8.GetString(buffer);
    }
}
