from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
OUTPUT_DIR = ROOT / "build-assets"
OUTPUT = OUTPUT_DIR / "zervdiag.ico"
WIZARD_LARGE = OUTPUT_DIR / "wizard_large.bmp"
WIZARD_SMALL = OUTPUT_DIR / "wizard_small.bmp"


DARK = (11, 31, 51, 255)
DEEP = (7, 24, 39, 255)
MID = (23, 58, 92, 255)
CYAN = (0, 188, 212, 255)
LIGHT = (230, 236, 242, 255)
MUTED = (90, 103, 115, 255)


def _draw_mark(draw, s, compact=False):
    def width(value):
        return max(1, s(value))

    # Truck crane chassis and cab.
    draw.rounded_rectangle(
        [s(38), s(165), s(130), s(185)],
        radius=max(1, s(5)),
        fill=LIGHT,
    )
    draw.rounded_rectangle(
        [s(48), s(145), s(84), s(169)],
        radius=max(1, s(4)),
        fill=LIGHT,
    )
    if not compact:
        draw.rectangle([s(55), s(151), s(76), s(163)], fill=MID)

    for x in (57, 91, 121):
        draw.ellipse(
            [s(x - 12), s(178), s(x + 12), s(202)],
            fill=DARK,
            outline=LIGHT,
            width=width(6),
        )

    # Long straight telescopic boom.
    draw.line(
        [s(78), s(151), s(166), s(47)],
        fill=LIGHT,
        width=width(18),
    )
    draw.line(
        [s(83), s(146), s(163), s(52)],
        fill=MID,
        width=width(10),
    )
    draw.line(
        [s(104), s(119), s(116), s(126)],
        fill=CYAN,
        width=width(5),
    )
    draw.line(
        [s(132), s(87), s(144), s(94)],
        fill=CYAN,
        width=width(5),
    )
    draw.ellipse(
        [s(160), s(37), s(176), s(53)],
        fill=CYAN,
        outline=LIGHT,
        width=width(4),
    )

    # Rope and hook.
    draw.line(
        [s(170), s(50), s(170), s(111)],
        fill=LIGHT,
        width=width(6),
    )
    draw.arc(
        [s(145), s(102), s(178), s(132)],
        start=0,
        end=165,
        fill=CYAN,
        width=width(7),
    )

    # Diagnostic magnifier.
    draw.ellipse(
        [s(117), s(116), s(213), s(212)],
        fill=DARK,
        outline=LIGHT,
        width=width(10),
    )
    draw.line(
        [s(199), s(198), s(226), s(225)],
        fill=LIGHT,
        width=width(13),
    )

    # Hook block in magnifier.
    draw.rounded_rectangle(
        [s(148), s(139), s(174), s(167)],
        radius=max(1, s(5)),
        fill=CYAN,
    )
    if not compact:
        draw.line(
            [s(151), s(146), s(171), s(160)],
            fill=DARK,
            width=width(5),
        )
        draw.line(
            [s(151), s(157), s(163), s(166)],
            fill=DARK,
            width=width(5),
        )
    draw.line(
        [s(161), s(167), s(161), s(177)],
        fill=LIGHT,
        width=width(7),
    )
    draw.arc(
        [s(158), s(169), s(181), s(191)],
        start=0,
        end=170,
        fill=LIGHT,
        width=width(7),
    )

    # Diagnostic pulse at the rear of the crane.
    pulse = [
        (27, 119),
        (45, 119),
        (53, 105),
        (63, 137),
        (74, 113),
        (83, 119),
        (94, 119),
    ]
    draw.line(
        [(s(x), s(y)) for x, y in pulse],
        fill=CYAN,
        width=width(7),
    )


def _icon(size):
    scale = size / 256.0
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    def s(value):
        return int(round(value * scale))

    draw.rounded_rectangle(
        [s(10), s(10), s(246), s(246)],
        radius=max(1, s(46)),
        fill=DARK,
    )
    _draw_mark(draw, s, compact=size <= 32)
    return image


def _wizard_large():
    width, height = 164, 314
    image = Image.new("RGB", (width, height), DEEP[:3])
    draw = ImageDraw.Draw(image)

    # Subtle technical grid.
    grid = (18, 50, 75)
    for x in range(0, width, 20):
        draw.line((x, 0, x, height), fill=grid, width=1)
    for y in range(0, height, 20):
        draw.line((0, y, width, y), fill=grid, width=1)

    # Cyan vertical accent and scaled product mark.
    draw.rectangle((0, 0, 5, height), fill=CYAN[:3])
    mark = _icon(142).convert("RGBA")
    image.paste(mark, (11, 64), mark)

    # Diagnostic pulse along the lower panel.
    pulse = [(12, 272), (38, 272), (48, 254), (61, 291), (75, 263), (88, 272), (147, 272)]
    draw.line(pulse, fill=CYAN[:3], width=4)
    draw.rectangle((12, 297, 152, 299), fill=(45, 77, 100))
    return image


def _wizard_small():
    image = Image.new("RGB", (55, 55), DARK[:3])
    icon = _icon(51).convert("RGBA")
    image.paste(icon, (2, 2), icon)
    return image


def main():
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    image = _icon(256)
    image.save(
        OUTPUT,
        format="ICO",
        sizes=[(size, size) for size in sizes],
    )
    _wizard_large().save(WIZARD_LARGE, format="BMP")
    _wizard_small().save(WIZARD_SMALL, format="BMP")
    print(f"BRAND ICON: {OUTPUT}")
    print(f"WIZARD LARGE: {WIZARD_LARGE}")
    print(f"WIZARD SMALL: {WIZARD_SMALL}")


if __name__ == "__main__":
    main()
