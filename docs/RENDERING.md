# Rendering

How a frame gets drawn, one pass at a time. The code is in `src/Pipes/Rendering/`: `PipeRenderer.cs` runs the
passes, and `Shaders.cs` holds the GLSL for each one.

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
   └────────────────────────────────────────► │ scene: background + lit pipes │  HDR, MSAA
                             │                └───────────────────────────────┘
                             │                                │ resolve (average MSAA samples)
                             │                                ▼
                             └──────────────────────► depth of field (optional)
                                                              ▼
                                                   bloom: down ×6, up ×5 (optional)
                                                              ▼
                                   post: bloom mix, tonemap, vignette, gamma, dither, fade ──► screen
```

The prepass only runs if SSAO or depth of field is on, and each optional pass only runs (and only allocates its
textures) if enabled.

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
  to the texel size in the light's view (`LightViewProj`).
- **No visible edge.** Shadows fade out over the outer 10% of the map, so where it ends never shows as a line.

**In the tunnel,** a sun from above should mostly be blocked by the tunnel wall, and it is: the key light only gets
in through gaps between pipes, which dapples the pipes like sunlight through a forest canopy. Ambient and fill light
keep the rest colourful rather than murky.

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
next section). Then:

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
  gradient plus two bright "studio light" strips, and a dim warm floor below the horizon so the undersides of
  metal pipes (which show only reflections) don't go black. Rough materials widen the strips and dim them by the
  same factor, which fakes a blurred reflection. It uses the isotropic roughness (brushed metal's two values
  averaged), and a roughness-aware Fresnel, `F0 + (max(1 − rough, F0) − F0)(1 − N·V)^5`, so rough surfaces
  don't get a mirror-bright rim.
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
the default density); its costs are in the *Shadows* and *Surfaces* sections above.

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
| light directions/colours | `PipeFragment` (key light: `KeyLight` in `PipeRenderer.cs`) | the overall lighting look |
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

## Further reading

- LearnOpenGL: [Instancing](https://learnopengl.com/Advanced-OpenGL/Instancing), [HDR](https://learnopengl.com/Advanced-Lighting/HDR),
  [SSAO](https://learnopengl.com/Advanced-Lighting/SSAO), [Physically Based Bloom](https://learnopengl.com/Guest-Articles/2022/Phys.-Based-Bloom)
- Jorge Jimenez, *Next Generation Post Processing in Call of Duty: Advanced Warfare* (SIGGRAPH 2014): the bloom filters
- Dennis Gustafsson, *Bokeh depth of field in a single pass* (blog post): the DoF approach
- Krzysztof Narkowicz, *ACES Filmic Tone Mapping Curve*: the tonemap fit used here
