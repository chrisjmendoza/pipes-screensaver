# Roadmap

## Next up

Nothing is committed to yet. Candidates, roughly smallest first:

- **Snapping growth for classic mode:** the original's pipes grew in visible jumps rather than smoothly. An option
  to quantise growth would complete the nostalgia.
- **"Multiple pipes per colour" mode:** like the original's option, where several pipes share a colour.
- **Tunnel width setting:** the tunnel's inner and outer radius are constants in `TunnelSpace`. They could be a
  slider, like flight speed.
- **Per-scene lighting moods:** e.g. a warm sunset key light, cool moonlight, or neon rim lights, picked per scene
  (or per tunnel stretch in fly-through).

## Known issues

- **One-off ~200 ms stall about 30 seconds in.** Seen on the development PC (NVIDIA, three monitors at mixed 60/75
  Hz) in roughly half of test runs, entirely inside the driver, and always at about the same time after start
  rather than at any particular moment in the scene. It isn't garbage collection (none happens in that frame) and
  wasn't caused by frame pacing (it also happens with it switched off). Most likely the GPU dropping to a lower
  power state once it notices how little work it's doing, which is known to briefly stall mixed-refresh
  multi-monitor setups. It happens at most once per run.

## Ideas

- **Ray-traced reflections:** the hardware ray tracing on RTX-class cards can't be reached from OpenGL (it needs
  Vulkan or DirectX 12), but reflections can be traced in our own shader code: the pipes sit on a grid, and a grid
  is a ready-made structure for stepping a reflection ray cell by cell, testing only the few pipes in each. Metal
  pipes would then mirror the coloured pipes around them. Probably several milliseconds a frame, so an option that
  stays off on weak GPUs; the curved elbows are the hard part to trace exactly. Start with the box scenes, where
  metal pipes sit close enough to reflect each other.
- **Presets:** a dropdown of starting points (Default, Classic 1995, Zen flight, Wild flight, Low power), and
  perhaps "save current as" for the user's own.
- **"Flex" pipes:** ribbed flexible conduit (a ripple in the cylinder shader's radius along its length).
- **Performance mode:** half-resolution SSAO and DoF for integrated GPUs and very large multi-monitor setups.

## Done

- Curved elbows, ball joints, or mixed
- Variable pipe thickness
- Per-pipe materials (glossy/satin plastic, polished/brushed metal)
- Procedural surfaces: grime, worn and chipped paint, rust, copper patina, galvanised spangle, cast iron; GGX
  highlights, anisotropic for brushed metal
- Fittings: valves, couplings, flanges; tee junctions with branch pipes
- The teapot easter egg
- SSAO, bloom, depth of field
- Shadows from the key light (shadow map with soft edges), including dappled light in the fly-through tunnel
- Smoother take-off: the tunnel mouth builds along with the box, no pause, and the spawn band catches up instead of
  leaving a hole
- Depth of field off in flight; two-column settings dialog with Reset to defaults, and a Cancel that closes it
- Camera modes: still, orbit, float
- Classic (lite) style: the original's look at about a tenth of the GPU cost
- A scene per monitor (with portrait-aware grids), and no rendering in the gaps between monitors
- Fly-through: the camera takes off as the scene finishes building and flies on through an endless, self-building
  tunnel
- Banking: the fly-through camera rolls through turns like a plane (including 180° rolls into dives)
- Flight speed setting
- Roll momentum: banking swings a little past each bank and past level, then eases back
- Pilot feel: overbanks and holds through the corner, leads the roll-out, varies each turn, faint stick wobble
- Sweeping bends and meanders in the flight path, banked by how sharply they curve
- Corkscrews: the flight path spirals, and the camera barrel-rolls through it with a sloping lean
- Course complexity slider (zen to wild: straight lengths and maneuver mix), tunnel density slider, a pilot who varies the speed, and
  momentum in the gentle curves
- Frame pacing that sleeps instead of letting the driver spin: CPU from up to a full core down to 7–15%
