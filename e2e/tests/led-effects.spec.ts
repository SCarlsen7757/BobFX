import type { APIRequestContext, Page } from '@playwright/test';
import { test, expect } from './fixtures';

async function openEffects(page: Page) {
  await page.goto('/drgb');
  // Prerendered HTML is visible before the Blazor event handlers connect.
  await expect(page.getByRole('button', { name: 'Enable audio', exact: true })).toBeEnabled();
  await expect(page.locator('.led-pixel')).toHaveCount(30);
}

async function expectOutput(request: APIRequestContext, effect: string, streaming: boolean) {
  await expect.poll(async () => {
    const response = await request.get('/api/drgb/status');
    expect(response.ok()).toBeTruthy();
    const status = await response.json();
    return { effect: status.currentEffect, streaming: status.streaming };
  }).toEqual({ effect, streaming });
}

test.beforeEach(async ({ request }) => {
  // Previous tests only cancel pre-countdowns, which return immediately to Idle.
  expect((await request.post('/api/countdown/stop')).ok()).toBeTruthy();
  expect((await request.post('/api/drgb/stop')).ok()).toBeTruthy();
});

test.afterEach(async ({ request }) => {
  await request.post('/api/countdown/stop');
  await expect.poll(async () => (await (await request.get('/api/countdown/status')).json()).phase,
    { timeout: 8000 }).toBe('Idle');
  await request.post('/api/drgb/stop');
});

test('navigation during a WLED scan keeps the circuit usable', async ({ page }) => {
  await page.goto('/wled');
  await expect(page.getByRole('button', { name: 'Enable audio', exact: true })).toBeEnabled();
  for (let visit = 0; visit < 3; visit++) {
    // The startup discovery worker may hold the shared scan gate; retry until this page owns a scan.
    await expect.poll(async () => {
      const scan = page.getByRole('button', { name: 'Scan', exact: true });
      if (await scan.count()) await scan.click();
      return page.getByRole('button', { name: 'Scanning…', exact: true }).count();
    }, { timeout: 10000 }).toBe(1);
    await page.getByRole('link', { name: 'Game', exact: true }).click();
    await expect(page.getByRole('button', { name: /Start Countdown/ })).toBeEnabled();
    await page.getByRole('link', { name: 'WLED devices', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Scan', exact: true })).toBeEnabled();
  }
});

test('rapid game transitions never show a previous run ending', async ({ page, request }) => {
  await page.goto('/');
  await expect(page.getByRole('button', { name: 'Enable audio', exact: true })).toBeEnabled();
  for (let run = 0; run < 3; run++) {
    expect((await request.post('/api/countdown/start', { data: { durationSeconds: 60, deviationSeconds: 0 } })).ok()).toBeTruthy();
    await expect(page.getByText('BOB!', { exact: true })).toBeVisible();
    expect((await request.post('/api/countdown/stop')).ok()).toBeTruthy();
    expect((await request.post('/api/countdown/start-with-pre', {
      data: { preDurationSeconds: 30, durationSeconds: 60, deviationSeconds: 0 },
    })).ok()).toBeTruthy();
    await expect(page.getByRole('button', { name: 'Stop pre-countdown' })).toBeVisible();
    await expect(page.getByText('Game Over!', { exact: true })).toHaveCount(0);
    await expect(page.getByText('BOB!', { exact: true })).toHaveCount(0);
    expect((await request.post('/api/countdown/stop')).ok()).toBeTruthy();
    await expect(page.getByRole('button', { name: /Start Countdown/ })).toBeVisible();
    await expect(page.getByText('Game Over!', { exact: true })).toHaveCount(0);
  }
});

test('Strobe renders both colors and can start and stop without losing its circuit', async ({ page, request }) => {
  await openEffects(page);
  await page.getByLabel('Effect', { exact: true }).selectOption('Strobe');
  await expect(page.getByLabel('Primary Color', { exact: false })).toHaveValue('#ff0000');
  await expect(page.getByLabel('Secondary Color', { exact: false })).toHaveValue('#00ff00');
  await page.getByRole('button', { name: 'Start', exact: true }).click();
  await expectOutput(request, 'Strobe', true);
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  await expectOutput(request, 'Off', false);
});

test('every registered effect can render its controls and start and stop', async ({ page, request }) => {
  const response = await request.get('/api/drgb/effects');
  expect(response.ok()).toBeTruthy();
  const effects: { type: string; info: { totalColors: number } }[] = await response.json();
  expect(effects.length).toBeGreaterThan(0);
  await openEffects(page);
  for (const effect of effects) {
    await test.step(effect.type, async () => {
      await page.getByLabel('Effect', { exact: true }).selectOption(effect.type);
      await expect(page.locator('input[type="color"]')).toHaveCount(effect.info.totalColors);
      await page.getByRole('button', { name: 'Start', exact: true }).click();
      await expectOutput(request, effect.type, true);
      await page.getByRole('button', { name: 'Stop', exact: true }).click();
      await expectOutput(request, 'Off', false);
      await expect(page.locator('#blazor-error-ui')).toBeHidden();
    });
  }
});

test('invalid effect input shows feedback and preserves the active output', async ({ page, request }) => {
  await openEffects(page);
  await page.getByLabel('Effect', { exact: true }).selectOption('Solid');
  await page.getByRole('button', { name: 'Start', exact: true }).click();
  await expectOutput(request, 'Solid', true);
  await page.getByLabel('Speed:', { exact: true }).fill('0');
  await page.getByRole('button', { name: 'Start', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Speed must be');
  await expectOutput(request, 'Solid', true);
  await page.getByLabel('Speed:', { exact: true }).fill('100');
  await page.getByRole('button', { name: 'Start', exact: true }).click();
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('manual controls respect game ownership and recover after keyboard pre-countdown Stop', async ({ page, request }) => {
  await openEffects(page);
  expect((await request.post('/api/countdown/start-with-pre')).ok()).toBeTruthy();
  await page.getByRole('button', { name: 'Start', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('game owns the lights');
  await page.getByRole('link', { name: 'Game', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Stop pre-countdown' })).toBeVisible();
  page.once('dialog', dialog => dialog.accept());
  // Exercise keyboard operation; the pre-countdown container has a perpetual pulse animation.
  await page.getByRole('button', { name: 'Stop pre-countdown' }).press('Enter');
  await expect(page.getByRole('button', { name: /Start Countdown/ })).toBeVisible();
  await page.getByRole('link', { name: 'LED effects', exact: true }).click();
  await page.getByRole('button', { name: 'Start', exact: true }).click();
  await expectOutput(request, 'Rainbow', true);
});

test('navigation and full reload keep audio initialization and LED controls usable', async ({ page, request }) => {
  await openEffects(page);
  for (let visit = 0; visit < 3; visit++) {
    await page.getByRole('link', { name: 'Game', exact: true }).click();
    await page.getByRole('link', { name: 'LED effects', exact: true }).click();
    await expect(page.locator('.led-pixel')).toHaveCount(30);
    await page.reload();
    await expect(page.getByRole('button', { name: 'Enable audio', exact: true })).toBeEnabled();
  }
  await page.getByRole('button', { name: 'Start', exact: true }).click();
  await expectOutput(request, 'Rainbow', true);
});
