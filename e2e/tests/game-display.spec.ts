import type { Page } from '@playwright/test';
import { test, expect } from './fixtures';

test.beforeEach(async ({ request }) => {
  await request.post('/api/countdown/stop');
  await expect.poll(async () => (await (await request.get('/api/countdown/status')).json()).phase,
    { timeout: 8000 }).toBe('Idle');
});

test.afterEach(async ({ request }) => {
  await request.post('/api/countdown/stop');
  await expect.poll(async () => (await (await request.get('/api/countdown/status')).json()).phase,
    { timeout: 8000 }).toBe('Idle');
});

async function ready(page: Page, path = '/') {
  await page.goto(path);
  await expect(page.getByRole('button', { name: 'Enable audio', exact: true })).toBeEnabled();
}

async function expectFitsViewport(page: Page) {
  const dimensions = await page.evaluate(() => ({
    width: innerWidth, height: innerHeight,
    contentWidth: document.documentElement.scrollWidth,
    contentHeight: document.documentElement.scrollHeight,
  }));
  expect(dimensions.contentWidth).toBeLessThanOrEqual(dimensions.width);
  expect(dimensions.contentHeight).toBeLessThanOrEqual(dimensions.height + 1);
  const start = await page.getByRole('button', { name: 'Start Countdown', exact: true }).boundingBox();
  expect(start!.height).toBeGreaterThanOrEqual(64);
  expect(start!.y + start!.height).toBeLessThanOrEqual(dimensions.height);
}

test('game fits portrait and landscape tablets, including navigation-free mode', async ({ page }) => {
  await ready(page);
  for (const viewport of [
    { width: 768, height: 1024 }, { width: 1024, height: 768 },
    { width: 820, height: 1180 }, { width: 1180, height: 820 },
  ]) {
    await page.setViewportSize(viewport);
    await expectFitsViewport(page);
  }
  // Direct visits work without native fullscreen or a prior tap.
  await ready(page, '/game/fullscreen');
  await expect(page.getByRole('navigation')).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Exit full screen' })).toBeVisible();
  for (const viewport of [{ width: 768, height: 1024 }, { width: 1024, height: 768 }]) {
    await page.setViewportSize(viewport);
    await expectFitsViewport(page);
  }
});

test('entering and leaving full screen preserves the running game and audio opt-in', async ({ page, request }) => {
  await ready(page);
  const headerAudio = page.locator('.app-header').getByRole('button', { name: 'Enable audio', exact: true });
  await expect(headerAudio).toHaveText('');
  await expect(headerAudio.locator('svg')).toHaveCount(1);
  await expect(headerAudio).toHaveAttribute('aria-pressed', 'false');
  await headerAudio.click();
  await expect(page.getByRole('button', { name: 'Mute audio', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Start Countdown', exact: true }).click();
  // The pre-countdown is always three seconds, so the game starts running while this test works.
  await expect(page.getByRole('button', { name: 'Stop pre-countdown' })).toBeVisible();
  await expect(page.getByRole('timer', { name: 'Seconds until game starts' })).toHaveText(/^[1-3]$/);
  const initial = await (await request.get('/api/ui/state')).json();
  await page.getByRole('link', { name: 'Full screen', exact: true }).click();
  await expect(page).toHaveURL(/\/game\/fullscreen$/);
  if (await page.evaluate(() => document.fullscreenEnabled)) {
    await expect.poll(() => page.evaluate(() => !!document.fullscreenElement)).toBe(true);
  }
  await expect(page.getByRole('navigation')).toHaveCount(0);
  await expect(page.locator('.game-heading').getByRole('button', { name: 'Mute audio', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('button', { name: /^Stop (pre-countdown|Countdown)$/ })).toBeVisible();
  const current = await (await request.get('/api/ui/state')).json();
  expect(['PreCountdown', 'Running']).toContain(current.game.value.phase);
  expect(current.game.value.runId).toBe(initial.game.value.runId); // Still the same game.
  await page.getByRole('link', { name: 'Exit full screen' }).click();
  await expect(page).toHaveURL(/\/$/);
  await expect.poll(() => page.evaluate(() => !!document.fullscreenElement)).toBe(false);
  await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeVisible();
  await expect(page.locator('.app-header').getByRole('button', { name: 'Mute audio', exact: true })).toHaveAttribute('aria-pressed', 'true');
  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('button', { name: /^Stop (pre-countdown|Countdown)$/ }).click();
  await expect(page.getByRole('button', { name: 'Start Countdown', exact: true })).toBeVisible();
});

test('fullscreen route survives reload and browser back when native fullscreen is unavailable', async ({ page }) => {
  await page.addInitScript(() => {
    Object.defineProperty(Element.prototype, 'requestFullscreen', { value: undefined, configurable: true });
  });
  await ready(page);
  await page.getByRole('link', { name: 'Full screen', exact: true }).click();
  await expect(page.getByRole('navigation')).toHaveCount(0);
  await page.reload();
  await expect(page.getByRole('button', { name: 'Enable audio', exact: true })).toBeEnabled();
  await expect(page.getByRole('navigation')).toHaveCount(0);
  await page.goBack();
  await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeVisible();
});

test('running game keeps Stop available during BOB and hides the remaining time', async ({ page, request }) => {
  await ready(page, '/game/fullscreen');
  expect((await request.post('/api/countdown/start', {
    data: { durationSeconds: 60, deviationSeconds: 0 },
  })).ok()).toBeTruthy();
  await expect(page.getByRole('button', { name: 'Stop pre-countdown' })).toBeVisible();
  await expect(page.getByText('BOB!', { exact: true })).toBeVisible({ timeout: 10000 }); // After the three-second pre-countdown.
  await expect(page.getByRole('button', { name: 'Stop Countdown', exact: true })).toBeVisible();
  await expect(page.getByRole('timer')).toHaveCount(0);
  await expect(page.getByText('Game on!', { exact: true })).toBeVisible();
  await expect(page.getByRole('timer')).toHaveCount(0);
  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('button', { name: 'Stop Countdown', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Start Countdown', exact: true })).toBeVisible();
});

test('small screens keep game controls and navigation within the page width', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 780 });
  await ready(page);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(360);
  await expect(page.getByRole('link', { name: 'Full screen', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Start Countdown', exact: true })).toBeVisible();
});

test('two screens receive the same live game without polling or reloading', async ({ page, context, request }) => {
  await ready(page);
  const second = await context.newPage();
  const errors: string[] = [];
  second.on('pageerror', error => errors.push(error.message));
  try {
    await ready(second, '/game/fullscreen');
    expect((await request.post('/api/countdown/start')).ok()).toBeTruthy();
    await expect(page.getByRole('button', { name: /^Stop (pre-countdown|Countdown)$/ })).toBeVisible();
    await expect(second.getByRole('button', { name: /^Stop (pre-countdown|Countdown)$/ })).toBeVisible();
    second.once('dialog', dialog => dialog.accept());
    await second.getByRole('button', { name: /^Stop (pre-countdown|Countdown)$/ }).click();
    await expect(page.getByRole('button', { name: 'Start Countdown', exact: true })).toBeVisible();
    await expect(second.getByRole('button', { name: 'Start Countdown', exact: true })).toBeVisible();
    expect(errors).toEqual([]);
  } finally { await second.close(); }
});

test('offline controls cannot queue a command and reconnect restores current server state', async ({ page, context, request }) => {
  await ready(page);
  await expect(page.getByRole('button', { name: 'Start Countdown', exact: true })).toBeEnabled();
  await context.setOffline(true);
  await expect(page.getByTestId('connection-status')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Start Countdown', exact: true })).toBeDisabled();
  expect((await request.post('/api/countdown/start')).ok()).toBeTruthy();
  await context.setOffline(false);
  await expect(page.getByTestId('connection-status')).toBeHidden({ timeout: 10000 });
  await expect(page.getByRole('button', { name: /^Stop (pre-countdown|Countdown)$/ })).toBeEnabled();
});

test('mountains move without an animation toggle or saved opt-out', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('bobfx.background-motion', 'off'));
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await page.setViewportSize({ width: 1024, height: 768 });
  await ready(page);
  await expect(page.getByLabel('Background motion')).toHaveCount(0);
  await expect(page.locator('.sunset-background')).toHaveAttribute('data-renderer', 'webgl');
  // This patch of mountains sits above the floor grid and outside the controls.
  const clip = { x: 24, y: 300, width: 160, height: 180 };
  const before = await page.screenshot({ clip });
  await expect.poll(async () => Buffer.compare(before, await page.screenshot({ clip })), { timeout: 5000 }).not.toBe(0);
});

test('system reduced motion keeps a static background without an app toggle', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await ready(page);
  await expect(page.locator('.sunset-background')).toHaveAttribute('data-renderer', 'static');
  await expect(page.locator('.sunset-canvas')).toHaveCount(0);
  await page.reload();
  await expect(page.getByLabel('Background motion')).toHaveCount(0);
  await expect(page.locator('.sunset-canvas')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Start Countdown', exact: true })).toBeEnabled();
});

test('unavailable WebGL keeps the static sunset and usable game controls', async ({ page }) => {
  await page.addInitScript(() => {
    const original = HTMLCanvasElement.prototype.getContext;
    HTMLCanvasElement.prototype.getContext = function (this: HTMLCanvasElement, type: string, ...args: unknown[]) {
      if (type === 'webgl2') return null;
      return Reflect.apply(original, this, [type, ...args]);
    } as typeof original;
  });
  await ready(page);
  await expect(page.locator('.sunset-background')).toHaveAttribute('data-renderer', 'static');
  await expect(page.getByRole('button', { name: 'Start Countdown', exact: true })).toBeEnabled();
  await expect(page.locator('.sunset-canvas')).toHaveCount(0);
});

test('WebGL context loss falls back without interrupting game controls', async ({ page, browserName }) => {
  test.skip(browserName !== 'chromium', 'GPU context-loss exercise uses Chromium; WebKit covers the static fallback.');
  await ready(page);
  await expect(page.locator('.sunset-background')).toHaveAttribute('data-renderer', 'webgl');
  await page.locator('.sunset-canvas').evaluate(canvas => canvas.dispatchEvent(new Event('webglcontextlost', { cancelable: true })));
  await expect(page.locator('.sunset-background')).toHaveAttribute('data-renderer', 'static');
  await expect(page.getByRole('button', { name: 'Start Countdown', exact: true })).toBeEnabled();
});

test('a delayed initial snapshot cannot overwrite a newer realtime update', async ({ page, request }) => {
  let release!: () => void;
  let entered!: () => void;
  let liveUpdateReceived = false;
  const blocked = new Promise<void>(resolve => { release = resolve; });
  const captured = new Promise<void>(resolve => { entered = resolve; });
  page.on('websocket', socket => socket.on('framereceived', frame => {
    const value = frame.payload.toString();
    if (value.includes('GameStateChanged') && value.includes('PreCountdown')) liveUpdateReceived = true;
  }));
  await page.route('**/api/ui/state', async route => {
    const response = await route.fetch();
    entered(); await blocked; await route.fulfill({ response });
  });
  try {
    await page.goto('/'); await captured;
    expect((await request.post('/api/countdown/start')).ok()).toBeTruthy();
    await expect.poll(() => liveUpdateReceived).toBe(true);
    release();
    await expect(page.getByRole('button', { name: /^Stop (pre-countdown|Countdown)$/ })).toBeEnabled();
  } finally { release(); }
});

test('an initial SignalR connection failure retries without reloading', async ({ page, expectedApiErrors }) => {
  let failed = false;
  expectedApiErrors.add('503 /hubs/ui/negotiate');
  await page.route('**/hubs/ui/negotiate?*', async route => {
    if (failed) return route.continue();
    failed = true; await route.fulfill({ status: 503, body: 'Temporarily unavailable' });
  });
  await ready(page);
  await expect(page.getByTestId('connection-status')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Start Countdown', exact: true })).toBeEnabled({ timeout: 10000 });
  expect(failed).toBe(true);
});
