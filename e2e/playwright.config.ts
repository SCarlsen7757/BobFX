import { defineConfig, devices } from '@playwright/test';

// Each browser controls the same singleton game/renderer, so run tests sequentially.
const baseURL = 'http://127.0.0.1:5187';

export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: 0,
  timeout: 30_000,
  expect: { timeout: 5_000 },
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'node run-app.mjs',
    url: `${baseURL}/health/ready`,
    reuseExistingServer: false,
    timeout: 120_000,
    stdout: 'pipe',
    stderr: 'pipe',
    env: {
      ASPNETCORE_ENVIRONMENT: 'Production',
      ASPNETCORE_URLS: baseURL,
      AllowedHosts: '127.0.0.1;localhost',
      Security__AllowedClientNetworks__0: '127.0.0.0/8',
      WLed__Udp__TargetAddress: '127.0.0.1',
      WLed__Led__LedCount: '30',
      WLed__Discovery__UpdateIntervalSeconds: '3600',
      Countdown__PreDurationSeconds: '30',
      Countdown__DurationSeconds: '60',
      Countdown__DeviationSeconds: '0',
    },
  },
});
