import { test as base, expect, type APIRequestContext } from '@playwright/test';

/** The saved installation as the API returns it. Tests spread it and replace the parts they are about, so its
 * shape is left open here. */
export type Installation = Record<string, any>;

/**
 * Stops whatever a test may have left holding the lights: a light show test, a game, a board check, a manual
 * effect. Settings can only be saved once the lights are free, and the next test expects to find them free.
 */
export async function releaseLights(request: APIRequestContext) {
  const state = async () => (await request.get('/api/ui/state')).json();
  await request.delete('/api/installation/light-show/test');
  await request.post('/api/countdown/stop');
  const check = (await state()).installation.value.calibration;
  if (check) await request.delete(`/api/installation/calibration/${check.sessionId}`);
  // A stopped game still plays its ending, and a locate runs for a few seconds.
  await expect.poll(async () => {
    const current = await state();
    return current.game.value.phase === 'Idle' && !current.installation.value.locating;
  }, { message: 'the lights to be released', timeout: 15_000 }).toBe(true);
  await request.post('/api/drgb/stop');
}

export const test = base.extend<{ expectedApiErrors: Set<string>; original: Installation }>({
  expectedApiErrors: async ({}, use) => { await use(new Set()); },
  /**
   * The installation as it was before the test. A test that asks for it may save anything: afterwards the lights
   * are released and these settings are saved back, whether the test passed or not.
   */
  original: async ({ request }, use) => {
    const original = await (await request.get('/api/installation')).json() as Installation;
    await use(original);
    await releaseLights(request);
    expect((await request.put('/api/installation', { data: original })).ok(), 'the installation to be restored after the test').toBeTruthy();
  },
  page: async ({ page, expectedApiErrors }, use, testInfo) => {
    const errors: string[] = [];
    const consoleMessages: string[] = [];
    page.on('pageerror', error => errors.push(error.stack ?? error.message));
    page.on('console', message => {
      consoleMessages.push(`[${message.type()}] ${message.text()}`);
      // Expected validation/conflict responses are checked separately below.
      if (message.type() === 'error' && !/Failed to load resource:.*server responded with a status/.test(message.text())) errors.push(message.text());
    });
    page.on('response', response => {
      if (response.status() < 400) return;
      const key = `${response.status()} ${new URL(response.url()).pathname}`;
      if (!expectedApiErrors.delete(key)) errors.push(`Unexpected HTTP response: ${key}`);
    });
    await use(page);
    await testInfo.attach('browser-console', {
      body: consoleMessages.join('\n'),
      contentType: 'text/plain',
    });
    expect.soft(errors, 'Browser JavaScript errors or unexpected HTTP failures').toEqual([]);
    await expect.soft(page.getByTestId('connection-status'), 'Realtime connection status').toBeHidden();
  },
});

export { expect };
