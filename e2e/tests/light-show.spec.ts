import type { APIRequestContext } from '@playwright/test';
import { test, expect } from './fixtures';

const device = { mac: '001122334455', name: 'North', address: '192.168.1.20', httpPort: 80, ledCount: 16, udpPort: 21324, selected: true };
const board = { id: 'north', name: 'Board 1', deviceMac: '001122334455', rotation: 0,
  edges: [0, 4, 8, 12].map(start => ({ start, count: 4, reversed: false })) };
const defaults = { theme: 'neonSunset', brightness: 100, speed: 100, intensity: 'calm',
  custom: { primary: '#ff4fad', secondary: '#ffad66', tertiary: '#8c50ff', accent: '#96eddf' } };

async function seed(request: APIRequestContext) {
  const original = await (await request.get('/api/installation')).json();
  expect((await request.put('/api/installation', { data: { ...original, devices: [device], boards: [board],
    idleSync: { ...original.idleSync, enabled: false }, lightShow: defaults } })).ok()).toBeTruthy();
  return original;
}
async function restore(request: APIRequestContext, original: unknown) {
  await request.delete('/api/installation/light-show/test');
  await request.post('/api/countdown/stop');
  // A stopped game may still be playing its ending; settings can only be restored once the lights are free.
  await expect.poll(async () => (await (await request.get('/api/countdown/status')).json()).phase, { timeout: 15_000 }).toBe('Idle');
  expect((await request.put('/api/installation', { data: original })).ok()).toBeTruthy();
}

test('light show settings save their own section and survive a reload', async ({ page, request, expectedApiErrors }) => {
  const original = await seed(request);
  try {
    await page.goto('/lights');
    await expect(page.getByRole('heading', { name: 'Light show', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Enable audio', exact: true })).toBeEnabled();
    const message = page.locator('.light-show-page > p[role="status"]');
    await expect(page.getByRole('radio', { name: 'Neon sunset', exact: true })).toBeChecked();
    await expect(page.getByLabel('Main color', { exact: true })).toBeHidden();

    await page.getByRole('radio', { name: 'Aurora', exact: true }).check();
    await page.getByLabel('Brightness', { exact: true }).fill('60');
    await page.getByLabel('Intensity', { exact: true }).selectOption('lively');
    await page.getByLabel('Drift speed', { exact: true }).fill('150');
    await page.getByLabel('Comet seconds', { exact: true }).fill('4');
    await page.getByRole('button', { name: 'Save light show', exact: true }).click();
    await expect(message).toHaveText('Light show saved.');
    let saved = await (await request.get('/api/installation')).json();
    expect(saved.lightShow).toMatchObject({ theme: 'aurora', brightness: 60, speed: 150, intensity: 'lively' });
    expect(saved.animation.dotSeconds).toBe(4);
    expect(saved.boards).toHaveLength(1);

    // The route is served by the backend, and the saved values come back.
    await page.reload();
    await expect(page.getByRole('radio', { name: 'Aurora', exact: true })).toBeChecked();
    await expect(page.getByLabel('Brightness', { exact: true })).toHaveValue('60');
    await expect(page.getByLabel('Drift speed', { exact: true })).toHaveValue('150');
    await expect(page.getByLabel('Intensity', { exact: true })).toHaveValue('lively');

    // Custom colors appear only for the custom theme.
    await page.getByRole('radio', { name: 'Custom', exact: true }).check();
    await page.getByLabel('Main color', { exact: true }).fill('#00ff00');
    await page.getByRole('button', { name: 'Save light show', exact: true }).click();
    await expect(message).toHaveText('Light show saved.');
    saved = await (await request.get('/api/installation')).json();
    expect(saved.lightShow.theme).toBe('custom');
    expect(saved.lightShow.custom.primary).toBe('#00ff00');

    // An invalid draft is rejected in the browser and the saved settings stay as they were.
    await page.getByLabel('Flare seconds', { exact: true }).fill('0');
    await page.getByRole('button', { name: 'Save light show', exact: true }).click();
    await expect(message).toHaveText('Flare seconds must be 1–10.');
    expect((await (await request.get('/api/installation')).json()).animation.flashSeconds).toBe(saved.animation.flashSeconds);
    await page.getByRole('button', { name: 'Reload saved settings', exact: true }).click();
    await expect(page.getByLabel('Flare seconds', { exact: true })).toHaveValue(String(saved.animation.flashSeconds));
    expect(expectedApiErrors.size).toBe(0);
  } finally {
    await restore(request, original);
  }
});

test('light show tests play an unsaved draft and hold the lights until they end', async ({ page, request }) => {
  const original = await seed(request);
  try {
    await page.goto('/lights');
    await expect(page.getByRole('button', { name: 'Enable audio', exact: true })).toBeEnabled();
    const status = page.getByTestId('light-test-status');
    const playing = page.getByRole('button', { name: 'Playing', exact: true });
    const stop = page.getByRole('button', { name: 'Stop test', exact: true });
    await expect(playing).toBeEnabled();
    await expect(stop).toBeDisabled();

    await page.getByRole('radio', { name: 'Ember', exact: true }).check();
    await playing.click();
    await expect(status).toContainText('Playing “Playing” on the boards');
    let state = (await (await request.get('/api/ui/state')).json()).installation.value;
    expect(state.lightShowTest.segment).toBe('playing');
    expect(state.settings.lightShow.theme).toBe('neonSunset');
    expect((await request.post('/api/countdown/start')).status()).toBe(409);

    // Another test replaces the running one; Stop hands the lights back.
    await page.getByRole('button', { name: 'Game over', exact: true }).click();
    await expect(status).toContainText('Playing “Game over” on the boards');
    await stop.click();
    await expect(status).toBeHidden();
    state = (await (await request.get('/api/ui/state')).json()).installation.value;
    expect(state.lightShowTest).toBeNull();

    // A test ends by itself.
    await page.getByRole('button', { name: 'Countdown + GO', exact: true }).click();
    await expect(status).toContainText('Playing “Countdown + GO” on the boards');
    await expect(status).toBeHidden({ timeout: 12_000 });
    await expect(stop).toBeDisabled();

    // A game owns the lights: no tests and no saving until it is over.
    expect((await request.post('/api/countdown/start')).ok()).toBeTruthy();
    await expect(playing).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Save light show', exact: true })).toBeDisabled();
    await expect(status).toContainText('The lights are in use');
  } finally {
    await restore(request, original);
  }
});

test('the game page shows a light show test and cannot start a game during it', async ({ page, request }) => {
  const original = await seed(request);
  try {
    await page.goto('/game/fullscreen');
    const start = page.getByRole('button', { name: 'Start Countdown', exact: true });
    await expect(start).toBeEnabled();
    expect((await request.post('/api/installation/light-show/test', { data: { segment: 'playing', lightShow: defaults, animation: original.animation } })).ok()).toBeTruthy();
    await expect(page.getByRole('heading', { name: 'Testing light show', exact: true })).toBeVisible();
    await expect(start).toBeDisabled();
    expect((await request.delete('/api/installation/light-show/test')).ok()).toBeTruthy();
    await expect(page.getByRole('heading', { name: 'Ready to play', exact: true })).toBeVisible();
    await expect(start).toBeEnabled();
  } finally {
    await restore(request, original);
  }
});
