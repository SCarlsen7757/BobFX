"""A simple sound player using pygame to play mp3 files from a specified directory."""

import os
import pygame


class SoundPlayer:
    """A simple sound player using pygame to play mp3 files from a specified directory."""

    def __init__(self, sound_dir: str):
        pygame.mixer.init()

        # Load all mp3 files into a dictionary
        self.sounds: dict[str, pygame.mixer.Sound] = {}
        for filename in os.listdir(sound_dir):
            if filename.lower().endswith(".mp3"):
                sound_name = os.path.splitext(filename)[0]
                full_path = os.path.join(sound_dir, filename)
                self.sounds[sound_name] = pygame.mixer.Sound(full_path)

        first_key = next(iter(self.sounds))
        self.current_sound: pygame.mixer.Sound = self.sounds[first_key]

    def play_current_sound(self):
        """Play the currently selected sound."""
        if self.current_sound:
            self.current_sound.play()

    def play_sound(self, sound_name: str):
        """Play a sound by its name. Stops any currently playing sound."""
        sound = self.sounds.get(sound_name)
        if sound:
            self.current_sound.stop()
            self.current_sound = sound
            self.current_sound.play()

    def stop_sound(self):
        """Stop the currently playing sound, if any."""
        if self.current_sound:
            self.current_sound.stop()
