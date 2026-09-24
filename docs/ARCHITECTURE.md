# Architecture

This is a tour of how the screensaver is put together. It follows the path from launch to a frame on screen, then
goes deeper into the pipe simulation. The graphics side has its own guide, [RENDERING.md](RENDERING.md).

## The big picture

```
Program.Main ──► parses /s /p /c /w /shot /bench
     │
     ├─ /c ──────► ConfigForm (WinForms settings dialog)
     │
     └─ /s /p /w ─► GLHost.Run ─── loop every frame ───┐
                                                        │
                    ┌───────────────────────────────────┘
                    ▼
                 for each View (one per monitor, or just one):
                    Scene.Update(dt)                     what to draw
                       ├─ PipeWorld.Update(dt)           grow pipes (simulation)
                       ├─ camera motion                  where to look from
                       └─ PipeWorld.Collect(Pieces)      list every piece to draw
                    PipeRenderer.Render(...)             how to draw it (OpenGL), into the view's rectangle
                    ▼
                 SwapBuffers                             show it
```

There are three layers, and each only talks to the one below it:

| Layer | Files | Knows about |
|---|---|---|
| Host | `Program.cs`, `GLHost.cs`, `View.cs`, `Native/Win32.cs` | Windows: windows, messages, monitors, the OpenGL context, screensaver command-line rules |
| Scene | `Scene.cs`, `Simulation/*` | Pipes, the grid, spaces and flight paths, the camera. **No OpenGL at all.** |
| Rendering | `Rendering/*` | OpenGL, shaders, meshes. Knows nothing about grids or pipe rules. |

The simulation produces plain data (`PieceLists`: lists of cylinders, spheres, elbows...). The renderer consumes
that data. Because neither depends on the other's internals, you can change pipe behaviour without touching
graphics code, and the other way round.

## Host: talking to Windows

A Windows screensaver is just an `.exe` renamed to `.scr`. Windows launches it with a flag:

- `/s`: run fullscreen. We make one borderless topmost window covering the whole *virtual desktop* (the smallest
  rectangle around all monitors). By default each monitor then gets its own scene; see *Views* below.
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
3. For each view: update its scene, then render it into its rectangle.
4. `SwapBuffers`. VSync is on, so this waits for the monitor's refresh. That's what paces the loop: no busy-waiting.

### Views: a scene per monitor

A `View` (`View.cs`) is one independent picture: its own `Scene` (grid, pipes, colours, camera, timing) plus its
own `PipeRenderer` (with render targets sized to it), drawn into one rectangle of the window.

- Windowed, preview, and "one scene across all monitors": a single view covers the whole window.
- Fullscreen with **Own scene on each monitor** (the default): `GLHost.MonitorLayout` asks Windows for every
  monitor's rectangle (`EnumDisplayMonitors`) and makes a view for each.

Why this matters on a real multi-monitor desktop, for example a portrait monitor left of the main one and a smaller
one to the right, offset vertically:

```
 virtual desktop (what one window covers)
┌──────┬─────────────────────┬──────────────┐
│      │█████████████████████│░░░░░░░░░░░░░░│  ░ no monitor here: never seen
│ port-│█ main monitor ██████│░░░░░░░░░░░░░░│
│ rait │█████████████████████├──────────────┤
│      │█████████████████████│ right monitor│
│      ├─────────────────────┤              │
│      │░░░░░░░░░░░░░░░░░░░░░│              │
└──────┴─────────────────────┴──────────────┘
```

A single scene would be framed for the whole wide rectangle, so each monitor shows an awkward slice of it, and the
hatched areas get rendered for nobody. With views, each monitor gets a scene framed for its own shape, and nothing
is drawn where no monitor is. On the three-monitor desktop this was tested on, that made fullscreen about 15%
cheaper even though it draws three complete scenes.

A few details:

- **Coordinates:** monitor rectangles come relative to the main monitor, so one left of it has a negative X. The
  window's top-left is the virtual desktop's top-left, so each rectangle is shifted by that origin. Windows also
  measures Y downwards, while OpenGL viewports measure it upwards, so `View.Render` flips it.
- **One window, not one per monitor:** each window would wait for VSync separately in `SwapBuffers`, which can
  divide the frame rate by the number of monitors.
- **Rendering into a rectangle:** each renderer does all its passes in its own offscreen buffers at its own size.
  Only the very last step (the post pass, or the final copy in classic mode) writes into the window, at the view's
  offset. That's why `PipeRenderer.Render` takes a `targetX`/`targetY`.

> **A bug worth learning from:** the window title used to show just "P". Win32 functions come in two flavours,
> `...A` (ANSI, 1 byte per character) and `...W` (Unicode, UTF-16). The window was created with the W version,
> but `DefWindowProc` was declared without a `CharSet`, so .NET picked the A version. The A version read "Pipes"
> in UTF-16 (`P\0i\0p\0...`) as a byte string and stopped at the first zero byte. The fix is to declare every
> text-handling function with `CharSet = CharSet.Unicode`. See the comment in `Native/Win32.cs`.

## Scene: the life of one pipe world

`Scene` runs a small state machine:

```
FadeIn ──► Growing ──► Hold ──► FadeOut ──► (new world) FadeIn ...
                         │
                         └─ fly-through ──► Flying (150 s) ──► FadeOut ──► ...
```

Each new world is a fresh `PipeWorld` with a grid shaped to the view: 12 cells across its shorter side and as many
as fit along the longer one (so a portrait monitor gets a tall grid), and a new random camera angle. `Scene` also
owns camera motion:

- **Still:** fixed.
- **Orbit:** yaw (the left/right angle) increases slowly.
- **Float:** orbit, plus a few slow sine waves on pitch, distance and target point. The waves have unrelated
  speeds (0.21, 0.13, 0.11, 0.17), so their combination doesn't visibly repeat and reads as organic drifting.
- **Fly through:** see the next section.

## Fly-through: an endless tunnel

In this mode the scene builds as usual, then the camera takes off, dives into the pipes, and flies on through a
tunnel that keeps building itself ahead. Three pieces make it work.

**The flight path** (`Simulation/FlightPath.cs`): straight runs along grid axes (18–40 units), joined by wide
quarter-circle turns (radius 8). It's generated on demand, ahead of the camera, and stored as points every 0.5
units, so "distance along the path" is just an index. Two properties matter:

- **It never doubles back.** Once it has moved in a direction (say +X), it's never allowed to move in the opposite
  one (−X). So it only ever advances along each axis, and can't loop round into the tunnel it already built. There
  are always at least two turns left to choose from, so it never gets stuck.
- **Fast "how far from the path?" lookups.** Path points are filed in 8-unit buckets, so finding the nearest point
  to a cell only checks the 27 buckets around it, not the whole path.

**The tunnel space** (`Simulation/TunnelSpace.cs`), an `IPipeSpace` (see below):

- A **corridor** within 2.4 units of the path is always kept empty, so the camera never flies through a pipe. It's
  cut through the box from the start, so while the scene builds you can see a gap through the middle where the
  camera is about to go.
- Before take-off, pipes grow only in the box, and the box can fill up as usual.
- After take-off, new pipes spawn in the **wall**, a shell 2.4–6.5 units from the path, between 14 and 42 units
  ahead of the camera. You see them start and grow as you approach. Fog hides the far end.
- **Flow:** each stretch of path randomly flows with or against the flight. Near the path, `Flow()` returns that
  direction, snapped to a grid axis, and `PipeWorld` biases pipes to follow it. Pipes running with the flow turn a
  third as often, and pipes running across it turn into it. That's what lines the tunnel with long runs of pipe.

**The flying camera** (`Scene.UpdateFlyingCamera`):

- It starts at the beginning of the path, looking head-on at the box. The path runs straight through the box's
  middle, so take-off is continuous: the camera just starts moving. The speed eases in over 3 seconds (a smoothstep
  curve), so there's no jolt.
- It looks at a point 5 units further along the path, so it turns into bends slightly early, the way a driver
  looks into a corner.
- **Banking like a plane** (`Scene.BankedUp`). A plane doesn't skid sideways into a turn: it rolls until the turn
  is "overhead", pulls back on the stick, and rolls level afterwards. The camera follows one rule: **during a turn,
  its up points at the turn's centre**. The maneuvers all follow from that:

  | Turn | What happens |
  |---|---|
  | Left / right | roll 90° into it, pull round, level out (to upright, or inverted if it was flying inverted) |
  | Up | no roll needed (the centre is already overhead): just pull up |
  | Down | roll 180° onto your back, pull through into the dive |
  | Any pull (up, down, out of a climb or dive) | no roll afterwards: the attitude it ends with is the new level |

  **It's treated like flying through space.** The tunnel has no horizon, so upside down is just another attitude:
  whatever a turn leaves the camera in becomes the new "level", and nothing rolls back afterwards. If the next turn
  needs a different attitude, it rolls in just before that turn. The only roll-out is after a left or right turn,
  which leaves the camera on its side. That levels out like a plane, to upright or inverted, whichever it was
  flying before.

  This took a couple of rounds to get right, which is typical of "feel" work. The first version rolled back
  upright after every maneuver, and then tried rolling to line up with the next turn straight after pulling into a
  climb. Both looked wrong in motion: a roll straight after a pull reads as the camera correcting itself.

  Each turn has a *roll-in* just before its arc, the arc itself (up locked on the centre), and a *roll-out* just
  after (when there is one). Rolls are eased with smoothstep, and a 180° roll takes 8 units of path against 6.5 for
  90°.

  Two implementation notes worth copying elsewhere:
  - The up vector is a **pure function of how far along the path** the camera is, not something updated a bit each
    frame. Each frame replays the turns from the start of the flight to work out the current "level" (a few dozen
    at most). So it can't drift, and the same moment of a flight always looks the same (handy with `/shot`).
  - Rolling uses **Rodrigues' rotation formula**: rotating `v` by angle θ around a unit axis `k` gives
    `v·cos θ + (k × v)·sin θ + k·(k·v)(1 − cos θ)`. The signed angle between two vectors around an axis is
    `atan2(axis · (a × b), a · b)`.
- **Recycling:** every frame, chunks whose centre is more than 14 units behind the camera are dropped with
  `PipeWorld.Recycle`, including their geometry, their occupied cells, and any pipe still growing there. Memory and
  drawing cost stay flat: a two-minute flight held steady at about 115 MB.
- **Enough pipes:** the camera uncovers (ring area × flight speed) cells of wall per second. `FlightConcurrency`
  raises the number of pipes growing at once so they fill about 60% of that. Fuller than that looked like a solid
  wall, and emptier looked bare. Slow growth speeds get more pipes to compensate.

## Simulation: how pipes grow

`Simulation/PipeWorld.cs` holds the rules. The world is a grid of cells, one unit apart. There's no fixed size:
occupied cells are a `HashSet<Int3>`, so the world can extend without limit. Where pipes may grow is decided by an
**`IPipeSpace`** (`Simulation/PipeSpace.cs`):

- `Contains(cell)`: may a pipe be here?
- `SpawnCandidate(rng)`: somewhere to try starting a new pipe.
- `Flow(cell)`: a preferred direction, or none.
- `IsBounded`: whether the space can fill up.

`BoxSpace` is the classic box. `TunnelSpace` is fly-through's box plus tunnel. Splitting "the rules" (`PipeWorld`)
from "the region" (the space) is what let the same growth rules fill both. When the world was made boundless, the
old and new versions were checked by rendering the same seeds before and after: the pictures matched to within a
few dozen faint pixels.

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
   or `Knee` (per the Joints setting). Occasionally (1 in 300) the knee becomes a teapot. (If the space has a flow
   here, the odds shift: see *Fly-through*.)
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

When a step completes, its geometry is added to the `PieceLists` of the **chunk** (an 8×8×8-cell cube) it's in.
Finished pieces never change. Each frame, `Collect` gathers every chunk's pieces and then appends the partial step
of each live pipe, plus a small sphere on each head to keep the growing end rounded. Nothing is rebuilt from
scratch. Filing by chunk is what lets fly-through drop everything behind the camera a cube at a time.

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
doesn't trigger it. Its dropdown item order matches the enum order, so `(JointStyle)_joints.SelectedIndex` converts
directly.

## Development workflow

- `Pipes.exe /w` for a resizable window.
- `Pipes.exe /shot out.png 12 1600 900 3` simulates 12 seconds at a fixed 60 steps per second with random seed 3,
  then saves one frame. The same seed gives the same picture, so it's great for before/after comparisons.
- For visual debugging, temporarily make a shader output an intermediate value, then render a shot. For example,
  in `PipeFragment`, `FragColor = vec4(vec3(ao), 1.0);` shows the ambient occlusion buffer directly. This is how
  the AO strength was tuned.
- Add the word `monitors` to `/shot` or `/bench` to use the real fullscreen layout (every monitor, each with its
  own view) instead of one width × height view. That's how the per-monitor mode was tested without taking over the
  screens.
- `Pipes.exe /bench bench.txt 300 1920 1080` renders 300 frames offscreen with no VSync and writes the average
  time per frame. `_gl.Finish()` before stopping the clock makes it include the GPU's work, not just the time the
  CPU took to queue commands. Combine it with `PIPES_SETTINGS` to compare settings.
- For intermittent glitches, make them countable. Flag the bad pixels in a shader (e.g. `isnan()` → magenta),
  render a few thousand frames offscreen, and count. See "Bug story: the black boxes" in RENDERING.md.
