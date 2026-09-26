# Rendering

How a frame gets drawn, one pass at a time. The code is in `src/Pipes/Rendering/`: `PipeRenderer.cs` runs the
passes, `Shaders.cs` holds the GLSL for each one, and `ReflectionGrid.cs` files the scene into a grid for traced
reflections.

There are two styles. **Modern** is the full chain described in most of this document. **Classic (lite)** is a
single pass, described in its own section below.

## The pass chain

```
                    ┌────────────────────────────┐
 pieces ──────────► │ shadow map (from the light)│───────────────────────────┐ (shadow texture)
   │                └────────────────────────────┘                           │
   │                ┌──────────────────┐   ┌──────┐   ┌─────────┐            │
   ├──────────────► │ geometry prepass │──►│ SSAO │──►│ AO blur │───────────┐│ (AO texture)
   │                │ normals + depth  │   └──────┘   └─────────┘           ││
   │                └──────────────────┘                                    ▼▼
   │                         │ (depth)        ┌───────────────────────────────┐
   ├────────────────────────────────────────► │ scene: background + lit pipes │  HDR, MSAA
   │                         │                │ (traced reflections: a depth  │
   │                         │                │ prepass first, and the pipes  │
   └──► reflection grid ─────┼──────────────► │ trace rays through the grid)  │
        (traced only)        │                └───────────────────────────────┘
                             │                                │ resolve (average MSAA samples)
                             │                                ▼
                             └──────────────────────► depth of field (optional)
                                                              ▼
                                                   bloom: down ×6, up ×5 (optional)
                                                              ▼
                                   post: bloom mix, tonemap, vignette, gamma, dither, fade ──► screen
```

The geometry prepass only runs if SSAO or depth of field is on, and each optional pass only runs (and only
allocates its textures) if enabled. With traced reflections, the CPU also files every piece into a grid each frame,
and the scene pass starts with a depth-only prepass of its own; see "Traced reflections".

## Shadows: a shadow map

**The idea:** a point is in shadow if something sits between it and the light. The classic way to answer that on a
GPU is a **shadow map**:

1. **From the light** (`ShadowPass`): draw every piece's depth into a 2048×2048 texture, as the light "sees" it.
   Each texel ends up holding the distance to the nearest surface along that ray of light. Only depth is written,
   so the fragment shader (`ShadowFragment`) is empty, and it's cheap.
2. **From the camera** (`keyLightVisibility` in `PipeFragment`): for each pixel, work out where its point lands in
   the shadow map and how far it is from the light. If the map holds a nearer surface there, something is in the
   way: shadow.

Only the key light (warm, from above) casts shadows. The dim blue fill light doesn't, which is what a fill light
is for: it keeps shadowed sides from going black.

**Details that make it look right:**

- **Orthographic, like the sun.** The key light is a sun, so its rays are parallel: no perspective. The light's
  "camera" is a box looking along the light, fitted around a sphere the scene chooses (`Camera.SetShadowFocus`):
  the whole box of pipes in the classic modes, and a region reaching ~38 units ahead of the camera in flight. The
  box reaches three radii towards the light, so a pipe outside the sphere can still shadow something inside it.
- **Shadow acne.** A surface compares against its *own* depth in the map, and rounding decides whether it shadows
  itself, which shows as speckled stripes, worst on curved pipes. Two standard fixes, used together: `glPolygonOffset`
  pushes stored depths slightly away from the light (more on steep surfaces), and the shader looks up a point nudged
  1.5 texels off the surface along its normal (*normal offset*).
- **Soft edges (PCF).** The map is a `sampler2DShadow` with comparison mode on: sampling returns "how lit" (0..1)
  rather than a depth, and with linear filtering the GPU blends the 4 nearest texels' answers for free. Nine such
  lookups spread over a few texels (*percentage-closer filtering*) turn a jagged edge into a soft one.
- **No shimmer.** In flight the shadow region moves every frame. If it slid smoothly, each shadow's edge would land
  on different texels every frame and crawl. So the light's box only moves in whole texels: its centre is rounded
  to the texel size in the light's view (`LightViewProj`). (With the moving light, below, the box also turns a
  little every frame, so this no longer holds exactly. The shadows are moving anyway, and the soft edges hide it.)
- **No visible edge.** Shadows fade out over the outer 10% of the map, so where it ends never shows as a line.

**In the tunnel,** a sun from above should mostly be blocked by the tunnel wall, and it is: the key light only gets
in through gaps between pipes, which dapples the pipes like sunlight through a forest canopy. Ambient and fill light
keep the rest colourful rather than murky.

**Moving light** (the Moving light setting). The key light, the fill light and the light strips in the reflections
form one "rig" that slowly turns around the vertical axis, once every 150 seconds, while the key light rises and
dips between about 30 and 62 degrees above the horizon (a 67 second cycle, so the path doesn't repeat).
`Scene.UpdateLight` sets the angles on the camera each frame, `PipeRenderer.LightRig` turns the two light
directions, and the shader turns `sky()` by the same angle (`uSkyTurn`), so the highlights stay where the lights
are. Nothing else changes: the shadow map is re-rendered every frame anyway, so a moving light costs nothing
extra. It never gets close to straight overhead, which keeps the light's view (built with world up as its "up")
well defined.

**Cost,** measured at 1920×1080 with 4× MSAA: +0.2 ms per frame for a box of ~3,000 pieces (0.9 → 1.1 ms), and
+0.45 ms in flight with ~9,700 pieces (1.5 → 2.0 ms). A 60 Hz frame has 16.7 ms. Classic (lite) mode has no
shadows, like the original.

## Instancing: thousands of pieces, a handful of draw calls

A busy scene has several thousand cylinders and spheres. Issuing a draw call per piece would be slow, because each
call has CPU overhead. Instead, each **shape** is one mesh uploaded once (a unit cylinder, a unit sphere...), and
each frame we upload a small **instance buffer** with 20 floats per piece. One `glDrawElementsInstanced` call per
shape draws every piece of that shape.

The trick is `glVertexAttribDivisor(location, 1)` in `CreateMesh`. Normally, vertex attributes advance once per
vertex. With divisor 1, attributes 2–7 advance once per *instance*. So inside the vertex shader, `aPos`/`aNormal`
are the mesh vertex, and `iStart`/`iAxis`/`iSide`/`iColor`/`iParams`/`iSurface` are "which piece am I drawing".

**Streaming the instance data.** Each instance buffer keeps a fixed, generous size (room for 16,384 pieces, doubling
if ever needed). Every frame, `glBufferData` with no data "orphans" it: the driver hands over fresh memory of the
same size while the GPU may still be reading last frame's. Then `glBufferSubData` fills just the part in use.
Re-creating the buffer at the exact size every frame also works, but it makes the driver allocate a different size
each time, which it can't simply recycle.

### One vertex shader, four shapes

`PipeVertex` switches on `uMode`:

- **Cylinder (0):** builds an orthonormal frame with `y` along the pipe (the cross product with a "helper" vector
  gives a perpendicular `x`, and another cross product gives `z`). It then stretches the unit cylinder to the
  pipe's length and radius. The normal goes through the same frame.
- **Sphere (1):** scale and move.
- **Torus section (2):** the interesting one. The mesh doesn't store positions at all. Each vertex is
  `(t, cos φ, sin φ)`: `t` is how far along the bend (0..1), and φ is the angle around the tube. The shader computes:

  ```
  θ      = t × sweep                    // angle along the bend
  radial = -e2·cos θ + e1·sin θ         // centre of the torus → middle of the tube
  normal = radial·cos φ + b·sin φ       // b = e1 × e2, the torus axis
  world  = centre + radial·bendRadius + normal·tubeRadius
  ```

  Because the sweep angle is per-instance data, the **same mesh** draws a 90° elbow, a partly grown elbow (sweep
  going 0 → 90° as the pipe grows round the corner), and a full 360° valve wheel. (Rings use a denser copy of the
  mesh so the full circle stays smooth.)
- **Oriented mesh (3):** the teapot. Its model axes are mapped onto the instance's "forward" and "up" vectors.

## Shading (`PipeFragment`)

The shading model borrows the core ideas of physically based rendering (PBR) without all of its maths. First,
`surface()` works out what the pipe looks like at this pixel: colour, metalness, roughness and a normal (see the
next section). Then (with Surface detail off, the older and simpler version described in "Turning them off"):

- **Diffuse:** `albedo × (1 − metallic) × max(N·L, 0)` per light (Lambert). Metals have none: all their colour
  comes from reflections.
- **Specular highlight:** a *microfacet* model. The idea: a surface is made of countless microscopic mirrors
  (facets), and roughness says how scattered their directions are. The light that reaches the eye is
  `D × V × F × N·L`:
  - **D, the GGX distribution:** how many facets face along the half-vector `H` (halfway between the light and the
    eye), so would mirror the light straight at us. GGX has a bright core and a long soft tail, which is why its
    highlights look like real paint and metal rather than the "plastic" blob of the old Blinn-Phong model. It uses
    `alpha = roughness²`, which makes the roughness scale look even to the eye. The shader uses the *anisotropic*
    form (Burley 2012): `D = 1 / (π·at·ab·(Ht²/at² + Hb²/ab² + Hn²)²)`, with `Ht`, `Hb`, `Hn` the half-vector
    measured along the pipe (`T`), across it (`B`) and along the normal. With `at = ab` it is plain GGX; brushed
    metal makes `ab` bigger, which stretches its highlight around the pipe.
  - **V, visibility:** facets hiding and shadowing each other at grazing angles. Hammon's cheap fit,
    `0.5 / mix(2·N·L·N·V, N·L + N·V, alpha)`.
  - **F, Fresnel (Schlick's approximation):** `F = F0 + (1 − F0)(1 − V·H)^5`. Every surface gets more
    reflective at grazing angles. That's why pipe edges catch a bright rim. `F0` is about 4% for paint, and the
    surface colour itself for metal, which is what makes gold look gold.

  GGX is *normalised*: a rough highlight is wider but dimmer, a smooth one small and very bright, with the same
  total light. So there's no strength fudge (the Blinn-Phong version needed a `specScale` to keep tight
  highlights from looking weak). The overall brightness came out the same as before, so the light colours didn't
  need retuning.
- **Specular anti-aliasing:** a very smooth, thin, distant pipe's highlight can be narrower than a pixel. It then
  lands on one pixel and misses the next, and sparkles as things move. The shader widens `alpha` by how much the
  normal changes across the pixel (Kaplanyan and Tokuyoshi's trick, as used in Google's Filament), which keeps
  small highlights stable.
- **Environment reflection:** there's no real environment map. `sky(dir, rough)` is a function that returns a
  dark gradient plus two bright "studio light" strips, and a dim warm floor below the horizon so the undersides of
  metal pipes (which show only reflections) don't go black. The gradient is kept close to the background on
  purpose. It used to be a bright blue sky, 25 times brighter than the background, and Fresnel makes every surface
  a good mirror at grazing angles, so wherever the camera looked along the pipes (or up the tunnel, in flight)
  they mirrored that sky and turned pale and washed out. A reflection has to look like the world it's reflecting.
  Rough materials widen the strips and dim them by the same factor, which fakes a blurred reflection. It uses the
  isotropic roughness (brushed metal's two values averaged), and a roughness-aware Fresnel,
  `F0 + (max(1 − rough, F0) − F0)(1 − N·V)^5`, so rough surfaces don't get a mirror-bright rim. With **Traced
  reflections** on, the lookup goes through `environment()`, which fires a real reflection ray first: where it hits
  a pipe, that pipe replaces `sky()` (see "Traced reflections" below).
- **Ambient:** a hemisphere light, brighter from above than below.
- **Fog:** exponential-squared, so distant pipes sink gently into the background.

## Surfaces: procedural texture

**Why procedural.** The meshes have no texture coordinates: a cylinder is stretched per instance, and a torus
section's mesh doesn't even store positions (see "One vertex shader, four shapes"). Rather than add UVs and image
textures, each surface is a small function in the fragment shader that computes its pattern from the pixel's
**world position**, using noise. That needs no memory and no loading, never looks stretched or tiled, and a pattern
flows continuously from a pipe onto its elbow and its ball joint, because they share the same world space.

**Per-pipe data.** Each instance carries one more `vec4` (`iSurface`, location 7): the `Surface` kind, a **seed**
and a **wear** amount (plus a spare). The seed shifts the noise (`p = world + seed × 37`), so two rusty pipes don't
rust identically; wear (0..1) sets how much rust, how many chips, how much patina. `PipeWorld.NextMaterial` picks
them. Seed and wear come from a hash of a counter, not from the random generator, so every other random choice
(and so a `/shot` seed's layout) is the same as before surfaces existed.

**Noise.** Everything is built from **value noise** (`vnoise`): a random number at each corner of a unit lattice,
blended smoothly between the 8 corners around the point. Three helpers sit on top: `fbm` (a few octaves, each at
double the frequency and half the strength, for ragged blotches), `bumpNormal` (a bump map computed on the fly: it
treats the noise as a height field and tilts the normal down its slope, found from 3 extra samples), and `detail`
(see below). The hash behind it all is the classic `fract(sin(x) × 43758.5)`, not great randomness but plenty for
texture, with two tricks explained in the shader: coordinates wrap every 289 cells so `sin()` stays precise far
out in the tunnel, and the 8 corners share one `dot()` (which only works because the hash's constants make every
sum exact; a first version without that broke the noise into blocks).

**Budget.** One `vnoise` is 8 hashes; no surface uses more than 6 per pixel (`bumpNormal` counts as 4). That sounds
cheap, but in the tunnel pipes overlap many layers deep and hidden layers are shaded too, so each one is paid
several times per pixel. The measured cost is under a millisecond (see below).

**Detail that fades with distance.** MSAA smooths triangle edges, but it shades each pixel once, so a pattern finer
than a pixel (grain, speckles, scratches on a distant pipe) turns into sparkling noise that crawls as the camera
moves. `detail(freq)` fades a pattern out as its features shrink towards a pixel, leaving its average. (This is
"frequency clamping", the standard fix for procedural textures.)

**Along the pipe.** Brushed metal needs to know which way the pipe runs, so `place()` also returns a **tangent**:
the axis for a cylinder, the direction of the sweep for a torus (the derivative of `radial`), world up flattened onto
the surface for a ball (a ball has no "along"), and the pot's up for the teapot. The fragment shader straightens it
against the (possibly bumped) normal per pixel.

**Patterns on axis-aligned pipes.** Pipes run along the world axes, so anything built on a grid (the spangle's
cells, value noise's own lattice) can line up with them and show as neat squares. Those patterns are read through
a fixed tilted rotation (`TILT`), which cuts the pipes at odd angles and makes the same grid look irregular.

The surfaces (sizes to keep in mind: pipes are 0.12–0.24 units thick, a cell is 1 unit):

| Surface | What it imitates |
|---|---|
| `Gloss` | glossy paint, with faint grime blotches that vary the sheen ±0.1 and the colour ±3% |
| `Satin` | the same, rougher (0.55) |
| `Polished` | polished metal tinted by the palette colour: smudged patches and fine scratches that run mostly along the pipe |
| `Brushed` | brushed metal: anisotropic GGX (0.25 along, 0.6 across) and a fine grain that varies across the pipe only |
| `WornPaint` | gloss paint chipped and scratched to bare steel (more at joints, in clusters), dirt streaked vertically |
| `Rusty` | paint blistered into rust patches (coverage from wear): two mottled rust tones, pitted bumps, a dark rim |
| `Patina` | copper with soft clouds of green verdigris, more on top where rain sits, brown tarnish at their edge |
| `Galvanized` | zinc-coated steel with a "spangle": ~0.12-unit crystal cells, each its own roughness and brightness |
| `CastIron` | nearly black, rough, with a sand-cast bump and a few brighter speckles |

Valve stems and bolts are `Brushed`; valve wheels stay `Gloss`. The **Finish** setting picks them: Plastic → Gloss,
Metallic → Polished, Weathered → mostly worn paint and rust with some patina, galvanised, cast iron and brushed,
and Mixed → a bit of everything, mostly clean.

**Cost** (`/bench`, 1920×1080, 4× MSAA, fly-through with ~9,000 pieces): Plastic 2.24 → 2.63 ms per frame,
Mixed 2.14 → 2.83 ms. Most of it is the noise; the GGX lighting itself costs about 0.1 ms. The classic style is
untouched (it ignores the new data), and renders pixel-for-pixel as before.

### Turning them off: the original look

The **Surface detail** setting (Graphics group, modern style only) switches all of this off and brings back the
modern look from before the surfaces: one flat colour, metalness and roughness per pipe, a Blinn-Phong highlight,
and the darker floor in `sky()`. Both versions live in the one `PipeFragment` source, split by the preprocessor:

```glsl
#if SURFACES
    Surf s = surface(...);  // noise, GGX, anisotropy...
#else
    float shininess = ...;  // the original Blinn-Phong
#endif
```

`PipeRenderer` compiles it with `#define SURFACES 1` or `0` inserted after the `#version` line
(`Shaders.WithDefine`). The choice is made once, when the shader is compiled, rather than with a uniform and an
`if` at run time, so the "off" shader really is the old one: the noise functions are never called, and the compiler
throws them away. Generating shader *variants* from one source like this is how most engines handle features that
can be switched on and off.

The simulation side has to change too. With surfaces off, `PipeWorld.NextPlainMaterial` hands out the original
materials (for example Mixed picks from the old four finishes instead of the nine surfaces), and valve stems and
bolts go back to plain steel. It also draws from the random generator exactly as the old code did, so a `/shot`
seed grows the same scene as before, and the "off" render matches the pre-surfaces version. The Weathered finish is
nothing but surface detail, so without it it falls back to Mixed; the settings dialog keeps the two consistent
(picking Weathered ticks Surface detail, unticking it moves Weathered to Mixed). The classic style always uses the
original materials.

### Why HDR?

The scene renders into a 16-bit floating-point buffer (`Rgba16f`), so values can go well above 1.0. A specular
highlight might be 5.0. That matters for two reasons:

1. **Bloom** needs to know what's *really* bright, not just "clipped at white".
2. **Tonemapping** can compress the range smoothly instead of clipping hard.

### Linear colour

All lighting maths happens in **linear** colour space, where doubling a value doubles the light. The palette is
written in sRGB (how monitors encode colour), so `PipeWorld.ToLinear` converts with `pow(c, 2.2)`, and the post
pass converts back with `pow(c, 1/2.2)`. Doing lighting in sRGB instead makes highlights look muddy and blends
look wrong.

## Traced reflections

`sky()` is a fake: a metal pipe mirrors a studio that isn't there, and never the pipes right next to it. With the
**Traced reflections** setting (Graphics group, modern style only, off by default), the pipe shader fires one real
reflection ray per pixel into the scene, and if it hits a pipe, shows that pipe instead of the sky. Rays that miss
fall back to `sky()` exactly as before. Metal pipes then mirror their neighbours, and paint does too at grazing
angles. It's all ordinary GLSL 3.3 in the existing shader: no ray-tracing hardware, which OpenGL can't reach anyway.

It's compiled as a shader variant, like the surfaces: `#define TRACED 1` or `0`, with all the new shader code under
`#if TRACED`. With the setting off, the shader is the one from before the feature, and `/shot` renders are
pixel-for-pixel identical to it (checked on a Metallic box scene, a Mixed box scene with surface detail, and two
flight frames).

### Why a grid

Testing a ray against all 10,000 pieces of a tunnel, for every pixel, would take seconds a frame. Ray tracers
normally build a tree of boxes around the geometry (a *BVH*) so a ray only visits the few boxes it passes through.
Here there's something simpler: the pipes already sit on the integer grid, and a piece is at most about a cell long.
So the scene is filed into a **uniform grid** of unit cells, each listing the pieces that touch it, and a ray walks
through the cells in order, testing only what's listed in the cells it crosses. The cost then depends on how far a
ray travels, not on how many pieces there are. A grid is also cheap enough to rebuild from scratch every frame, which
matters: pipes grow every frame, and in flight the whole scene streams past.

**Building it** (`Rendering/ReflectionGrid.cs`, on the CPU, every frame, from the same `PieceLists` the renderer
draws):

- **Cells are centred on integers:** cell (i, j, k) covers i − 0.5 to i + 0.5 on each axis. Pipes' centre lines lie
  on integer coordinates, so each pipe runs down the middle of its cells: a straight step (face to face through one
  cell) is listed in just that cell, and a ball joint in 1. With cells starting at integers, every pipe would run
  along cell walls and be listed in 4 cells.
- **Bounding boxes:** a cylinder's ends are flat discs, so along each axis it only reaches past its end points by
  the disc's extent, `radius × √(1 − a²)` (`a` being the axis direction's component on that axis): a pipe along x
  doesn't stick out along x at all. Padding by the radius on every axis would have put a straight step in 3 cells;
  the tight box made the whole frame 15–20% cheaper with tracing on. A sphere is its centre ± radius; a bend is the
  box of its centre, `Start − Side` and `Start + Axis` (the quarter arc lies in the quadrant between them), plus the
  tube radius; a ring is its centre ± (ring radius + tube radius).
- **Bounds:** the box around all the pieces, but at most 64 cells along each axis. Flying, the tunnel's pieces reach
  further than that as the path turns, and then a 64-cell window centred on the shadow focus (a point about 14 units
  ahead of the camera) is kept. Pieces outside it are too far away, and too fogged, to matter in a reflection.
- **A counting sort:** count how many pieces touch each cell; a running total turns the counts into each cell's
  first slot in one long list of entries; then drop each piece into its cells' slots. Two passes over the pieces,
  one over the cells, and no allocation once the arrays have grown to size.

Measured CPU time, which `/bench` reports (the build and its upload calls, averaged over the timed frames after the
warm-up): 0.02 ms a frame for a box scene of ~1,200 pieces, 0.16 ms for a tunnel of ~7,200, and 0.47 ms for the
densest tunnel, ~21,800 pieces.

**Getting it to the shader.** Two *texture buffers* (a buffer object read in a shader with `texelFetch`, like a 1D
texture of any length): per cell, (first entry, count); per entry, `(kind << 24) | index`, where `index` is the
piece's position in its kind's list. The pieces themselves aren't uploaded again: the instance buffers the draw calls
already use are *also* bound as `RGBA32F` texture buffers, 5 texels (20 floats) per piece. GLSL 3.3 can't pick a
sampler out of an array with an index computed at run time, so each traced kind has its own sampler (`uPiecesCyl`,
`uPiecesSph`, `uPiecesElb`, `uPiecesRing`) and a `switch` picks one. They sit on texture units 2–7; the pipe shader
already uses 0 (AO) and 1 (the shadow map).

### Walking the grid: the 3D DDA

`tracePipes()` visits exactly the cells a ray passes through, in order, with **Amanatides and Woo's 3D DDA**
("digital differential analyser", the same idea as drawing a line on pixels):

```
    ┌─────┬─────┬─────┐     For each axis keep:
    │     │     │   ↗ │       tMax    how far along the ray its next cell wall is
    ├─────┼─────┼──/──┤       tDelta  how far apart that axis's walls are along the ray
    │     │   ↗ │ /   │
    ├─────┼──/──┼─────┤     Each step crosses whichever wall is nearest (the smallest tMax),
    │  o──┼─    │     │     moves one cell along that axis, and adds tDelta to that tMax.
    └─────┴─────┴─────┘
```

No cell is skipped and none is visited twice. A ray that starts outside the grid is first clipped to its box (the
*slab test*: on each axis, the stretch of the ray between the two planes; the ray is inside where all three
overlap). Each cell's pieces are tested and the nearest hit kept, and the walk **stops as soon as the best hit is
nearer than the current cell's exit**: a piece can span cells, so a hit found while searching one cell may lie in a
later one, but once it's inside the current cell, nothing in a later cell can be nearer. It gives up after
`MAX_TRACE_CELLS` (48) cells and shows the sky. To check the walk, a temporary shader also tested every piece by
brute force: the two agreed on every pixel (apart from valve wheels, which the check left out).

### Intersections

Each piece is read with `texelFetch`: start, axis, side, colour, then radius, sweep, metallic and roughness.

- **Sphere:** the nearer root of the quadratic `|o + t·d − c|² = r²`. Only the nearer root: a ray from outside only
  ever sees a ball's outside.
- **Cylinder:** remove everything along the pipe's axis from the ray, and it becomes a 2D ray against a circle: the
  same quadratic. A hit counts if it lands between the ends. **Plus the two end discs:** flanges are short, fat
  cylinders, mostly seen face-on, and without their caps they'd be missing from reflections. A ray from outside can
  only come in through the cap facing it.
- **Bends and rings:** a torus is a quartic, with no closed form worth solving per pixel. Instead the shader
  **sphere-traces its signed distance field (SDF)**: a function that says how far the nearest surface is from any
  point. The ray can safely step that far without passing through anything, then ask again; near a surface the steps
  shrink towards it. At most 16 steps (`ELBOW_STEPS`), stopping at a distance under 0.001, starting where the ray
  enters the piece's bounding sphere. The distance is Inigo Quilez's *capped torus*: in a frame where the arc lies
  in the xy plane, symmetric about +y (so +y points at the middle of the bend), mirror the point to x ≥ 0; if it lies
  beyond the arc's end, measure to the end point, otherwise to the circle; subtract the tube radius. A ring uses the
  same function with a half-angle of π, where it reduces to a plain torus. (It passes `(sin, cos) = (0, −1)`
  exactly: in float, `sin(π)` is −8.7·10⁻⁸, which made the end-point test fire on one side and left a sliver of the
  ring missing.) The normal at the hit is analytic: from the
  nearest point on the centre circle out through the hit point.

**Self-hits.** The ray starts at the pixel's position nudged 0.01 off the surface, and ignores hits nearer than 0.02,
so it doesn't find the surface it starts from. With surface detail, a bumped normal can tip the reflection below the
real surface, into the pipe, so the traced ray is lifted to just above the geometric surface. A ray hitting the
*same* bend on its inner side is a real reflection, and allowed. Pieces are fattened for the ray cone (below), but
never so far that the fattened surface reaches the ray's origin; that's why contacts reflect (see the cone section).

### Which pixels trace

`environment(R, rough, weight)` replaces the `sky(R, rough)` call in both versions of the shading (surface detail on
and off), and everything multiplied onto it afterwards is unchanged, so a miss is exactly the old picture. `weight`
is how visible the reflection will be: the Fresnel reflectance (its largest channel) times the metal boost,
`mix(0.5, 2.4, metallic)`. Head-on paint is 0.04 × 0.5 = 0.02 and never traces; metal always does; paint at a grazing
angle does. The traced pipe is blended in as `weight` goes from 0.2 to 0.3 rather than switched on at a threshold (a
hard switch shows as an edge along paint, at the angle where tracing starts), and faded back to the sky as roughness
goes from 0.35 to 0.75, standing in for a blurred reflection.

### The ray cone: why one ray per pixel sparkled

The first version did just the above. From a distance it looked right, but zoomed in, metal pipes were covered in
**speckles**: pixels showing a reflected pipe next to pixels showing the sky, at random. The grid walk was fine (the
brute-force check above); the problem was sampling. A curved mirror spreads a pixel's view wide: across a pipe 12
pixels wide the reflection direction swings through 180°, so each pixel sees about 15° of the scene, and a thin pipe
a few units away is much smaller than that. One ray per pixel hits it or misses it more or less at random, so the
reflected scene turns to noise, and it would crawl as the camera moves. (MSAA doesn't help: it shades each pixel
once.) It's the same problem as a texture squeezed into fewer pixels than it has texels, which mipmapping solves by
averaging in advance.

The fix treats each ray as a **cone** reaching out to the next pixel's ray, measured with screen-space derivatives
of the reflection direction (`dFdx(R)`, `dFdy(R)`: how much it changes from one pixel to the next). Neighbouring
cones overlap; half that radius, so they only just touched, left more noise.

- Every piece is **fattened** by the cone's radius where the ray passes it, so every pixel whose cone touches a thin
  pipe finds it, not just the few whose centre ray happens to.
- It's blended in by how much of the cone it really fills, `cover = radius ÷ (radius + cone radius)`. A thin, distant
  pipe comes out as a faint, soft streak: roughly the average a finely sampled image would show there.
- The fattening stops at 0.4 units (`MAX_CONE`), because a piece is only listed in the cells it really touches;
  fattened further, it would reach into cells whose lists don't include it. Past that width a cone could slip between
  pieces that should have filled it and the noise would come back, so `cover` fades to 0 as the cone gets there:
  anything reflected smaller than that is left to the sky.
- End discs keep their real radius. Fattened, a pipe's cap would catch the rays leaving the next length of the same
  pipe, whose surface runs right up to it, and dot the pipe with its own colour.
- **The fattening never reaches the ray's origin.** Where a pipe runs into a ball, a ray leaving the pipe next to the
  contact starts within a cone's width of the ball. Fattened by the full width, the ball would contain the ray's
  origin: its near root would fall behind the ray, the ball would be dropped, and reflections vanished exactly where
  pieces touch. So each piece is fattened by `min(width, room)`, where `room` is the distance from the origin to the
  piece's *real* surface less 0.005 (`fatten()`): close to the origin a piece is fattened less, down to not at all,
  and is still found. `cover` uses the width actually added; the fade towards `MAX_CONE` still uses the width the
  cone asked for. A bend is skipped only when the ray starts inside its real tube.

The result: flying, where pipes are big on screen, neighbours reflect cleanly and distant clutter fades to soft
streaks. In a box scene the pipes are thin on screen, so only close neighbours (the balls and pipes around a joint,
pipes running side by side) are resolved and the rest shows the sky as before; at higher resolutions more of it
resolves. The edges of reflected pipes aren't anti-aliased (MSAA only smooths real geometry), and a little fine noise
remains in the busiest reflections, mostly on big, close balls mirroring the tunnel.

### Shading the hit

`shadeHit()` lights the reflected pipe simply: its flat colour, metallic and roughness (no surface pattern), Lambert
diffuse from both lights with the key light shadowed by the shadow map (`keyLightVisibilityAt(p, N)`, the pixel's own
shadow test, asked at the hit point), the hemisphere ambient, and for metal a cheap **second bounce**: the sky it
would mirror, tinted by its colour. Without that, metal seen in metal would be black, since metal has no diffuse. No
highlights, no AO, and the second bounce never traces again. Then fog over the whole path the light travels, camera
to pixel plus pixel to hit, with the main shading's formula, `f(d) = clamp(1 − exp(−k·d²), 0, 0.85)`. The pixel is
fogged again at the end of `main()` over camera-to-pixel, though (reflection included), which the first version
counted twice. So `shadeHit()` adds only the extra: two fog steps leave `(1 − fExtra)(1 − fCam)` of the colour, and
for that to be `1 − fTot`, `fExtra = 1 − (1 − fTot) ÷ (1 − fCam)`. Copper (patina), galvanised zinc and cast iron ignore the
palette colour, so with surface detail on, reflections give them a colour of their own too.

### Depth prepass

Normally a fragment hidden behind a nearer pipe gets shaded and then overwritten, which only wastes cheap shading.
With a ray per fragment it gets expensive, and in the tunnel pipes overlap many layers deep. So with traced
reflections the scene pass first draws every piece's depth only (with the shadow map's depth-only program, the
camera's matrix, and colour writes off), then draws the lit pipes with the depth test at "less than or equal" and
depth writes off: only the frontmost surface at each sample passes, and only it traces. With MSAA the depths match per
sample. Both passes run the same vertex shader, so the depths come out identical. GLSL only promises that across two
programs for outputs declared `invariant` (otherwise the compiler may order or fuse the arithmetic differently per
program, and the depths can differ in the last bit), so with `TRACED` the vertex shader declares
`invariant gl_Position`, and both programs are compiled with that define. The prepass saved 25–30%
of the frame with tracing on (see the table).

### Cost

`/bench`, 1920×1080, 300 frames, RTX 3080, ms per frame. The flight rows use the settings the fly-through is
usually run with here: Mixed finish, tunnel density 40%, flight speed 10, shadows, AO and bloom. The last column
comes from a temporary build without the prepass, measured before the final round of fixes (fattening limit, fog,
`invariant`), which left the other columns within 0.03 ms.

| Scene | Off | On | On, without the depth prepass |
|---|---|---|---|
| Box, Metallic, still camera, 4× MSAA (~1,200 pieces) | 0.89 | 1.60 | 2.20 |
| Flight, 8× MSAA, surface detail off (~7,200 pieces) | 1.81 | 3.12 | 4.13 |
| Flight, 4× MSAA, surface detail on | 1.98 | 3.07 | 4.46 |
| Flight, 8×, density 100% (~21,800 pieces) | 3.83 | 5.78 | |
| Flight, 8×, every pipe Metallic, smooth elbows (~4,900 pieces) | 1.69 | 5.51 | |

The CPU's grid build is included (0.02–0.47 ms, above). The worst case is an all-metal tunnel with curved elbows:
every pixel traces, and bends are the expensive shape (up to 16 SDF steps each). Halving `MAX_TRACE_CELLS` and
`ELBOW_STEPS` together saved 5–11%, at the cost of shorter reflections, so they stayed as they are. All of it fits
well inside a 60 Hz frame (16.7 ms) on this card; on a weaker GPU, or across several big monitors, it's the setting
to leave off.

### Limitations

- One bounce: reflected pipes don't show their own reflections (metal seen in metal shows the sky, as a second
  bounce).
- Reflected pipes are flat-shaded: no surface pattern (rust, scratches), no highlights, no AO.
- Rough reflections aren't blurred, only faded to the sky's soft strips.
- Teapots aren't traced: they're rare, and a mesh with no closed-form intersection.
- One ray per pixel: reflections of thin or distant pipes are faded out rather than resolved (the ray cone above),
  so box scenes show mostly close-up reflections, and the edges of reflected pipes aren't anti-aliased.
- Only the nearest hit is kept. When a thin piece is hit through the cone at low `cover`, the rest of the cone shows
  the sky, not the pipe behind the thin piece, so thin fittings in front of a reflected pipe can leave faint,
  sky-coloured gaps in it.
- The grid covers at most 64 cells a side; in flight, pieces outside that window are never reflected (they're deep
  in the fog).

## MSAA (anti-aliasing)

The scene buffer is **multisampled**: each pixel stores 2, 4 or 8 samples along triangle edges, which smooths
jagged edges. Multisampled buffers can't be read by a shader directly, so after the scene pass `glBlitFramebuffer`
**resolves** them (averages each pixel's samples) into a normal texture for the post passes to read.

## SSAO: screen-space ambient occlusion

**The idea:** ambient light arrives from all directions, so a point surrounded by geometry (where two pipes cross,
or where a pipe enters a ball joint) receives less of it. Real AO would need ray tracing. SSAO estimates it from
the depth buffer alone.

**The prepass** (`GeometryFragment`) draws all pieces again, cheaply, writing each pixel's view-space normal into a
texture, alongside a depth texture. (The main pass can't provide these because it's multisampled.)

**The SSAO pass** (`SsaoFragment`), for each pixel:

1. Rebuild its 3D position from depth: screen position + depth → normalised device coordinates → multiply by
   the inverse projection matrix. That's `viewPos()`.
2. Take 16 points in a hemisphere around the surface normal, within 1 world unit (`AoRadius`). The kernel has more
   points near the centre, because close geometry matters most.
3. Project each point back onto the screen and look up the depth there. If the visible surface is *in front of*
   the point, that direction is blocked.
4. Visibility = 1 − (blocked ÷ 16).

Two details make it look good:

- **Range check:** if the blocking surface is far in front (a pipe much closer to the camera), it isn't really
  occluding. Without this check, every silhouette gets a dark halo. The `inRange` smoothstep fades those out.
- **Noise + blur:** each pixel rotates the 16-point pattern by a random angle (interleaved gradient noise). That
  turns banding into fine noise, which the **blur pass** (`AoBlurFragment`, a depth-aware 5×5 average) then smooths
  away. "Depth-aware" means neighbours at a very different depth are mostly ignored, so AO doesn't bleed across
  edges.

**Using it:** the scene pass reads the blurred AO texture at `gl_FragCoord` and multiplies it into the ambient and
reflection terms, and partly into direct light.

**Tuning story:** the first version used a 0.55 radius and was nearly invisible. Pipes sit a whole unit apart, so
most surfaces had nothing within range. Rendering the raw AO buffer (see *Development workflow* in
ARCHITECTURE.md) showed it was working, just too weak. A 1.0 radius, a squared curve in the blur, and a stronger
effect on direct light made the contact shadows read.

## Depth of field

A camera lens is only sharp at one distance. Something at depth `z` is blurred by an amount proportional to
`|1/focus − 1/z|` (the thin-lens model). The scene tells `Camera.LookAt` where to focus: on the middle of the grid,
with the grid's front face fully blurred.

**Not in flight.** Flying through the tunnel it used to focus about 10 units ahead, and that blurred most of the
screen: the tunnel walls are always right beside the camera, and the thin-lens formula blurs near things hardest
(`1/z` is big when `z` is small). No focus distance fixes that, so the blur fades out over the take-off
(`lensBlur` in `LookAt`), and at zero the renderer skips the pass altogether.

`DofFragment` gathers samples along a **golden-angle spiral** (each sample is rotated 137.5° from the last, which
covers a disc evenly with no pattern). It's based on Dennis Gustafsson's single-pass bokeh DoF:

- A sample contributes if **its own** blur radius reaches the current pixel. This is "scatter as gather": blurry
  foreground objects spill over sharp background, the way they do in a real lens.
- Samples *behind* the pixel have their blur capped, so a sharp foreground edge doesn't melt into a blurry
  background.

**Speckle fix:** HDR highlights are very bright, so the few spiral samples that hit one showed up as dotted rings.
Colours are now compressed with `c / (1 + max(c))` before averaging and expanded afterwards, so every sample counts
roughly equally. (Brian Karis's trick for filtering HDR images.)

## Bloom

Real lenses scatter a little light from bright spots into their surroundings. Bloom fakes that:

1. **Downsample** (`BloomDownFragment`): half size, quarter size, ... six levels. Each step uses the 13-tap filter
   from *Next Generation Post Processing in Call of Duty* (Jimenez, 2014), which avoids the shimmering a simple
   2×2 average gives on moving highlights. The first step also uses a **Karis average** (weighting each group by
   `1/(1 + brightness)`), so a single super-bright pixel can't produce a blinking blob.
2. **Upsample** (`BloomUpFragment`): from the smallest level back up, blur with a 3×3 tent and **add** onto the
   next larger level (additive blending). The result mixes blur sizes from small to huge, which looks much more
   natural than one big blur.
3. **Composite** (in `PostFragment`): `mix(scene, bloom, strength)`. Mixing rather than adding keeps overall
   brightness the same, so light is redistributed, not created. Level 0 holds roughly (level count) × the light,
   so the strength is divided by the level count to stay consistent across screen sizes.

## Post: from HDR to your screen

`PostFragment`, in order:

1. Mix in bloom.
2. **ACES filmic tonemap:** squeezes 0..∞ into 0..1 along an S-curve like film stock: gentle roll-off in
   highlights, slightly punchy mid-tones.
3. **Vignette:** darken the corners a little.
4. **Gamma:** linear → sRGB for the monitor.
5. **Dither:** add ±½ of an 8-bit step of noise. Dark gradients like the background would otherwise show visible
   bands, because 8 bits per channel isn't enough in the dark range.
6. **Fade:** multiply by the scene fade (0 to 1) for transitions. Last, so a faded frame is a dimmed copy of the
   finished picture.

## Classic (lite) style

The lite style renders the way the 1995 original did on the OpenGL hardware of the time, and it's also the cheap
option. The whole frame is one pass:

```
pieces ──► pipes, lit per vertex, into an 8-bit MSAA buffer (black background) ──► resolve ──► screen
```

**Gouraud shading.** The modern style lights every *pixel* (`PipeFragment`). Classic lights every *vertex*
(`ClassicVertex`) and lets the GPU blend the colours across each triangle. With a 12-sided cylinder, that's
12 lighting calculations around the pipe instead of one for every pixel it covers. It's also the visual signature
of 90s 3D: highlights look slightly faceted and "crawl" along the edges as the pipes are viewed from new angles,
because the highlight only exists where it lands on a vertex.

The lighting model is the classic fixed-function one: a single white directional light (think `GL_LIGHT0`), flat
ambient, and a Blinn-Phong highlight with a fixed shininess. Colours are lit directly in sRGB, as old hardware did,
with no HDR, tonemapping or gamma correction. (The simulation stores linear colours for the modern style, so the
classic shader converts them back first.)

**Lower-poly meshes.** 12-sided cylinders and 8×12 spheres instead of 28 and 16×28: about a third of the triangles.

**Sharing code between shaders.** Both styles need identical vertex placement (the cylinder/sphere/torus/teapot
maths). Rather than copying it, `Shaders.Placement` holds the inputs and a `place()` function, and both
`PipeVertex` and `ClassicVertex` are built by string concatenation: `#version` line + `Placement` + their own
`main()`. GLSL has no `#include`, so gluing strings together is the usual approach.

**Cost** (`/bench` on an RTX 3080, with the settings otherwise at their defaults: shadows on, floating camera, the
default pipe counts; the numbers vary by about ±30% run to run as the GPU changes clock speed, but the gaps between
rows are the point):

| Style | 1920×1080 | 5680×1920 (three monitors) |
|---|---|---|
| Classic, no AA | ~0.2 ms/frame | ~0.8 ms/frame |
| Classic, 4x AA | ~0.35 ms/frame | ~1.3 ms/frame |
| Modern, 4x AA, AO + bloom + shadows | ~1.5 ms/frame | ~6.5 ms/frame |
| Modern, 8x AA, AO + bloom + shadows + DoF | ~3 ms/frame | ~24 ms/frame |

A 60 Hz frame is 16.7 ms, so the last row is the one combination that can't hold 60 fps across three monitors:
8× MSAA at that size means resolving 87 million samples a frame, and the depth-of-field gather runs at full
resolution on top. The fly-through tunnel has many more pieces than a box scene (about 10,000 against 1,300 at
the default density); its costs are in the *Shadows*, *Surfaces* and *Traced reflections* sections above.

At 60 fps a frame lasts 16.7 ms, and with VSync the GPU idles for the rest. So in classic mode it's idle more
than 95% of the time.

## Bug story: the black boxes (NaN)

After bloom and depth of field were added, black boxes started flickering in and out, a frame or two at a time.

**Diagnosis.** A temporary check in the post shader painted any pixel whose value was NaN ("not a number") or
infinity magenta if it was in the scene image, and cyan if it was in the bloom image. A temporary command then
rendered 1,800 consecutive frames and counted those pixels. The result: about 1 frame in 150 had a bad pixel.
Every time, it was about 90 pixels in the scene and up to half the screen in bloom.

The 90 was the giveaway. It's the area of one depth-of-field blur disc, so a *single* NaN pixel was going into the
effects. NaN is contagious: any arithmetic with NaN gives NaN, even `mix(a, NaN, 0.0)`. So depth of field spread it
into a disc, and every bloom level spread it further into ever bigger squares. On screen, NaN shows as black.

Turning features off one at a time (AO, DoF, MSAA, fittings, joint style) never made it go away, so the culprit had
to be in the basic pipe shading. It was the Fresnel term:

```glsl
float NdotV = max(dot(N, V), 0.0);
vec3 F = F0 + (1.0 - F0) * pow(1.0 - NdotV, 5.0);
```

Where a surface faces the camera exactly head-on, floating-point rounding can make `dot(N, V)` come out as
1.0000001. Then `1.0 - NdotV` is slightly negative, and in GLSL `pow(x, y)` is undefined for `x < 0`. It returns
NaN. The first version of this project had the same line, but one black pixel for one frame is invisible. The new
effects amplified it.

**Fix.** Clamp both ends: `clamp(dot(N, V), 0.0, 1.0)`. As a safety net, the pipe shader now also replaces any
NaN/infinite result with black before writing it. A related latent bug was fixed at the same time: SSAO marked
background pixels with a depth of `1e6`, but its texture is 16-bit float (maximum 65504), where that becomes
infinity, and the blur's `inf - inf` is NaN.

**Lessons:**
- In HDR pipelines with blur-type effects, one bad pixel becomes a big visible artifact. Guard inputs to `pow`,
  `sqrt`, `normalize` and divisions.
- Know your formats' ranges. 16-bit floats top out at 65504.
- Count problems, don't eyeball them. An intermittent bug became a number (12 bad frames out of 1,800), and the fix
  was proven by that number dropping to 0 in every configuration.

## Tuning knobs

| Constant | Where | Effect |
|---|---|---|
| `BloomStrength` | `PipeRenderer.cs` | glow amount (0.12 ÷ level count) |
| `AoRadius` | `PipeRenderer.cs` | how far SSAO looks for occluders (world units) |
| `pow(..., 2.0)` in `AoBlurFragment` | `Shaders.cs` | AO contrast |
| `maxBlur` in `DepthOfFieldPass` | `PipeRenderer.cs` | DoF blur size at 1080p (pixels) |
| `depthOfFocus` in `Scene.UpdateOrbitCamera` | `Scene.cs` | how quickly things blur away from focus |
| light directions/colours | `PipeFragment` (directions: `KeyLight`, `FillLight` in `PipeRenderer.cs`) | the overall lighting look |
| `LightTurnSeconds`, `LightRise*` | `Scene.cs` | how fast and how far the moving light travels |
| `ShadowMapSize` | `PipeRenderer.cs` | shadow sharpness (and memory: 2048² × 3 bytes, about 12–16 MB depending on how the driver pads 24-bit depth) |
| `flightShadowRadius` | `Scene.cs` | how far ahead shadows reach in flight (bigger = blurrier) |
| `PolygonOffset`, normal offset | `ShadowPass`, `keyLightVisibility` | shadow acne vs shadows detaching from their pipes |
| `sky()` | `PipeFragment` | what reflections show |
| one `case` per surface in `surface()` | `PipeFragment` | how each `Surface` looks: noise frequencies (features per unit), thresholds, colours |
| `WeatheredSurfaces`, `MixedSurfaces` | `PipeWorld.cs` | which surfaces each finish uses, and how often |
| wear ranges in `NextMaterial` | `PipeWorld.cs` | how weathered pipes get (rust coverage, chips, patina) |
| `BaseValues` | `PipeWorld.cs` | each surface's starting metallic/roughness |
| `detail()` | `PipeFragment` | how early fine patterns fade with distance (sparkle vs. blur) |
| `widen` (specular anti-aliasing) | `PipeFragment` | highlight stability on thin, distant pipes vs. sharpness |
| `MAX_TRACE_CELLS` | `PipeFragment` | how many grid cells a reflection ray walks before giving up on the sky (48): cost vs. how far reflections reach |
| `ELBOW_STEPS` | `PipeFragment` | sphere-tracing steps per bend or ring (16): cost vs. holes in reflected bends |
| `MAX_CONE` | `PipeFragment` | how far pieces are fattened for the ray cone (0.4 units); reflections fade out past it: noise vs. how much of a busy scene is reflected |
| `smoothstep(0.2, 0.3, weight)`, `smoothstep(0.35, 0.75, rough)` in `environment()` | `PipeFragment` | which pixels trace a reflection (paint at grazing angles, metal), and how rough before it fades to the sky |
| `MaxCells` | `ReflectionGrid.cs` | the most grid cells per axis (64); in flight, how far around the shadow focus pieces can be reflected |

## Further reading

- LearnOpenGL: [Instancing](https://learnopengl.com/Advanced-OpenGL/Instancing), [HDR](https://learnopengl.com/Advanced-Lighting/HDR),
  [SSAO](https://learnopengl.com/Advanced-Lighting/SSAO), [Physically Based Bloom](https://learnopengl.com/Guest-Articles/2022/Phys.-Based-Bloom)
- Jorge Jimenez, *Next Generation Post Processing in Call of Duty: Advanced Warfare* (SIGGRAPH 2014): the bloom filters
- Dennis Gustafsson, *Bokeh depth of field in a single pass* (blog post): the DoF approach
- Krzysztof Narkowicz, *ACES Filmic Tone Mapping Curve*: the tonemap fit used here
- John Amanatides and Andrew Woo, *A Fast Voxel Traversal Algorithm for Ray Tracing* (Eurographics 1987): the grid
  walk used by traced reflections
- Inigo Quilez, *Distance functions* (iquilezles.org): the capped torus, and many other shapes' SDFs
