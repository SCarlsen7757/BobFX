"""Module for STROBE effect"""

from typing import List, Iterator
from .utility import ConvertHexStringToTupleRGB


def update(
    number_of_leds: int = 0,
    color: tuple[int, int, int] = ConvertHexStringToTupleRGB("#FFFFFF"),
) -> Iterator[tuple[tuple[tuple[int, int, int], ...], bool]]:
    """Strobe effect (flashes all LEDs on and off)"""
    leds_on = True
    leds: List[tuple[int, int, int]] = []

    while True:
        leds.clear()
        if leds_on:
            leds.extend([color for _ in range(number_of_leds)])  # type: ignore
        else:
            leds.extend([(0, 0, 0) for _ in range(number_of_leds)])

        new_color: tuple[int, int, int] = (yield tuple(leds), True)  # type: ignore
        if new_color:
            color = new_color

        leds_on = not leds_on
