"""Module for SOLID effect"""

from typing import List, Iterator
from .utility import ConvertHexStringToTupleRGB


def update(
    number_of_leds: int = 0,
    color: tuple[int, int, int] = ConvertHexStringToTupleRGB("#FF0000"),
) -> Iterator[tuple[tuple[tuple[int, int, int], ...], bool]]:
    """Solid effect (all LEDs the same color)"""
    leds: List[tuple[int, int, int]] = [color] * number_of_leds
    step: int = 0
    while True:
        color = (yield tuple(leds), step == 0)  # type: ignore
        step += 1
        if color:
            step = 0
            leds = [color] * number_of_leds
