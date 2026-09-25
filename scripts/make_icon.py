#!/usr/bin/env python3
"""
Generates src/Pipes/Pipes.ico procedurally (no external art assets).

Renders two versions of the same idea -- a few glossy 3D pipes with a right-angle bend and
"ball joint" elbows, in the classic Pipes screensaver palette, on a dark navy rounded-square
background:

  - a "detailed" scene (three pipes) used for the larger icon sizes (48-256px), and
  - a "simple" scene (two thick pipes, one crossing) used for the small sizes (16-32px),
    where the detailed scene's thinner pipes would turn to mush.

Each scene is rendered once at a high working resolution (see MASTER_SIZE) with numpy-based
per-pixel shading (a cylinder lighting model for the pipe tubes, a sphere lighting model for the
ball joints/end caps), then downscaled per output size with Lanczos resampling for smooth
antialiasing. The results are packed into a single multi-resolution .ico by hand (Pillow's ICO
writer only ever resizes one source image, and we want the small sizes to come from the
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

# A single light direction (normalized) shared by every pipe and joint, so the whole icon reads
# as one consistently-lit scene rather than a pile of separately-lit parts.
_light = np.array([-0.55, -0.55, 0.63])
LIGHT = _light / np.linalg.norm(_light)

AMBIENT = 0.30      # base illumination so shadowed sides aren't pure black
DIFFUSE = 0.75       # how much the lambertian term contributes
SPEC_POWER = 26.0    # tight highlight = glossy plastic; lower would look more matte
SPEC_STRENGTH = 0.9
EDGE_AA_PX = 1.6      # antialiasing feather width, in master-canvas pixels


def _shade(nx: np.ndarray, ny: np.ndarray, nz: np.ndarray, color: tuple[int, int, int]) -> np.ndarray:
    """Blinn-Phong-ish shading for a surface normal field (nx, ny, nz), each in [-1, 1]."""
    ndotl = np.clip(nx * LIGHT[0] + ny * LIGHT[1] + nz * LIGHT[2], 0.0, 1.0)
    diffuse = AMBIENT + DIFFUSE * ndotl
    specular = SPEC_STRENGTH * np.power(ndotl, SPEC_POWER)
    base = np.array(color, dtype=np.float64)
    rgb = base[None, None, :] * diffuse[..., None] + 255.0 * specular[..., None]
    return np.clip(rgb, 0, 255)


def _capsule(X: np.ndarray, Y: np.ndarray, p0, p1, radius: float, color):
    """Shaded color + antialiased coverage mask for a pipe segment (a thick rounded line)."""
    x0, y0 = p0
    x1, y1 = p1
    dx, dy = x1 - x0, y1 - y0
    seg_len2 = dx * dx + dy * dy
    if seg_len2 < 1e-9:
        t = np.zeros_like(X)
        dirx, diry = 1.0, 0.0
    else:
        t = np.clip(((X - x0) * dx + (Y - y0) * dy) / seg_len2, 0.0, 1.0)
        seg_len = seg_len2 ** 0.5
        dirx, diry = dx / seg_len, dy / seg_len
    projx, projy = x0 + t * dx, y0 + t * dy
    offx, offy = X - projx, Y - projy
    dist = np.sqrt(offx * offx + offy * offy)

    # Perpendicular axis to the pipe's direction, used to place the point on the tube's round
    # cross-section (s = sin of the angle around the cylinder, in [-1, 1]).
    perpx, perpy = -diry, dirx
    s = np.clip((offx * perpx + offy * perpy) / radius, -1.0, 1.0)
    nz = np.sqrt(np.clip(1.0 - s * s, 0.0, 1.0))
    nx, ny = perpx * s, perpy * s

    alpha = np.clip((radius - dist) / EDGE_AA_PX + 0.5, 0.0, 1.0)
    rgb = _shade(nx, ny, nz, color)
    return rgb, alpha


def _sphere(X: np.ndarray, Y: np.ndarray, center, radius: float, color):
    """Shaded color + antialiased coverage mask for a ball joint / rounded end cap."""
    cx, cy = center
    dx, dy = X - cx, Y - cy
    dist = np.sqrt(dx * dx + dy * dy)
    r = np.clip(dist / radius, 0.0, 1.0)
    nz = np.sqrt(np.clip(1.0 - r * r, 0.0, 1.0))
    nx, ny = dx / radius, dy / radius

    alpha = np.clip((radius - dist) / EDGE_AA_PX + 0.5, 0.0, 1.0)
    rgb = _shade(nx, ny, nz, color)
    return rgb, alpha


def _composite(base: np.ndarray, rgb: np.ndarray, alpha: np.ndarray) -> None:
    """Alpha-blend (rgb, alpha) over base (H, W, 4) float image, in place. `alpha` is a 0..1
    fraction; base's own alpha channel is stored 0..255 like its color channels, so it needs
    scaling up when folded into the "over" formula."""
    a = alpha[..., None]
    base[..., :3] = base[..., :3] * (1 - a[..., 0])[..., None] + rgb * a
    base[..., 3] = alpha * 255.0 + base[..., 3] * (1 - alpha)


class Pipe:
    """A polyline of axis-aligned points: straight capsule segments joined by ball joints, with
    rounded caps at the two open ends."""

    def __init__(self, points: list[tuple[float, float]], radius: float, color: tuple[int, int, int]):
        self.points = points
        self.radius = radius
        self.color = color

    def draw(self, canvas: np.ndarray, X: np.ndarray, Y: np.ndarray) -> None:
        for p0, p1 in zip(self.points, self.points[1:]):
            rgb, alpha = _capsule(X, Y, p0, p1, self.radius, self.color)
            _composite(canvas, rgb, alpha)
        # Ball joints at interior bend points, a little larger than the pipe -- the classic
        # "3D Pipes" look -- drawn after the segments so they cleanly cover the seam at the bend.
        for bend in self.points[1:-1]:
            rgb, alpha = _sphere(X, Y, bend, self.radius * 1.15, self.color)
            _composite(canvas, rgb, alpha)
        # Rounded caps at the two open ends (same radius as the pipe, so it reads as a capped tube).
        for end in (self.points[0], self.points[-1]):
            rgb, alpha = _sphere(X, Y, end, self.radius, self.color)
            _composite(canvas, rgb, alpha)

    def coverage(self, X: np.ndarray, Y: np.ndarray) -> np.ndarray:
        """Unshaded silhouette (max alpha across every part), used for the drop-shadow pass."""
        cov = np.zeros(X.shape, dtype=np.float64)
        for p0, p1 in zip(self.points, self.points[1:]):
            _, alpha = _capsule(X, Y, p0, p1, self.radius, self.color)
            cov = np.maximum(cov, alpha)
        for bend in self.points[1:-1]:
            _, alpha = _sphere(X, Y, bend, self.radius * 1.15, self.color)
            cov = np.maximum(cov, alpha)
        for end in (self.points[0], self.points[-1]):
            _, alpha = _sphere(X, Y, end, self.radius, self.color)
            cov = np.maximum(cov, alpha)
        return cov


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


def _add_contact_shadow(canvas: np.ndarray, pipes: list[Pipe], X: np.ndarray, Y: np.ndarray, size: int) -> None:
    """Soft dark blob under the pipes (offset + blurred silhouette), so they look like they sit
    slightly above the background instead of being pasted flat onto it."""
    offset = size * 0.018
    shadow = np.zeros((size, size), dtype=np.float64)
    for pipe in pipes:
        Xo, Yo = X - offset, Y - offset * 1.3
        shadow = np.maximum(shadow, pipe.coverage(Xo, Yo))
    shadow_img = Image.fromarray((shadow * 255).astype(np.uint8), mode="L")
    shadow_img = shadow_img.filter(ImageFilter.GaussianBlur(radius=size * 0.012))
    shadow_alpha = (np.asarray(shadow_img, dtype=np.float64) / 255.0) * 0.5
    # Only darken where the background itself is opaque (inside the rounded square).
    shadow_alpha *= canvas[..., 3] / 255.0
    canvas[..., :3] *= (1 - shadow_alpha[..., None])


def _scene(pipes: list[Pipe], size: int) -> Image.Image:
    canvas = _rounded_square_background(size)
    xs = np.arange(size, dtype=np.float64) + 0.5
    X, Y = np.meshgrid(xs, xs)
    _add_contact_shadow(canvas, pipes, X, Y, size)
    for pipe in pipes:
        pipe.draw(canvas, X, Y)
    return Image.fromarray(np.clip(canvas, 0, 255).astype(np.uint8), mode="RGBA")


def _detailed_scene(size: int) -> Image.Image:
    """Three pipes: red and blue cross once (blue drawn on top, so it reads as passing in front),
    plus a short yellow accent pipe for a third color pop. Coordinates are in a 1024-unit space,
    scaled to `size`."""
    s = size / 1024.0

    def pts(*xy: tuple[float, float]) -> list[tuple[float, float]]:
        return [(x * s, y * s) for x, y in xy]

    pipes = [
        Pipe(pts((185, 780), (185, 430), (575, 430), (575, 245)), radius=70 * s, color=RED),
        Pipe(pts((820, 780), (440, 780), (440, 345)), radius=70 * s, color=BLUE),
        Pipe(pts((695, 185), (695, 305), (810, 305)), radius=52 * s, color=YELLOW),
    ]
    return _scene(pipes, size)


def _simple_scene(size: int) -> Image.Image:
    """Two thick pipes with one bend each and a single crossing -- bold enough to survive being
    shrunk to 16px. Same 1024-unit coordinate space as the detailed scene."""
    s = size / 1024.0

    def pts(*xy: tuple[float, float]) -> list[tuple[float, float]]:
        return [(x * s, y * s) for x, y in xy]

    pipes = [
        Pipe(pts((195, 800), (195, 400), (610, 400)), radius=140 * s, color=RED),
        Pipe(pts((830, 800), (500, 800), (500, 235)), radius=140 * s, color=CYAN),
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
