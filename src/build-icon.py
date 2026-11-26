"""Build the Windows icon and a preview at standard desktop sizes."""

import io
import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw


SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
SCALE = 3
SIDE = 256 * SCALE


def point(x, y):
    return round(x * SCALE), round(y * SCALE)


def polygon(draw, positions, color):
    draw.polygon([point(*position) for position in positions], fill=color)


def stroke(draw, positions, color, width):
    points = [point(*position) for position in positions]
    weight = round(width * SCALE)
    draw.line(points, fill=color, width=weight, joint="curve")
    for x, y in points:
        radius = weight // 2
        draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=color)


def render(size):
    mask = Image.new("L", (SIDE, SIDE))
    ImageDraw.Draw(mask).rounded_rectangle((*point(8, 8), *point(248, 248)), radius=49 * SCALE, fill=255)
    tile = Image.new("RGBA", (SIDE, SIDE))
    draw = ImageDraw.Draw(tile)
    light, dark = (27, 98, 79), (15, 52, 45)
    if size >= 24:
        for y in range(SIDE):
            ratio = y / (SIDE - 1)
            color = tuple(round(a + (b - a) * ratio) for a, b in zip(light, dark))
            draw.line((0, y, SIDE, y), fill=color)
    else:
        draw.rectangle((0, 0, SIDE, SIDE), fill=(17, 70, 58))

    if size >= 32:
        draw.rounded_rectangle((*point(13, 13), *point(243, 243)), radius=45 * SCALE,
                               outline=(62, 126, 107), width=2 * SCALE)

    # Isometric carton, with its tape extending from the lid onto the front.
    polygon(draw, [(43, 137), (96, 108), (149, 137), (96, 166)], "#FCEBC9")
    polygon(draw, [(43, 137), (96, 166), (96, 222), (43, 193)], "#E2A05F")
    polygon(draw, [(96, 166), (149, 137), (149, 193), (96, 222)], "#F5C389")
    if size >= 24:
        polygon(draw, [(66, 124), (77, 118), (130, 147), (119, 153)], "#D58E4A")
        polygon(draw, [(119, 153), (130, 147), (130, 171), (119, 177)], "#AE6938")

    # Upward forecast line, with a clear gap between it and the shipping carton.
    mint = "#A6F1D2"
    width = 15 if size >= 24 else 18
    stroke(draw, [(86, 93), (116, 70), (143, 89), (207, 48)], mint, width)
    stroke(draw, [(177, 48), (207, 48), (207, 79)], mint, width)
    tile.putalpha(mask)
    return tile.resize((size, size), Image.Resampling.LANCZOS)


def encode_frame(frame):
    if frame.width == 256:
        output = io.BytesIO()
        frame.save(output, format="PNG")
        return output.getvalue()

    width = frame.width
    pixels = frame.tobytes("raw", "BGRA")
    rows = [pixels[start:start + width * 4] for start in range(0, len(pixels), width * 4)]
    mask_stride = (width + 31) // 32 * 4
    mask = bytearray(mask_stride * width)
    alpha = frame.getchannel("A").tobytes()
    for index, opacity in enumerate(alpha):
        if opacity == 0:
            y, x = divmod(index, width)
            mask[(width - y - 1) * mask_stride + x // 8] |= 1 << (7 - x % 8)
    header = struct.pack("<IiiHHIIiiII", 40, width, width * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    return header + b"".join(reversed(rows)) + mask


def write_icon(frames, destination):
    payloads = [encode_frame(frame) for frame in frames]
    start = 6 + len(frames) * 16
    with destination.open("wb") as output:
        output.write(struct.pack("<HHH", 0, 1, len(frames)))
        for frame, data in zip(frames, payloads):
            dimension = frame.width % 256
            output.write(struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(data), start))
            start += len(data)
        output.writelines(payloads)


def write_preview(frames, destination):
    preview = Image.new("RGBA", (580, 320), "#EAF2EE")
    draw = ImageDraw.Draw(preview)
    draw.rounded_rectangle((17, 17, 304, 303), radius=22, fill="white")
    draw.rounded_rectangle((319, 17, 562, 303), radius=22, fill="#203630")
    preview.alpha_composite(frames[-1], (32, 32))
    for frame, center, y in zip((frames[7], frames[5], frames[3], frames[0]),
                               (441, 441, 409, 480), (42, 172, 251, 259)):
        preview.alpha_composite(frame, (center - frame.width // 2, y))
    preview.convert("RGB").save(destination)


if __name__ == "__main__":
    folder = Path(sys.argv[1])
    frames = [render(size) for size in SIZES]
    write_icon(frames, folder / "replenishment.ico")
    write_preview(frames, folder / "replenishment-icon-preview.png")
