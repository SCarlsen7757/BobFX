import { test, expect } from './fixtures';

test('background scans leave Scan available and last seen updates as elapsed time', async ({ page }) => {
  const seenAt = '2026-11-09T11:59:48Z';
  await page.clock.setFixedTime(new Date('2026-11-09T12:00:00Z'));
  await page.route('**/api/ui/state', async route => {
    const response = await route.fetch();
    const state = await response.json();
    // Hold discovery data stable while testing the clock and background-scan UI.
    state.discovery.revision += 1000;
    state.discovery.value = { scanning: true, error: null, lastScanUtc: seenAt, devices: [
      { mac: '001122334455', address: '192.168.1.20', port: 80, name: 'Clock fixture', hostName: 'wled-test',
        version: 'test', lastSeen: seenAt, isOnline: true, webUrl: 'http://192.168.1.20/' },
    ] };
    await route.fulfill({ response, json: state });
  });
  await page.goto('/wled');
  const scan = page.getByRole('button', { name: 'Scan', exact: true });
  await expect(page.locator('.device-toolbar [role="status"]')).toHaveText('Scanning…');
  await expect(scan).toBeEnabled();
  const lastSeen = page.getByRole('row').filter({ hasText: 'Clock fixture' }).locator('time');
  await expect(lastSeen).toHaveText('12 seconds ago');
  await expect(lastSeen).toHaveAttribute('datetime', seenAt);
  await expect(lastSeen).toHaveAttribute('title', await page.evaluate(value => new Date(value).toLocaleString(), seenAt));
  const response = page.waitForResponse(r => r.url().endsWith('/api/wled/scan') && r.request().method() === 'POST');
  await scan.click();
  expect((await response).status()).toBe(202);
  await expect(scan).toBeEnabled();
  await page.clock.setFixedTime(new Date('2026-11-09T12:01:04Z'));
  await expect(lastSeen).toHaveText('1 minute ago');
});

test('game reflects Locate ownership and becomes playable when Locate ends', async ({ page, request }) => {
  const original = await (await request.get('/api/installation')).json();
  let locating: ReturnType<typeof request.post> | undefined;
  try {
    expect((await request.put('/api/installation', { data: { ...original, boards: [],
      idleSync: { ...original.idleSync, enabled: false }, devices: [
        { mac: '001122334455', name: 'Locate target', address: '192.168.1.20', httpPort: 80, ledCount: 16, udpPort: 21324, selected: false },
      ] } })).ok()).toBeTruthy();
    await page.goto('/game/fullscreen');
    const start = page.getByRole('button', { name: 'Start Countdown', exact: true });
    await expect(start).toBeEnabled();
    locating = request.post('/api/installation/devices/001122334455/locate');
    await expect(page.getByRole('heading', { name: 'Locating board', exact: true })).toBeVisible();
    await expect(start).toBeDisabled();
    expect((await locating).ok()).toBeTruthy();
    await expect(page.getByRole('heading', { name: 'Ready to play', exact: true })).toBeVisible();
    await expect(start).toBeEnabled();
  } finally {
    if (locating) await locating;
    expect((await request.put('/api/installation', { data: original })).ok()).toBeTruthy();
  }
});

test('layout persists board order and mappings and rejects overlaps', async ({ page, request }) => {
  const original = await (await request.get('/api/installation')).json();
  const device = (mac: string, name: string, address: string) => ({ mac, name, address, httpPort: 80, ledCount: 16, udpPort: 21324, selected: true });
  const board = (id: string, deviceMac: string) => ({ id, name: id, deviceMac, rotation: 0,
    edges: [0, 4, 8, 12].map(start => ({ start, count: 4, reversed: false })) });
  try {
    expect((await request.put('/api/installation', { data: { ...original,
      devices: [device('001122334455', 'North', '192.168.1.20'), device('aabbccddeeff', 'South', '192.168.1.21')],
      boards: [board('North', '001122334455'), board('South', 'aabbccddeeff')],
    } })).ok()).toBeTruthy();
    await page.goto('/layout');
    await expect(page.getByRole('button', { name: 'Enable audio', exact: true })).toBeEnabled();
    await page.getByRole('button', { name: 'Move South left', exact: true }).click();
    await page.getByRole('button', { name: 'Save layout and settings', exact: true }).click();
    await expect(page.locator('p[role="status"]')).toHaveText('Layout saved.');
    await page.reload();
    await expect(page.locator('.board-tile').first()).toContainText('1. South');
    await page.locator('.board-tile').first().click();
    await page.getByLabel('First LED index', { exact: true }).nth(1).fill('3');
    await page.getByRole('button', { name: 'Apply board settings', exact: true }).click();
    await expect(page.locator('p[role="status"]')).toContainText('overlap');
    const saved = await (await request.get('/api/installation')).json();
    expect(saved.boards[0].edges[1].start).toBe(4);
    await page.getByLabel('First LED index', { exact: true }).nth(1).fill('4');
    await page.getByLabel('Rotation', { exact: true }).selectOption('90');
    await page.getByRole('button', { name: 'Save layout and settings', exact: true }).click();
    await expect(page.locator('p[role="status"]')).toHaveText('Layout saved.');
    await page.reload();
    await page.locator('.board-tile').first().click();
    await expect(page.getByLabel('Rotation', { exact: true })).toHaveValue('90');
  } finally { expect((await request.put('/api/installation', { data: original })).ok()).toBeTruthy(); }
});

test('optional controllers save without enabling connections and settings reject game ownership', async ({ page, request }) => {
  const original = await (await request.get('/api/installation')).json();
  try {
    await page.goto('/controllers');
    await expect(page.getByRole('button', { name: 'Enable audio', exact: true })).toBeEnabled();
    await page.getByRole('button', { name: 'Add BUSY Bar', exact: true }).click();
    await page.getByLabel('Name', { exact: true }).fill('Family remote');
    await page.getByLabel('Private IPv4 address', { exact: true }).fill('192.168.1.30');
    const password = `test-${Date.now()}-local-password`;
    await page.getByLabel('Device API password', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Save controllers', exact: true }).click();
    await expect(page.locator('p[role="status"]')).toContainText('Controllers saved');
    await page.reload();
    await page.getByRole('button', { name: /Family remote ·/ }).click();
    await expect(page.getByLabel('Enable integration', { exact: true })).not.toBeChecked();
    await expect(page.getByLabel('Show remaining game timer', { exact: true })).toHaveCount(0);
    await expect(page.getByLabel('Device API password', { exact: true })).toHaveValue('');
    await expect(page.getByText('Password saved. Leave blank to keep it, or enter a replacement.')).toBeVisible();
    let savedController = (await (await request.get('/api/installation')).json()).controllers.find((c: { name: string }) => c.name === 'Family remote');
    const passwordEndpoint = `/api/installation/controllers/${savedController.id}/password`;
    expect((await (await request.get(passwordEndpoint)).json()).hasPassword).toBe(true);
    expect(await (await request.get('/api/installation')).text()).not.toContain(password);
    await page.getByRole('button', { name: 'Save controllers', exact: true }).click();
    await expect(page.locator('p[role="status"]')).toContainText('Controllers saved');
    expect((await (await request.get(passwordEndpoint)).json()).hasPassword).toBe(true);
    await page.getByLabel('Device API password', { exact: true }).fill(`${password}-replacement`);
    await page.getByRole('button', { name: 'Save controllers', exact: true }).click();
    await expect(page.locator('p[role="status"]')).toContainText('Controllers saved');
    await expect(page.getByLabel('Device API password', { exact: true })).toHaveValue('');
    const configured = await (await request.get('/api/installation')).json();
    expect((await request.put('/api/installation', { data: { ...configured, devices: [
      { mac: '001122334455', name: 'Locate target', address: '192.168.1.20', httpPort: 80, ledCount: 16, udpPort: 21324, selected: false },
    ] } })).ok()).toBeTruthy();
    expect((await request.post('/api/countdown/start', { data: { durationSeconds: 60, deviationSeconds: 0 } })).ok()).toBeTruthy();
    expect((await request.put('/api/installation', { data: original })).status()).toBe(409);
    expect((await request.post('/api/installation/devices/001122334455/locate')).status()).toBe(409);
  } finally {
    await request.post('/api/countdown/stop');
    expect((await request.put('/api/installation', { data: original })).ok()).toBeTruthy();
  }
});
