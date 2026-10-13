const sessions = new Map();
let nextId = 0;
const soundPath = '/sounds/game_over/game-over-yeah.mp3';
export function initialize(button, callback) {
    const player = new Audio(soundPath);
    player.preload = 'auto';
    const session = { button, callback, player, enabled: false, disposed: false };
    const id = ++nextId;
    session.click = async () => {
        if (session.disposed) return;
        button.disabled = true;
        let status;
        try {
            if (session.enabled) {
                player.pause();
                session.enabled = false;
                status = 'Audio muted.';
            } else {
                // Begin playback immediately inside the native user gesture.
                player.volume = 0;
                player.currentTime = 0;
                const playback = player.play();
                await playback;
                player.pause();
                player.currentTime = 0;
                player.volume = 1;
                session.enabled = true;
                status = 'Audio enabled in this browser.';
            }
        } catch {
            player.pause();
            player.volume = 1;
            session.enabled = false;
            status = 'Audio could not be enabled. Check media availability and try again.';
        } finally {
            if (!session.disposed) {
                button.disabled = false;
                try { await callback.invokeMethodAsync('AudioStatusChanged', session.enabled, status); } catch { /* Circuit has disconnected. */ }
            }
        }
    };
    button.addEventListener('click', session.click);
    sessions.set(id, session);
    return id;
}
export async function play(id, path) {
    const session = sessions.get(id);
    if (!session || !session.enabled || session.disposed) return;
    if (path !== soundPath) throw new Error('Unknown sound');
    try {
        session.player.currentTime = 0;
        await session.player.play();
    } catch (error) {
        session.enabled = false;
        throw error;
    }
}
export function dispose(id) {
    const session = sessions.get(id);
    if (!session) return;
    session.disposed = true;
    session.enabled = false;
    session.button.removeEventListener('click', session.click);
    session.player.pause();
    session.player.removeAttribute('src');
    session.player.load();
    sessions.delete(id);
}
