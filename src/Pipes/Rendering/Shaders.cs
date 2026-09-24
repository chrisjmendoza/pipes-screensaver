namespace Pipes.Rendering;

internal static class Shaders
{
    // Instanced pipe pieces. uMode 0 = cylinder (unit, +Y, 0..1), 1 = sphere (unit radius).
    public const string PipeVertex = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec3 iStart;
        layout(location = 3) in vec3 iAxis;
        layout(location = 4) in vec3 iColor;
        layout(location = 5) in float iRadius;

        uniform mat4 uViewProj;
        uniform int uMode;

        out vec3 vWorld;
        out vec3 vNormal;
        out vec3 vColor;

        void main()
        {
            vec3 world;
            vec3 normal;
            if (uMode == 0)
            {
                float len = length(iAxis);
                vec3 y = len > 1e-5 ? iAxis / len : vec3(0.0, 1.0, 0.0);
                vec3 helper = abs(y.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
                vec3 x = normalize(cross(helper, y));
                vec3 z = cross(x, y);
                world = iStart + x * aPos.x * iRadius + y * aPos.y * len + z * aPos.z * iRadius;
                normal = x * aNormal.x + y * aNormal.y + z * aNormal.z;
            }
            else
            {
                world = iStart + aPos * iRadius;
                normal = aNormal;
            }
            vWorld = world;
            vNormal = normal;
            vColor = iColor;
            gl_Position = uViewProj * vec4(world, 1.0);
        }
        """;

    // Glossy shading: two lights, hemispheric ambient, Schlick fresnel and a fake environment
    // reflection (sky gradient) so pipes pick up a sheen instead of flat 90s Gouraud.
    public const string PipeFragment = """
        #version 330 core
        in vec3 vWorld;
        in vec3 vNormal;
        in vec3 vColor;

        uniform vec3 uCameraPos;
        uniform float uMetallic;
        uniform vec3 uFogColor;
        uniform float uFogDensity;

        out vec4 FragColor;

        // Fake "studio" environment for reflections: continuous at the horizon (no seam), with two soft
        // light strips so glossy and metallic pipes get long, readable highlights.
        vec3 sky(vec3 dir)
        {
            vec3 horizon = vec3(0.10, 0.12, 0.18);
            vec3 zenith  = vec3(0.45, 0.55, 0.80);
            vec3 ground  = vec3(0.015, 0.015, 0.02);
            vec3 c = dir.y >= 0.0
                ? mix(horizon, zenith, pow(dir.y, 0.7))
                : mix(horizon, ground, min(-dir.y * 3.0, 1.0));
            float strip1 = smoothstep(0.10, 0.0, abs(dir.y - 0.45)) * smoothstep(-0.2, 0.4, dir.x);
            float strip2 = smoothstep(0.06, 0.0, abs(dir.x + 0.55)) * smoothstep(-0.1, 0.3, dir.y);
            return c + vec3(2.2, 2.1, 2.0) * strip1 + vec3(0.9, 1.0, 1.2) * strip2;
        }

        void main()
        {
            vec3 N = normalize(vNormal);
            vec3 V = normalize(uCameraPos - vWorld);
            if (dot(N, V) < 0.0) N = -N;

            vec3 base = vColor;
            vec3 F0 = mix(vec3(0.05), base, uMetallic);
            float NdotV = max(dot(N, V), 0.0);
            vec3 F = F0 + (1.0 - F0) * pow(1.0 - NdotV, 5.0);

            vec3 lightDirs[2] = vec3[](normalize(vec3(0.5, 0.8, 0.6)), normalize(vec3(-0.7, 0.2, -0.4)));
            vec3 lightCols[2] = vec3[](vec3(2.6, 2.45, 2.25), vec3(0.45, 0.55, 0.8));

            vec3 color = vec3(0.0);
            for (int i = 0; i < 2; i++)
            {
                vec3 L = lightDirs[i];
                vec3 H = normalize(L + V);
                float NdotL = max(dot(N, L), 0.0);
                float spec = pow(max(dot(N, H), 0.0), mix(90.0, 160.0, uMetallic));
                vec3 diffuse = base * (1.0 - uMetallic) * NdotL;
                color += lightCols[i] * (diffuse + F * spec * NdotL * 2.5);
            }

            // Ambient: hemisphere light for the diffuse part, sky reflection for the specular part.
            vec3 hemi = mix(vec3(0.03, 0.03, 0.04), vec3(0.16, 0.18, 0.24), N.y * 0.5 + 0.5);
            color += base * hemi * (1.0 - uMetallic * 0.7);
            vec3 R = reflect(-V, N);
            // Plastic: a faint clear-coat reflection. Metal: the environment tinted by the pipe colour.
            color += sky(R) * F * mix(0.5, 2.4, uMetallic);

            float dist = length(uCameraPos - vWorld);
            float fog = 1.0 - exp(-uFogDensity * dist * dist);
            color = mix(color, uFogColor, clamp(fog, 0.0, 0.85));

            FragColor = vec4(color, 1.0);
        }
        """;

    // Fullscreen triangle, used by the background and post passes.
    public const string FullscreenVertex = """
        #version 330 core
        out vec2 vUv;
        void main()
        {
            vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
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

    // HDR resolve: ACES tonemap, gamma, vignette, fade, and a little dither against banding.
    public const string PostFragment = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uScene;
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
