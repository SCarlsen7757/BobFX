from typing import Iterator
from .utility import ConvertHexStringToTupleRGB


def update(
    number_of_leds: int = 0,
    color: tuple[int, int, int] = ConvertHexStringToTupleRGB("#FF0000"),
) -> Iterator[tuple[tuple[tuple[int, int, int], ...], bool]]:
    """Solid effect (all LEDs the same color)"""
    matrix = render_text_5x7(
        "BobFX",
        width=32,
        height=8,
        fg=ConvertHexStringToTupleRGB("#00FFD5"),
        bg=ConvertHexStringToTupleRGB("#FF0000"),
    )
    leds: tuple[tuple[int, int, int]] = matrix_to_strip(matrix)
    step: int = 0
    while True:
        _ = (yield tuple(leds), True)  # type: ignore
        step += 1


def matrix_to_strip(
    matrix: list[list[tuple[int, int, int]]],
) -> tuple[tuple[int, int, int]]:
    """Convert 2D matrix into 1D LED strip array"""
    height = len(matrix)
    width = len(matrix[0])
    strip = []

    for y in range(height):
        row = matrix[y]
        if y % 2 == 0:
            # left to right
            strip.extend(row)
        else:
            # right to left (zig-zag)
            strip.extend(reversed(row))
    return strip


# 5x7 font definition (1 = pixel on, 0 = off)
FONT_5x7 = {
    "B": [
        "11110",
        "10001",
        "11110",
        "10001",
        "10001",
        "11110",
        "00000",
    ],
    "o": [
        "0000 ",
        "0000 ",
        "0110 ",
        "1001 ",
        "1001 ",
        "0110 ",
        "0000 ",
    ],
    "b": [
        "1000 ",
        "1000 ",
        "1110 ",
        "1001 ",
        "1001 ",
        "1110 ",
        "0000 ",
    ],
    "F": [
        "11111",
        "10000",
        "11110",
        "10000",
        "10000",
        "10000",
        "00000",
    ],
    "X": [
        "10001",
        "01010",
        "00100",
        "00100",
        "01010",
        "10001",
        "00000",
    ],
}


def render_text_5x7(
    text: str,
    width: int = 32,
    height: int = 8,
    fg: tuple[int, int, int] = ConvertHexStringToTupleRGB("#00FFD5"),
    bg: tuple[int, int, int] = ConvertHexStringToTupleRGB("#FF0000"),
) -> list[list[tuple[int, int, int]]]:
    """Render text into 2D LED matrix using 5x7 font"""
    matrix = [[bg for _ in range(width)] for _ in range(height)]

    x_offset = 2
    y_offset = 1
    for char in text:
        if char not in FONT_5x7:
            continue
        char_bitmap = FONT_5x7[char]
        for y, row in enumerate(char_bitmap):
            for x, pixel in enumerate(row):
                target_y = y + y_offset
                if pixel == "1" and 0 <= target_y < height and (x + x_offset) < width:
                    matrix[target_y][x + x_offset] = fg
        x_offset += len(char_bitmap[0]) + 1  # add space
    return matrix
