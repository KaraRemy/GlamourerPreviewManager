# Glamourer Preview Manager (GPM)

A premium companion plugin for FINAL FANTASY XIV (Dalamud) that solves native preview limitations, enabling high-quality character preview cards directly inside your **Glamourer** Design Panel!

---

## Features

- **True GUI Injection**: Renders custom preview images natively inside Glamourer's design details pane (below the action/apply buttons and above the customization list).
- **Reflection-Based Selection**: Resolves the active selected design via runtime reflection on Glamourer's internal assembly and dependency injection container. This provides a 100% accurate source of truth for the active design and is immune to folder structures or name collisions.
- **Stable ImGui Hooks**: Uses a custom post-draw button detour on native `cimgui.dll` calls (`igButton`) to target Glamourer's details layout reliably regardless of screen height, scrolling, or header expansion states.
- **Vertical/Portrait Capture Support**: Specifically optimized for vertical character screenshots. Defaults to a **9:16 Aspect Ratio** (portrait), with options for 3:4, 1:1 (square), 4:3, and 16:9 aspect ratios.
- **Cropped Screenshot Overlay**: Features a camera overlay that dims the surrounding screen, draws a sky-blue glowing crop box, and guides you to take a screenshot using `[Space]` or exit with `[Esc]`.
- **4K & High-DPI Calibration**: Built-in settings sliders for Box Scale and X/Y offsets, allowing users on 4K or custom high-DPI displays to calibrate the capture window bounds perfectly.
- **Auto-Apply on Screenshot**: An optional settings toggle that programmatically runs Glamourer's slash command `/glamour apply [design] | <me>` when triggering the screenshot button, instantly dressing your character before the screenshot is taken.
- **Rediscover Previews**: A settings action that scans the previews folder, automatically strips filename suffixes (like copy counters ` (1).png`), and maps existing images back to active Glamourer designs in one click.
- **Import Options Panel**: Supports pasting images directly from your clipboard (e.g. from ShareX/Snipping Tool), browsing local files, or taking an in-game cropped screenshot.
- **Centralized Allocation**: Saves the mapping manifest (`allocation.json`) in the central plugin folder (keeping your preview directory clean of configuration files), with automatic migration path support.
- **Dynamic Resizeable UI**: Fully resizeable settings window with scrollbar support and custom min-size constraints (`450x300`) to match your game client resolution.
- **Middle-Click Zoom**: Hold **Middle-Click** over a preview image to trigger a full-screen magnifier overlay, complete with a custom Zoom Scale slider in configurations.

---

## Installation

To install the **Glamourer Preview Manager** using my custom repository:

1. Launch FINAL FANTASY XIV and open Dalamud Settings using `/xlsettings` in chat.
2. Navigate to the **Experimental** tab.
3. Under **Custom Plugin Repositories**, paste the following URL:
   `https://raw.githubusercontent.com/KaraRemy/TomokPlugins/main/pluginmaster.json`
4. Click the **`+`** button to add the repository, then click **Save and Close**.
5. Open the Plugin Installer using `/xlplugins` in chat.
6. Search for **Glamourer Preview Manager** and click **Install**!

---

## Support & Community

If you need help, want to report a bug, or suggest new features, join my support Discord server:
📢 **[Join the Support Discord](https://discord.gg/PvxW4mXaWp)**

---

## FAQ & Troubleshooting

For common questions, performance guidelines, and best practices regarding preview management, check the official FAQ:
📖 **[Read the FAQ](https://github.com/KaraRemy/TomokPlugins/blob/main/FAQ.md)**

---

## Developer / Building Instructions

If you wish to build the source code manually:

### Prerequisites
- .NET 8.0 SDK or higher.
- FINAL FANTASY XIV, XIVLauncher, and Dalamud installed to default directories.

### Steps
1. Open the solution file `GlamourerPreviewManager.sln` in your C# IDE of choice (e.g. Visual Studio, Rider).
2. Set configuration to `Release` and build the project.
3. The packaged plugin `.zip` folder will be automatically generated at:
   `GlamourerPreviewManager/bin/x64/Release/GlamourerPreviewManager/latest.zip`

---

## AI Disclosure / Collaboration Note

> [!NOTE]
> This plugin was co-authored, coded, and polished with the assistance of agentic AI coding assistants (Google DeepMind's Antigravity). All design aesthetics, custom reflection selection layers, and coordinate calibration frameworks were developed through collaborative pair programming.
