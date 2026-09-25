#!/usr/bin/env python3
"""
Generates src/Pipes/Pipes.ico procedurally (no external art assets).

Renders two versions of the same idea -- glossy 3D pipes with ball joints at the bends, in the
classic Pipes screensaver palette, seen from above at a diagonal like the app's camera, on a dark
navy rounded-square background:

  - a "detailed" scene (three pipes) used for the larger icon sizes (48-256px), and
  - a "simple" scene (two fat pipes) used for the small sizes (16-32px), where the detailed
    scene's thinner pipes would turn to mush.

The pipes are real 3D cylinders and spheres, ray traced with numpy: a ray per pixel, the nearest
shape it hits, then the app's lighting at that point (key light with a shadow ray, fill light,
highlight, ambient, tonemap). Each scene is rendered once at a high working resolution (see
MASTER_SIZE), then downscaled per output size with Lanczos resampling, which is what smooths the
edges (supersampling). The results are packed into a single multi-resolution .ico by hand (Pillow's
ICO writer only ever resizes one source image, and we want the small sizes to come from the
simplified scene instead of a shrunk copy of the detailed one).

Re-run this after changing anything below:
    python scripts/make_icon.py
"""
from __future__ import annotations

import io
import struct
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

REPO_ROOT = Path(__file__).resolve().parent.parent
ICO_PATH = REPO_ROOT / "src" / "Pipes" / "Pipes.ico"
PREVIEW_DIR = REPO_ROOT / "scripts" / "_icon_preview"

# Sizes Windows actually looks for in an .ico (Explorer, taskbar, Alt+Tab, shortcut properties...).
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]
SIMPLE_SIZES = {16, 24, 32}  # rendered from the simplified 2-pipe scene

# Working resolution for both scenes. 1024 gives 4x supersampling over the largest (256) output
# and 64x/42x/32x over the smallest (16/24/32) outputs, so Lanczos downscaling has plenty of
# detail to average away -- that's what keeps edges smooth instead of jagged.
MASTER_SIZE = 1024

# Classic "3D Pipes" palette: saturated primaries that read clearly against the dark background.
RED = (224, 48, 42)
BLUE = (36, 108, 224)
YELLOW = (232, 182, 40)
CYAN = (34, 186, 206)

BG_TOP = (10, 18, 38)      # dark navy gradient for the rounded-square background
BG_BOTTOM = (19, 31, 56)
CORNER_RADIUS_FRAC = 0.185  # squircle-ish corner rounding, as a fraction of the canvas size

# ---- 3D scene --------------------------------------------------------------------------------------
# The pipes are real 3D shapes (cylinders for the runs, spheres for the ball joints and end caps) on a
# grid, like the app's, and each pixel is ray traced: a ray from the camera through the pixel, the
# nearest shape it hits, then lighting at that point. It's the same idea as the app's renderer, done
# the slow, simple way on the CPU with numpy: one shape at a time, over all pixels at once.

# The app's lights (PipeRenderer.KeyLight / FillLight): a warm key light from above, front and right,
# which casts the shadows, and a dim blue fill from the back left. Colours are linear-light intensities.
KEY_DIR = np.array([0.5, 0.8, 0.6]) / np.linalg.norm([0.5, 0.8, 0.6])
FILL_DIR = np.array([-0.7, 0.2, -0.4]) / np.linalg.norm([-0.7, 0.2, -0.4])
KEY_COLOR = np.array([2.6, 2.45, 2.25])
FILL_COLOR = np.array([0.45, 0.55, 0.8])

# Where the camera sits relative to the scene: up, to the right and in front, looking down across the
# pipes at a diagonal, the way the app's camera usually sees them. Distance is in grid cells; the
# picture is then zoomed to fit the canvas (see _camera), so this only sets how strong the perspective is.
VIEW_DIR = np.array([0.95, 0.75, 1.25]) / np.linalg.norm([0.95, 0.75, 1.25])
VIEW_DISTANCE = 11.0

BALL_SCALE = 1.5   # ball joints are this much fatter than the pipe, as in the app (PipeWorld.BallScale)
MARGIN = 0.1       # empty border around the pipes, as a fraction of the canvas


def _to_linear(c):
    """sRGB 0..255 to linear light, like PipeWorld.ToLinear: lighting maths only works in linear."""
    return (np.array(c, dtype=np.float64) / 255.0) ** 2.2


class Pipe:
    """A run of pipe through grid points (x, y, z): straight cylinders between them, a ball joint at
    every bend and a round cap at both open ends."""

    def __init__(self, points, radius: float, color):
        self.points = [np.array(p, dtype=np.float64) for p in points]
        self.radius = radius
        self.albedo = _to_linear(color)

    def shapes(self):
        """(kind, a, b, radius, albedo) tuples: "cyl" from a to b, or "sph" centred on a (b unused)."""
        out = []
        for a, b in zip(self.points, self.points[1:]):
            out.append(("cyl", a, b, self.radius, self.albedo))
        for bend in self.points[1:-1]:
            out.append(("sph", bend, None, self.radius * BALL_SCALE, self.albedo))
        for end in (self.points[0], self.points[-1]):
            out.append(("sph", end, None, self.radius, self.albedo))
        return out


def _dot(a, b):
    return np.einsum("...i,...i", a, b)


def _hit_sphere(o, d, c, r):
    """Distance along each ray (o + t*d) to the sphere's near side, or inf for a miss. Solves
    |o + t*d - c|^2 = r^2, a quadratic in t (d is unit length, so its first coefficient is 1)."""
    oc = o - c
    b = _dot(oc, d)
    disc = b * b - (_dot(oc, oc) - r * r)
    t = -b - np.sqrt(np.maximum(disc, 0.0))
    return np.where((disc >= 0.0) & (t > 1e-4), t, np.inf)


def _hit_cylinder(o, d, a, b, r):
    """Distance to the side of a finite cylinder from a to b (its ends are covered by spheres). The
    same quadratic as a sphere, but with the part along the axis removed, so it's the distance from
    the axis that must equal r; then the hit only counts if it lands between the two ends."""
    length = np.linalg.norm(b - a)
    axis = (b - a) / length
    oc = o - a
    d_perp = d - (d @ axis)[..., None] * axis
    oc_perp = oc - (oc @ axis)[..., None] * axis
    qa = _dot(d_perp, d_perp)
    qb = _dot(oc_perp, d_perp)
    qc = _dot(oc_perp, oc_perp) - r * r
    disc = qb * qb - qa * qc
    t = (-qb - np.sqrt(np.maximum(disc, 0.0))) / np.maximum(qa, 1e-12)
    along = (oc + t[..., None] * d) @ axis
    ok = (disc >= 0.0) & (t > 1e-4) & (along >= 0.0) & (along <= length)
    return np.where(ok, t, np.inf)


def _normal(shape, p):
    kind, a, b, _, _ = shape
    if kind == "sph":
        n = p - a
    else:
        axis = (b - a) / np.linalg.norm(b - a)
        rel = p - a
        n = rel - (rel @ axis)[..., None] * axis  # straight out from the axis
    return n / np.linalg.norm(n, axis=-1, keepdims=True)


def _trace(shapes, o, d):
    """Nearest hit over all shapes: (distance, index of the shape) per ray, inf / -1 for a miss."""
    best = np.full(d.shape[:-1], np.inf)
    idx = np.full(d.shape[:-1], -1)
    for i, (kind, a, b, r, _) in enumerate(shapes):
        t = _hit_sphere(o, d, a, r) if kind == "sph" else _hit_cylinder(o, d, a, b, r)
        closer = t < best
        best = np.where(closer, t, best)
        idx = np.where(closer, i, idx)
    return best, idx


def _camera(pipes):
    """Camera position and axes, and the image-plane window that just fits the pipes plus a MARGIN."""
    pts = np.array([p for pipe in pipes for p in pipe.points])
    centre = (pts.min(axis=0) + pts.max(axis=0)) / 2.0
    eye = centre + VIEW_DIR * VIEW_DISTANCE
    forward = (centre - eye) / np.linalg.norm(centre - eye)
    right = np.cross(forward, [0.0, 1.0, 0.0])
    right /= np.linalg.norm(right)
    up = np.cross(right, forward)

    # Project each grid point onto the image plane one unit in front of the eye (u, v = sideways and
    # upward offset / depth), grown by its ball joint's radius, and frame the square around them all.
    us, vs = [], []
    for pipe in pipes:
        for p in pipe.points:
            rel = p - eye
            depth = rel @ forward
            grow = pipe.radius * BALL_SCALE / depth
            u, v = rel @ right / depth, rel @ up / depth
            us += [u - grow, u + grow]
            vs += [v - grow, v + grow]
    cu, cv = (min(us) + max(us)) / 2.0, (min(vs) + max(vs)) / 2.0
    half = max(max(us) - min(us), max(vs) - min(vs)) / 2.0 / (1.0 - 2.0 * MARGIN)
    return eye, forward, right, up, cu, cv, half


def _shade(shapes, idx, p, n, d):
    """Lighting at the hit points, after the app's modern shader with surface detail off: Lambert
    diffuse and a Blinn-Phong highlight from both lights (the key light shadowed), a hemisphere
    ambient, and a faint reflection of the dark sky at grazing angles (Fresnel)."""
    albedo = np.zeros(p.shape)
    for i, shape in enumerate(shapes):
        albedo[idx == i] = shape[4]

    # Shadow ray towards the key light: if it hits anything, this point is in shadow. It starts a hair
    # off the surface so it can't hit the very shape it leaves (the ray tracer's "shadow acne").
    t_shadow, _ = _trace(shapes, p + n * 1e-3, np.broadcast_to(KEY_DIR, p.shape))
    lit = np.isinf(t_shadow).astype(np.float64)

    v = -d
    color = np.zeros(p.shape)
    for light, light_color, visible in ((KEY_DIR, KEY_COLOR, lit), (FILL_DIR, FILL_COLOR, 1.0)):
        ndotl = np.clip(n @ light, 0.0, 1.0)
        h = (light + v) / np.linalg.norm(light + v, axis=-1, keepdims=True)
        spec = np.clip(_dot(n, h), 0.0, 1.0) ** 90.0 * 0.8  # tight: glossy plastic
        color += (visible * ndotl)[..., None] * light_color * (albedo + spec[..., None])
    color += albedo * (0.6 + 0.4 * n[..., 1:2]) * np.array([0.16, 0.18, 0.24])  # brighter from above
    ndotv = np.clip(_dot(n, v), 0.0, 1.0)
    color += (0.04 + 0.96 * (1.0 - ndotv) ** 5)[..., None] * np.array([0.08, 0.1, 0.14])
    return color


def _render(pipes, size: int) -> np.ndarray:
    """RGBA (0..255 floats) of the pipes alone, transparent where no pipe is."""
    shapes = [s for pipe in pipes for s in pipe.shapes()]
    eye, forward, right, up, cu, cv, half = _camera(pipes)
    px = (np.arange(size) + 0.5) / size * 2.0 - 1.0
    U, V = np.meshgrid(cu + px * half, cv - px * half)  # image rows go down, v goes up
    d = forward + U[..., None] * right + V[..., None] * up
    d /= np.linalg.norm(d, axis=-1, keepdims=True)
    o = np.broadcast_to(eye, d.shape)

    t, idx = _trace(shapes, o, d)
    hit = np.isfinite(t)
    p = o + np.where(hit, t, 0.0)[..., None] * d
    n = np.zeros(d.shape)
    n[..., 1] = 1.0  # anything, for the rays that hit nothing
    for i, shape in enumerate(shapes):
        mask = idx == i
        if mask.any():
            n[mask] = _normal(shape, p[mask])

    color = _shade(shapes, idx, p, n, d)
    # HDR to display, as in the app's post pass: a filmic tonemap (Narkowicz's ACES fit), then gamma.
    mapped = np.clip((color * (2.51 * color + 0.03)) / (color * (2.43 * color + 0.59) + 0.14), 0.0, 1.0)
    rgba = np.zeros((size, size, 4))
    rgba[..., :3] = mapped ** (1 / 2.2) * 255.0
    rgba[..., 3] = hit * 255.0
    return rgba


def _add_contact_shadow(canvas: np.ndarray, layer: np.ndarray, size: int) -> None:
    """A soft dark halo behind the pipes (their silhouette, blurred and nudged down), so they stand
    off the background instead of looking pasted onto it."""
    silhouette = Image.fromarray(layer[..., 3].astype(np.uint8), mode="L")
    silhouette = silhouette.transform(silhouette.size, Image.AFFINE, (1, 0, 0, 0, 1, -size * 0.02))
    blurred = silhouette.filter(ImageFilter.GaussianBlur(radius=size * 0.02))
    shadow = np.asarray(blurred, dtype=np.float64) / 255.0 * 0.55 * (canvas[..., 3] / 255.0)
    canvas[..., :3] *= (1 - shadow[..., None])


def _scene(pipes, size: int) -> Image.Image:
    canvas = _rounded_square_background(size)
    layer = _render(pipes, size)
    _add_contact_shadow(canvas, layer, size)
    a = layer[..., 3:4] / 255.0 * (canvas[..., 3:4] / 255.0)
    canvas[..., :3] = canvas[..., :3] * (1 - a) + layer[..., :3] * a
    return Image.fromarray(np.clip(canvas, 0, 255).astype(np.uint8), mode="RGBA")


def _rounded_square_background(size: int) -> np.ndarray:
    """Dark navy vertical gradient, clipped to a rounded square (transparent outside it)."""
    mask_img = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask_img).rounded_rectangle(
        [0, 0, size - 1, size - 1], radius=int(size * CORNER_RADIUS_FRAC), fill=255
    )
    mask = np.asarray(mask_img, dtype=np.float64) / 255.0

    t = np.linspace(0.0, 1.0, size)[:, None]
    top, bottom = np.array(BG_TOP, dtype=np.float64), np.array(BG_BOTTOM, dtype=np.float64)
    gradient = top[None, None, :] * (1 - t)[..., None] + bottom[None, None, :] * t[..., None]
    gradient = np.broadcast_to(gradient, (size, size, 3)).copy()

    canvas = np.zeros((size, size, 4), dtype=np.float64)
    canvas[..., :3] = gradient
    canvas[..., 3] = mask * 255.0
    return canvas


def _detailed_scene(size: int) -> Image.Image:
    """Three pipes around the corner of a small block of grid cells, as the app's camera sees its box: red
    up and over the top, blue passing underneath it (in its shadow), and a short yellow run in front."""
    pipes = [
        Pipe([(0, 0, 2), (0, 2, 2), (2, 2, 2), (2, 2, 0)], radius=0.3, color=RED),
        Pipe([(3, 1, 1), (1, 1, 1), (1, -1, 1)], radius=0.3, color=BLUE),
        Pipe([(1, 0, 3), (2, 0, 3), (2, 1, 3)], radius=0.3, color=YELLOW),
    ]
    return _scene(pipes, size)


def _simple_scene(size: int) -> Image.Image:
    """Two fat pipes: a red elbow and a short cyan run. Bold enough to survive being shrunk to 16px."""
    pipes = [
        Pipe([(0, 0, 1), (0, 2, 1), (2, 2, 1)], radius=0.5, color=RED),
        Pipe([(2, 0, 0), (2, 1, 0)], radius=0.5, color=CYAN),
    ]
    return _scene(pipes, size)


def _write_ico(images: dict[int, Image.Image], path: Path) -> None:
    """Hand-rolled multi-resolution .ico writer (PNG-compressed entries, which every Windows
    version since Vista accepts at any size). Pillow's own ICO writer always derives every size
    by resizing a single source image, which would throw away our simplified small-size scene --
    so we build the container ourselves from the exact per-size renders instead."""
    sizes = sorted(images)
    entries = []
    payloads = []
    for sz in sizes:
        buf = io.BytesIO()
        images[sz].save(buf, format="PNG")
        data = buf.getvalue()
        payloads.append(data)
        # width/height fields are bytes; 256 wraps to 0 per the ICO spec.
        dim = 0 if sz == 256 else sz
        entries.append((dim, dim, 0, 0, 1, 32, len(data)))

    header = struct.pack("<HHH", 0, 1, len(sizes))
    offset = 6 + 16 * len(sizes)
    dir_entries = b""
    for (w, h, colors, reserved, planes, bitcount, size_bytes), data in zip(entries, payloads):
        dir_entries += struct.pack("<BBBBHHII", w, h, colors, reserved, planes, bitcount, size_bytes, offset)
        offset += len(data)

    path.write_bytes(header + dir_entries + b"".join(payloads))


def main() -> None:
    detailed_master = _detailed_scene(MASTER_SIZE)
    simple_master = _simple_scene(MASTER_SIZE)

    images: dict[int, Image.Image] = {}
    for sz in ICO_SIZES:
        master = simple_master if sz in SIMPLE_SIZES else detailed_master
        images[sz] = master.resize((sz, sz), Image.LANCZOS)

    ICO_PATH.parent.mkdir(parents=True, exist_ok=True)
    _write_ico(images, ICO_PATH)
    print(f"Wrote {ICO_PATH} ({', '.join(str(s) for s in ICO_SIZES)})")

    # Preview PNGs (not part of the build) so the result can be eyeballed without an ICO viewer.
    PREVIEW_DIR.mkdir(parents=True, exist_ok=True)
    images[256].save(PREVIEW_DIR / "preview_256.png")
    images[32].save(PREVIEW_DIR / "preview_32.png")
    images[32].resize((256, 256), Image.NEAREST).save(PREVIEW_DIR / "preview_32_zoomed.png")
    print(f"Wrote previews to {PREVIEW_DIR}")


if __name__ == "__main__":
    main()
