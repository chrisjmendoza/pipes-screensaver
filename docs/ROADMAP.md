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

- **Textured/"flex" pipes:** ribbed flexible conduit (a ripple in the cylinder shader's radius along its length),
  or subtle surface textures.
- **Shadows:** real shadows from the key light (a shadow map) would add a lot of depth. SSAO currently does part of
  that job.
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
- A scene per monitor (with portrait-aware grids), and no rendering in the gaps between monitors
- Fly-through: the camera dives into the finished scene and flies on through an endless, self-building tunnel
- Banking: the fly-through camera rolls through turns like a plane (including 180° rolls into dives)
- Flight speed setting
- Roll momentum: banking swings a little past each bank and past level, then eases back
- Pilot feel: overbanks and holds through the corner, leads the roll-out, varies each turn, faint stick wobble
- Sweeping bends and meanders in the flight path, banked by how sharply they curve
- Corkscrews: the flight path spirals, and the camera barrel-rolls through it with a sloping lean
- Flight style slider (zen to wild: straight lengths and maneuver mix), a pilot who varies the speed, and
  momentum in the gentle curves
- Frame pacing that sleeps instead of letting the driver spin: CPU from up to a full core down to 7–15%
