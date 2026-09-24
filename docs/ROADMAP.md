# Roadmap

## Next up

Nothing is committed to yet. Candidates, roughly smallest first:

- **Snapping growth for classic mode:** the original's pipes grew in visible jumps rather than smoothly. An option
  to quantise growth would complete the nostalgia.
- **"Multiple pipes per colour" mode:** like the original's option, where several pipes share a colour.
- **Lower CPU while waiting for VSync:** the frame itself takes well under a millisecond of CPU and about 1 ms of
  GPU, but NVIDIA's driver sometimes *spins* (keeping a CPU core at 100%) while it waits for the monitor's refresh,
  rather than sleeping. Measurements on the same settings went from 10% to 105% between runs. The fix is to wait
  for the vertical blank ourselves with a sleeping call (`D3DKMTWaitForVerticalBlankEvent` on the window's
  monitor), so the driver never has to wait. Calling `DwmFlush()` with the driver's VSync off also worked (about
  12% CPU), but the compositor ran at the fastest monitor's rate (75 Hz) instead of the window's (60 Hz), which
  would judder.
- **Tunnel width setting:** the tunnel's inner and outer radius are constants in `TunnelSpace`. They could be a
  slider, like flight speed.
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
- Flight speed setting
