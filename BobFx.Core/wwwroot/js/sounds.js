window.bobfx = window.bobfx || {};

// Preload audio elements mapped by key
window.bobfx._audio = window.bobfx._audio || {};

// Map logical keys to file paths (subfolders under /sounds)
window.bobfx._map = window.bobfx._map || {
 "start": "/sounds/game_on/start.mp3",
 "end": "/sounds/game_over/end.mp3"
};

window.bobfx.playSound = function (keyOrPath) {
 try {
 var path;
 if (typeof keyOrPath === 'string' && keyOrPath.startsWith('/')) {
 path = keyOrPath; // full relative path provided by server
 } else {
 path = window.bobfx._map[keyOrPath] || ("/sounds/" + keyOrPath + ".mp3");
 }

 if (!window.bobfx._audio[path]) {
 var audio = new Audio(path);
 audio.preload = 'auto';
 window.bobfx._audio[path] = audio;
 }

 var player = window.bobfx._audio[path];
 // If already playing, rewind
 try { player.pause(); } catch (e) { }
 player.currentTime = 0;
 player.play().catch(function (e) {
 // Autoplay may be blocked; ignore errors
 });
 } catch (e) {
 // Ignore errors to keep server resilient
 }
};
