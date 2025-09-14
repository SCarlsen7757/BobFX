"""Module for RAINBOW effect"""

from typing import List, Iterator
import colorsys


def update(
    number_of_leds: int = 0, color: str = "#FF0000"
) -> Iterator[tuple[tuple[str], bool]]:
    """Off effect (all LEDs black)"""
    leds: List[str] = []
    step: int = 0
    while True:
        leds.clear()
        for i in range(number_of_leds):
            hue_step = 360 / number_of_leds  # base hue spacing
            factor = 3  # increase spacing (try 2, 3, etc.)

            hue = ((i * hue_step * factor) + step) % 360 / 360.0
            r, g, b = colorsys.hls_to_rgb(hue, 0.5, 1)
            leds.append(
                "#{:02x}{:02x}{:02x}".format(int(r * 255), int(g * 255), int(b * 255))
            )

        color = (yield tuple(leds), True)  # type: ignore
        step = (step + 1) % 360
