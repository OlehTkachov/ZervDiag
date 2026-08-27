from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
OUTPUT_DIR = ROOT / "build-assets"
OUTPUT = OUTPUT_DIR / "zervdiag.ico"


def _icon(size):
    scale = size / 256.0
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    def s(value):
        return int(round(value * scale))

    amber = (245, 158, 11, 255)
    ink = (17, 25, 35, 255)
    light = (232, 238, 245, 255)

    draw.rounded_rectangle(
        [s(12), s(12), s(244), s(244)],
        radius=max(1, s(44)),
        fill=ink,
    )

    # Crane boom and hook.
    draw.line(
        [s(48), s(78), s(166), s(44)],
        fill=amber,
        width=max(1, s(14)),
    )
    draw.line(
        [s(164), s(44), s(178), s(54)],
        fill=amber,
        width=max(1, s(12)),
    )
    draw.line(
        [s(176), s(50), s(176), s(105)],
        fill=amber,
        width=max(1, s(10)),
    )
    draw.arc(
        [s(148), s(94), s(184), s(126)],
        start=0,
        end=155,
        fill=amber,
        width=max(1, s(10)),
    )

    # Diagnostic magnifier and pulse.
    draw.ellipse(
        [s(98), s(104), s(204), s(210)],
        outline=light,
        width=max(1, s(11)),
    )
    draw.line(
        [s(189), s(195), s(222), s(228)],
        fill=light,
        width=max(1, s(14)),
    )

    pulse = [
        (112, 158),
        (127, 158),
        (136, 139),
        (148, 177),
        (159, 151),
        (168, 158),
        (188, 158),
    ]
    draw.line(
        [(s(x), s(y)) for x, y in pulse],
        fill=amber,
        width=max(1, s(9)),
    )

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
    print(f"BRAND ICON: {OUTPUT}")


if __name__ == "__main__":
    main()
