namespace Pipes.Rendering;

/// <summary>
/// All GLSL source, as C# raw string literals. See docs/RENDERING.md for how the passes fit together:
/// <code>
///  geometry prepass ─► SSAO ─► AO blur ─┐
///                                        ▼
///  background + pipes (HDR, MSAA) ─► resolve ─► depth of field ─► bloom ─► post (tonemap) ─► screen
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
    /// </summary>
    private const string Placement = """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec3 iStart;
        layout(location = 3) in vec3 iAxis;
        layout(location = 4) in vec3 iSide;
        layout(location = 5) in vec3 iColor;
        layout(location = 6) in vec4 iParams; // radius, sweep (radians), metallic, roughness

        uniform mat4 uViewProj;
        uniform int uMode; // 0 cylinder, 1 sphere, 2 torus section, 3 oriented mesh

        void place(out vec3 world, out vec3 normal)
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
            }
            else if (uMode == 1)
            {
                world = iStart + aPos * radius;
                normal = aNormal;
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
            }
            else
            {
                // Oriented mesh (teapot): +X of the model along iSide, +Y along iAxis. Both are pre-scaled.
                vec3 right = iSide;
                vec3 up = iAxis;
                vec3 fwd = cross(normalize(right), normalize(up)) * length(up);
                world = iStart + right * aPos.x + up * aPos.y + fwd * aPos.z;
                normal = normalize(right) * aNormal.x + normalize(up) * aNormal.y + normalize(fwd) * aNormal.z;
            }
        }
        """;

    /// <summary>Modern vertex shader: place the vertex and pass everything on for per-pixel lighting.</summary>
    public const string PipeVertex = """
        #version 330 core
        """ + NewLine + Placement + NewLine + """

        out vec3 vWorld;
        out vec3 vNormal;
        flat out vec3 vColor;    // "flat": same for the whole triangle, no interpolation needed
        flat out vec2 vMaterial; // metallic, roughness

        void main()
        {
            vec3 world, normal;
            place(world, normal);
            vWorld = world;
            vNormal = normal;
            vColor = iColor;
            vMaterial = iParams.zw;
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
            vec3 world, normal;
            place(world, normal);
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
    /// Shading: two lights, hemispheric ambient, Schlick fresnel and a fake environment reflection, with a
    /// per-pipe metallic/roughness. Not full PBR, but the same ideas, tuned to look good rather than be exact.
    /// </summary>
    public const string PipeFragment = """
        #version 330 core
        in vec3 vWorld;
        in vec3 vNormal;
        flat in vec3 vColor;
        flat in vec2 vMaterial;

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
        // factor, which keeps their total energy about the same (like a real blurry reflection).
        vec3 sky(vec3 dir, float rough)
        {
            vec3 horizon = vec3(0.10, 0.12, 0.18);
            vec3 zenith  = vec3(0.45, 0.55, 0.80);
            vec3 ground  = vec3(0.015, 0.015, 0.02);
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
            vec3 N = normalize(vNormal);
            vec3 V = normalize(uCameraPos - vWorld);
            if (dot(N, V) < 0.0) N = -N; // seeing the inside of something (e.g. a spout): light it as the front

            float metal = vMaterial.x;
            float rough = vMaterial.y;
            vec3 base = vColor;

            // Fresnel: surfaces reflect more at grazing angles. F0 is the head-on reflectance: ~4% for plastic,
            // the surface colour itself for metal.
            vec3 F0 = mix(vec3(0.04), base, metal);
            // Clamp to 1 as well as 0: where a surface faces the camera exactly, rounding can make dot(N, V) a hair
            // over 1, and pow() of the negative (1 - NdotV) below is undefined (NaN). One NaN pixel is invisible on
            // its own, but bloom and depth of field smear it into big black boxes.
            float NdotV = clamp(dot(N, V), 0.0, 1.0);
            vec3 F = F0 + (1.0 - F0) * pow(1.0 - NdotV, 5.0);

            // Blinn-Phong highlight whose tightness comes from roughness. The strength is scaled up a bit for tight
            // highlights so a small highlight is also a bright one.
            float shininess = exp2(mix(8.5, 3.0, rough));
            float specScale = 2.5 * sqrt(clamp(shininess / 120.0, 0.25, 2.0));

            // The key light (warm, from above) casts the shadows; the dim blue fill doesn't, which is what a fill
            // light is for: it keeps the shadowed side from going black.
            vec3 lightDirs[2] = vec3[](uKeyLight, normalize(vec3(-0.7, 0.2, -0.4)));
            vec3 lightCols[2] = vec3[](vec3(2.6, 2.45, 2.25) * keyLightVisibility(N), vec3(0.45, 0.55, 0.8));

            vec3 direct = vec3(0.0);
            for (int i = 0; i < 2; i++)
            {
                vec3 L = lightDirs[i];
                vec3 H = normalize(L + V);
                float NdotL = max(dot(N, L), 0.0);
                float spec = pow(max(dot(N, H), 0.0), shininess);
                vec3 diffuse = base * (1.0 - metal) * NdotL; // metals have no diffuse, only reflection
                direct += lightCols[i] * (diffuse + F * spec * NdotL * specScale);
            }

            float ao = uUseAO == 1 ? texture(uAO, gl_FragCoord.xy * uInvViewport).r : 1.0;

            // Ambient: a hemisphere light (brighter from above) for the diffuse part, and the sky reflection for the
            // specular part. AO darkens these, since they're light arriving from all around. Direct light gets a
            // lighter touch of it (that is really a shadow's job), which still helps contact points read.
            vec3 hemi = mix(vec3(0.03, 0.03, 0.04), vec3(0.16, 0.18, 0.24), N.y * 0.5 + 0.5);
            vec3 ambient = base * hemi * (1.0 - metal * 0.7);
            vec3 R = reflect(-V, N);
            vec3 reflection = sky(R, rough) * F * mix(0.5, 2.4, metal) * mix(1.0, 0.6, rough);

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
    /// Final pass: blend in bloom, map HDR to displayable 0..1 with the ACES filmic curve, gamma-encode, darken
    /// the corners (vignette), fade to black between scenes, and add a tiny noise dither so dark gradients don't
    /// show bands.
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
