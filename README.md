# Pipes

A remake of the classic Windows 3D Pipes screensaver, touched up.

## What's new compared to the original

- **Smooth growth:** pipe heads extend continuously instead of snapping cell to cell.
- **Real elbows:** bends can be proper curved pipe (a quarter torus), classic ball joints, or a mix.
- **Fittings:** occasional valves (with handwheels), couplings, bolted flanges, and tee junctions that split off a
  branch pipe. And, like the original, the very rare teapot.
- **Variety:** pipes come in several thicknesses, and the Mixed finish gives each pipe its own material (glossy or
  satin plastic, polished or brushed metal).
- **Modern shading:** HDR lighting, two lights plus sky ambient, Fresnel reflections, ACES tonemapping,
  up to 8x MSAA, depth fog, vignette and dithering (no banding).
- **Effects:** screen-space ambient occlusion (soft contact shadows), subtle bloom, and optional depth of field.
- **Scene flow:** pipes have a length budget so every scene gets a variety of colours. When a scene is done it
  holds for a moment, fades out, and restarts from a new angle. The camera can stay still, orbit, or float.
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

To try settings without touching your real ones, point `PIPES_SETTINGS` at another JSON file:

```powershell
$env:PIPES_SETTINGS = "$PWD\test-settings.json"
Pipes.exe /shot test.png
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
  Scene.cs                scene lifecycle: fade in, grow, hold, fade out; camera motion
  PipesSettings.cs        settings model + JSON persistence
  ConfigForm.cs           settings dialog (WinForms, code-only)
  Simulation/
    PipeWorld.cs          grid walk rules, bends, fittings, tees, colours, length budget
    Pieces.cs             the drawable pieces the simulation hands to the renderer
  Rendering/
    PipeRenderer.cs       instanced drawing and the chain of effect passes
    Shaders.cs            all GLSL: pipe shading, SSAO, bloom, depth of field, tonemapping
    MeshBuilder.cs        cylinder, sphere, torus and teapot meshes
    Camera.cs             view/projection, fog and focus
  Native/Win32.cs         P/Invoke declarations
scripts/publish.ps1       single-file publish -> Pipes.scr (optionally install)
docs/                     how it all works (start with ARCHITECTURE.md)
```

Rendering uses [Silk.NET.OpenGL](https://github.com/dotnet/Silk.NET) bindings on a hand-made WGL context.
There's no windowing library because preview mode must parent into a foreign HWND.

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): how the program is put together, from `Main` to a frame on screen,
  and how the pipe simulation works.
- [docs/RENDERING.md](docs/RENDERING.md): the graphics techniques, pass by pass (instancing, shading, HDR, SSAO,
  bloom, depth of field, tonemapping).
- [docs/ROADMAP.md](docs/ROADMAP.md): ideas and plans, including the fly-through tunnel mode.

## License

[MIT](LICENSE). Use it, change it, share it.
