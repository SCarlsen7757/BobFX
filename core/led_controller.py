"""LED controller for WS2812B LED strips using rpi_ws281x library."""

from typing import List
import importlib.util

# Check if rpi_ws281x is available (only on Raspberry Pi)
IS_WS2812B_COMPATIBLE = importlib.util.find_spec("rpi_ws281x") is not None


class LEDController:
    """Class to control an LED strip"""

    def __init__(self, led_gpio: int = 18, led_count: int = 30):
        self.use_hardware = IS_WS2812B_COMPATIBLE

        if self.use_hardware:
            from rpi_ws281x import PixelStrip, Color

            self.strip = PixelStrip(led_count, led_gpio)
            self.strip.begin()

    async def update(self, colors: List[str]):
        """
        Update LED strip with a list of colors (hex strings, e.g. "#FF0000").
        """
        if not self.use_hardware:
            return

        # Convert hex to RGB
        for i, color in enumerate(colors):
            r = int(color[1:3], 16)
            g = int(color[3:5], 16)
            b = int(color[5:7], 16)
            self.strip.setPixelColor(i, Color(r, g, b))
        self.strip.show()
