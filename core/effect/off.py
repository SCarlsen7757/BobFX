"""Module for OFF effect"""

from typing import List, Iterator
from .utility import ConvertHexStringToTupleRGB


def update(
    number_of_leds: int = 0,
    color: tuple[int, int, int] = ConvertHexStringToTupleRGB("#000000"),
) -> Iterator[tuple[tuple[tuple[int, int, int], ...], bool]]:
    """Off effect (all LEDs black)"""
    leds: List[tuple[int, int, int]] = [
        ConvertHexStringToTupleRGB("#000000")
    ] * number_of_leds
    step: int = 0
    while True:
        _ = (yield tuple(leds), step <= 2)  # type: ignore
        step += 1
