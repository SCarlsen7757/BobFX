import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('../BobFx.Core/wwwroot/js/sounds.js', import.meta.url), 'utf8');
const audio = await import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

class FakeAudio {
    static instances = [];
    constructor(path) { this.path = path; this.plays = 0; FakeAudio.instances.push(this); }
    play() { this.plays++; return this.failure ? Promise.reject(this.failure) : Promise.resolve(); }
    pause() { this.paused = true; }
    removeAttribute(name) { if (name === 'src') this.path = ''; }
    load() { this.released = true; }
}
globalThis.Audio = FakeAudio;

function session() {
    const listeners = new Map();
    const button = {
        disabled: false,
        addEventListener: (name, callback) => listeners.set(name, callback),
        removeEventListener: name => listeners.delete(name),
    };
    const changes = [];
    const id = audio.initialize(button, { invokeMethodAsync: async (...args) => changes.push(args) });
    return { id, button, changes, player: FakeAudio.instances.at(-1), click: () => listeners.get('click')(), listeners };
}

test('enable starts playback synchronously inside the native click', async () => {
    const client = session();
    try {
        const activation = client.click();
        assert.equal(client.player.plays, 1);
        assert.equal(client.button.disabled, true);
        await activation;
        assert.equal(client.changes.at(-1)[1], true);
        assert.equal(client.player.volume, 1);
    } finally { audio.dispose(client.id); }
});

test('enabling and muting one browser session leaves the other independent', async () => {
    const first = session(), second = session();
    try {
        await first.click();
        await second.click();
        await first.click();
        await audio.play(first.id, '/sounds/game_over/game-over-yeah.mp3');
        await audio.play(second.id, '/sounds/game_over/game-over-yeah.mp3');
        assert.equal(first.player.plays, 1);
        assert.equal(second.player.plays, 2);
    } finally { audio.dispose(first.id); audio.dispose(second.id); }
});

test('autoplay failure reports disabled state and permits retry', async () => {
    const client = session();
    try {
        client.player.failure = new Error('autoplay denied');
        await client.click();
        assert.equal(client.changes.at(-1)[1], false);
        assert.equal(client.button.disabled, false);
        client.player.failure = null;
        await client.click();
        assert.equal(client.changes.at(-1)[1], true);
        await assert.rejects(audio.play(client.id, 'https://untrusted.example/sound.mp3'), /Unknown sound/);
    } finally { audio.dispose(client.id); }
});

test('disposing removes the listener and releases media', async () => {
    const client = session();
    await client.click();
    audio.dispose(client.id);
    assert.equal(client.listeners.size, 0);
    assert.equal(client.player.released, true);
    await audio.play(client.id, '/sounds/game_over/game-over-yeah.mp3');
    assert.equal(client.player.plays, 1);
});
