# Roadmap

## Next up: fly-through tunnel mode

The idea: a normal scene builds up, then the camera starts flying into it. Pipes keep growing ahead of the camera,
forming a tunnel of pipes around its path. Every so often the tunnel changes direction, and the pipes' general flow
changes with it.

### What has to change

**1. An endless grid instead of a box.**
`PipeWorld` uses a fixed `bool[,,]` sized to the screen. A tunnel needs a world with no edges:

- Replace the array with a sparse set of occupied cells (`HashSet<Int3>`), or a dictionary of fixed-size chunks
  (for example 16×16×16) that are created as the camera approaches and dropped once they're behind it.
- `InBounds` becomes "inside the tunnel region" (see 3).

**2. A camera path.**
The camera follows a path made of straight runs joined by wide, smooth turns:

- It moves along a grid axis (say +Z) at a steady speed.
- Every N seconds it picks a new perpendicular axis and turns along an arc with a large radius (5–10 cells), so the
  turn feels like a slow bank, not a snap. The same quarter-circle maths used for the pipe elbows works here,
  just bigger.
- The look direction leads the path slightly (look at a point a few cells ahead on the path), so turns are
  anticipated, the way a driver looks into a corner.

**3. A tunnel-shaped spawn region.**
Pipes should only spawn and grow inside a thick shell around the path:

- Distance from the path's axis between `innerRadius` (keep the flight corridor clear, e.g. 1.5 cells) and
  `outerRadius` (e.g. 6 cells).
- Spawning happens mostly in a band 15–30 cells ahead, so growth is visible as the camera approaches. Fog hides
  the far edge, so pipes never visibly pop in.
- Pipes that would grow into the corridor count as "blocked" and turn away.

**4. Flow direction.**
To make the pipes *flow* along the tunnel, bias turns: when a pipe turns, prefer directions parallel to the
current path direction (or directly against it for counter-flow). When the tunnel changes direction, the preferred
axis changes too, and new pipes follow the new flow.

**5. Recycling.**
Geometry behind the camera gets removed, so instance counts stay bounded however long it flies. With chunks,
that's dropping a chunk's `PieceLists`. It also means splitting `_finished` into per-chunk lists.

**6. Scene flow.**
A new `Phase.FlyThrough` in `Scene`: after the normal scene finishes growing, instead of fading out, the camera
eases from its orbit onto the path's start and accelerates in. Depth of field focus should track a point a few
cells ahead.

**7. Setting.**
Add `FlyThrough` to `CameraMotion` ("Fly through the pipes").

### Suggested order

1. Sparse/chunked grid with the existing box behaviour (no visible change: a pure refactor, easy to verify).
2. Camera path + a fly mode that just flies through a static prebuilt field.
3. Spawn region around the path + recycling behind.
4. Flow bias and direction changes.
5. The transition from a normal scene into the fly-through.

## Ideas

- **Snapping growth for classic mode:** the original's pipes grew in visible jumps rather than smoothly. An option
  to quantise growth would complete the nostalgia.
- **Textured/"flex" pipes:** ribbed flexible conduit (a ripple in the cylinder shader's radius along its length),
  or subtle surface textures.
- **"Multiple pipes per colour" mode:** like the original's option, where several pipes share a colour.
- **Separate scenes per monitor:** each monitor gets its own grid and camera instead of one scene spanning all of
  them. Needs a viewport per monitor, from `EnumDisplayMonitors`.
- **Shadows:** real shadows from the key light (a shadow map) would add a lot of depth. SSAO currently does part of
  that job.
- **Per-scene lighting moods:** e.g. a warm sunset key light, cool moonlight, or neon rim lights, picked per scene.
- **Performance mode:** half-resolution SSAO and DoF for integrated GPUs and very large multi-monitor setups.

## Done

- Curved elbows, ball joints, or mixed
- Variable pipe thickness
- Per-pipe materials (glossy/satin plastic, polished/brushed metal)
- Fittings: valves, couplings, flanges; tee junctions with branch pipes
- The teapot easter egg
- SSAO, bloom, depth of field
- Camera modes: still, orbit, float
- Classic (lite) style: the original's look at about a tenth of the GPU cost
