"""Module for RANDOM COLOR effect"""

from typing import List, Iterator
import random
from .utility import ConvertHexStringToTupleRGB


def update(
    number_of_leds: int = 0,
    color: tuple[int, int, int] = ConvertHexStringToTupleRGB("#FFFFFF"),
) -> Iterator[tuple[tuple[tuple[int, int, int], ...], bool]]:
    """Random color effect (each LED slowly changes to random colors)"""
    leds: List[tuple[int, int, int]] = [(0, 0, 0)] * number_of_leds
    target_colors = [color] * number_of_leds

    while True:
        new_leds = []
        for i in range(number_of_leds):
            r, g, b = leds[i]
            tr, tg, tb = target_colors[i]

            # Slowly move toward target color
            r = int(r + (tr - r) * 0.1)
            g = int(g + (tg - g) * 0.1)
            b = int(b + (tb - b) * 0.1)

            new_leds.append((r, g, b))

            # Occasionally pick a new random color
            if random.random() < 0.05:
                target_colors[i] = (
                    random.randint(0, 255),
                    random.randint(0, 255),
                    random.randint(0, 255),
                )

        leds = new_leds
        _ = (yield tuple(leds), True)  # type: ignore
