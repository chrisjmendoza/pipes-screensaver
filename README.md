# Pipes

A remake of the classic Windows 3D Pipes screensaver, touched up.

## What's new compared to the original

- **Smooth growth:** pipe heads extend continuously instead of snapping cell to cell.
- **Real elbows:** bends can be proper curved pipe (a quarter torus), classic ball joints, or a mix.
- **Fittings:** occasional valves (with handwheels), couplings, bolted flanges, and tee junctions that split off a
  branch pipe. And, like the original, the very rare teapot.
- **Variety:** pipes come in several thicknesses and surfaces: glossy or satin paint, polished or brushed metal,
  and a Weathered finish with chipped paint, rust, copper gone green, galvanised steel and cast iron. The Mixed
  finish gives each pipe its own. The surfaces are procedural (computed per pixel from noise, no textures), so the
  wear on each pipe is different. Untick **Surface detail** for the original clean look: flat colours, plastic
  highlights.
- **Modern shading:** HDR lighting, two lights plus sky ambient, GGX highlights (stretched along brushed metal),
  Fresnel reflections, ACES tonemapping, up to 8x MSAA, depth fog, vignette and dithering (no banding).
- **Effects:** shadows from the key light (soft-edged, and dappled inside the tunnel; optionally the light slowly
  circles the scene so the shadows sweep across the pipes), screen-space ambient
  occlusion (soft contact shadows), subtle bloom, and optional depth of field (before take-off only, in
  fly-through). Optional **traced reflections**: metal pipes mirror the pipes around them, with reflection rays
  traced through a grid of the scene in the pipe shader (no ray-tracing hardware needed).
- **Scene flow:** pipes have a length budget so every scene gets a variety of colours. When a scene is done it
  holds for a moment, fades out, and restarts from a new angle. The camera can stay still, orbit, or float.
- **Fly through the pipes:** a camera mode where, as the scene finishes building, the camera takes off into it and flies on
  through an endless tunnel of pipes growing ahead of it. The tunnel mixes tight turns, long sweeping bends,
  snaking meanders and corkscrews, and its pipes flow with or against the flight. The camera banks like a plane
  flown by a good (not perfect) pilot, with momentum: it rolls into left and right turns, pulls straight up into
  climbs, rolls onto its back to pull into dives, and barrel-rolls through corkscrews. With no horizon, it flies
  like a spaceship: whichever way up a maneuver leaves it becomes the new level. Flight speed is adjustable (and
  the pilot can vary it), a course complexity slider goes from zen cruising to wild, maneuver after maneuver, and a
  tunnel density slider sets how full the walls are (the setting to turn down on a slower GPU).
- **Classic (lite) mode:** for nostalgia, or a slower PC. It renders like the 1990s original: low-poly pipes lit
  per vertex on a black background, no effects. It uses roughly a tenth of the GPU time of the modern style.
- **Multi-monitor aware:** each monitor gets its own scene, framed for its shape (portrait monitors too), and
  nothing is rendered in the gaps between monitors. Or, if you prefer, one scene spanning them all.
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
Pipes.exe /shot out.png [seconds=20] [width=1920] [height=1080] [seed=1] [monitors]
```

Add `monitors` to render your whole desktop exactly as fullscreen would lay it out, one scene per monitor.

To measure how expensive a settings combination is, `/bench` warms the GPU up for a second and a half, then
renders a few hundred frames offscreen (no VSync) and writes the average time per frame to a text file:

```powershell
Pipes.exe /bench bench.txt [frames=300] [width=1920] [height=1080] [monitors]
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
| `/shot <png> [s] [w] [h] [seed] [monitors]` | Offscreen still (dev) |
| `/bench <txt> [frames] [w] [h] [monitors]` | Time rendering, write ms/frame (dev) |

Settings are stored at `%LOCALAPPDATA%\PipesScreensaver\settings.json`.

## Layout

```
src/Pipes/
  Program.cs              argument parsing, mode dispatch
  GLHost.cs               bare Win32 window + WGL context (fullscreen / preview / windowed / offscreen), monitor layout
  View.cs                 one scene + renderer drawn into one rectangle (one per monitor in fullscreen)
  Scene.cs                scene lifecycle (fade in, grow, hold, fly, fade out); camera motion
  PipesSettings.cs        settings model + JSON persistence
  ConfigForm.cs           settings dialog (WinForms, code-only)
  Simulation/
    PipeWorld.cs          grid walk rules, bends, fittings, tees, colours, length budget, chunks
    PipeSpace.cs          where pipes may grow: the IPipeSpace interface and the classic box
    TunnelSpace.cs        fly-through's space: box + clear corridor + endless tunnel wall
    FlightPath.cs         the camera's endless route: straights, turns, sweeps, meanders, corkscrews
    Pieces.cs             the drawable pieces the simulation hands to the renderer
  Rendering/
    PipeRenderer.cs       instanced drawing and the chain of effect passes
    Shaders.cs            all GLSL: pipe shading and surfaces, traced reflections, shadows, SSAO, bloom, DoF, tonemapping
    ReflectionGrid.cs     the scene filed into a grid each frame, for reflection rays to walk (traced reflections)
    MeshBuilder.cs        cylinder, sphere, torus and teapot meshes
    Camera.cs             view/projection, fog, focus and the shadow region
  Native/Win32.cs         P/Invoke declarations
  Native/VBlankWaiter.cs  sleeps until the monitor's refresh, so the driver never spins a CPU core waiting
  Pipes.ico               app icon (exe/scr, settings dialog, GL window); generated, see scripts/make_icon.py
scripts/publish.ps1       single-file publish -> Pipes.scr (optionally install)
scripts/make_icon.py      generates src/Pipes/Pipes.ico procedurally (re-run after changing it)
docs/                     how it all works (start with ARCHITECTURE.md)
```

Rendering uses [Silk.NET.OpenGL](https://github.com/dotnet/Silk.NET) bindings on a hand-made WGL context.
There's no windowing library because preview mode must parent into a foreign HWND.

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): how the program is put together, from `Main` to a frame on screen,
  and how the pipe simulation works.
- [docs/RENDERING.md](docs/RENDERING.md): the graphics techniques, pass by pass (instancing, shading, surfaces,
  traced reflections, shadows, HDR, SSAO, bloom, depth of field, tonemapping).
- [docs/ROADMAP.md](docs/ROADMAP.md): ideas, known issues, and what's done.
- [CHANGELOG.md](CHANGELOG.md): notable user-visible changes, by date.

## License

[MIT](LICENSE). Use it, change it, share it.
