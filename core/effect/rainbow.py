"""Module for RAINBOW effect"""

from typing import List, Iterator
import colorsys
from .utility import ConvertHexStringToTupleRGB


def update(
    number_of_leds: int = 0,
    color: tuple[int, int, int] = ConvertHexStringToTupleRGB("#FF0000"),
) -> Iterator[tuple[tuple[tuple[int, int, int], ...], bool]]:
    """Off effect (all LEDs black)"""
    leds: List[tuple[int, int, int]] = []
    step: int = 0
    while True:
        leds.clear()
        for i in range(number_of_leds):
            hue_step = 360 / number_of_leds  # base hue spacing
            factor = 3  # increase spacing (try 2, 3, etc.)

            hue = ((i * hue_step * factor) + step) % 360 / 360.0
            r, g, b = colorsys.hls_to_rgb(hue, 0.5, 1)
            leds.append((int(r * 255), int(g * 255), int(b * 255)))

        _ = (yield tuple(leds), True)  # type: ignore
        step = (step + 1) % 360
