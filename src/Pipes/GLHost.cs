using System.Diagnostics;
using ImageFormat = System.Drawing.Imaging.ImageFormat;
using ImageLockMode = System.Drawing.Imaging.ImageLockMode;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;
using System.Runtime.InteropServices;
using Pipes.Native;
using Pipes.Rendering;
using Silk.NET.OpenGL;

namespace Pipes;

internal enum HostMode
{
    /// <summary>/s: borderless, topmost, spanning every monitor; any input exits.</summary>
    Fullscreen,
    /// <summary>/p hwnd: child of the little monitor picture in Screen Saver Settings.</summary>
    Preview,
    /// <summary>/w: resizable window for development; Esc or close exits.</summary>
    Windowed,
}

/// <summary>
/// A bare Win32 window with a WGL context. Deliberately no windowing library, because the preview mode needs
/// to parent into a foreign HWND, which GLFW/SDL-style libraries can't do.
/// </summary>
internal sealed unsafe class GLHost : IDisposable
{
    private const string ClassName = "PipesScreensaverWindow";
    private const int MouseMoveTolerance = 6;

    private readonly HostMode _mode;
    private readonly IntPtr _parent;
    private readonly Win32.WndProc _wndProc; // keep alive: native code holds a pointer to it
    private IntPtr _hwnd, _hdc, _hglrc;
    private GL _gl = null!;
    private bool _running = true;
    private bool _resized;
    private Win32.POINT? _initialCursor;
    private readonly Stopwatch _sinceStart = Stopwatch.StartNew();

    public GLHost(HostMode mode, IntPtr parent = default)
    {
        _mode = mode;
        _parent = parent;
        _wndProc = WindowProc;
    }

    /// <summary>Create a hidden window and context for offscreen rendering (screenshots).</summary>
    public static GLHost CreateOffscreen()
    {
        var host = new GLHost(HostMode.Windowed);
        host.CreateWindow(visible: false);
        return host;
    }

    public GL GL => _gl;

    public void Run(PipesSettings settings)
    {
        CreateWindow(visible: true);
        using var renderer = new PipeRenderer(_gl, RenderOptions.From(settings));
        var scene = new Scene(settings, new Random());

        var (w, h) = ClientSize();
        renderer.Resize(w, h);
        scene.Start((float)w / Math.Max(1, h));

        if (_mode == HostMode.Fullscreen)
        {
            Win32.ShowCursor(false);
            if (Win32.GetCursorPos(out var p)) _initialCursor = p;
        }

        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed.TotalSeconds;
        while (_running)
        {
            while (Win32.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
            {
                if (msg.message == Win32.WM_QUIT) { _running = false; break; }
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessage(ref msg);
            }
            if (!_running) break;

            // The Screen Saver Settings dialog destroys our parent when it closes or switches savers.
            if (_mode == HostMode.Preview && !Win32.IsWindow(_parent)) break;

            if (_resized)
            {
                _resized = false;
                (w, h) = ClientSize();
                renderer.Resize(w, h);
                scene.SetAspect((float)w / Math.Max(1, h));
            }

            var now = clock.Elapsed.TotalSeconds;
            var dt = (float)Math.Min(now - last, 0.1); // clamp after hitches so pipes don't jump
            last = now;

            scene.Update(dt);
            renderer.Render(scene.Camera, scene.Pieces, scene.Fade, targetFbo: 0);
            Win32.SwapBuffers(_hdc);
        }

        if (_mode == HostMode.Fullscreen) Win32.ShowCursor(true);
    }

    /// <summary>Simulates <paramref name="seconds"/> of animation at a fixed step, renders one frame offscreen, saves a PNG.</summary>
    public void Screenshot(PipesSettings settings, int width, int height, float seconds, string path, int seed)
    {
        using var renderer = new PipeRenderer(_gl, RenderOptions.From(settings));
        var scene = new Scene(settings, new Random(seed));
        renderer.Resize(width, height);
        scene.Start((float)width / height);
        for (var t = 0f; t < seconds; t += 1f / 60f) scene.Update(1f / 60f);

        // Render into an 8-bit target we can read back (the default framebuffer of a hidden window is undefined).
        var fbo = _gl.GenFramebuffer();
        var tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);

        renderer.Render(scene.Camera, scene.Pieces, scene.Fade, fbo);

        var pixels = new byte[width * height * 4];
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        fixed (byte* p = pixels)
            _gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Bgra, PixelType.UnsignedByte, p);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.DeleteFramebuffer(fbo);
        _gl.DeleteTexture(tex);

        using var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bmp.PixelFormat);
        for (var y = 0; y < height; y++) // GL rows are bottom-up
            Marshal.Copy(pixels, (height - 1 - y) * width * 4, data.Scan0 + y * data.Stride, width * 4);
        bmp.UnlockBits(data);
        bmp.Save(path, ImageFormat.Png);
    }

    /// <summary>
    /// Times rendering: simulates a busy scene, then renders <paramref name="frames"/> frames offscreen as fast as
    /// possible and writes the average GPU+CPU cost per frame to <paramref name="reportPath"/>. No VSync here, so
    /// the number is the real work per frame, not the monitor's refresh interval.
    /// </summary>
    public void Benchmark(PipesSettings settings, int width, int height, int frames, string reportPath)
    {
        using var renderer = new PipeRenderer(_gl, RenderOptions.From(settings));
        var scene = new Scene(settings, new Random(1));
        renderer.Resize(width, height);
        scene.Start((float)width / height);
        for (var t = 0f; t < 12f; t += 1f / 60f) scene.Update(1f / 60f); // let the scene fill up first

        var fbo = _gl.GenFramebuffer();
        var tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);

        // Warm up (shader compilation, driver caches), then time. Finish() waits for the GPU, so the clock covers
        // the GPU's work, not just the CPU queueing commands.
        for (var i = 0; i < 30; i++) renderer.Render(scene.Camera, scene.Pieces, 1f, fbo);
        _gl.Finish();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < frames; i++)
        {
            scene.Update(1f / 60f);
            renderer.Render(scene.Camera, scene.Pieces, 1f, fbo);
        }
        _gl.Finish();
        var ms = clock.Elapsed.TotalMilliseconds / frames;

        _gl.DeleteFramebuffer(fbo);
        _gl.DeleteTexture(tex);
        File.WriteAllText(reportPath,
            $"{settings.Style} {width}x{height} AA={settings.Antialiasing}: {ms:F2} ms/frame ({1000 / ms:F0} fps max), " +
            $"{scene.Pieces[Simulation.MeshKind.Cylinder].Count + scene.Pieces[Simulation.MeshKind.Sphere].Count} pieces\n");
    }

    private void CreateWindow(bool visible)
    {
        var instance = Win32.GetModuleHandle(null);
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            style = Win32.CS_OWNDC | Win32.CS_HREDRAW | Win32.CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            hCursor = Win32.LoadCursor(IntPtr.Zero, new IntPtr(Win32.IDC_ARROW)),
            lpszClassName = ClassName,
        };
        Win32.RegisterClassEx(ref wc);

        uint style, exStyle = 0;
        int x, y, w, h;
        switch (_mode)
        {
            case HostMode.Fullscreen:
                style = Win32.WS_POPUP | Win32.WS_CLIPCHILDREN | Win32.WS_CLIPSIBLINGS;
                exStyle = Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW;
                x = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
                y = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
                w = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
                h = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
                break;
            case HostMode.Preview:
                style = Win32.WS_CHILD | Win32.WS_CLIPCHILDREN | Win32.WS_CLIPSIBLINGS;
                Win32.GetClientRect(_parent, out var r);
                (x, y, w, h) = (0, 0, r.Right - r.Left, r.Bottom - r.Top);
                break;
            default:
                style = visible ? Win32.WS_OVERLAPPEDWINDOW | Win32.WS_CLIPCHILDREN | Win32.WS_CLIPSIBLINGS : Win32.WS_POPUP;
                (x, y, w, h) = (100, 100, 1280, 760);
                break;
        }

        _hwnd = Win32.CreateWindowEx(exStyle, ClassName, "Pipes", style, x, y, w, h,
            _mode == HostMode.Preview ? _parent : IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");

        _hdc = Win32.GetDC(_hwnd);
        var pfd = new Win32.PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<Win32.PIXELFORMATDESCRIPTOR>(),
            nVersion = 1,
            dwFlags = Win32.PFD_DRAW_TO_WINDOW | Win32.PFD_SUPPORT_OPENGL | Win32.PFD_DOUBLEBUFFER,
            iPixelType = Win32.PFD_TYPE_RGBA,
            cColorBits = 32,
            cDepthBits = 24,
            iLayerType = Win32.PFD_MAIN_PLANE,
        };
        var format = Win32.ChoosePixelFormat(_hdc, ref pfd);
        if (format == 0 || !Win32.SetPixelFormat(_hdc, format, ref pfd))
            throw new InvalidOperationException("No usable OpenGL pixel format.");

        _hglrc = Win32.wglCreateContext(_hdc);
        if (_hglrc == IntPtr.Zero || !Win32.wglMakeCurrent(_hdc, _hglrc))
            throw new InvalidOperationException("Could not create an OpenGL context.");

        var opengl32 = Win32.LoadLibrary("opengl32.dll");
        _gl = GL.GetApi(name =>
        {
            var p = Win32.wglGetProcAddress(name);
            // wglGetProcAddress returns 0..3 or -1 for GL 1.1 entry points; those live in opengl32.dll itself.
            return p is 0 or 1 or 2 or 3 or -1 ? Win32.GetProcAddress(opengl32, name) : p;
        });

        // VSync: smooth motion and no busy-looping.
        var swapInterval = Win32.wglGetProcAddress("wglSwapIntervalEXT");
        if (swapInterval is not (0 or 1 or 2 or 3 or -1))
            ((delegate* unmanaged[Stdcall]<int, int>)swapInterval)(1);

        if (visible) Win32.ShowWindow(_hwnd, Win32.SW_SHOW);
    }

    private (int Width, int Height) ClientSize()
    {
        Win32.GetClientRect(_hwnd, out var r);
        return (Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
    }

    private IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_SIZE:
                _resized = true;
                break;
            case Win32.WM_ERASEBKGND:
                return 1;
            case Win32.WM_CLOSE:
                _running = false;
                break;
            case Win32.WM_DESTROY:
                _running = false;
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
            case Win32.WM_SYSCOMMAND when _mode == HostMode.Fullscreen && ((int)wParam & 0xFFF0) == Win32.SC_SCREENSAVE:
                return IntPtr.Zero; // already the screensaver; don't let Windows start another
            case Win32.WM_SETCURSOR when _mode == HostMode.Fullscreen:
                Win32.SetCursor(IntPtr.Zero);
                return 1;
        }

        if (_mode == HostMode.Fullscreen && IsExitInput(msg)) _running = false;
        if (_mode == HostMode.Windowed && msg == Win32.WM_KEYDOWN && (int)wParam == Win32.VK_ESCAPE) _running = false;

        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private bool IsExitInput(int msg)
    {
        // Ignore the burst of synthetic messages Windows sends while the window appears.
        if (_sinceStart.Elapsed < TimeSpan.FromMilliseconds(500)) return false;

        switch (msg)
        {
            case Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN or Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN
                or Win32.WM_MBUTTONDOWN or Win32.WM_MOUSEWHEEL:
                return true;
            case Win32.WM_MOUSEMOVE:
                if (!Win32.GetCursorPos(out var p)) return false;
                if (_initialCursor is not { } start) { _initialCursor = p; return false; }
                return Math.Abs(p.X - start.X) > MouseMoveTolerance || Math.Abs(p.Y - start.Y) > MouseMoveTolerance;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        if (_hglrc != IntPtr.Zero)
        {
            Win32.wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            Win32.wglDeleteContext(_hglrc);
            _hglrc = IntPtr.Zero;
        }
        if (_hwnd != IntPtr.Zero)
        {
            Win32.ReleaseDC(_hwnd, _hdc);
            Win32.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
