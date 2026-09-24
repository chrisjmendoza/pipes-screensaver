using System.Numerics;
using System.Runtime.InteropServices;
using Pipes.Simulation;
using Silk.NET.OpenGL;

namespace Pipes.Rendering;

/// <summary>
/// Draws the pipe world: background gradient, instanced cylinders and spheres into a multisampled HDR
/// buffer, then a post pass (tonemap, vignette, fade) into the target framebuffer.
/// </summary>
internal sealed unsafe class PipeRenderer : IDisposable
{
    private const int FloatsPerInstance = 10; // start(3) axis(3) color(3) radius(1)

    private readonly GL _gl;
    private readonly int _samples;
    private readonly uint _pipeProgram, _bgProgram, _postProgram;
    private readonly uint _emptyVao;
    private readonly MeshBuffers _cylinder, _sphere;

    private uint _msaaFbo, _msaaColor, _msaaDepth;
    private uint _resolveFbo, _resolveTex;
    private int _width, _height;
    private float[] _instanceScratch = [];

    public PipeRenderer(GL gl, int samples)
    {
        _gl = gl;
        _samples = samples;
        _pipeProgram = Program(Shaders.PipeVertex, Shaders.PipeFragment);
        _bgProgram = Program(Shaders.FullscreenVertex, Shaders.BackgroundFragment);
        _postProgram = Program(Shaders.FullscreenVertex, Shaders.PostFragment);
        _emptyVao = gl.GenVertexArray();

        var (cv, ci) = MeshBuilder.Cylinder(28);
        var (sv, si) = MeshBuilder.Sphere(16, 28);
        _cylinder = CreateMesh(cv, ci);
        _sphere = CreateMesh(sv, si);
    }

    public void Resize(int width, int height)
    {
        if (width == _width && height == _height) return;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        DeleteTargets();

        // Multisampled HDR scene target (or single-sampled when AA is off).
        _msaaFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _msaaFbo);
        _msaaColor = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _msaaColor);
        _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)_samples, InternalFormat.Rgba16f, (uint)_width, (uint)_height);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _msaaColor);
        _msaaDepth = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _msaaDepth);
        _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)_samples, InternalFormat.DepthComponent24, (uint)_width, (uint)_height);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _msaaDepth);
        CheckFramebuffer("scene");

        // Resolved HDR texture sampled by the post pass.
        _resolveFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _resolveFbo);
        _resolveTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _resolveTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)_width, (uint)_height, 0, PixelFormat.Rgba, PixelType.HalfFloat, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _resolveTex, 0);
        CheckFramebuffer("resolve");

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Render(Camera camera, List<PipeInstance> cylinders, List<PipeInstance> spheres, float metallic, float fade, uint targetFbo)
    {
        // ---- Scene pass (HDR, multisampled) ----
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _msaaFbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.UseProgram(_bgProgram);
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.CullFace); // open cylinders; the shader flips back-facing normals

        _gl.UseProgram(_pipeProgram);
        var viewProj = camera.View * camera.Projection;
        _gl.UniformMatrix4(_gl.GetUniformLocation(_pipeProgram, "uViewProj"), 1, false, (float*)&viewProj);
        _gl.Uniform3(_gl.GetUniformLocation(_pipeProgram, "uCameraPos"), camera.Position.X, camera.Position.Y, camera.Position.Z);
        _gl.Uniform1(_gl.GetUniformLocation(_pipeProgram, "uMetallic"), metallic);
        _gl.Uniform3(_gl.GetUniformLocation(_pipeProgram, "uFogColor"), 0.004f, 0.006f, 0.014f);
        _gl.Uniform1(_gl.GetUniformLocation(_pipeProgram, "uFogDensity"), camera.FogDensity);

        var modeLoc = _gl.GetUniformLocation(_pipeProgram, "uMode");
        _gl.Uniform1(modeLoc, 0);
        DrawInstanced(_cylinder, cylinders);
        _gl.Uniform1(modeLoc, 1);
        DrawInstanced(_sphere, spheres);

        // ---- Resolve MSAA ----
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _msaaFbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _resolveFbo);
        _gl.BlitFramebuffer(0, 0, _width, _height, 0, 0, _width, _height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);

        // ---- Post pass into the target ----
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
        _gl.Viewport(0, 0, (uint)_width, (uint)_height);
        _gl.Disable(EnableCap.DepthTest);
        _gl.UseProgram(_postProgram);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _resolveTex);
        _gl.Uniform1(_gl.GetUniformLocation(_postProgram, "uScene"), 0);
        _gl.Uniform1(_gl.GetUniformLocation(_postProgram, "uFade"), fade);
        _gl.Uniform1(_gl.GetUniformLocation(_postProgram, "uAspect"), (float)_width / _height);
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    private void DrawInstanced(MeshBuffers mesh, List<PipeInstance> instances)
    {
        if (instances.Count == 0) return;

        var needed = instances.Count * FloatsPerInstance;
        if (_instanceScratch.Length < needed) _instanceScratch = new float[Math.Max(needed, _instanceScratch.Length * 2)];
        var span = CollectionsMarshal.AsSpan(instances);
        for (int i = 0, o = 0; i < span.Length; i++, o += FloatsPerInstance)
        {
            ref var p = ref span[i];
            _instanceScratch[o] = p.Start.X; _instanceScratch[o + 1] = p.Start.Y; _instanceScratch[o + 2] = p.Start.Z;
            _instanceScratch[o + 3] = p.Axis.X; _instanceScratch[o + 4] = p.Axis.Y; _instanceScratch[o + 5] = p.Axis.Z;
            _instanceScratch[o + 6] = p.Color.X; _instanceScratch[o + 7] = p.Color.Y; _instanceScratch[o + 8] = p.Color.Z;
            _instanceScratch[o + 9] = p.Radius;
        }

        _gl.BindVertexArray(mesh.Vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, mesh.InstanceVbo);
        fixed (float* data = _instanceScratch)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(needed * sizeof(float)), data, BufferUsageARB.StreamDraw);
        _gl.DrawElementsInstanced(PrimitiveType.Triangles, mesh.IndexCount, DrawElementsType.UnsignedInt, null, (uint)instances.Count);
    }

    private MeshBuffers CreateMesh(float[] vertices, uint[] indices)
    {
        var vao = _gl.GenVertexArray();
        _gl.BindVertexArray(vao);

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

        var instanceVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, instanceVbo);
        const uint stride = FloatsPerInstance * sizeof(float);
        (uint Loc, int Size, int Offset)[] attrs = [(2, 3, 0), (3, 3, 3), (4, 3, 6), (5, 1, 9)];
        foreach (var (loc, size, offset) in attrs)
        {
            _gl.EnableVertexAttribArray(loc);
            _gl.VertexAttribPointer(loc, size, VertexAttribPointerType.Float, false, stride, (void*)(offset * sizeof(float)));
            _gl.VertexAttribDivisor(loc, 1);
        }

        _gl.BindVertexArray(0);
        return new MeshBuffers(vao, vbo, ebo, instanceVbo, (uint)indices.Length);
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
        if (_msaaFbo != 0) _gl.DeleteFramebuffer(_msaaFbo);
        if (_msaaColor != 0) _gl.DeleteRenderbuffer(_msaaColor);
        if (_msaaDepth != 0) _gl.DeleteRenderbuffer(_msaaDepth);
        if (_resolveFbo != 0) _gl.DeleteFramebuffer(_resolveFbo);
        if (_resolveTex != 0) _gl.DeleteTexture(_resolveTex);
        _msaaFbo = _msaaColor = _msaaDepth = _resolveFbo = _resolveTex = 0;
    }

    public void Dispose()
    {
        DeleteTargets();
        foreach (var m in new[] { _cylinder, _sphere })
        {
            _gl.DeleteVertexArray(m.Vao);
            _gl.DeleteBuffer(m.Vbo);
            _gl.DeleteBuffer(m.Ebo);
            _gl.DeleteBuffer(m.InstanceVbo);
        }
        _gl.DeleteVertexArray(_emptyVao);
        _gl.DeleteProgram(_pipeProgram);
        _gl.DeleteProgram(_bgProgram);
        _gl.DeleteProgram(_postProgram);
    }

    private readonly record struct MeshBuffers(uint Vao, uint Vbo, uint Ebo, uint InstanceVbo, uint IndexCount);
}
