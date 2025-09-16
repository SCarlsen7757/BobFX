"""Module for RGB effects"""

from typing import Callable, Iterator, Dict
from enum import Enum
from . import off, rainbow, solid, bob_fx


class RGBEffect(Enum):
    """Enumeration of RGB effects"""

    SOLID = "solid"
    RAINBOW = "rainbow"
    # THEATER_CHASE = "theater_chase"
    # COLOR_WIPE = "color_wipe"
    # SCANNER = "scanner"
    # FADE = "fade"
    # RANDOM_COLOR = "random_color"
    # STROBE = "strobe"
    # BREATHING = "breathing"
    BOBFX = "bob_fx"
    OFF = "off"


# Define type alias for effect functions
EffectFn = Callable[[int, str], Iterator[tuple[tuple[str], bool]]]


class RGBEffectController:
    """Class to manage RGB effects"""

    def __init__(self, number_of_leds: int) -> None:
        self.current_effect: RGBEffect = RGBEffect.RAINBOW
        self.number_of_leds: int = number_of_leds
        self.color: str = "#FF0000"  # Default color (not used in all effects)

        self.effects: Dict[RGBEffect, EffectFn] = {
            RGBEffect.SOLID: solid.update,
            RGBEffect.RAINBOW: rainbow.update,
            # RGBEffect.THEATER_CHASE: "Theater Chase",
            # RGBEffect.COLOR_WIPE: "Color Wipe",
            # RGBEffect.SCANNER: "Scanner",
            # RGBEffect.FADE: "Fade",
            # RGBEffect.RANDOM_COLOR: "Random Color",
            # RGBEffect.STROBE: "Strobe",
            # RGBEffect.BREATHING: "Breathing",
            RGBEffect.BOBFX: bob_fx.update,
            RGBEffect.OFF: off.update,
        }

        self._generator: Iterator[tuple[tuple[str], bool]] | None = None
        self.set_effect(self.current_effect)

    def set_effect(self, effect: RGBEffect) -> None:
        """Switch to a new effect"""
        self.current_effect = effect
        effect_fn = self.effects[effect]
        # start new generator
        self._generator = effect_fn(self.number_of_leds, self.color)
        next(self._generator)  # prime the generator

    def set_color(self, color: str) -> None:
        """Set the color for the current effect"""
        self.color = color

    def next_effect(self):
        """Cycle to the next effect"""
        effects = list(self.effects.keys())
        current_index = effects.index(self.current_effect)
        next_index = (current_index + 1) % len(effects)
        self.set_effect(effects[next_index])

    def previous_effect(self):
        """Cycle to the previous effect"""
        effects = list(self.effects.keys())
        current_index = effects.index(self.current_effect)
        previous_index = (current_index - 1) % len(effects)
        self.set_effect(effects[previous_index])

    def update(self) -> tuple[tuple[str], bool]:
        """Get next frame (list of colors) from current effect"""
        if self._generator is None:
            return tuple(), True  # type: ignore
        try:
            colors = self._generator.send(self.color)
            return colors
        except StopIteration:
            # If generator ends, restart it
            self.set_effect(self.current_effect)
            return next(self._generator)
