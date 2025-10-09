def ConvertHexStringToTupleRGB(hex_color: str) -> tuple[int, int, int]:
    hex_color = hex_color.lstrip("#")

    if len(hex_color) != 6:
        raise ValueError("Invalid hex color string!")

    r = int(hex_color[0:2], 16)
    g = int(hex_color[2:4], 16)
    b = int(hex_color[4:6], 16)

    return (r, g, b)


def ConvertTuplesRGBToTupleHex(colors: tuple[tuple[int, int, int]]) -> tuple[str, ...]:
    return tuple(f"#{r:02X}{g:02X}{b:02X}" for r, g, b in colors)
