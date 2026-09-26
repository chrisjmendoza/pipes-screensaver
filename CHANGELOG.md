# Changelog

Notable user-visible changes to Pipes: new features, settings, look, performance and fixes. Internal
refactors and documentation-only changes aren't listed here. Format based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/); versions follow [Semantic Versioning](https://semver.org/).
Each version has a [GitHub release](https://github.com/chrisjmendoza/pipes-screensaver/releases) with `Pipes.scr`
attached.

## [Unreleased]

Changes merged to `main` since the last release go here, and move under a new version heading when it's released.

### Added

- **Performance stats overlay** (Graphics group: "Show performance stats", off by default; F3 toggles it in the
  windowed preview): a panel in the top-left corner of the main monitor with FPS, a live frame-time graph (frame
  interval, GPU and CPU time, and the refresh interval), average FPS, 1% and 0.1% lows and the worst frame over
  the last ten seconds, GPU and CPU time per frame as a share of the refresh interval, the GPU time split by render
  pass (shadows, AO, scene, bloom...), and GPU load, temperature, core and memory clocks, power, VRAM, P-state and
  CPU load where the hardware reports them (NVIDIA through NVML; other GPUs get load from Windows' counters). It
  names the GPU OpenGL is drawing on and warns when a laptop is using its integrated GPU while the NVIDIA one sits
  idle. Costs about 0.05 ms of GPU time a frame. (docs/ARCHITECTURE.md, "Stats overlay")
- **Quality presets:** a Quality dropdown at the top of the Graphics group (Lite, Low, Medium, High, Ultra) sets
  the style, anti-aliasing, surface detail, shadows, ambient occlusion, bloom and traced reflections in one go.
  It never changes the pipes, the camera or the other look settings, and shows "Custom" when the options don't
  match a preset. High is the defaults; Ultra adds traced reflections and 8x MSAA; Lite is the classic style.
  (docs/ARCHITECTURE.md, "Quality presets")
- **Test this PC…** button next to it: times each preset for a moment on your real screens, at your monitor's
  refresh rate, and recommends the highest one whose frames (nine in ten) take at most three-quarters of the time
  between refreshes, leaving the rest as headroom. A preset past that mark but still inside the frame budget is
  reported as keeping up with little to spare, not as "too slow" — at 60 Hz, 12.6 ms a frame is about 80 fps and
  misses nothing — and only a preset that can't hold the refresh at all is called too slow. Testing stops there
  too, rather than at the headroom mark, so the levels either side of it still get measured. Shows the results per
  preset and which graphics chip was used, with a hint for laptops that ran it on the integrated chip. The test runs in a separate process, so a graphics driver crash just fails the
  test instead of closing the settings. (docs/ARCHITECTURE.md, "Test this PC")
- `/autotune <result.json>` command behind the button, which writes the measurements as JSON.
- **On battery** dropdown next to Quality: while a laptop runs on its battery, hold the graphics settings down to
  a preset of your choosing, and let go again when it's plugged back in. Off by default. It's a ceiling rather than
  a setting, so running something lighter already means nothing changes, and like any preset it moves only the
  graphics settings — the pipes and the camera stay put. Noticed within two seconds of the lead moving, and applied
  by rebuilding the renderers around the scene that's already on screen. Does nothing on a machine with no battery.
  (docs/ARCHITECTURE.md, "On battery")

## [1.0.0] - 2026-09-26

The first release: everything from the first three days of development, listed below by the day it landed on
`main`. `Pipes.scr` is now built self-contained (the .NET runtime is bundled inside it), so it runs on any 64-bit
Windows 10 or 11 without installing anything, and the file carries its version in Properties > Details.

### 2026-09-26

#### Added

- **Traced reflections** (Graphics group, modern style only, off by default): metal pipes, and paint at grazing
  angles, mirror the pipes around them instead of a fake sky, traced by a ray through a grid of the scene rebuilt
  each frame — no ray-tracing hardware needed. Cost at 1080p: a box scene goes from 0.89 to 1.60 ms a frame, the
  usual fly-through from 1.82 to 3.12 ms, and an all-metal tunnel of smooth elbows from 1.69 to 5.51 ms.
  (docs/RENDERING.md, "Traced reflections")

### 2026-09-25

#### Added

- **Shadows** from the key light: a shadow map with soft edges (dappled inside the fly-through tunnel too). On by
  default in the modern style; costs about 0.2-0.45 ms a frame at 1080p. (docs/RENDERING.md, "Shadows: a shadow
  map")
- **Moving light** setting (modern style, off by default): the key light, fill light and reflected light strips
  slowly circle the scene, so shadows and highlights sweep across the pipes, in flight too. Costs nothing extra,
  since the shadow map is rebuilt every frame regardless. (docs/RENDERING.md, "Shadows: a shadow map")
- **Procedural surfaces** for the modern style: glossy or satin paint, polished or brushed metal, worn and
  chipped paint, rust, copper patina, galvanised spangle and cast iron, computed per pixel from noise rather than
  a flat colour — so wear varies from pipe to pipe. A new **Weathered** finish sits between Metallic and Mixed,
  and Mixed now draws from all of them. Cost in fly-through at 1080p, 4x MSAA: Plastic 2.24 to 2.63 ms, Mixed
  2.14 to 2.83 ms. (docs/RENDERING.md, "Surfaces: procedural texture")
- **Surface detail** checkbox (Graphics group, modern style only, on by default) to turn the procedural surfaces
  back off for the original flat-coloured look; Weathered falls back to Mixed when it's off.
- **Path variety in fly-through:** long sweeping bends and meanders (shallow arcs swinging side to side) join the
  original tight turns, banked by how sharply they curve, and corkscrews that spiral the flight path with a
  barrel-rolling camera (1, 2 or 3 rolls).
- **Pilot feel in fly-through:** turns overbank a few degrees and hold through the corner, lead the roll-out
  before the arc ends, vary a little from turn to turn, and a faint stick wobble keeps straights from looking
  perfectly still; gentle curves swing 8-10 degrees past each reversal instead of tracking exactly.
- **Course complexity** slider (1 zen cruising to 10 wild, renamed from "Flight style"; level 5 matches the
  previous behaviour), a **tunnel density** slider (10-100%, default 60%, sets how full the tunnel walls are —
  the setting to turn down on a slower GPU), and a **"Pilot varies the speed"** option that eases the throttle
  off into tight turns and opens it up on long straights.
- **Reset to defaults** button in the settings dialog.
- Procedurally generated app icon: ray-traced 3D pipes seen from the same diagonal the app's camera uses,
  lit and tonemapped the same way, in the classic screensaver palette.

#### Changed

- Settings dialog relaid out in two columns of groups (Animation and Flight on the left, Pipes and Graphics on
  the right); the Flight group greys out unless the camera flies through.
- Smoother fly-through take-off: the tunnel now builds together with the box from the start instead of pausing
  once the box is done, and the spawn band sweeps ahead to catch up so fast flights no longer leave an unseeded
  gap behind the camera.
- Depth of field now fades out over take-off and is skipped entirely in flight, where it used to blur most of
  the screen (the tunnel walls sit right beside the camera, and a thin lens blurs near things hardest).

#### Fixed

- Pipes turned pale and milky whenever the camera looked along them, or up the tunnel in flight: the fake sky
  they reflect is now dark instead of a bright gradient, so only the studio light strips stay bright.
  (docs/RENDERING.md, "Shading (`PipeFragment`)")
- Cancel button in the settings dialog now actually closes it (it previously relied on a dialog result that only
  applies to dialogs shown modally, and the settings window is the app's main window).

### 2026-09-24

#### Added

- Initial release: OpenGL 3D pipes screensaver remake with smooth pipe growth, HDR shading with ACES tonemapping,
  plastic and metallic finishes, ball joints, multi-monitor fullscreen, a Screen Saver Settings preview,
  and a settings dialog.
- Curved elbows (a real quarter-torus bend), classic ball-jointed corners, or a mix; occasional valves, couplings
  and bolted flanges; tee junctions that split off a branch pipe; the rare teapot easter egg.
- Per-pipe thickness and material (glossy or satin plastic, polished or brushed metal, via a new Mixed finish).
- Screen-space ambient occlusion, bloom, and optional depth of field.
- Camera modes: still, orbit, or float.
- **Classic (lite) style:** renders like the 1990s original — low-poly pipes lit per vertex, black background, no
  effects — at about a tenth of the modern style's GPU cost. Picking it in the dialog also switches in the
  original's pipe options as a starting point. (docs/RENDERING.md, "Classic (lite) style")
- Each monitor now gets its own scene in fullscreen, framed for its shape (portrait monitors too), with nothing
  rendered in the gaps between monitors; new "Own scene on each monitor" setting (on by default). About 15%
  cheaper on a three-monitor desktop than one scene stretched across them. (docs/ARCHITECTURE.md, "Views: a scene
  per monitor")
- **Fly-through camera mode:** as the scene finishes building, the camera dives in and flies on through an
  endless, self-building tunnel of pipes, turning every so often and fading into a new scene after 150 seconds.
  (docs/ARCHITECTURE.md, "Fly-through: an endless tunnel")
- The fly-through camera banks like a plane through turns: rolls into left and right turns, pulls straight up
  into climbs, rolls onto its back to pull into dives. With no horizon, whichever way up a maneuver leaves it
  becomes the new level, so subsequent turns don't "correct" back to upright.
- **Flight speed** setting (1-20 cells/s, default 5); pipe count, spawn distance, fog and the far plane all scale
  with it automatically.
- Roll momentum: the camera swings a little past each bank and past level, then eases back like a pendulum.
- MIT license.
- `/bench` developer command, for measuring how expensive a settings combination is (renders offscreen and
  writes the average time per frame to a text file).

#### Changed

- Raised limits: up to 20 pipes growing at once, 200 per scene, and growth speed up to 80.
- Settings dropdowns now size themselves to their longest item instead of clipping it (e.g. "Classic (lite, like
  the original)").

#### Fixed

- Window title showed only "P" (the window procedure was bound to the ANSI entry points while the window itself
  used Unicode, so the title was read back as a one-character string).

#### Performance

- Frames are now paced by sleeping until the monitor's vblank instead of letting the graphics driver spin a CPU
  core while waiting for VSync: CPU use dropped from as much as a full core to a steady 7-15% at 60 fps.
  (docs/ARCHITECTURE.md, "Frame pacing: sleeping, not spinning")

[Unreleased]: https://github.com/chrisjmendoza/pipes-screensaver/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/chrisjmendoza/pipes-screensaver/releases/tag/v1.0.0
