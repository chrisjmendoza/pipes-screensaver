using System.Numerics;
using System.Runtime.InteropServices;
using Pipes.Simulation;
using Silk.NET.OpenGL;

namespace Pipes.Rendering;

/// <summary>
/// How the renderer is set up. Fixed for the renderer's lifetime. <see cref="Classic"/> is the lite mode, which
/// turns every effect off regardless of the other switches. <see cref="Surfaces"/> picks which version of the modern
/// pipe shader to compile: procedural surfaces, or the original flat shading.
/// </summary>
internal readonly record struct RenderOptions(
    int Samples, bool AmbientOcclusion, bool Bloom, bool DepthOfField, bool Shadows, bool Classic, bool Surfaces)
{
    public static RenderOptions From(PipesSettings s) => s.Style == GraphicsStyle.Classic
        ? new(s.Antialiasing, false, false, false, false, Classic: true, Surfaces: false)
        : new(s.Antialiasing, s.AmbientOcclusion, s.Bloom, s.DepthOfField, s.Shadows, Classic: false, s.SurfaceDetail);
}

/// <summary>
/// Draws a frame. Every shape is one instanced draw call.
/// <para>
/// <b>Classic (lite) style</b> is a single pass: pipes with per-vertex lighting on low-poly meshes into a plain 8-bit
/// buffer, copied to the screen. No HDR, no effects.
/// </para>
/// <b>Modern style</b> adds fullscreen effect passes over offscreen textures. In order:
/// <list type="number">
/// <item><b>Shadow map</b> (optional): depth as seen from the key light.</item>
/// <item><b>Geometry prepass</b> (only if AO is on, or DoF is on and blurring): normals + depth into textures.</item>
/// <item><b>SSAO</b> + <b>blur</b> (optional): how enclosed each pixel is, used by the next pass to darken ambient light.</item>
/// <item><b>Scene</b>: background and lit pipes into a multisampled, HDR (16-bit float) buffer.</item>
/// <item><b>Resolve</b>: average the MSAA samples into a plain texture.</item>
/// <item><b>Depth of field</b> (optional, and skipped in flight): blur by distance from the focus plane.</item>
/// <item><b>Bloom</b> (optional): downsample/upsample chain for the glow.</item>
/// <item><b>Post</b>: bloom mix, tonemap, vignette, gamma, dither, fade, into the window (or a screenshot target).</item>
/// </list>
/// See docs/RENDERING.md for the longer explanation.
/// </summary>
internal sealed unsafe class PipeRenderer : IDisposable
{
    /// <summary>
    /// start(3) axis(3) side(3) color(3) params(4) surface(4): must match <see cref="PipeInstance"/> and the vertex
    /// shader. The last vec4 is (surface kind, seed, wear, spare) for the procedural surfaces.
    /// </summary>
    private const int FloatsPerInstance = 20;

    private const int MaxBloomLevels = 6;

    /// <summary>How much of the frame's light is spread into the glow. Small, so it reads as a lens effect, not haze.</summary>
    private const float BloomStrength = 0.12f;

    /// <summary>SSAO search radius in world units (a cell is 1 unit, a pipe about 0.2 thick).</summary>
    private const float AoRadius = 1.0f;

    /// <summary>
    /// Shadow map size in texels. Over the whole box (about 30 units across) that's ~70 texels per unit, and in
    /// flight (48 units) ~40; a pipe is 0.2–0.3 units thick.
    /// </summary>
    private const int ShadowMapSize = 2048;

    /// <summary>Direction towards the key light, the one that casts shadows. Warm, from above, front and right.</summary>
    private static readonly Vector3 KeyLight = Vector3.Normalize(new Vector3(0.5f, 0.8f, 0.6f));

    private readonly GL _gl;
    private readonly RenderOptions _options;
    // Only the programs the style needs are compiled; the rest stay 0 (which OpenGL ignores on delete).
    private readonly uint _pipeProgram, _geometryProgram, _bgProgram, _postProgram, _classicProgram;
    private readonly uint _ssaoProgram, _aoBlurProgram, _dofProgram, _bloomDownProgram, _bloomUpProgram, _shadowProgram;

    // The shadow map doesn't depend on the screen size, so it's made once, not on every resize.
    private readonly uint _shadowFbo, _shadowTex;
    private readonly uint _emptyVao;
    private readonly MeshBuffers[] _meshes = new MeshBuffers[PieceLists.KindCount];
    private readonly int[] _instanceCounts = new int[PieceLists.KindCount];

    /// <summary>
    /// How many instances each mesh's GPU buffer has room for. Buffers start roomy and only ever double, so they're
    /// almost never resized (see <see cref="UploadInstances"/>).
    /// </summary>
    private readonly int[] _instanceCapacity = new int[PieceLists.KindCount];

    /// <summary>Starting room per mesh: enough for a busy scene without ever growing (about 1 MB each).</summary>
    private const int InitialInstanceCapacity = 16_384;
    private readonly float[] _ssaoKernel;

    // Render targets, recreated on resize. Everything created is also tracked in these lists for cleanup.
    private readonly List<uint> _textures = [], _framebuffers = [], _renderbuffers = [];
    private uint _sceneFbo;                      // multisampled HDR colour + depth
    private uint _resolveFbo, _resolveTex;       // MSAA resolved
    private uint _geometryFbo, _normalTex, _depthTex;
    private uint _aoFbo, _aoTex, _aoBlurFbo, _aoBlurTex;
    private uint _dofFbo, _dofTex;
    private readonly List<(uint Fbo, uint Tex, int W, int H)> _bloomChain = [];
    private int _width, _height;
    private float[] _instanceScratch = [];

    public PipeRenderer(GL gl, RenderOptions options)
    {
        _gl = gl;
        _options = options;
        _emptyVao = gl.GenVertexArray();

        if (options.Classic)
        {
            _classicProgram = Program(Shaders.ClassicVertex, Shaders.ClassicFragment);

            // Coarser meshes: about a third of the triangles, and with per-vertex lighting the facets are part of
            // the look.
            _meshes[(int)MeshKind.Cylinder] = CreateMesh(MeshBuilder.Cylinder(12));
            _meshes[(int)MeshKind.Sphere] = CreateMesh(MeshBuilder.Sphere(8, 12));
            _meshes[(int)MeshKind.Elbow] = CreateMesh(MeshBuilder.TorusSection(6, 12));
            _meshes[(int)MeshKind.Ring] = CreateMesh(MeshBuilder.TorusSection(24, 8));
            _meshes[(int)MeshKind.Teapot] = CreateMesh(MeshBuilder.Teapot());
            _ssaoKernel = [];
            return;
        }

        _pipeProgram = Program(Shaders.PipeVertex, Shaders.WithDefine(Shaders.PipeFragment, "SURFACES", options.Surfaces ? 1 : 0));
        _geometryProgram = Program(Shaders.PipeVertex, Shaders.GeometryFragment);
        _bgProgram = Program(Shaders.FullscreenVertex, Shaders.BackgroundFragment);
        _postProgram = Program(Shaders.FullscreenVertex, Shaders.PostFragment);
        _ssaoProgram = Program(Shaders.FullscreenVertex, Shaders.SsaoFragment);
        _aoBlurProgram = Program(Shaders.FullscreenVertex, Shaders.AoBlurFragment);
        _dofProgram = Program(Shaders.FullscreenVertex, Shaders.DofFragment);
        _bloomDownProgram = Program(Shaders.FullscreenVertex, Shaders.BloomDownFragment);
        _bloomUpProgram = Program(Shaders.FullscreenVertex, Shaders.BloomUpFragment);

        _meshes[(int)MeshKind.Cylinder] = CreateMesh(MeshBuilder.Cylinder(28));
        _meshes[(int)MeshKind.Sphere] = CreateMesh(MeshBuilder.Sphere(16, 28));
        _meshes[(int)MeshKind.Elbow] = CreateMesh(MeshBuilder.TorusSection(12, 28)); // a 90 degree bend needs few steps
        _meshes[(int)MeshKind.Ring] = CreateMesh(MeshBuilder.TorusSection(48, 12));  // a full circle needs more
        _meshes[(int)MeshKind.Teapot] = CreateMesh(MeshBuilder.Teapot());

        _ssaoKernel = BuildSsaoKernel();

        if (options.Shadows)
        {
            _shadowProgram = Program(Shaders.PipeVertex, Shaders.ShadowFragment);
            (_shadowFbo, _shadowTex) = CreateShadowMap();
        }
    }

    /// <summary>Which vertex-shader path (uMode) draws each kind of mesh.</summary>
    private static int ShaderMode(MeshKind kind) => kind switch
    {
        MeshKind.Cylinder => 0,
        MeshKind.Sphere => 1,
        MeshKind.Elbow or MeshKind.Ring => 2,
        _ => 3,
    };

    private bool NeedsGeometryPass => _options.AmbientOcclusion || _options.DepthOfField;

    public void Resize(int width, int height)
    {
        if (width == _width && height == _height) return;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        DeleteTargets();

        // Multisampled scene target: HDR (16-bit float) for modern, plain 8-bit for classic. Renderbuffers, because
        // MSAA buffers can't be sampled; we blit them instead.
        var sceneFormat = _options.Classic ? InternalFormat.Rgba8 : InternalFormat.Rgba16f;
        _sceneFbo = NewFramebuffer();
        var color = NewRenderbuffer(sceneFormat);
        var depth = NewRenderbuffer(InternalFormat.DepthComponent24);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, color);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depth);
        CheckFramebuffer("scene");

        // The resolve target must have the same format as the scene: a multisample resolve can't convert formats.
        _resolveTex = NewTexture(sceneFormat, PixelFormat.Rgba, _width, _height);
        _resolveFbo = FramebufferFor(_resolveTex, "resolve");

        if (NeedsGeometryPass)
        {
            _normalTex = NewTexture(InternalFormat.Rgb10A2, PixelFormat.Rgba, _width, _height, linear: false);
            _depthTex = NewTexture(InternalFormat.DepthComponent24, PixelFormat.DepthComponent, _width, _height, linear: false);
            _geometryFbo = FramebufferFor(_normalTex, "geometry", _depthTex);
        }
        if (_options.AmbientOcclusion)
        {
            _aoTex = NewTexture(InternalFormat.RG16f, PixelFormat.RG, _width, _height, linear: false);
            _aoFbo = FramebufferFor(_aoTex, "ssao");
            _aoBlurTex = NewTexture(InternalFormat.RG16f, PixelFormat.RG, _width, _height, linear: false);
            _aoBlurFbo = FramebufferFor(_aoBlurTex, "ssao blur");
        }
        if (_options.DepthOfField)
        {
            _dofTex = NewTexture(InternalFormat.Rgba16f, PixelFormat.Rgba, _width, _height);
            _dofFbo = FramebufferFor(_dofTex, "depth of field");
        }
        if (_options.Bloom)
        {
            // Half size, quarter size, ... until it gets tiny.
            int w = _width, h = _height;
            for (var i = 0; i < MaxBloomLevels && Math.Min(w, h) > 8; i++)
            {
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
                var tex = NewTexture(InternalFormat.R11fG11fB10f, PixelFormat.Rgb, w, h);
                _bloomChain.Add((FramebufferFor(tex, $"bloom {i}"), tex, w, h));
            }
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>
    /// Draw a frame into <paramref name="targetFbo"/> with its bottom-left corner at
    /// (<paramref name="targetX"/>, <paramref name="targetY"/>). All the intermediate passes use this renderer's
    /// own buffers; only the final output lands in the target, so several renderers can share one window.
    /// </summary>
    public void Render(Camera camera, PieceLists pieces, float fade, uint targetFbo, int targetX = 0, int targetY = 0)
    {
        UploadInstances(pieces);

        if (_options.Classic)
        {
            ClassicPass(camera, fade);
            // Two copies: MSAA resolve into a same-format texture, then a plain copy to the target. Going straight
            // from the multisampled buffer to the window isn't allowed if their formats differ even slightly.
            Blit(_sceneFbo, _resolveFbo);
            Blit(_resolveFbo, targetFbo, targetX, targetY);
            return;
        }

        var shadows = _options.Shadows && camera.ShadowRadius > 0f;
        var lightViewProj = shadows ? LightViewProj(camera) : Matrix4x4.Identity;
        if (shadows) ShadowPass(lightViewProj);
        // Depth of field is skipped while the camera asks for no blur (in flight), and then the prepass it needs
        // (if AO doesn't) can be skipped too.
        var depthOfField = _options.DepthOfField && camera.FocusScale > 0f;
        if (_options.AmbientOcclusion || depthOfField) GeometryPass(camera);
        if (_options.AmbientOcclusion) AmbientOcclusionPass(camera);
        ScenePass(camera, shadows, lightViewProj, camera.ShadowRadius * 2f / ShadowMapSize);

        // Resolve MSAA: the GPU averages each pixel's samples as it copies.
        Blit(_sceneFbo, _resolveFbo);

        var image = _resolveTex;
        if (depthOfField)
        {
            DepthOfFieldPass(camera, image);
            image = _dofTex;
        }
        if (_options.Bloom) BloomPass(image);
        PostPass(image, fade, targetFbo, targetX, targetY);
    }

    // ---- Passes ----

    /// <summary>Lite mode's only pass: black background, pipes lit per vertex, fade applied in the shader.</summary>
    private void ClassicPass(Camera camera, float fade)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.ClearColor(0f, 0f, 0f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.CullFace);

        _gl.UseProgram(_classicProgram);
        SetMatrix(_classicProgram, "uViewProj", camera.View * camera.Projection);
        SetVector(_classicProgram, "uCameraPos", camera.Position);
        _gl.Uniform1(Loc(_classicProgram, "uFade"), fade);
        DrawPieces(_classicProgram);
    }

    /// <summary>
    /// The view from the key light, as a matrix that takes a world position to shadow-map coordinates (-1..1 across
    /// the map, and depth).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The key light is a sun: very far away, so its rays are parallel. That makes it an <b>orthographic</b>
    /// projection (no perspective), a box looking along the light, sized to hold the camera's shadow sphere.
    /// </para>
    /// <para>
    /// <b>Texel snapping.</b> In flight the sphere moves every frame. If the box slid smoothly with it, each shadow's
    /// edge would land on slightly different texels every frame and visibly crawl ("shimmer"). So the box only moves
    /// in whole texels: in the light's view, the centre is rounded to a multiple of the texel size.
    /// </para>
    /// <para>
    /// The box reaches 3 radii towards the light, not just 1, so a pipe outside the sphere but between it and the
    /// light still casts its shadow into it.
    /// </para>
    /// </remarks>
    private static Matrix4x4 LightViewProj(Camera camera)
    {
        var r = camera.ShadowRadius;
        // A fixed rotation, looking along the light from the origin. Only the orientation matters for an
        // orthographic view; where the box sits is chosen below.
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, -KeyLight, Vector3.UnitY);
        var centre = Vector3.Transform(camera.ShadowCentre, view);
        var texel = 2f * r / ShadowMapSize;
        centre.X = MathF.Round(centre.X / texel) * texel;
        centre.Y = MathF.Round(centre.Y / texel) * texel;
        // View space looks down -Z, so a point's distance in front is -Z.
        var projection = Matrix4x4.CreateOrthographicOffCenter(
            centre.X - r, centre.X + r, centre.Y - r, centre.Y + r, -centre.Z - 3f * r, -centre.Z + r);
        return view * projection;
    }

    /// <summary>
    /// Render every piece's depth from the key light into the shadow map. When the scene pass shades a point, it
    /// checks the map: if something nearer the light is recorded there, the point is in shadow.
    /// </summary>
    private void ShadowPass(Matrix4x4 lightViewProj)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
        _gl.Viewport(0, 0, ShadowMapSize, ShadowMapSize);
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.CullFace);
        // Push the stored depths slightly away from the light, more on surfaces steeply angled to it. Along with
        // the normal offset in the shader, this stops surfaces shadowing themselves.
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(1.5f, 3f);

        _gl.UseProgram(_shadowProgram);
        SetMatrix(_shadowProgram, "uViewProj", lightViewProj);
        DrawPieces(_shadowProgram);

        _gl.Disable(EnableCap.PolygonOffsetFill);
    }

    private void GeometryPass(Camera camera)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _geometryFbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.ClearColor(0.5f, 0.5f, 1f, 1f); // "facing the camera" normal for empty pixels
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.ClearColor(0f, 0f, 0f, 0f);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.CullFace);

        _gl.UseProgram(_geometryProgram);
        SetMatrix(_geometryProgram, "uViewProj", camera.View * camera.Projection);
        SetMatrix(_geometryProgram, "uView", camera.View);
        SetVector(_geometryProgram, "uCameraPos", camera.Position);
        DrawPieces(_geometryProgram);
    }

    private void AmbientOcclusionPass(Camera camera)
    {
        Matrix4x4.Invert(camera.Projection, out var invProj);
        _gl.Disable(EnableCap.DepthTest);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _aoFbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.UseProgram(_ssaoProgram);
        BindTexture(_ssaoProgram, "uDepth", 0, _depthTex);
        BindTexture(_ssaoProgram, "uNormal", 1, _normalTex);
        SetMatrix(_ssaoProgram, "uProj", camera.Projection);
        SetMatrix(_ssaoProgram, "uInvProj", invProj);
        _gl.Uniform1(Loc(_ssaoProgram, "uRadius"), AoRadius);
        fixed (float* k = _ssaoKernel) _gl.Uniform3(Loc(_ssaoProgram, "uKernel"), (uint)(_ssaoKernel.Length / 3), k);
        DrawFullscreen();

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _aoBlurFbo);
        _gl.UseProgram(_aoBlurProgram);
        BindTexture(_aoBlurProgram, "uAO", 0, _aoTex);
        _gl.Uniform2(Loc(_aoBlurProgram, "uTexel"), 1f / _width, 1f / _height);
        DrawFullscreen();
    }

    private void ScenePass(Camera camera, bool shadows, Matrix4x4 lightViewProj, float shadowTexel)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // Background gradient first, without touching depth, so pipes always draw over it.
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.UseProgram(_bgProgram);
        DrawFullscreen();

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.CullFace); // spout openings are visible from inside; the shader flips back-facing normals

        _gl.UseProgram(_pipeProgram);
        SetMatrix(_pipeProgram, "uViewProj", camera.View * camera.Projection);
        SetVector(_pipeProgram, "uCameraPos", camera.Position);
        _gl.Uniform3(Loc(_pipeProgram, "uFogColor"), 0.004f, 0.006f, 0.014f);
        _gl.Uniform1(Loc(_pipeProgram, "uFogDensity"), camera.FogDensity);
        _gl.Uniform1(Loc(_pipeProgram, "uUseAO"), _options.AmbientOcclusion ? 1 : 0);
        _gl.Uniform2(Loc(_pipeProgram, "uInvViewport"), 1f / _width, 1f / _height);
        if (_options.AmbientOcclusion) BindTexture(_pipeProgram, "uAO", 0, _aoBlurTex);
        SetVector(_pipeProgram, "uKeyLight", KeyLight);
        _gl.Uniform1(Loc(_pipeProgram, "uUseShadow"), shadows ? 1 : 0);
        if (shadows)
        {
            BindTexture(_pipeProgram, "uShadowMap", 1, _shadowTex);
            SetMatrix(_pipeProgram, "uLightViewProj", lightViewProj);
            _gl.Uniform1(Loc(_pipeProgram, "uShadowTexel"), shadowTexel);
        }
        else
        {
            // Samplers of different types (sampler2D, sampler2DShadow) mustn't share a texture unit, even unused.
            _gl.Uniform1(Loc(_pipeProgram, "uShadowMap"), 1);
        }
        DrawPieces(_pipeProgram);
    }

    private void DepthOfFieldPass(Camera camera, uint image)
    {
        Matrix4x4.Invert(camera.Projection, out var invProj);
        // Blur size is set for 1080p and scaled with the screen, so it looks the same at any resolution.
        var maxBlur = MathF.Max(2f, 8f * _height / 1080f);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _dofFbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.Disable(EnableCap.DepthTest);
        _gl.UseProgram(_dofProgram);
        BindTexture(_dofProgram, "uScene", 0, image);
        BindTexture(_dofProgram, "uDepth", 1, _depthTex);
        SetMatrix(_dofProgram, "uInvProj", invProj);
        _gl.Uniform2(Loc(_dofProgram, "uTexel"), 1f / _width, 1f / _height);
        _gl.Uniform1(Loc(_dofProgram, "uFocus"), camera.FocusDistance);
        _gl.Uniform1(Loc(_dofProgram, "uFocusScale"), camera.FocusScale);
        _gl.Uniform1(Loc(_dofProgram, "uMaxBlur"), maxBlur);
        // Spiral spacing grows with blur size so the sample count stays near ~60-80 on big screens.
        _gl.Uniform1(Loc(_dofProgram, "uRadScale"), MathF.Max(0.5f, maxBlur * maxBlur / 160f));
        DrawFullscreen();
    }

    private void BloomPass(uint image)
    {
        _gl.Disable(EnableCap.DepthTest);

        // Down: each level is a filtered half-size copy of the one before.
        _gl.UseProgram(_bloomDownProgram);
        for (var i = 0; i < _bloomChain.Count; i++)
        {
            var (fbo, _, w, h) = _bloomChain[i];
            var (srcTex, srcW, srcH) = i == 0 ? (image, _width, _height) : (_bloomChain[i - 1].Tex, _bloomChain[i - 1].W, _bloomChain[i - 1].H);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            _gl.Viewport(0, 0, (uint)w, (uint)h);
            BindTexture(_bloomDownProgram, "uSource", 0, srcTex);
            _gl.Uniform2(Loc(_bloomDownProgram, "uTexel"), 1f / srcW, 1f / srcH);
            _gl.Uniform1(Loc(_bloomDownProgram, "uFirst"), i == 0 ? 1 : 0);
            DrawFullscreen();
        }

        // Up: blur each level and add it onto the next larger one. Level 0 ends up holding the finished glow.
        _gl.UseProgram(_bloomUpProgram);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        for (var i = _bloomChain.Count - 1; i > 0; i--)
        {
            var src = _bloomChain[i];
            var dst = _bloomChain[i - 1];
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, dst.Fbo);
            _gl.Viewport(0, 0, (uint)dst.W, (uint)dst.H);
            BindTexture(_bloomUpProgram, "uSource", 0, src.Tex);
            _gl.Uniform2(Loc(_bloomUpProgram, "uTexel"), 1f / src.W, 1f / src.H);
            DrawFullscreen();
        }
        _gl.Disable(EnableCap.Blend);
    }

    private void PostPass(uint image, float fade, uint targetFbo, int targetX, int targetY)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
        _gl.Viewport(targetX, targetY, (uint)_width, (uint)_height);
        _gl.Disable(EnableCap.DepthTest);
        _gl.UseProgram(_postProgram);
        BindTexture(_postProgram, "uScene", 0, image);
        var bloom = _options.Bloom && _bloomChain.Count > 0;
        if (bloom) BindTexture(_postProgram, "uBloom", 1, _bloomChain[0].Tex);
        // The upsample chain adds every level together, so level 0 holds roughly (level count) x the light.
        // Dividing here keeps the glow's strength the same whatever the screen size (and so level count).
        _gl.Uniform1(Loc(_postProgram, "uBloomStrength"), bloom ? BloomStrength / _bloomChain.Count : 0f);
        _gl.Uniform1(Loc(_postProgram, "uFade"), fade);
        _gl.Uniform1(Loc(_postProgram, "uAspect"), (float)_width / _height);
        DrawFullscreen();
    }

    // ---- Drawing helpers ----

    /// <summary>Copy each kind's instances into its GPU buffer. Done once per frame, then reused by every pass.</summary>
    private void UploadInstances(PieceLists pieces)
    {
        for (var k = 0; k < PieceLists.KindCount; k++)
        {
            var instances = pieces[(MeshKind)k];
            _instanceCounts[k] = instances.Count;
            if (instances.Count == 0) continue;

            var needed = instances.Count * FloatsPerInstance;
            if (_instanceScratch.Length < needed) _instanceScratch = new float[Math.Max(needed, _instanceScratch.Length * 2)];
            var span = CollectionsMarshal.AsSpan(instances);
            for (int i = 0, o = 0; i < span.Length; i++, o += FloatsPerInstance)
            {
                ref var p = ref span[i];
                _instanceScratch[o] = p.Start.X; _instanceScratch[o + 1] = p.Start.Y; _instanceScratch[o + 2] = p.Start.Z;
                _instanceScratch[o + 3] = p.Axis.X; _instanceScratch[o + 4] = p.Axis.Y; _instanceScratch[o + 5] = p.Axis.Z;
                _instanceScratch[o + 6] = p.Side.X; _instanceScratch[o + 7] = p.Side.Y; _instanceScratch[o + 8] = p.Side.Z;
                _instanceScratch[o + 9] = p.Color.X; _instanceScratch[o + 10] = p.Color.Y; _instanceScratch[o + 11] = p.Color.Z;
                _instanceScratch[o + 12] = p.Radius; _instanceScratch[o + 13] = p.Sweep;
                _instanceScratch[o + 14] = p.Metallic; _instanceScratch[o + 15] = p.Roughness;
                _instanceScratch[o + 16] = p.Surface; _instanceScratch[o + 17] = p.Seed;
                _instanceScratch[o + 18] = p.Wear; _instanceScratch[o + 19] = 0f; // spare
            }

            // The usual streaming pattern: the buffer keeps a fixed, generous size. Each frame, BufferData with no
            // data "orphans" it (the driver hands us fresh memory of the same size, while the GPU may still be
            // reading last frame's), then BufferSubData fills just the part in use. Uploading with the exact size
            // every frame instead makes the driver allocate a differently sized buffer every frame, which it can't
            // simply recycle.
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _meshes[k].InstanceVbo);
            if (instances.Count > _instanceCapacity[k])
                _instanceCapacity[k] = Math.Max(instances.Count, Math.Max(_instanceCapacity[k] * 2, InitialInstanceCapacity));
            var capacityBytes = (nuint)(_instanceCapacity[k] * FloatsPerInstance * sizeof(float));
            _gl.BufferData(BufferTargetARB.ArrayBuffer, capacityBytes, null, BufferUsageARB.StreamDraw);
            fixed (float* data = _instanceScratch)
                _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(needed * sizeof(float)), data);
        }
    }

    private void DrawPieces(uint program)
    {
        var modeLoc = Loc(program, "uMode");
        for (var k = 0; k < PieceLists.KindCount; k++)
        {
            if (_instanceCounts[k] == 0) continue;
            _gl.Uniform1(modeLoc, ShaderMode((MeshKind)k));
            _gl.BindVertexArray(_meshes[k].Vao);
            _gl.DrawElementsInstanced(PrimitiveType.Triangles, _meshes[k].IndexCount, DrawElementsType.UnsignedInt, null, (uint)_instanceCounts[k]);
        }
    }

    /// <summary>
    /// Copy this renderer's full-size image from one framebuffer to another, placing it at (<paramref name="toX"/>,
    /// <paramref name="toY"/>). Resolves MSAA if the source has it.
    /// </summary>
    private void Blit(uint from, uint to, int toX = 0, int toY = 0)
    {
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, from);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, to);
        _gl.BlitFramebuffer(0, 0, _width, _height, toX, toY, toX + _width, toY + _height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
    }

    private void DrawFullscreen()
    {
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    /// <summary>
    /// 16 random points in a unit hemisphere (+Z up) for SSAO. More of them are packed close to the centre, because
    /// nearby geometry matters most for contact shadows.
    /// </summary>
    private static float[] BuildSsaoKernel()
    {
        var rng = new Random(1234); // fixed: the same pattern every run
        var kernel = new float[16 * 3];
        for (var i = 0; i < 16; i++)
        {
            var v = Vector3.Normalize(new Vector3(rng.NextSingle() * 2 - 1, rng.NextSingle() * 2 - 1, rng.NextSingle() * 0.9f + 0.1f));
            var t = i / 16f;
            v *= rng.NextSingle() * float.Lerp(0.1f, 1f, t * t);
            kernel[i * 3] = v.X; kernel[i * 3 + 1] = v.Y; kernel[i * 3 + 2] = v.Z;
        }
        return kernel;
    }

    private int Loc(uint program, string name) => _gl.GetUniformLocation(program, name);

    private void SetMatrix(uint program, string name, Matrix4x4 m) => _gl.UniformMatrix4(Loc(program, name), 1, false, (float*)&m);

    private void SetVector(uint program, string name, Vector3 v) => _gl.Uniform3(Loc(program, name), v.X, v.Y, v.Z);

    private void BindTexture(uint program, string name, int unit, uint texture)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.Uniform1(Loc(program, name), unit);
    }

    // ---- Resource creation ----

    private MeshBuffers CreateMesh((float[] Vertices, uint[] Indices) mesh)
    {
        var (vertices, indices) = mesh;
        var vao = _gl.GenVertexArray();
        _gl.BindVertexArray(vao);

        // Per-vertex data: position (location 0) and normal (location 1).
        var vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* v = vertices)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), v, BufferUsageARB.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));

        var ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        fixed (uint* i = indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), i, BufferUsageARB.StaticDraw);

        // Per-instance data (locations 2-7). A divisor of 1 makes the GPU advance these once per instance
        // instead of once per vertex; that's what makes instancing work.
        var instanceVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, instanceVbo);
        const uint stride = FloatsPerInstance * sizeof(float);
        (uint Loc, int Size, int Offset)[] attrs = [(2, 3, 0), (3, 3, 3), (4, 3, 6), (5, 3, 9), (6, 4, 12), (7, 4, 16)];
        foreach (var (loc, size, offset) in attrs)
        {
            _gl.EnableVertexAttribArray(loc);
            _gl.VertexAttribPointer(loc, size, VertexAttribPointerType.Float, false, stride, (void*)(offset * sizeof(float)));
            _gl.VertexAttribDivisor(loc, 1);
        }

        _gl.BindVertexArray(0);
        return new MeshBuffers(vao, vbo, ebo, instanceVbo, (uint)indices.Length);
    }

    /// <summary>
    /// The shadow map: a depth texture with <b>comparison</b> switched on. Sampled through a <c>sampler2DShadow</c>,
    /// it doesn't return a depth but the answer to "is this depth nearer than what's stored?" (1 lit, 0 shadowed),
    /// and with linear filtering the GPU blends that answer across the 4 nearest texels for free.
    /// </summary>
    private (uint Fbo, uint Texture) CreateShadowMap()
    {
        var tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, ShadowMapSize, ShadowMapSize, 0,
            PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)GLEnum.CompareRefToTexture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)GLEnum.Lequal);

        var fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, tex, 0);
        _gl.DrawBuffer(DrawBufferMode.None); // depth only: no colour to write or read
        _gl.ReadBuffer(ReadBufferMode.None);
        CheckFramebuffer("shadow map");
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return (fbo, tex);
    }

    private uint NewTexture(InternalFormat internalFormat, PixelFormat format, int width, int height, bool linear = true)
    {
        var tex = _gl.GenTexture();
        _textures.Add(tex);
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        // No data yet (null); the passes render into it. The type only has to be valid for the format.
        var type = format == PixelFormat.DepthComponent ? PixelType.UnsignedInt : PixelType.Float;
        _gl.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, (uint)width, (uint)height, 0, format, type, null);
        var filter = linear ? (int)TextureMinFilter.Linear : (int)TextureMinFilter.Nearest;
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, filter);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, filter);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return tex;
    }

    private uint NewFramebuffer()
    {
        var fbo = _gl.GenFramebuffer();
        _framebuffers.Add(fbo);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        return fbo;
    }

    private uint NewRenderbuffer(InternalFormat format)
    {
        var rb = _gl.GenRenderbuffer();
        _renderbuffers.Add(rb);
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, rb);
        _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)_options.Samples, format, (uint)_width, (uint)_height);
        return rb;
    }

    private uint FramebufferFor(uint colorTex, string name, uint depthTex = 0)
    {
        var fbo = NewFramebuffer();
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colorTex, 0);
        if (depthTex != 0)
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, depthTex, 0);
        CheckFramebuffer(name);
        return fbo;
    }

    private uint Program(string vertexSource, string fragmentSource)
    {
        var vs = Compile(ShaderType.VertexShader, vertexSource);
        var fs = Compile(ShaderType.FragmentShader, fragmentSource);
        var program = _gl.CreateProgram();
        _gl.AttachShader(program, vs);
        _gl.AttachShader(program, fs);
        _gl.LinkProgram(program);
        _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var ok);
        if (ok == 0) throw new InvalidOperationException("Shader link failed: " + _gl.GetProgramInfoLog(program));
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        return program;
    }

    private uint Compile(ShaderType type, string source)
    {
        var shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out var ok);
        if (ok == 0) throw new InvalidOperationException($"{type} compile failed: " + _gl.GetShaderInfoLog(shader));
        return shader;
    }

    private void CheckFramebuffer(string name)
    {
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"Framebuffer '{name}' incomplete: {status}");
    }

    private void DeleteTargets()
    {
        foreach (var f in _framebuffers) _gl.DeleteFramebuffer(f);
        foreach (var t in _textures) _gl.DeleteTexture(t);
        foreach (var r in _renderbuffers) _gl.DeleteRenderbuffer(r);
        _framebuffers.Clear();
        _textures.Clear();
        _renderbuffers.Clear();
        _bloomChain.Clear();
    }

    public void Dispose()
    {
        DeleteTargets();
        foreach (var m in _meshes)
        {
            _gl.DeleteVertexArray(m.Vao);
            _gl.DeleteBuffer(m.Vbo);
            _gl.DeleteBuffer(m.Ebo);
            _gl.DeleteBuffer(m.InstanceVbo);
        }
        _gl.DeleteVertexArray(_emptyVao);
        _gl.DeleteFramebuffer(_shadowFbo);
        _gl.DeleteTexture(_shadowTex);
        foreach (var p in new[] { _pipeProgram, _geometryProgram, _bgProgram, _postProgram, _classicProgram, _ssaoProgram, _aoBlurProgram, _dofProgram, _bloomDownProgram, _bloomUpProgram, _shadowProgram })
            _gl.DeleteProgram(p);
    }

    private readonly record struct MeshBuffers(uint Vao, uint Vbo, uint Ebo, uint InstanceVbo, uint IndexCount);
}
