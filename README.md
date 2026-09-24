# Pipes

A remake of the classic Windows 3D Pipes screensaver, touched up.

## What's new compared to the original

- **Smooth growth:** pipe heads extend continuously instead of snapping cell to cell.
- **Modern shading:** HDR lighting, two lights plus sky ambient, Fresnel reflections, ACES tonemapping,
  4x MSAA, depth fog, vignette and dithering (no banding).
- **Finishes:** glossy plastic (closest to the original) or metallic with studio-style reflections.
- **Joints:** classic ball joints, smooth elbows, or mixed.
- **Scene flow:** pipes have a length budget so every scene gets a variety of colours. When a scene is done it
  holds for a moment, fades out, and restarts from a new angle. The camera slowly orbits (optional).
- **Proper screensaver:** fullscreen across all monitors, per-monitor high-DPI, works in the Screen Saver
  Settings preview box, and has a settings dialog.

## Build & try

Requires the .NET 10 SDK.

```powershell
dotnet build src/Pipes
.\src\Pipes\bin\Debug\net10.0-windows\Pipes.exe /w      # windowed, Esc to quit
.\src\Pipes\bin\Debug\net10.0-windows\Pipes.exe /s      # fullscreen, any input quits
.\src\Pipes\bin\Debug\net10.0-windows\Pipes.exe         # settings dialog
```

Render a still offscreen (no window appears). Handy for checking visual changes:

```powershell
Pipes.exe /shot out.png [seconds=20] [width=1920] [height=1080] [seed=1]
```

## Install as your screensaver

```powershell
.\scripts\publish.ps1            # -> publish\Pipes.scr
```

Then either right-click `publish\Pipes.scr` and choose **Install**, or run `.\scripts\publish.ps1 -Install` from
an elevated PowerShell. That copies it to System32 so "Pipes" appears in the Screen Saver Settings dropdown.

## Command line (standard screensaver contract)

| Args | Meaning |
|---|---|
| `/s` | Run fullscreen |
| `/p <hwnd>` | Draw inside the Screen Saver Settings preview |
| `/c[:hwnd]` or none | Settings dialog |
| `/w` | Windowed (dev) |
| `/shot <png> [s] [w] [h] [seed]` | Offscreen still (dev) |

Settings are stored at `%LOCALAPPDATA%\PipesScreensaver\settings.json`.

## Layout

```
src/Pipes/
  Program.cs              argument parsing, mode dispatch
  GLHost.cs               bare Win32 window + WGL context (fullscreen / preview / windowed / offscreen)
  Scene.cs                scene lifecycle: fade in, grow, hold, fade out; camera placement and drift
  PipesSettings.cs        settings model + JSON persistence
  ConfigForm.cs           settings dialog (WinForms, code-only)
  Simulation/PipeWorld.cs grid walk rules, joints, colours, length budget
  Rendering/              meshes, GLSL shaders, instanced renderer with MSAA + HDR post pass, camera
  Native/Win32.cs         P/Invoke declarations
scripts/publish.ps1       single-file publish -> Pipes.scr (optionally install)
```

Rendering uses [Silk.NET.OpenGL](https://github.com/dotnet/Silk.NET) bindings on a hand-made WGL context.
There's no windowing library because preview mode must parent into a foreign HWND.

## Ideas

- The original's rare **Utah teapot** joint easter egg
- Textured/"flex" pipes, and a "multiple pipes per colour" mode
- Separate scenes per monitor instead of one spanning scene
- Optional bloom on metallic highlights
