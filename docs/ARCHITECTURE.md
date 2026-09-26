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
2. **Sleep until the monitor's next refresh** (`VBlankWaiter.Wait`, see below).
3. Measure `dt`, the seconds since the last frame. It's clamped to 0.1s so a hitch (e.g. the PC waking up) doesn't
   make the pipes jump.
4. For each view: update its scene, then render it into its rectangle.
5. `SwapBuffers`, which queues the frame to be shown at the next refresh.

### Frame pacing: sleeping, not spinning

A frame is about 1 ms of work. The rest of each 16.7 ms (at 60 Hz) is waiting for the monitor. Normally, with
VSync on, the OpenGL driver does that waiting, usually inside the first draw call of the next frame, when it needs
a buffer the display is still showing. NVIDIA's driver sometimes waits by **spinning**: checking over and over,
keeping a CPU core at 100% doing nothing. It varied between runs of identical settings, from 10% to 105% of a core.
That's a poor trait in something that runs whenever the PC is idle.

The fix (`Native/VBlankWaiter.cs`) is to do the waiting ourselves with a call that puts the thread to sleep,
`D3DKMTWaitForVerticalBlankEvent`, on the monitor the window is (mostly) on. When it returns, the display has just
started showing the previous frame, so a buffer is free. Drawing doesn't block, and `SwapBuffers` just queues the
frame. The driver never waits, so it never spins. VSync stays on in the driver too, so there's still no tearing. If
the call isn't available, or ever fails (say the display turns off), the app falls back to letting the driver pace
it.

Measured on the development PC: CPU went from anywhere between 10% and 105% of a core to a steady 7–15%, at a
steady 60 fps. The measurements were taken by temporarily timing each part of the frame (wait, work, swap), which
showed the "work" was about 1 ms and the rest was all waiting.

What *didn't* work, for the record: calling `DwmFlush()` (which sleeps until the desktop compositor's next frame)
with the driver's VSync off. It got CPU down to about 12%, but the compositor runs at the *fastest* monitor's rate.
On a PC with a 75 Hz and a 60 Hz monitor, it rendered at 75 fps for a 60 Hz window, which judders.

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
              └─ fly-through (last pipe started) ──► Flying (150 s) ──► FadeOut ──► ...
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

**The flight path** (`Simulation/FlightPath.cs`): straight runs along grid axes, joined by *maneuvers*. It's
generated on demand, ahead of the camera, and stored as points every 0.5 units, so "distance along the path" is
just an index. The maneuvers:

| Maneuver | Shape |
|---|---|
| Turn | quarter circle, radius 8, into a new direction |
| Sweep | quarter circle, radius 22–30: a long, lazy curve into a new direction |
| Meander | shallow arcs (radius 18–26) swinging 20–30° side to side, 2–4 times, then straightening up the same way |
| Corkscrew | a spiral round the direction of travel: radius 3.5–5, one coil every 32–40 units, 1–3 coils (one barrel roll each: one 45% of the time, two 35%, three 20%) |

How long the straights are, and how often each maneuver comes up, is the **course complexity** setting
(`FlightPath.Complexity`), a slider from 1 (zen) to 10 (wild). Three anchor levels, with the slider blending between
them in straight lines (1→5, then 5→10):

| | Straights | Turn | Sweep | Meander | Corkscrew |
|---|---|---|---|---|---|
| 1, Zen | 45–90 | 15% | 40% | 40% | 5% |
| 5, Balanced (default) | 18–40 | 50% | 22% | 18% | 10% |
| 10, Wild | 3–10 | 50% | 10% | 15% | 25% |

Level 5 is the mix the flight used before the setting existed.

A meander's swings are `+θ, −2θ, +2θ, …, ±θ`: they add up to no turn, so it comes out heading exactly the way it
went in.

A corkscrew (`FlightPath.Helix`) is the one shape that isn't made of arcs. It's defined by distance along its
axis, `a`: the path winds round the axis by `2π·a ÷ pitch` and sits `radius(a)` out from it. The radius opens up
with a smoothstep over the first half coil and closes the same way over the last. Where a smoothstep is 0 its slope
is 0 too, so at both ends the spiral is heading straight down the axis and joins the straights without a kink. Path
points must be evenly spaced *along the path*, not along the axis, so it walks the axis in tiny steps, measures the
distance covered, and drops a point every 0.5 units.

Two properties matter:

- **It never doubles back.** Once it has moved in a direction (say +X), it's never allowed to move in the opposite
  one (−X). So it only ever advances along each axis, and can't loop round into the tunnel it already built. There
  are always at least two turns left to choose from, so it never gets stuck. A meander wanders a few units back and
  forth sideways, but its sideways axis is picked like a turn, so its overall drift obeys the rule. A corkscrew's
  coils are a whole pitch (32+ units) apart. Checked on eight flights, corkscrews included: no two parts of the path
  at least 20 units apart ever came closer than 16.6 units (the chord across an ordinary turn), and the tunnel walls
  would only touch at 13.
- **Fast "how far from the path?" lookups.** Path points are filed in 8-unit buckets, so finding the nearest point
  to a cell only checks the 27 buckets around it, not the whole path.

**The tunnel space** (`Simulation/TunnelSpace.cs`), an `IPipeSpace` (see below):

- A **corridor** within 2.4 units of the path is always kept empty, so the camera never flies through a pipe. It's
  cut through the box from the start, so while the scene builds you can see a gap through the middle where the
  camera is about to go.
- The **tunnel starts where the path leaves the far side of the box**. No tunnel pipe ever grows before that, so
  nothing gets between the camera and the box it's looking at.
- **Before take-off,** pipes grow in the box and in the tunnel's first 20 units beyond it: 30% of the scene's pipes
  start there, and the scene gets 1 ÷ 0.7 as many pipes, so the box is as full as usual. The box and the mouth of
  the tunnel build together as one scene. The box can fill up as usual.
- **After take-off,** new pipes spawn in the **wall**, a shell 2.4–6.5 units from the path, in a band 28 units deep
  that normally starts 14 units ahead of the camera (further at high flight speeds, see below). You see them start
  and grow as you approach. Fog hides the far end.
- **The band never skips ahead** (`TunnelSpace.AdvanceSpawnBand`). At take-off it starts where the tunnel does, and
  sweeps forward at twice the peak flight speed until it's back where it belongs. It used to jump straight to "14+
  units ahead of the camera", and at high speed that was 72 units ahead while the box ended 37 in: the stretch in
  between never got any pipes, a hole you could see. Measured at 20 cells/s, counting wall pieces 6–14 units ahead
  of the camera every 5 units flown: before, seven zeros in a row after the box; after, never below about 220.
- **Flow:** each stretch of path randomly flows with or against the flight. Near the path, `Flow()` returns that
  direction, snapped to a grid axis, and `PipeWorld` biases pipes to follow it. Pipes running with the flow turn a
  third as often, and pipes running across it turn into it. That's what lines the tunnel with long runs of pipe.

**The flying camera** (`Scene.UpdateFlyingCamera`):

- It starts at the beginning of the path, looking head-on at the box. The path runs straight through the box's
  middle, so take-off is continuous: the camera just starts moving. The speed eases in over 3 seconds (a smoothstep
  curve), so there's no jolt.
- **It takes off as soon as the scene's last pipe has started**, with no pause for the finished scene: the last few
  pipes finish growing as the camera gets going, so building and flying run into each other.
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

  Each turn has a *roll-in* just before its arc, the arc itself (up locked on the centre), and a *roll-out* when
  one is needed. Rolls are eased with smoothstep, and a 180° roll takes 8 units of path against 6.5 for 90° (a
  little more at high flight speeds, up to 8.5, so neighbouring turns' rolls never overlap).

  **Flown like a pilot, not a machine.** Done exactly, a left or right turn was three separate moves: roll to 90°,
  hold it perfectly, roll back. Two habits of real pilots join them into one gesture:
  - **Overbank:** the roll-in carries on 3–8° past what the turn needs and holds there, slightly steep, through
    the corner.
  - **Lead the roll-out:** pilots start rolling out *before* reaching the new heading (the rule of thumb is half
    the bank angle early), so they arrive level instead of overshooting the heading. Here the roll-out starts
    25–45% of the way before the end of the arc, and takes the overbank back out with it.

  Each maneuver gets a random `Seed` when the path is built, and `Scene.Quirk` turns it into the overbank, the roll-out
  lead, and a ±15% roll-in pace. So no two turns are flown quite alike, but replaying the same turn gives the same
  answer (the "pure function" property below still holds). On top of that there's a faint **stick wobble**: two
  slow sine waves adding up to about ±2° of roll. Measured through a flight, a right turn now goes: roll in to
  about 100°, settle at 92–97° through the corner, roll out in one motion, swing about 7° past level, and settle.

  **Sweeps and meanders bank partly** (`Scene.GentleCurve`). "Up points at the centre" would put a lazy curve at a
  full 90° too. Instead the camera keeps its level and banks by **how sharply the path curves sideways**:
  `bank = atan(sideways curvature × 22)`, capped at 60°. That's the coordinated-turn formula real planes follow,
  `tan(bank) = speed² ÷ (radius × g)`: a tighter turn needs a steeper bank. So a radius-22 sweep banks 45°, a
  radius-30 one 36°. A curve straight up or down (relative to the camera) has no sideways part, so it just pitches,
  like cresting a hill.
  - **Curvature** is how fast the direction of travel changes per unit flown. Averaged over a stretch it's simply
    `(direction at far end − direction at near end) ÷ length`. Taking the stretch about 3.5 units either side of the
    camera (more at high flight speeds, up to 7, and never past the room either side)
    smooths the bank in and out, and starts it a moment *before* the curve, as a pilot would.
  - **Level through a curve** is carried along by the smallest rotation from the old direction of travel to the
    current one. For a curve in one plane that's exactly what *parallel transport* gives: how up moves if you don't
    roll at all. So a sideways sweep keeps the camera upright, and a sweep upwards tips its up over backwards.

  Sideways sweeps hold about 32–50° through the curve (each curve's pilot banks 85–120% as steeply as the formula
  says); meanders weave between about +50° and −50°, like flying
  down a snake; vertical sweeps don't roll.

  **Gentle curves get a looser spring.** With the normal roll spring (below), a meander's bank changes so gradually
  that the spring hardly trails it, and the weave looked machine-perfect. So in sweeps and meanders the spring is
  softer and less damped (`LooseRollSnap`, `LooseRollDamping`), blended in and out over about a second, and the
  curvature is averaged over a shorter stretch (3.5 units either side), so the bank changes briskly enough to build
  up some momentum. Each curve's pilot also banks 85–120% as steeply as the formula says. Measured: the roll trails
  by up to about 28° while a meander reverses, then swings 8–10° past the new bank before settling. An even looser
  spring was tried first and swung 25–30° past on every reversal: a pendulum, not a pilot.

  **Corkscrews are a slow barrel roll** (`Scene.Corkscrew`). The centre of a spiral's curve always lies towards its
  axis, so the tight-turn rule applies: up faces the axis. As the path winds round, that means rolling steadily, one
  full roll per coil, with the tunnel ahead always curving the same way on screen while the pipes spin round you.
  Each corkscrew leans 15–45° off facing the axis, so the curve ahead runs diagonally: a long sloping bank.
  - The roll is an angle that **keeps growing** instead of wrapping at ±180°, so it runs smoothly through whole
    turns. It's the start angle plus how far the path has wound round the axis, nudged to the exact attitude (the
    camera looks along the spiral, not straight down the axis).
  - It **blends in** while the spiral opens up, and **blends out** to the nearest whole number of turns while it
    closes, which is level again. Both blends take the shorter way round, so neither is more than half a turn.
  - Measured: once the spiral is open, the attitude holds steady relative to the axis (lean plus 5–8° of spring
    lag, since the spring always trails a steady roll slightly), and it comes out within 2° of level.

  **Room to roll.** On a wild course, straights can be as short as 3 units, and neighbouring maneuvers'
  rolls would overlap and fight over the camera. So each maneuver may only reach half the straight on either side
  (`Scene.Room`): a tight turn's roll-in and roll-out squeeze into it (getting quicker), and so does a gentle
  curve's averaging stretch. Then the next maneuver always starts from exactly the attitude the last one left.

  Two implementation notes worth copying elsewhere:
  - The *target* up vector is a **pure function of how far along the path** the camera is, not something updated a
    bit each frame. Each frame replays the turns from the start of the flight to work out the current "level" (a
    few dozen at most). So it can't drift. (`/shot` steps at a fixed 1/60 s, so a given moment still always looks
    the same even with the momentum below.)
  - Rolling uses **Rodrigues' rotation formula**: rotating `v` by angle θ around a unit axis `k` gives
    `v·cos θ + (k × v)·sin θ + k·(k·v)(1 − cos θ)`. The signed angle between two vectors around an axis is
    `atan2(axis · (a × b), a · b)`.
- **Roll momentum** (`Scene.FollowRoll`). Following `BankedUp` exactly looked like the camera was on rails: each
  roll stopped dead on its target. Now the camera's actual roll *chases* that target through a **damped spring**,
  the same model as a pendulum with drag or a car's suspension:

  ```
  acceleration = −ω²·offset − 2ζω·rollRate      (offset = how far the roll is from its target)
  ```

  The first term pulls towards the target, harder the further away it is; the second resists rolling fast. With
  the damping ratio ζ below 1 it's *underdamped*: it arrives still rolling, swings a little past, and eases back.
  ζ = 0.45 gives about 7° past a 90° bank (10° past a 180° roll into a dive), then a degree or two back the other
  way, then still. Levelling out after a turn swings past level the same way.

  - **Only the roll is sprung.** As the camera pitches and yaws through a turn, last frame's up is carried along by
    projecting it perpendicular to the new forward direction, which rotates it by exactly the pitch and not at all
    for yaw. The spring then closes only the leftover roll angle, so the view never lags behind the path itself.
  - **Speed-independent feel.** The spring's natural frequency ω is set as 6 ÷ (seconds a 90° roll takes at the
    current flight speed). The swing depends only on ω × roll time, so it looks the same at 2 cells/s or 20.
  - **Stepping:** semi-implicit Euler (update the rate from the acceleration, then the angle from the new rate) in
    fixed steps of at most 1/120 s. Plain Euler can gain energy and blow up; this version stays stable and gives
    the same motion at any refresh rate. The whole thing is a few multiplies per frame, so it costs nothing.
  - **Never the wrong way round.** The gap to the target is measured with a signed angle, which always answers
    the shorter way round (±180°). In a wild flight, quick back-to-back rolls can leave the camera trailing by up to
    about 75°, and if that ever passed 180°, "the shorter way" would flip and the camera would suddenly roll back the
    other way. So each frame takes whichever equivalent angle (±360°) is nearest last frame's gap, and the roll
    carries on the way it was going.
- **Recycling:** every frame, chunks whose centre is more than 14 units behind the camera are dropped with
  `PipeWorld.Recycle`, including their geometry, their occupied cells, and any pipe still growing there. Memory and
  drawing cost stay flat: a two-minute flight held steady at about 115 MB.
- **Enough pipes:** the camera uncovers (ring area × flight speed) cells of wall per second. `FlightConcurrency`
  sets the number of pipes growing at once so they fill a share of that (up to 80 pipes). The share is the
  **tunnel density** setting, 10–100%, default 60%: fuller than that looked like a solid wall, and emptier looked
  bare (though 20% makes a calm, sparse drift through space). Slow growth speeds get more pipes to compensate.
  It deliberately ignores "pipes at once" and "pipes per scene", which are about building the box: in flight the
  pipe count has to follow the flight speed. Density sets how many pieces there are to draw, so it's the setting
  to turn down on a slower graphics card. Measured at 1080p with 4× MSAA:

  | Density | Pieces | ms per frame |
  |---|---|---|
  | 20% | 1,860 | 0.77 |
  | 40% | 4,550 | 1.24 |
  | 60% (default) | 9,690 | 2.25 |
  | 100% | 15,550 | 3.19 |
- **Flight speed** is a setting (1–20 cells/s, default 5). A few things scale with it so a fast flight still looks
  right:
  - **Pipe count:** more pipes grow at once (above). With a varying speed (below), it's sized for halfway between
    the setting and the peak.
  - **Spawn distance:** new pipes start further ahead, about 2.5 seconds of flight at the *peak* speed, so they've
    had time to grow before the camera arrives.
  - **Fog and far plane:** pushed back to match, so the further-away spawning isn't hidden and doesn't pop in.
  - **Rolls:** stretched slightly, so they don't become a snap.
- **The pilot varies the speed** (a setting, on by default; `Scene.Throttle`). What the pilot wants is three things
  multiplied together:
  - **Drift:** two slow sine waves at unrelated speeds, about ±30% over a minute or so.
  - **Caution:** easing off to 85% coming up to a tight turn (starting 12 units before), holding it through the
    turn, and picking up over the next 10 units.
  - **Open road:** with 25 to 50+ units of clear straight ahead, opening up by as much as 20%.

  The result is kept between 60% and 145% of the setting, and the actual speed follows it through a *first-order
  lag*, `speed += (wanted − speed) × (1 − e^(−dt / 1.5 s))`: each moment it closes a fixed share of the gap, which
  feels like accelerating rather than jumping. Measured: at a setting of 5 it ranges 3.6–6.9 and averages 4.9–5.2
  (4.6–5.1 on a wild course, where there are more turns to be careful about). The *banking* still uses the speed
  setting, not the current speed, so a roll's shape along the path never changes mid-roll; only the spring's
  timing uses the current speed.

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
  roughness, and then the procedural surface: which `Surface` (glossy paint, rust, patina...), a per-pipe seed and
  a wear amount. 19 floats plus one spare make 20, which is also the GPU buffer layout (`FloatsPerInstance`).
- `Surface` / `PipeMaterial`: what a pipe is made of. `PipeWorld.NextMaterial` picks one per pipe from the
  `Finish` setting; the shader turns it into a pattern (see RENDERING.md, "Surfaces: procedural texture"). With
  the Surface detail setting off, `NextPlainMaterial` picks the original flat materials instead.
- `PieceLists`: a list per kind, plus helpers such as `Cylinder(from, to, radius, material)`,
  `Elbow(...)` and `Ring(...)` that fill in a `PipeInstance` correctly.

Fittings are built entirely from these primitives. For example, a valve is a sphere (body), a cylinder (stem), a
ring (handwheel), two thin cylinders (spokes) and a small sphere (hub). No special renderer support needed.

### The same pieces as a scene to trace

With **Traced reflections** on, the renderer uses the `PieceLists` a second way: as a scene a reflection ray can
search. `Rendering/ReflectionGrid.cs` files every piece into a grid of unit cells each frame, and the pipe shader
walks that grid to find what a pixel mirrors (RENDERING.md, "Traced reflections"). The data flows like this:

```
PieceLists ──► PipeRenderer.UploadInstances ──► instance buffers (one per MeshKind) ──► draw calls, as before
    │                                                  │
    │                                                  └─ also read as texture buffers: the pieces themselves
    └──► ReflectionGrid.Build (CPU, counting sort) ──► cell table + entry list ──► texture buffers
                                                                                        │
                                           PipeFragment: tracePipes() walks the grid ◄──┘
```

Nothing about the simulation changes: the grid is built from the same lists, entries point at a piece by its kind
and its index in that kind's list, and that index is also its place in the instance buffer, so the shader can read
the piece straight out of the buffer the draw calls already use.

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

`ConfigForm` is the dialog, built in code rather than the WinForms designer: two columns of labelled groups
(Animation and Flight on the left, Pipes and Graphics on the right), with the Flight group greyed out unless the
camera flies through. Choosing the Classic style also sets the pipe options to the original's (ball joints,
plastic, one thickness, no fittings, still camera) as a starting point. That handler is attached *after* the saved
settings are loaded into the controls, so opening the dialog doesn't trigger it. Its dropdown item order matches
the enum order, so `(JointStyle)_joints.SelectedIndex` converts directly. **Reset to defaults** loads a
`new PipesSettings()` into the controls; like any other change, nothing is saved until OK (or "Try it"). Cancel
closes the form explicitly: a button's `DialogResult` only closes a form shown with `ShowDialog()`, and this one is
the application's main window (`Application.Run`).

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
  time per frame. It renders untimed for 1.5 seconds first: an idle GPU runs at a low clock, and a benchmark this
  short can finish before it speeds up (the same settings once measured anywhere from 2.5 to 13 ms a frame).
  `_gl.Finish()` before stopping the clock makes it include the GPU's work, not just the time the CPU took to
  queue commands. Combine it with `PIPES_SETTINGS` to compare settings. With traced reflections on, the report
  also gives the CPU time spent building the reflection grid, upload calls included, averaged over the timed frames
  only (the warm-up's first builds pay for JIT compiling and growing the arrays). It's part of the frame time too.
- To check an acceleration structure (like the reflection grid), compare it with brute force: a temporary shader
  that also tests every piece and paints the pixels where the two disagree. Slow, but it settles "is the structure
  wrong, or is this something else?" at once. That's how the traced reflections' speckles were shown to be a
  sampling problem rather than a bug in the grid walk.
- For intermittent glitches, make them countable. Flag the bad pixels in a shader (e.g. `isnan()` → magenta),
  render a few thousand frames offscreen, and count. See "Bug story: the black boxes" in RENDERING.md.
