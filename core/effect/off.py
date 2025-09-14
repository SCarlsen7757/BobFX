"""Module for OFF effect"""

from typing import List, Iterator


def update(
    number_of_leds: int = 0, color: str = "#FF0000"
) -> Iterator[tuple[tuple[str], bool]]:
    """Off effect (all LEDs black)"""
    leds: List[str] = ["#000000"] * number_of_leds
    step: int = 0
    while True:
        color = (yield tuple(leds), step <= 2)  # type: ignore
        step += 1
