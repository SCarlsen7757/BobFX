"""Module for SCANNER effect"""

from typing import List, Iterator
from .utility import ConvertHexStringToTupleRGB


def update(
    number_of_leds: int = 0,
    color: tuple[int, int, int] = ConvertHexStringToTupleRGB("#FF0000"),
) -> Iterator[tuple[tuple[tuple[int, int, int], ...], bool]]:
    """Scanner effect (like Knight Rider or Cylon eye)"""
    leds: List[tuple[int, int, int]] = []
    pos = 0
    direction = 1

    while True:
        leds.clear()
        for i in range(number_of_leds):
            # Fade trail based on distance from the current position
            distance = abs(i - pos)
            fade = max(0, 1 - distance * 0.3)
            r = int(color[0] * fade)  # type: ignore
            g = int(color[1] * fade)  # type: ignore
            b = int(color[2] * fade)  # type: ignore
            leds.append((r, g, b))

        new_color: tuple[int, int, int] = (yield tuple(leds), True)  # type: ignore

        if new_color:
            color = new_color  # type: ignore

        pos += direction
        if pos >= number_of_leds - 1:
            direction = -1
        elif pos <= 0:
            direction = 1
