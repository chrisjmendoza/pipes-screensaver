# Architecture

This is a tour of how the screensaver is put together. It follows the path from launch to a frame on screen, then
goes deeper into the pipe simulation. The graphics side has its own guide, [RENDERING.md](RENDERING.md).

## The big picture

```
Program.Main ──► parses /s /p /c /w /shot
     │
     ├─ /c ──────► ConfigForm (WinForms settings dialog)
     │
     └─ /s /p /w ─► GLHost.Run ─── loop every frame ───┐
                                                        │
                    ┌───────────────────────────────────┘
                    ▼
                 Scene.Update(dt)                        what to draw
                    ├─ PipeWorld.Update(dt)              grow pipes (simulation)
                    ├─ camera motion                     where to look from
                    └─ PipeWorld.Collect(Pieces)         list every piece to draw
                    ▼
                 PipeRenderer.Render(camera, pieces)     how to draw it (OpenGL)
                    ▼
                 SwapBuffers                             show it
```

There are three layers, and each only talks to the one below it:

| Layer | Files | Knows about |
|---|---|---|
| Host | `Program.cs`, `GLHost.cs`, `Native/Win32.cs` | Windows: windows, messages, the OpenGL context, screensaver command-line rules |
| Scene | `Scene.cs`, `Simulation/*` | Pipes, the grid, the camera. **No OpenGL at all.** |
| Rendering | `Rendering/*` | OpenGL, shaders, meshes. Knows nothing about grids or pipe rules. |

The simulation produces plain data (`PieceLists`: lists of cylinders, spheres, elbows...). The renderer consumes
that data. Because neither depends on the other's internals, you can change pipe behaviour without touching
graphics code, and the other way round.

## Host: talking to Windows

A Windows screensaver is just an `.exe` renamed to `.scr`. Windows launches it with a flag:

- `/s`: run fullscreen. We make one borderless topmost window spanning the whole virtual desktop, so every monitor
  is covered by a single scene.
- `/p <hwnd>`: draw into the little monitor picture in the Screen Saver Settings dialog. `<hwnd>` is that dialog's
  window handle, and our window becomes a **child** of it. This is why the project has no windowing library
  (GLFW, SDL...): they can't create a child window inside another program's window.
- `/c`: show settings.

`GLHost` creates the window with raw Win32 calls (`CreateWindowEx`), then an OpenGL context on it with WGL, the
Windows-specific OpenGL setup API. Silk.NET then loads the OpenGL function pointers from that context.

The frame loop in `GLHost.Run` is:

1. Drain the Windows message queue (`PeekMessage`/`DispatchMessage`). Mouse moves and key presses arrive here,
   and in fullscreen mode any of them ends the screensaver.
2. Measure `dt`, the seconds since the last frame. It's clamped to 0.1s so a hitch (e.g. the PC waking up) doesn't
   make the pipes jump.
3. `scene.Update(dt)`, then `renderer.Render(...)`.
4. `SwapBuffers`. VSync is on, so this waits for the monitor's refresh. That's what paces the loop: no busy-waiting.

> **A bug worth learning from:** the window title used to show just "P". Win32 functions come in two flavours,
> `...A` (ANSI, 1 byte per character) and `...W` (Unicode, UTF-16). The window was created with the W version,
> but `DefWindowProc` was declared without a `CharSet`, so .NET picked the A version. The A version read "Pipes"
> in UTF-16 (`P\0i\0p\0...`) as a byte string and stopped at the first zero byte. The fix is to declare every
> text-handling function with `CharSet = CharSet.Unicode`. See the comment in `Native/Win32.cs`.

## Scene: the life of one pipe world

`Scene` runs a small state machine:

```
FadeIn ──► Growing ──► Hold ──► FadeOut ──► (new world) FadeIn ...
```

Each new world is a fresh `PipeWorld` with a grid sized to the screen's aspect ratio (always 12 cells tall), and a
new random camera angle. `Scene` also owns camera motion:

- **Still:** fixed.
- **Orbit:** yaw (the left/right angle) increases slowly.
- **Float:** orbit, plus a few slow sine waves on pitch, distance and target point. The waves have unrelated
  speeds (0.21, 0.13, 0.11, 0.17), so their combination doesn't visibly repeat and reads as organic drifting.

## Simulation: how pipes grow

`Simulation/PipeWorld.cs` holds the rules. The world is a 3D grid of cells, one unit apart, plus an
`_occupied[x,y,z]` array.

### Steps run face to face

A pipe's head moves through **one cell per step**, entering through the middle of one face and leaving through the
middle of another. So every cell a pipe passes through is one of a few "tiles":

| Step | Path through the cell | Length |
|---|---|---|
| `Start` | centre → exit face (the spawn cell) | 0.5 |
| `Straight` | face → opposite face | 1 |
| `Bend` | face → side face, along a quarter circle | π/4 ≈ 0.785 |
| `Knee` | face → centre → side face (a sharp corner under a ball joint or teapot) | 1 |
| `End` | face → centre, then an end cap | 0.5 |

This is what makes curved elbows possible. A quarter circle of radius 0.5 fits exactly between the middle of
one face and the middle of an adjacent face. With centre-to-centre steps, as in the original, there's nowhere to
put the curve.

Growth speed is measured in **world units per second**. Each frame, `pipe.Travel += dt * speed`, and when `Travel`
passes the step's length, the step completes. So a bend (shorter than a straight) takes proportionally less time,
and the head moves at a steady speed. At high speeds, one frame can complete several steps, which is why that's a
`while` loop.

### Decide on entry, reserve ahead

When the head enters a cell, the pipe immediately decides how it will leave (`BeginStep`):

1. Out of length budget? → `End`.
2. Is the cell ahead blocked, or did a 22% random roll say "turn"? → pick a free side direction and make a `Bend`
   or `Knee` (per the Joints setting). Occasionally (1 in 300) the knee becomes a teapot.
3. Otherwise → `Straight`, maybe with a fitting (valve, coupling, flange) or a tee.

Whatever it chose, it **reserves** the neighbouring cell it will move into. Reserving ahead is what guarantees two
pipes never grow into the same cell: by the time pipe A starts moving towards a cell, pipe B already sees it as
occupied.

### Why all randomness happens in `BeginStep`

`EmitStep` (which builds the geometry for the current step) runs **every frame**, drawing the partly grown step at
its current progress. If it rolled random numbers, a valve would flicker between shapes 60 times a second. So every
random decision about a cell (turn direction, which fitting, which way a valve wheel faces) is made once, in
`BeginStep`, and stored on the `Pipe` object. `EmitStep` only reads those fields. This split between *deciding*
and *drawing* comes up constantly in games and simulations.

### Finished vs. growing geometry

When a step completes, its geometry is added to `_finished`, a `PieceLists` that only ever grows. Each frame,
`Collect` copies `_finished` and then appends the partial step of each live pipe, plus a small sphere on each head
to keep the growing end rounded. Nothing is rebuilt from scratch.

### Tees and branches

A tee reserves a side cell as well as the cell ahead. When the tee step completes, `Branch()` creates a new
`Pipe` starting with a `Start` step in that side direction, with the same colour and thickness. It's appended to
`_active`, so it starts growing on the next frame. Branches count towards "pipes per scene".

## Pieces: the hand-off to the renderer

`Simulation/Pieces.cs` defines the contract between simulation and renderer:

- `MeshKind`: `Cylinder`, `Sphere`, `Elbow`, `Ring`, `Teapot`. One GPU mesh each.
- `PipeInstance`: one placed piece. Position, two direction vectors, colour, radius, sweep angle, metallic,
  roughness. Exactly 16 floats, which is also the GPU buffer layout.
- `PieceLists`: a list per kind, plus helpers such as `Cylinder(from, to, radius, material)`,
  `Elbow(...)` and `Ring(...)` that fill in a `PipeInstance` correctly.

Fittings are built entirely from these primitives. For example, a valve is a sphere (body), a cylinder (stem), a
ring (handwheel), two thin cylinders (spokes) and a small sphere (hub). No special renderer support needed.

### Walkthrough: adding a new fitting

Say you want a pressure gauge: a small disc on a stem.

1. Add `Gauge` to the `Joint` enum in `PipeWorld.cs`.
2. In `ChooseFitting`, include it in the random pick (e.g. `_rng.Next(4) switch { ..., 3 => Joint.Gauge }`), and
   set `pipe.FittingAxis` for the direction it points.
3. In `EmitJoint`, add a `case Joint.Gauge:` that emits the shapes: a thin cylinder for the stem along
   `p.FittingAxis`, then a short, fat cylinder for the dial.
4. Build and check it with `/shot`. To see it often while testing, temporarily raise `FittingChance`.

## Settings

`PipesSettings` is a plain class serialised to JSON with `System.Text.Json`. A few conventions:

- Every property has a default, so a settings file from an older version still loads. Missing properties just
  keep their defaults.
- `Clamped()` keeps values sane even if someone hand-edits the file.
- Renamed settings get a small compatibility shim (see `LegacyCameraDrift`), so upgrading doesn't reset anyone's
  choice.
- `PIPES_SETTINGS` (environment variable) overrides the file location. Useful for test renders.

`ConfigForm` is the dialog, built in code rather than the WinForms designer. Choosing the Classic style also sets
the pipe options to the original's (ball joints, plastic, one thickness, no fittings, still camera) as a starting
point. That handler is attached *after* the saved settings are loaded into the controls, so opening the dialog
doesn't trigger it. Its dropdown item order matches the
enum order, so `(JointStyle)_joints.SelectedIndex` converts directly.

## Development workflow

- `Pipes.exe /w` for a resizable window.
- `Pipes.exe /shot out.png 12 1600 900 3` simulates 12 seconds at a fixed 60 steps per second with random seed 3,
  then saves one frame. The same seed gives the same picture, so it's great for before/after comparisons.
- For visual debugging, temporarily make a shader output an intermediate value, then render a shot. For example,
  in `PipeFragment`, `FragColor = vec4(vec3(ao), 1.0);` shows the ambient occlusion buffer directly. This is how
  the AO strength was tuned.
- `Pipes.exe /bench bench.txt 300 1920 1080` renders 300 frames offscreen with no VSync and writes the average
  time per frame. `_gl.Finish()` before stopping the clock makes it include the GPU's work, not just the time the
  CPU took to queue commands. Combine it with `PIPES_SETTINGS` to compare settings.
- For intermittent glitches, make them countable. Flag the bad pixels in a shader (e.g. `isnan()` → magenta),
  render a few thousand frames offscreen, and count. See "Bug story: the black boxes" in RENDERING.md.
