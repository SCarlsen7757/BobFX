import { test as base, expect } from '@playwright/test';

export const test = base.extend<{ expectedApiErrors: Set<string> }>({
  expectedApiErrors: async ({}, use) => { await use(new Set()); },
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
