"""Module for BREATHING effect"""

from typing import List, Iterator
import math
from .utility import ConvertHexStringToTupleRGB


def update(
    number_of_leds: int = 0,
    color: tuple[int, int, int] = ConvertHexStringToTupleRGB("#FF0000"),
) -> Iterator[tuple[tuple[tuple[int, int, int], ...], bool]]:
    """Breathing effect (fades LEDs in and out smoothly)"""
    leds: List[tuple[int, int, int]] = []
    step: int = 0
    while True:
        leds.clear()
        brightness = (math.sin(step * 0.05) + 1) / 2  # 0 → 1 → 0
        r = int(color[0] * brightness)  # type: ignore
        g = int(color[1] * brightness)  # type: ignore
        b = int(color[2] * brightness)  # type: ignore
        for _ in range(number_of_leds):
            leds.append((r, g, b))

        new_color: tuple[int, int, int] = (yield tuple(leds), True)  # type: ignore
        if new_color:
            color = new_color  # type: ignore
        step = (step + 1) % 360
