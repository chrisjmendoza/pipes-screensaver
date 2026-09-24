# Roadmap

## Next up

Nothing is committed to yet. Candidates, roughly smallest first:

- **Snapping growth for classic mode:** the original's pipes grew in visible jumps rather than smoothly. An option
  to quantise growth would complete the nostalgia.
- **"Multiple pipes per colour" mode:** like the original's option, where several pipes share a colour.
- **Fly-through settings:** flight speed and tunnel width are constants in `Scene` and `TunnelSpace`. They could be
  sliders.
- **Per-scene lighting moods:** e.g. a warm sunset key light, cool moonlight, or neon rim lights, picked per scene
  (or per tunnel stretch in fly-through).

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
