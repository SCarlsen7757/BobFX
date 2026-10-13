import { test as base, expect } from '@playwright/test';

export const test = base.extend({
  page: async ({ page }, use, testInfo) => {
    const errors: string[] = [];
    const consoleMessages: string[] = [];
    page.on('pageerror', error => errors.push(error.stack ?? error.message));
    page.on('console', message => {
      consoleMessages.push(`[${message.type()}] ${message.text()}`);
      // Blazor circuit exceptions are console errors, often not pageerror events.
      if (message.type() === 'error') errors.push(message.text());
    });
    await use(page);
    await testInfo.attach('browser-console', {
      body: consoleMessages.join('\n'),
      contentType: 'text/plain',
    });
    expect.soft(errors, 'Browser JavaScript errors or Blazor circuit failures').toEqual([]);
    await expect.soft(page.locator('#blazor-error-ui'), 'Connection interrupted banner').toBeHidden();
  },
});

export { expect };
