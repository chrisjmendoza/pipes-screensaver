namespace Pipes.Rendering;

/// <summary>
/// All GLSL source, as C# raw string literals. See docs/RENDERING.md for how the passes fit together:
/// <code>
///  shadow map (from the key light) ─────┐
///  geometry prepass ─► SSAO ─► AO blur ─┤
///                                        ▼
///  background + pipes (HDR, MSAA) ─► resolve ─► depth of field (optional) ─► bloom (optional) ─► post (tonemap) ─► screen
/// </code>
/// </summary>
internal static class Shaders
{
    /// <summary>
    /// For gluing shader pieces together: a raw string literal has no trailing newline, and <c>#version</c> must be
    /// alone on its line.
    /// </summary>
    private const string NewLine = "\n";

    // ------------------------------------------------------------------------------------------------------------
    // Pipes
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Shared by the modern and classic vertex shaders: the mesh and per-instance inputs, and <c>place()</c>, which
    /// positions one vertex of one instance. The same function handles every shape; <c>uMode</c> says how to read
    /// the instance data (see <c>PipeInstance</c> in Pieces.cs for what each field means per shape).
    /// <para>
    /// <c>place()</c> also gives a <b>tangent</b>: the direction <em>along</em> the pipe at this vertex. The modern
    /// shader needs it for brushed metal, whose highlights stretch one way and whose grain runs one way. The classic
    /// shader ignores it (the compiler then drops the maths), and it never reads <c>iSurface</c>.
    /// </para>
    /// </summary>
    private const string Placement = """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec3 iStart;
        layout(location = 3) in vec3 iAxis;
        layout(location = 4) in vec3 iSide;
        layout(location = 5) in vec3 iColor;
        layout(location = 6) in vec4 iParams;  // radius, sweep (radians), metallic, roughness
        layout(location = 7) in vec4 iSurface; // surface kind (Surface enum), seed, wear, spare (modern style only)

        uniform mat4 uViewProj;
        uniform int uMode; // 0 cylinder, 1 sphere, 2 torus section, 3 oriented mesh

        void place(out vec3 world, out vec3 normal, out vec3 tangent)
        {
            float radius = iParams.x;

            if (uMode == 0)
            {
                // Cylinder: build an orthonormal frame (x, y, z) with y along the pipe, then stretch the unit
                // cylinder to the pipe's length and radius.
                float len = length(iAxis);
                vec3 y = len > 1e-5 ? iAxis / len : vec3(0.0, 1.0, 0.0);
                vec3 helper = abs(y.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
                vec3 x = normalize(cross(helper, y));
                vec3 z = cross(x, y);
                world = iStart + x * aPos.x * radius + y * aPos.y * len + z * aPos.z * radius;
                normal = x * aNormal.x + y * aNormal.y + z * aNormal.z;
                tangent = y; // the pipe's axis
            }
            else if (uMode == 1)
            {
                world = iStart + aPos * radius;
                normal = aNormal;
                // A ball has no "along". Any direction on the surface will do: world up flattened onto the surface,
                // or +X near the poles where up is (nearly) the normal itself.
                vec3 up = vec3(0.0, 1.0, 0.0) - normal * normal.y;
                tangent = dot(up, up) > 1e-4 ? normalize(up) : vec3(1.0, 0.0, 0.0);
            }
            else if (uMode == 2)
            {
                // Torus section. The mesh stores parameters, not positions: aPos = (t, cos phi, sin phi).
                //   e1, e2  the two in-plane directions; the bend starts at -e2 and sweeps towards +e1
                //   b       the torus axis (perpendicular to the plane of the bend)
                //   radial  direction from the torus centre to the middle of the tube at this point of the sweep
                float bend = length(iAxis);
                vec3 e1 = iAxis / bend;
                vec3 e2 = normalize(iSide);
                vec3 b = cross(e1, e2);
                float theta = aPos.x * iParams.y;
                vec3 radial = -e2 * cos(theta) + e1 * sin(theta);
                normal = radial * aPos.y + b * aPos.z;
                world = iStart + radial * bend + normal * radius;
                // Along the sweep: the derivative of radial with respect to theta.
                tangent = e1 * cos(theta) + e2 * sin(theta);
            }
            else
            {
                // Oriented mesh (teapot): +X of the model along iSide, +Y along iAxis. Both are pre-scaled.
                vec3 right = iSide;
                vec3 up = iAxis;
                vec3 fwd = cross(normalize(right), normalize(up)) * length(up);
                world = iStart + right * aPos.x + up * aPos.y + fwd * aPos.z;
                normal = normalize(right) * aNormal.x + normalize(up) * aNormal.y + normalize(fwd) * aNormal.z;
                tangent = normalize(up);
            }
        }
        """;

    /// <summary>Modern vertex shader: place the vertex and pass everything on for per-pixel lighting.</summary>
    public const string PipeVertex = """
        #version 330 core
        """ + NewLine + Placement + NewLine + """

        out vec3 vWorld;
        out vec3 vNormal;
        out vec3 vTangent;       // along the pipe (see place())
        flat out vec3 vColor;    // "flat": same for the whole triangle, no interpolation needed
        flat out vec2 vMaterial; // metallic, roughness
        flat out vec3 vSurface;  // surface kind, seed, wear

        void main()
        {
            vec3 world, normal, tangent;
            place(world, normal, tangent);
            vWorld = world;
            vNormal = normal;
            vTangent = tangent;
            vColor = iColor;
            vMaterial = iParams.zw;
            vSurface = iSurface.xyz;
            gl_Position = uViewProj * vec4(world, 1.0);
        }
        """;

    /// <summary>
    /// Classic (lite) mode, lit the way 90s OpenGL did it: <b>Gouraud shading</b>. Lighting is worked out once per
    /// <em>vertex</em>, and the GPU just blends the resulting colours across each triangle. It's much cheaper than
    /// per-pixel lighting, and it's the source of the original's look: soft, slightly faceted highlights that
    /// crawl across low-poly pipes as they turn.
    /// <para>
    /// It also skips the modern colour pipeline. Colours go back to their sRGB palette values and are lit directly,
    /// like the fixed-function hardware of the time, with no HDR and no tonemapping.
    /// </para>
    /// </summary>
    public const string ClassicVertex = """
        #version 330 core
        """ + NewLine + Placement + NewLine + """

        uniform vec3 uCameraPos;
        out vec3 vLit;

        void main()
        {
            vec3 world, normal, tangent; // tangent is unused here: classic has no procedural surfaces
            place(world, normal, tangent);
            vec3 N = normalize(normal);
            vec3 V = normalize(uCameraPos - world);
            if (dot(N, V) < 0.0) N = -N;

            // One white light from over the viewer's shoulder, plus flat ambient, like a GL_LIGHT0 setup.
            vec3 L = normalize(vec3(0.35, 0.6, 0.7));
            vec3 H = normalize(L + V);
            vec3 base = pow(iColor, vec3(1.0 / 2.2)); // simulation colours are linear; back to the sRGB palette
            float diffuse = max(dot(N, L), 0.0);
            float spec = pow(max(dot(N, H), 0.0), 40.0);
            vLit = base * (0.18 + 0.82 * diffuse) + vec3(0.7) * spec;

            gl_Position = uViewProj * vec4(world, 1.0);
        }
        """;

    public const string ClassicFragment = """
        #version 330 core
        in vec3 vLit;
        uniform float uFade;
        out vec4 FragColor;
        void main()
        {
            FragColor = vec4(min(vLit, vec3(1.0)) * uFade, 1.0);
        }
        """;

    /// <summary>
    /// Shading: a procedural surface per pipe (paint, metal, rust... computed from noise in world space, since the
    /// meshes have no texture coordinates), then two lights with a GGX specular (anisotropic, for brushed metal),
    /// hemispheric ambient, Schlick fresnel and a fake environment reflection. PBR ideas, tuned to look good rather
    /// than be exact. See docs/RENDERING.md, "Shading" and "Surfaces: procedural texture".
    /// </summary>
    public const string PipeFragment = """
        #version 330 core
        in vec3 vWorld;
        in vec3 vNormal;
        in vec3 vTangent;
        flat in vec3 vColor;
        flat in vec2 vMaterial;
        flat in vec3 vSurface; // surface kind, seed, wear

        uniform int uMode; // the same uniform as the vertex shader's: which shape is being drawn (1, 2 = joints)
        uniform vec3 uCameraPos;
        uniform vec3 uFogColor;
        uniform float uFogDensity;
        uniform sampler2D uAO;      // screen-space ambient occlusion (1 = open, 0 = fully occluded)
        uniform int uUseAO;
        uniform vec2 uInvViewport;  // 1 / framebuffer size, to turn gl_FragCoord into a texture coordinate
        uniform vec3 uKeyLight;     // direction towards the main light

        uniform sampler2DShadow uShadowMap; // depth as seen from the key light (see PipeRenderer.ShadowPass)
        uniform mat4 uLightViewProj;        // world -> shadow map
        uniform int uUseShadow;
        uniform float uShadowTexel;         // world size of one shadow-map texel

        out vec4 FragColor;

        const float PI = 3.14159265;

        // World size of one pixel at this point, set at the top of main(). detail() uses it.
        float gPixel;

        // A fixed rotation, for patterns that come from a grid (a lattice of cells, or value noise's lattice). The
        // pipes run along the world axes, so a grid aligned with them shows up as neat squares; seen through a
        // tilted grid, the same pattern looks irregular.
        const mat3 TILT = mat3(0.7648, -0.4004, 0.5046, 0.6442, 0.4754, -0.5991, 0.0, 0.7833, 0.6216);

        // ---- Noise ----------------------------------------------------------------------------------------------
        // Budget: one vnoise() is 8 hashes plus the blending. No surface evaluates more than 6 vnoise() per pixel
        // (bumpNormal() counts as 4). That matters more than it sounds: in the fly-through tunnel pipes overlap many
        // layers deep, and every hidden layer is shaded too, so each vnoise() is paid several times per pixel.

        // The hash's "random direction". Each component is a whole number of 64ths (831/64, 5007/64, 2414/64: close to
        // the usual 12.9898, 78.233, 37.719), so with lattice coordinates below 289 every product and sum in
        // dot(corner, K) is exact in a 32-bit float. vnoise() relies on that: see there.
        const vec3 HASH_K = vec3(12.984375, 78.234375, 37.71875);

        // A pseudo-random 0..1 from a number: the classic fract(sin(x) * big) hash. Not great randomness, plenty for
        // texture: sin() turns x into a smooth wave, and multiplying by a big number and keeping the fraction turns
        // tiny differences in that wave into unrelated-looking values.
        float hash11(float x) { return fract(sin(x) * 43758.5453); }

        // The same for a point: hash a dot product of it. The mod() keeps sin()'s argument small, because GPUs work
        // out sin() of big numbers imprecisely and in flight the world coordinates keep growing. The pattern repeats
        // every 289 lattice cells, too far apart to notice.
        float hash13(vec3 p) { return hash11(dot(mod(p, 289.0), HASH_K)); }

        // Value noise: a random value at every corner of a unit lattice, blended between the 8 corners around p
        // (trilinear), with a smoothstep fade so the lattice doesn't show as creases. 0..1, blobs about 1 unit across.
        // Speed trick: a corner's hash is sin(dot(corner, K)), and dot() is linear, so each corner's argument is the
        // first corner's plus a constant (K.x for one step in x...): one dot() and one mod() instead of eight. This
        // only works if the sums are exact: sin(x) * 43758 turns a rounding error in the last bit of x into a
        // completely different value, so neighbouring cells would disagree about the corners they share and the
        // noise would break into blocks. HASH_K is chosen to keep it exact. The one price is a seam every 289 cells,
        // where mod() wraps the first corner but not its neighbours; at the sizes used here that's a line every few
        // units in grain nobody can follow, or hundreds of units apart.
        float vnoise(vec3 p)
        {
            vec3 i = floor(p);
            vec3 f = fract(p);
            vec3 u = f * f * (3.0 - 2.0 * f);
            float n = dot(mod(i, 289.0), HASH_K);
            return mix(mix(mix(hash11(n),                       hash11(n + HASH_K.x), u.x),
                           mix(hash11(n + HASH_K.y),            hash11(n + HASH_K.x + HASH_K.y), u.x), u.y),
                       mix(mix(hash11(n + HASH_K.z),            hash11(n + HASH_K.x + HASH_K.z), u.x),
                           mix(hash11(n + HASH_K.y + HASH_K.z), hash11(n + HASH_K.x + HASH_K.y + HASH_K.z), u.x), u.y), u.z);
        }

        // Fractal noise ("fbm"): octaves of vnoise, each at double the frequency and half the amplitude, so there's
        // detail at several scales (big blotches with ragged edges). Scaled back to 0..1; values bunch around 0.5.
        float fbm(vec3 p, int octaves)
        {
            float sum = 0.0, amp = 0.5, total = 0.0;
            for (int i = 0; i < octaves; i++)
            {
                sum += vnoise(p) * amp;
                total += amp;
                amp *= 0.5;
                p = p * 2.02 + vec3(17.3); // shift each octave so the lattices don't line up
            }
            return sum / total;
        }

        // How much of a pattern with `freq` features per unit to show: 1 while a feature spans several pixels, fading
        // to 0 as it shrinks towards a pixel. MSAA doesn't help inside a triangle (it shades once per pixel), so fine
        // grain on a distant pipe would otherwise turn into sparkling, crawling noise. Fading detail to its average
        // before it gets that small is the standard fix for procedural textures ("frequency clamping").
        float detail(float freq)
        {
            return 1.0 - smoothstep(0.2, 0.5, gPixel * freq);
        }

        // A bump map computed on the fly: treat vnoise as a height field over the surface and tilt the normal down
        // its slope, so the surface looks pitted or grainy with no extra geometry. The slope comes from finite
        // differences: the noise here and a small step away along world x, y and z (4 vnoise). `height` returns the
        // noise itself, so a surface can colour its pits without paying for another vnoise.
        // Value noise flattens out at every lattice plane (the smoothstep fade has zero slope there), so its bumps
        // would line up in a grid along an axis-aligned pipe: the noise is read through the TILTed lattice instead.
        vec3 bumpNormal(vec3 N, vec3 p, float freq, float strength, out float height)
        {
            vec3 q = TILT * p * freq;
            const float e = 0.1;
            height = vnoise(q);
            vec3 slope = (vec3(vnoise(q + TILT[0] * e), vnoise(q + TILT[1] * e), vnoise(q + TILT[2] * e)) - height) / e;
            slope -= N * dot(slope, N); // only the part along the surface tilts it
            return normalize(N - slope * strength * detail(freq));
        }

        // ---- Surfaces ---------------------------------------------------------------------------------------------

        // What a surface looks like at one point. Roughness has two values for anisotropy: brushed metal is smoother
        // along the pipe than across it. For everything else they're equal.
        struct Surf { vec3 albedo; float metallic; float roughAlong; float roughAcross; vec3 N; };

        // The procedural surfaces (the Surface enum in Pieces.cs). p is the world position shifted by the pipe's
        // seed, so two pipes don't share a pattern, but a pipe's elbows and balls continue its own pattern. N is the
        // surface normal, T the direction along the pipe, and base/metal/rough the pipe's palette colour and base
        // values. Sizes to keep in mind: pipes are 0.12-0.24 units thick, and a grid cell is 1 unit.
        Surf surface(int kind, vec3 p, vec3 N, vec3 T, vec3 base, float metal, float rough, float wear)
        {
            Surf s = Surf(base, metal, rough, rough, N);
            bool joint = uMode == 1 || uMode == 2; // balls and bends get knocked about more than straight runs
            float h; // bump heights

            switch (kind)
            {
            case 0: // Gloss: glossy paint
            case 1: // Satin: the same paint, rougher (the base roughness says which)
            {
                // Faint grime and fingerprints: soft blotches where the sheen is a little duller and the colour a
                // touch darker. Subtle on purpose; it just stops the paint looking like perfect plastic. One octave
                // is plenty for something this faint, and this is the default finish, so it's worth keeping cheap.
                float g = clamp((vnoise(p * 3.0) - 0.5) * 2.5, -1.0, 1.0);
                s.roughAlong = s.roughAcross = rough + 0.1 * g;
                s.albedo = base * (1.0 - 0.03 * g);
                break;
            }
            case 2: // Polished: polished metal, tinted by the palette colour
            {
                // Smudges: patches where handling has dulled the shine.
                float smudge = smoothstep(0.5, 0.72, fbm(p * 2.5, 2));
                // Fine scratches. Value noise crosses 0.5 along thin winding lines, so "close to 0.5" draws lines.
                // Squashing the noise along the pipe makes the lines run mostly along it, like something slid
                // through, and a coarse mask keeps them in clusters.
                vec3 q = p - T * dot(p, T) * 0.9;
                float line = 1.0 - abs(vnoise(q * 24.0) * 2.0 - 1.0);
                float scratch = smoothstep(0.94, 0.985, line) * step(0.55, vnoise(p * 3.1 + 5.0)) * detail(24.0);
                s.metallic = 0.9;
                s.roughAlong = s.roughAcross = rough + 0.2 * smudge + 0.15 * scratch;
                break;
            }
            case 3: // Brushed: metal polished with an abrasive along the pipe
            {
                // The abrasive leaves fine parallel grooves. They spread reflections across the grooves much more
                // than along them (anisotropic roughness: the highlight stretches around the pipe), and they show
                // as a grain that changes across the pipe but hardly at all along it. Squashing the along-pipe
                // coordinate to 3% gives exactly that noise; its value tilts the normal sideways, groove by groove.
                vec3 q = p - T * dot(p, T) * 0.97;
                float grain = vnoise(q * 60.0) * 0.6 + vnoise(q * 150.0 + 3.0) * 0.4;
                vec3 across = normalize(cross(N, T));
                s.N = normalize(N + across * (grain - 0.5) * 0.5 * detail(60.0));
                s.albedo = base * (1.0 + (grain - 0.5) * 0.6 * detail(60.0));
                s.roughAlong = rough;
                s.roughAcross = 0.6;
                break;
            }
            case 4: // WornPaint: glossy paint, chipped and scratched down to bare steel, with dirt streaks
            {
                // Chips: where fbm rises past a threshold, the paint is gone. The threshold drops with wear (more
                // chips), further at joints, and it wanders with a slow noise so chips come in clusters (a knocked
                // corner) with clean stretches between. smoothstep over a narrow band gives paint's hard broken edge.
                float n = fbm(p * 8.0, 3); // the third octave makes the chips' outlines ragged
                float cluster = vnoise(p * 1.1 + 3.0);
                float threshold = mix(0.78, 0.62, wear) + (0.5 - cluster) * 0.3 - (joint ? 0.04 : 0.0);
                float chip = smoothstep(threshold, threshold + 0.012, n);
                // Scratches through the paint: thin winding lines where vnoise is close to 0.5.
                float line = 1.0 - abs(vnoise(p * 9.0 + 7.0) * 2.0 - 1.0);
                float scratch = smoothstep(0.985 - 0.02 * wear, 0.995, line) * detail(9.0 * 20.0);
                float bare = max(chip, scratch);
                // A thin darker rim round each chip, where the paint edge has lifted and collected grime.
                float rim = smoothstep(threshold - 0.035, threshold, n) * (1.0 - bare);
                // Dirt: noise stretched vertically runs in streaks, as if washed down the pipe by rain.
                float dirt = smoothstep(0.35, 0.8, vnoise(vec3(p.x * 6.0, p.y * 0.7, p.z * 6.0)));
                vec3 paint = base * (1.0 - 0.5 * dirt) * (1.0 - 0.4 * rim);
                float paintRough = rough + 0.35 * dirt + 0.2 * rim;
                s.albedo = mix(paint, vec3(0.26, 0.26, 0.27), bare);         // bare steel, a little dulled
                s.metallic = mix(0.0, 0.9, bare);
                s.roughAlong = mix(paintRough, 0.35, bare);                   // brushed-looking steel
                s.roughAcross = mix(paintRough, 0.55, bare);
                break;
            }
            case 5: // Rusty: paint that has blistered and rusted through in patches
            {
                // Patches about 0.3-0.8 units across: a coarse noise for the patches plus a finer one that makes their
                // edges ragged, with coverage from wear (0.2: a few spots, 1: mostly rust). A hard edge, a thin dark
                // rim just outside it where the paint is lifting and stained, and inside: rough, non-metallic, two
                // rust tones mottled together, pitted.
                float n = (vnoise(p * 2.2) * 2.0 + vnoise(p * 7.5 + 11.0)) / 3.0;
                float threshold = mix(0.7, 0.36, clamp((wear - 0.2) / 0.8, 0.0, 1.0)) - (joint ? 0.03 : 0.0);
                float rust = smoothstep(threshold, threshold + 0.015, n);
                float rim = smoothstep(threshold - 0.03, threshold, n) * (1.0 - rust);
                s.albedo = base * (1.0 - 0.6 * rim) + vec3(0.06, 0.02, 0.0) * rim;
                s.roughAlong = s.roughAcross = rough + 0.3 * rim;
                if (rust > 0.0) // only rust pays for the pitting
                {
                    vec3 pitted = bumpNormal(N, p, 16.0, 0.35, h);
                    float tone = smoothstep(0.25, 0.75, h * 0.7 + (n - threshold) * 1.5);
                    vec3 rustColor = mix(vec3(0.33, 0.10, 0.03), vec3(0.09, 0.03, 0.012), tone); // orange-brown -> dark
                    s.albedo = mix(s.albedo, rustColor, rust);
                    s.metallic = 0.0;
                    s.roughAlong = s.roughAcross = mix(s.roughAlong, 0.85, rust);
                    s.N = normalize(mix(N, pitted, rust));
                }
                break;
            }
            case 6: // Patina: copper going green. Ignores the palette colour.
            {
                // Verdigris (copper carbonate) spreads over the copper in soft-edged clouds, and a little more on top,
                // where rain sits. Just short of the green, the copper has darkened to a brown tarnish first.
                float n = fbm(p * 2.4, 3);
                float threshold = mix(0.64, 0.38, wear) - max(N.y, 0.0) * 0.05;
                float green = smoothstep(threshold - 0.06, threshold + 0.06, n);
                float tarnish = smoothstep(threshold - 0.22, threshold - 0.04, n);
                // Copper's reflectance is about (0.95, 0.64, 0.54). Pushed a little oranger here: the fake sky is
                // blue-white, and with the textbook value the copper read as pale pink.
                vec3 copper = vec3(0.93, 0.56, 0.4) * mix(vec3(1.0), vec3(0.45, 0.3, 0.22), tarnish);
                vec3 verdigris = vec3(0.10, 0.45, 0.38) * (0.75 + 0.5 * vnoise(p * 12.0));
                s.albedo = mix(copper, verdigris, green);
                s.metallic = 1.0 - green;
                s.roughAlong = s.roughAcross = mix(0.3 + 0.15 * tarnish, 0.8, green);
                break;
            }
            case 7: // Galvanized: zinc-coated steel. Ignores the palette colour.
            {
                // Zinc crystallises as it cools into a "spangle" of flat grains, each catching the light a little
                // differently. Cheap version: cut space into ~0.12-unit cubes and give each its own roughness and
                // brightness from a hash. The pipes run along the world axes, so an axis-aligned grid would show as
                // neat squares; the TILTed grid cuts a pipe's surface at odd angles, which gives irregular polygons,
                // and a little noise wobbles their edges.
                float wobble = vnoise(p * 6.0) - 0.5;
                float cell = hash13(floor(TILT * p / 0.12 + vec3(wobble, -wobble, wobble * 0.5) * 0.6));
                float k = detail(1.0 / 0.12);
                float dull = vnoise(p * 1.3); // broad, duller patches of white oxide
                s.albedo = vec3(0.6) * (1.0 + (fract(cell * 7.31) - 0.5) * 0.08 * k) * (1.0 - 0.1 * dull);
                s.metallic = 0.95;
                s.roughAlong = s.roughAcross = 0.4 + (cell - 0.5) * 0.3 * k + 0.1 * dull;
                break;
            }
            case 8: // CastIron: nearly black, rough, sand-cast. Ignores the palette colour.
            {
                // A bumpy normal for the sand-mould texture, and speckles: a few grains brighter than the rest (noise on the
                // TILTed lattice: at this fine scale the pipes' axis-aligned surfaces would show the plain one as blocks).
                s.N = bumpNormal(N, p, 28.0, 0.25, h);
                float speck = smoothstep(0.6, 0.9, vnoise(TILT * p * 90.0)) * detail(90.0);
                s.albedo = vec3(0.04) * (0.8 + 1.5 * speck) * (0.85 + 0.3 * h);
                s.metallic = 0.3;
                s.roughAlong = s.roughAcross = 0.75 + 0.1 * (h - 0.5);
                break;
            }
            }

            s.roughAlong = clamp(s.roughAlong, 0.05, 1.0);
            s.roughAcross = clamp(s.roughAcross, 0.05, 1.0);
            return s;
        }

        // How much of the key light reaches this point: 1 fully lit, 0 fully in shadow.
        float keyLightVisibility(vec3 N)
        {
            if (uUseShadow == 0) return 1.0;

            // "Normal offset": look up a point nudged off the surface along its normal. Without it, the surface
            // compares against its own depth in the map, rounding decides, and it speckles itself with stripes
            // of shadow ("shadow acne"), worst on curved pipes.
            vec3 p = vWorld + N * uShadowTexel * 1.5;
            vec3 uvz = (uLightViewProj * vec4(p, 1.0)).xyz * 0.5 + 0.5; // orthographic, so no divide by w

            // Fade the shadows out towards the map's edge, so where it ends never shows as a line.
            vec2 fromCentre = abs(uvz.xy * 2.0 - 1.0);
            float fade = smoothstep(0.8, 1.0, max(fromCentre.x, fromCentre.y));
            if (fade >= 1.0 || uvz.z >= 1.0) return 1.0;

            // Percentage-closer filtering: a sampler2DShadow compares the given depth against the map and returns
            // how lit it is, and with linear filtering the GPU already blends the 4 nearest texels' answers.
            // 9 such lookups spread over a few texels give a soft edge instead of a jagged one.
            vec2 texel = 1.5 / vec2(textureSize(uShadowMap, 0));
            float lit = 0.0;
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    lit += texture(uShadowMap, vec3(uvz.xy + vec2(x, y) * texel, uvz.z));
            return mix(lit / 9.0, 1.0, fade);
        }

        // Fake "studio" environment for reflections: a sky gradient with two soft light strips, so glossy pipes get
        // long, readable highlights. Rougher surfaces see blurrier strips: we widen them and dim them by the same
        // factor, which keeps their total energy about the same (like a real blurry reflection). Below the horizon,
        // a dim warm "floor" bounce, so the undersides of metal pipes (which show only reflections) don't go black.
        vec3 sky(vec3 dir, float rough)
        {
            vec3 horizon = vec3(0.10, 0.12, 0.18);
            vec3 zenith  = vec3(0.45, 0.55, 0.80);
            vec3 ground  = vec3(0.05, 0.04, 0.035);
            vec3 c = dir.y >= 0.0
                ? mix(horizon, zenith, pow(dir.y, 0.7))
                : mix(horizon, ground, min(-dir.y * 3.0, 1.0));
            float blur = 1.0 + rough * 5.0;
            float strip1 = smoothstep(0.10 * blur, 0.0, abs(dir.y - 0.45)) * smoothstep(-0.2, 0.4, dir.x);
            float strip2 = smoothstep(0.06 * blur, 0.0, abs(dir.x + 0.55)) * smoothstep(-0.1, 0.3, dir.y);
            return c + (vec3(2.2, 2.1, 2.0) * strip1 + vec3(0.9, 1.0, 1.2) * strip2) / blur;
        }

        void main()
        {
            vec3 Ngeo = normalize(vNormal);
            vec3 V = normalize(uCameraPos - vWorld);
            if (dot(Ngeo, V) < 0.0) Ngeo = -Ngeo; // seeing the inside of something (e.g. a spout): light it as the front

            // The surface: colour, metalness, roughness and a (maybe bumped) normal for this pixel. The seed shifts the
            // pattern per pipe; a plain translation is enough.
            gPixel = max(length(dFdx(vWorld)), length(dFdy(vWorld)));
            vec3 p = vWorld + vec3(vSurface.y * 37.0);
            Surf s = surface(int(vSurface.x + 0.5), p, Ngeo, vTangent, vColor, vMaterial.x, vMaterial.y, vSurface.z);
            vec3 N = s.N;

            // Tangent frame for the anisotropic highlight: T along the pipe, B across it, both perpendicular to the
            // (possibly bumped) normal. Gram-Schmidt: remove T's part along N and renormalise. On a ball the tangent can
            // come out (nearly) zero; then any perpendicular will do.
            vec3 T = vTangent - N * dot(N, vTangent);
            T = dot(T, T) > 1e-8 ? normalize(T) : normalize(cross(N, abs(N.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0)));
            vec3 B = cross(N, T);

            // GGX works with alpha = roughness squared (it makes the roughness scale look even to the eye). Then two
            // corrections to the alphas:
            // - Specular anti-aliasing: where the normal changes a lot within one pixel (thin, distant, curved pipes),
            //   a tight highlight can land on one pixel and miss the next, and it sparkles as things move. Widening
            //   the highlight by how much the normal varies across the pixel (Kaplanyan & Tokuyoshi's trick, as used
            //   in Filament) keeps it stable.
            // - A floor, so a highlight never gets so tiny and so bright that it turns into a firefly.
            vec3 dNx = dFdx(Ngeo), dNy = dFdy(Ngeo);
            float widen = min(0.5 * (dot(dNx, dNx) + dot(dNy, dNy)), 0.18);
            float at = max(sqrt(pow(s.roughAlong, 4.0) + widen), 0.004);
            float ab = max(sqrt(pow(s.roughAcross, 4.0) + widen), 0.004);
            float alpha = sqrt(at * ab);    // the isotropic equivalent
            float roughIso = sqrt(alpha);   // ... and back to a roughness, for the environment

            // Fresnel: surfaces reflect more at grazing angles. F0 is the head-on reflectance: ~4% for paint,
            // the surface colour itself for metal.
            vec3 F0 = mix(vec3(0.04), s.albedo, s.metallic);
            // Clamp to 1 as well as 0: where a surface faces the camera exactly, rounding can make dot(N, V) a hair
            // over 1, and pow() of the negative (1 - NdotV) below is undefined (NaN). One NaN pixel is invisible on
            // its own, but bloom and depth of field smear it into big black boxes. (The small minimum also keeps
            // a bumped normal tipped away from the camera from dividing by zero in the visibility term.)
            float NdotV = clamp(dot(N, V), 1e-4, 1.0);

            // The key light (warm, from above) casts the shadows; the dim blue fill doesn't, which is what a fill
            // light is for: it keeps the shadowed side from going black. Shadows use the geometric normal: the bumps
            // are far smaller than a shadow-map texel.
            vec3 lightDirs[2] = vec3[](uKeyLight, normalize(vec3(-0.7, 0.2, -0.4)));
            vec3 lightCols[2] = vec3[](vec3(2.6, 2.45, 2.25) * keyLightVisibility(Ngeo), vec3(0.45, 0.55, 0.8));

            vec3 direct = vec3(0.0);
            for (int i = 0; i < 2; i++)
            {
                vec3 L = lightDirs[i];
                vec3 H = normalize(L + V);
                float NdotL = max(dot(N, L), 0.0);

                // Microfacet specular = D * V * F:
                // D, the GGX distribution: how many microscopic facets face along H, i.e. would mirror the light
                //    into the eye. Anisotropic form (Burley 2012): the highlight stretches along T or B depending on
                //    which alpha is bigger. With at == ab it is ordinary isotropic GGX.
                float Ht = dot(H, T), Hb = dot(H, B), Hn = dot(H, N);
                float d = Ht * Ht / (at * at) + Hb * Hb / (ab * ab) + Hn * Hn;
                float D = 1.0 / (PI * at * ab * d * d);
                // V, visibility: facets shadowing and hiding each other at grazing angles (Hammon's cheap fit of
                //    the height-correlated Smith term).
                float Vis = 0.5 / max(mix(2.0 * NdotL * NdotV, NdotL + NdotV, alpha), 1e-5);
                // F, Schlick's fresnel, with the angle between the view and the facet (H).
                vec3 F = F0 + (1.0 - F0) * pow(1.0 - clamp(dot(V, H), 0.0, 1.0), 5.0);

                vec3 diffuse = s.albedo * (1.0 - s.metallic) * NdotL; // metals have no diffuse, only reflection
                direct += lightCols[i] * (diffuse + D * Vis * F * NdotL);
            }

            float ao = uUseAO == 1 ? texture(uAO, gl_FragCoord.xy * uInvViewport).r : 1.0;

            // Ambient: a hemisphere light (brighter from above) for the diffuse part, and the sky reflection for the
            // specular part. AO darkens these, since they're light arriving from all around. Direct light gets a
            // lighter touch of it (that is really a shadow's job), which still helps contact points read.
            // The reflection's fresnel is the roughness-aware version: a rough surface's grazing reflection is
            // blurred over many directions, so it shouldn't get the bright mirror-like rim a smooth one does.
            vec3 hemi = mix(vec3(0.03, 0.03, 0.04), vec3(0.16, 0.18, 0.24), N.y * 0.5 + 0.5);
            vec3 ambient = s.albedo * hemi * (1.0 - s.metallic * 0.7);
            vec3 R = reflect(-V, N);
            vec3 Fenv = F0 + (max(vec3(1.0 - roughIso), F0) - F0) * pow(1.0 - NdotV, 5.0);
            vec3 reflection = sky(R, roughIso) * Fenv * mix(0.5, 2.4, s.metallic) * mix(1.0, 0.6, roughIso);

            vec3 color = direct * mix(1.0, ao, 0.5) + ambient * ao + reflection * ao;

            float dist = length(uCameraPos - vWorld);
            float fog = 1.0 - exp(-uFogDensity * dist * dist);
            color = mix(color, uFogColor, clamp(fog, 0.0, 0.85));

            // Safety net: never let a NaN or infinity into the HDR buffer, for the same reason as above.
            if (any(isnan(color)) || any(isinf(color))) color = vec3(0.0);
            FragColor = vec4(color, 1.0);
        }
        """;

    /// <summary>
    /// Shadow pass: only depth matters (the GPU writes it without being asked), so there's nothing to do per pixel.
    /// </summary>
    public const string ShadowFragment = """
        #version 330 core
        void main() { }
        """;

    /// <summary>
    /// Geometry prepass: draws the same pieces again (without shading) into a normal buffer + depth buffer. SSAO and
    /// depth of field read these. The main pass is multisampled and can't be sampled directly, hence the separate pass.
    /// </summary>
    public const string GeometryFragment = """
        #version 330 core
        in vec3 vWorld;
        in vec3 vNormal;
        uniform mat4 uView;
        uniform vec3 uCameraPos;
        out vec4 Normal;
        void main()
        {
            vec3 N = normalize(vNormal);
            if (dot(N, uCameraPos - vWorld) < 0.0) N = -N;
            // View-space normal, packed from -1..1 into 0..1 for an unsigned texture format.
            Normal = vec4(normalize(mat3(uView) * N) * 0.5 + 0.5, 1.0);
        }
        """;

    // ------------------------------------------------------------------------------------------------------------
    // Fullscreen passes
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>One triangle that covers the whole screen, generated from gl_VertexID with no vertex buffer.</summary>
    public const string FullscreenVertex = """
        #version 330 core
        out vec2 vUv;
        void main()
        {
            vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2); // (0,0) (2,0) (0,2)
            vUv = p;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    public const string BackgroundFragment = """
        #version 330 core
        in vec2 vUv;
        out vec4 FragColor;
        void main()
        {
            vec3 top = vec3(0.012, 0.016, 0.035);
            vec3 bottom = vec3(0.0, 0.0, 0.0);
            FragColor = vec4(mix(bottom, top, vUv.y), 1.0);
        }
        """;

    /// <summary>Shared helper: rebuild a view-space position from the depth buffer.</summary>
    private const string ViewPosFromDepth = """
        uniform sampler2D uDepth;
        uniform mat4 uInvProj;
        vec3 viewPos(vec2 uv)
        {
            // Screen uv + depth -> normalised device coordinates -> undo the projection.
            float d = texture(uDepth, uv).r;
            vec4 p = uInvProj * vec4(vec3(uv, d) * 2.0 - 1.0, 1.0);
            return p.xyz / p.w;
        }
        """;

    /// <summary>
    /// Screen-space ambient occlusion. For each pixel, scatter points in a hemisphere above the surface (around its
    /// normal) and check each against the depth buffer: if something visible is in front of the point, that
    /// direction is blocked. The fraction of blocked points is how occluded the pixel is. Each pixel rotates the
    /// sample pattern differently (noise), and the blur pass afterwards smooths the noise out.
    /// Output: R = ambient visibility (1 = open), G = view depth (for the blur to respect edges).
    /// </summary>
    public const string SsaoFragment = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uNormal;
        uniform mat4 uProj;
        uniform float uRadius;
        uniform vec3 uKernel[16];
        out vec4 Out;
        """ + ViewPosFromDepth + """

        void main()
        {
            // Background: fully open, and a "very far" depth for the blur. It must fit in a 16-bit float (max 65504);
            // 1e6 would overflow to infinity, and the blur's (inf - inf) is NaN.
            if (texture(uDepth, vUv).r >= 1.0) { Out = vec4(1.0, 60000.0, 0.0, 0.0); return; }

            vec3 P = viewPos(vUv);
            vec3 N = normalize(texture(uNormal, vUv).xyz * 2.0 - 1.0);

            // Interleaved gradient noise: a cheap per-pixel random angle that blurs away nicely.
            float noise = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
            float angle = noise * 6.2831853;
            vec3 r = vec3(cos(angle), sin(angle), 0.0);
            vec3 T = r - N * dot(r, N);
            T = dot(T, T) < 1e-4 ? normalize(cross(N, vec3(0.0, 1.0, 0.0))) : normalize(T);
            mat3 TBN = mat3(T, cross(N, T), N); // hemisphere space -> view space

            float occlusion = 0.0;
            for (int i = 0; i < 16; i++)
            {
                vec3 S = P + TBN * uKernel[i] * uRadius;
                vec4 clip = uProj * vec4(S, 1.0);
                vec2 suv = clip.xy / clip.w * 0.5 + 0.5;
                float sceneZ = viewPos(suv).z;
                // View space looks down -Z, so "in front of the sample" means a larger (less negative) z.
                float blocked = sceneZ >= S.z + 0.02 ? 1.0 : 0.0;
                // Ignore occluders far outside the radius (e.g. a pipe far in front), or edges get dark halos.
                float inRange = smoothstep(0.0, 1.0, uRadius / abs(P.z - sceneZ));
                occlusion += blocked * inRange;
            }
            Out = vec4(1.0 - occlusion / 16.0, -P.z, 0.0, 0.0);
        }
        """;

    /// <summary>
    /// Depth-aware blur of the SSAO result: averages a 5x5 neighbourhood, but only neighbours at a similar depth,
    /// so a pipe's AO doesn't smear onto the pipe behind it.
    /// </summary>
    public const string AoBlurFragment = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uAO;
        uniform vec2 uTexel;
        out vec4 Out;
        void main()
        {
            vec2 centre = texture(uAO, vUv).rg;
            float sum = 0.0, weight = 0.0;
            for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                vec2 s = texture(uAO, vUv + vec2(x, y) * uTexel).rg;
                float w = 1.0 / (1.0 + abs(s.g - centre.g) * 8.0);
                sum += s.r * w;
                weight += w;
            }
            // A gentle curve deepens the creases a little without darkening open areas.
            Out = vec4(pow(sum / weight, 2.0), centre.g, 0.0, 0.0);
        }
        """;

    /// <summary>
    /// Depth of field, gathered in one pass (after Dennis Gustafsson's "Bokeh depth of field in a single pass").
    /// Each pixel collects samples along a golden-angle spiral. A sample counts if its own blur size reaches this
    /// far, which makes blurry things spread onto sharp ones naturally. Samples behind the pixel get their blur
    /// capped so a sharp foreground edge doesn't dissolve into the background.
    /// </summary>
    public const string DofFragment = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uScene;
        uniform vec2 uTexel;
        uniform float uFocus;
        uniform float uFocusScale;
        uniform float uMaxBlur;  // pixels
        uniform float uRadScale; // spiral spacing; bigger = fewer samples
        out vec4 Out;
        """ + ViewPosFromDepth + """

        float blurSize(float depth)
        {
            return clamp(abs(1.0 / uFocus - 1.0 / depth) * uFocusScale, 0.0, 1.0) * uMaxBlur;
        }

        // HDR highlights can be 10x brighter than their surroundings. Averaged directly, the few samples that land
        // on one show up as bright dots scattered around the blur. Squashing brightness before averaging (and
        // un-squashing after) makes each sample count about equally, which removes the speckles.
        vec3 compress(vec3 c) { return c / (1.0 + max(c.r, max(c.g, c.b))); }
        vec3 expand(vec3 c) { return c / max(1.0 - max(c.r, max(c.g, c.b)), 1e-3); }

        void main()
        {
            float centreDepth = -viewPos(vUv).z;
            float centreSize = blurSize(centreDepth);
            vec3 color = compress(texture(uScene, vUv).rgb);
            float total = 1.0;
            float radius = uRadScale;
            float angle = 0.0;
            for (int i = 0; i < 200 && radius < uMaxBlur; i++)
            {
                vec2 uv = vUv + vec2(cos(angle), sin(angle)) * uTexel * radius;
                vec3 sampleColor = compress(texture(uScene, uv).rgb);
                float sampleDepth = -viewPos(uv).z;
                float sampleSize = blurSize(sampleDepth);
                if (sampleDepth > centreDepth) sampleSize = clamp(sampleSize, 0.0, centreSize * 2.0);
                float m = smoothstep(radius - 0.5, radius + 0.5, sampleSize);
                color += mix(color / total, sampleColor, m);
                total += 1.0;
                radius += uRadScale / radius; // spiral outwards, evenly covering the disc
                angle += 2.39996323;          // golden angle
            }
            Out = vec4(expand(color / total), 1.0);
        }
        """;

    /// <summary>
    /// Bloom, step 1: shrink the image by half, many times over, into a chain of smaller and smaller textures.
    /// Uses the 13-tap filter from Jimenez's "Next Generation Post Processing in Call of Duty" (2014), which avoids
    /// the flicker a plain 2x2 average gives. On the first (full-size) step, each group of samples is weighted
    /// down by its brightness (a "Karis average") so single ultra-bright pixels can't make blinking blobs.
    /// </summary>
    public const string BloomDownFragment = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uSource;
        uniform vec2 uTexel; // of the source
        uniform int uFirst;
        out vec4 Out;

        vec3 s(float x, float y) { return texture(uSource, vUv + vec2(x, y) * uTexel).rgb; }
        float karis(vec3 c) { return 1.0 / (1.0 + dot(c, vec3(0.2126, 0.7152, 0.0722))); }

        void main()
        {
            vec3 a = s(-2, 2), b = s(0, 2), c = s(2, 2);
            vec3 d = s(-2, 0), e = s(0, 0), f = s(2, 0);
            vec3 g = s(-2, -2), h = s(0, -2), i = s(2, -2);
            vec3 j = s(-1, 1), k = s(1, 1), l = s(-1, -1), m = s(1, -1);

            // Five overlapping 2x2 groups: the inner one (j k l m) and four corner ones.
            vec3 g0 = (j + k + l + m) * 0.25;
            vec3 g1 = (a + b + d + e) * 0.25;
            vec3 g2 = (b + c + e + f) * 0.25;
            vec3 g3 = (d + e + g + h) * 0.25;
            vec3 g4 = (e + f + h + i) * 0.25;

            if (uFirst == 1)
            {
                float w0 = 0.5 * karis(g0), w1 = 0.125 * karis(g1), w2 = 0.125 * karis(g2), w3 = 0.125 * karis(g3), w4 = 0.125 * karis(g4);
                Out = vec4((g0 * w0 + g1 * w1 + g2 * w2 + g3 * w3 + g4 * w4) / (w0 + w1 + w2 + w3 + w4), 1.0);
            }
            else
            {
                Out = vec4(g0 * 0.5 + (g1 + g2 + g3 + g4) * 0.125, 1.0);
            }
        }
        """;

    /// <summary>
    /// Bloom, step 2: walk back up the chain, blurring each small texture with a 3x3 tent filter and adding it
    /// onto the next bigger one (additive blending is switched on for this). The result is a wide, smooth glow made
    /// of many blur sizes at once.
    /// </summary>
    public const string BloomUpFragment = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uSource;
        uniform vec2 uTexel; // of the source
        out vec4 Out;
        vec3 s(float x, float y) { return texture(uSource, vUv + vec2(x, y) * uTexel).rgb; }
        void main()
        {
            vec3 sum = s(0, 0) * 4.0
                     + (s(-1, 0) + s(1, 0) + s(0, -1) + s(0, 1)) * 2.0
                     + (s(-1, -1) + s(1, -1) + s(-1, 1) + s(1, 1));
            Out = vec4(sum / 16.0, 1.0);
        }
        """;

    /// <summary>
    /// Final pass: blend in bloom, map HDR to displayable 0..1 with the ACES filmic curve, darken the corners
    /// (vignette), gamma-encode, add a tiny noise dither so dark gradients don't show bands, then fade to black
    /// between scenes.
    /// </summary>
    public const string PostFragment = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uScene;
        uniform sampler2D uBloom;
        uniform float uBloomStrength;
        uniform float uFade;
        uniform float uAspect;
        out vec4 FragColor;

        vec3 aces(vec3 x)
        {
            const float a = 2.51, b = 0.03, c = 2.43, d = 0.59, e = 0.14;
            return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
        }

        void main()
        {
            vec3 hdr = texture(uScene, vUv).rgb;
            // Mixing (rather than adding) keeps overall brightness the same: a little of every pixel's light is
            // redistributed into its surroundings, like light scattering in a real lens.
            if (uBloomStrength > 0.0) hdr = mix(hdr, texture(uBloom, vUv).rgb, uBloomStrength);
            vec3 color = aces(hdr * 1.1);
            vec2 v = (vUv - 0.5) * vec2(uAspect, 1.0);
            color *= mix(1.0, 0.72, smoothstep(0.35, 1.1, length(v)));
            color = pow(color, vec3(1.0 / 2.2));
            float n = fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453);
            color += (n - 0.5) / 255.0;
            FragColor = vec4(color * uFade, 1.0);
        }
        """;
}
