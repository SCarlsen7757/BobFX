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
    await page.getByRole('button', { name: 'Save layout', exact: true }).click();
    await expect(page.locator('p[role="status"]')).toHaveText('Layout saved.');
    await page.reload();
    await expect(page.locator('.board-tile').first()).toContainText('1. South');
    await page.locator('.board-tile').first().click();
    await page.getByText('Advanced settings', { exact: true }).click();
    await page.getByLabel('First LED index', { exact: true }).nth(1).fill('3');
    await page.getByRole('button', { name: 'Save layout', exact: true }).click();
    await expect(page.locator('p[role="status"]')).toContainText('overlap');
    const saved = await (await request.get('/api/installation')).json();
    expect(saved.boards[0].edges[1].start).toBe(4);
    await page.getByLabel('First LED index', { exact: true }).nth(1).fill('4');
    await page.getByLabel('Rotation', { exact: true }).selectOption('90');
    await page.getByRole('button', { name: 'Save layout', exact: true }).click();
    await expect(page.locator('p[role="status"]')).toHaveText('Layout saved.');
    await page.reload();
    await page.locator('.board-tile').first().click();
    await page.getByText('Advanced settings', { exact: true }).click();
    await expect(page.getByLabel('Rotation', { exact: true })).toHaveValue('90');
  } finally { expect((await request.put('/api/installation', { data: original })).ok()).toBeTruthy(); }
});

test('boards can be added before controllers and calibrated without saving until finish', async ({ page, request }, testInfo) => {
  const original = await (await request.get('/api/installation')).json();
  let sessionId: string | undefined;
  try {
    expect((await request.put('/api/installation', { data: { ...original, boards: [],
      idleSync: { ...original.idleSync, enabled: false }, devices: [
        { mac: '001122334455', name: 'North', address: '192.168.1.20', httpPort: 80, ledCount: 16, udpPort: 21324, selected: true },
        { mac: 'aabbccddeeff', name: 'South', address: '192.168.1.21', httpPort: 80, ledCount: 16, udpPort: 21324, selected: true },
      ] } })).ok()).toBeTruthy();
    await page.goto('/layout');
    await expect(page.getByRole('button', { name: 'Add board', exact: true })).toBeEnabled();
    for (let i = 0; i < 3; i++) await page.getByRole('button', { name: 'Add board', exact: true }).click();
    await expect(page.locator('.board-tile')).toHaveCount(3);
    await expect(page.getByRole('button', { name: 'Calibrate boards', exact: true })).toBeDisabled();
    await page.getByRole('button', { name: 'Save layout', exact: true }).click();
    await expect(page.locator('p[role="status"]')).toHaveText('Layout saved.');
    await page.reload();
    const controllers = page.getByLabel('WLED controller', { exact: true });
    await expect(controllers).toHaveCount(3);
    await controllers.nth(0).selectOption('001122334455');
    await expect(controllers.nth(2).locator('option[value="001122334455"]')).toHaveCount(0);
    await controllers.nth(2).selectOption('aabbccddeeff');
    await page.locator('.board-tile').first().click();
    await page.screenshot({ path: testInfo.outputPath('board-row.png'), fullPage: true });
    const rowShape = await page.locator('.board-item').evaluateAll(items => items.map(item => Math.round(item.getBoundingClientRect().top)));
    expect(new Set(rowShape).size).toBe(1);
    const start = page.getByRole('button', { name: 'Calibrate boards', exact: true });
    await start.click();
    const panel = page.getByRole('region', { name: 'Board calibration', exact: true });
    await expect(panel).toBeVisible();
    const state = await (await request.get('/api/ui/state')).json();
    sessionId = state.installation.value.calibration.sessionId;
    await panel.getByRole('button', { name: 'Rotate right', exact: true }).click();
    await panel.getByRole('button', { name: 'Move board right', exact: true }).click();
    await expect(panel.getByRole('heading')).toHaveText('Check 2. Board 1');
    await panel.getByRole('button', { name: 'Next board', exact: true }).click();
    await expect(panel.getByRole('heading')).toHaveText('Check 3. Board 3');
    await expect(panel.getByText('2 of 2 assigned boards')).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath('board-calibration.png'), fullPage: true });
    const persisted = await (await request.get('/api/installation')).json();
    expect(persisted.boards.every((b: { deviceMac: string | null }) => b.deviceMac === null)).toBeTruthy();
    expect((await request.post('/api/countdown/start', { data: {} })).status()).toBe(409);
    await panel.getByRole('button', { name: 'Cancel calibration', exact: true }).click();
    sessionId = undefined;
    await expect(panel).toBeHidden();
    await expect(page.locator('.board-tile').first()).toContainText('1. Board 1');
    await expect(page.locator('.board-tile').first()).toContainText('0°');
    await page.locator('.board-tile').first().click();
    await start.click();
    await expect(panel).toBeVisible();
    sessionId = (await (await request.get('/api/ui/state')).json()).installation.value.calibration.sessionId;
    await panel.getByRole('button', { name: 'Rotate right', exact: true }).click();
    await panel.getByRole('button', { name: 'Finish and save', exact: true }).click();
    sessionId = undefined;
    await expect(panel).toBeHidden();
    await expect(page.locator('p[role="status"]')).toHaveText('Layout saved.');
    const saved = await (await request.get('/api/installation')).json();
    expect(saved.boards.map((b: { deviceMac: string | null }) => b.deviceMac)).toEqual(['001122334455', null, 'aabbccddeeff']);
    expect(saved.boards[0].rotation).toBe(90);
    expect(saved.boards[0].edges).toEqual([
      { start: 0, count: 4, reversed: false }, { start: 4, count: 4, reversed: false },
      { start: 12, count: 4, reversed: true }, { start: 8, count: 4, reversed: true },
    ]);
    await page.reload();
    await expect(page.locator('.board-tile').first()).toContainText('90°');
    await start.click();
    await expect(panel).toBeVisible();
    sessionId = (await (await request.get('/api/ui/state')).json()).installation.value.calibration.sessionId;
    await page.getByRole('link', { name: 'Manage WLED devices', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'WLED devices', exact: true })).toBeVisible();
    expect((await (await request.get('/api/ui/state')).json()).installation.value.calibration).toBeNull();
    sessionId = undefined;
  } finally {
    if (sessionId) await request.delete(`/api/installation/calibration/${sessionId}`);
    expect((await request.put('/api/installation', { data: original })).ok()).toBeTruthy();
  }
});

test('calibration expires while disconnected and preserves the unsaved draft after reconnect', async ({ page, context, request }) => {
  test.setTimeout(45_000);
  const original = await (await request.get('/api/installation')).json();
  let sessionId: string | undefined;
  try {
    expect((await request.put('/api/installation', { data: { ...original,
      idleSync: { ...original.idleSync, enabled: false },
      devices: [{ mac: '001122334455', name: 'North', address: '192.168.1.20', httpPort: 80, ledCount: 16, udpPort: 21324, selected: true }],
      boards: [{ id: 'north', name: 'Board 1', deviceMac: '001122334455', rotation: 0,
        edges: [{ start: 0, count: 4 }, { start: 4, count: 4 }, { start: 12, count: 4, reversed: true }, { start: 8, count: 4, reversed: true }] }],
    } })).ok()).toBeTruthy();
    await page.goto('/layout');
    await page.getByRole('button', { name: 'Calibrate boards', exact: true }).click();
    const panel = page.getByRole('region', { name: 'Board calibration', exact: true });
    await expect(panel).toBeVisible();
    sessionId = (await (await request.get('/api/ui/state')).json()).installation.value.calibration.sessionId;
    await panel.getByRole('button', { name: 'Rotate right', exact: true }).click();
    await context.setOffline(true);
    await expect(page.getByTestId('connection-status')).toBeVisible();
    await expect.poll(async () => (await (await request.get('/api/ui/state')).json()).installation.value.calibration,
      { timeout: 20_000, intervals: [500, 1000] }).toBeNull();
    sessionId = undefined;
    await context.setOffline(false);
    await expect(page.getByTestId('connection-status')).toBeHidden();
    await expect(panel).toBeHidden();
    await expect(page.locator('.board-tile').first()).toContainText('90°');
    expect((await (await request.get('/api/installation')).json()).boards[0].rotation).toBe(0);
    await page.getByRole('button', { name: 'Save layout', exact: true }).click();
    await expect(page.locator('p[role="status"]')).toHaveText('Layout saved.');
    expect((await (await request.get('/api/installation')).json()).boards[0].rotation).toBe(90);
  } finally {
    await context.setOffline(false);
    if (sessionId) await request.delete(`/api/installation/calibration/${sessionId}`);
    expect((await request.put('/api/installation', { data: original })).ok()).toBeTruthy();
  }
});

test('a failed finish save releases calibration and keeps the adjusted draft for retry', async ({ page, request, expectedApiErrors }) => {
  const original = await (await request.get('/api/installation')).json();
  let sessionId: string | undefined;
  try {
    expect((await request.put('/api/installation', { data: { ...original,
      idleSync: { ...original.idleSync, enabled: false },
      devices: [{ mac: '001122334455', name: 'North', address: '192.168.1.20', httpPort: 80, ledCount: 16, udpPort: 21324, selected: true }],
      boards: [{ id: 'north', name: 'Board 1', deviceMac: '001122334455', rotation: 0,
        edges: [{ start: 0, count: 4 }, { start: 4, count: 4 }, { start: 12, count: 4, reversed: true }, { start: 8, count: 4, reversed: true }] }],
    } })).ok()).toBeTruthy();
    await page.goto('/layout');
    await page.getByText('Advanced settings', { exact: true }).click();
    await page.getByLabel('Frames per second', { exact: true }).fill('9');
    await page.getByRole('button', { name: 'Calibrate boards', exact: true }).click();
    const panel = page.getByRole('region', { name: 'Board calibration', exact: true });
    await expect(panel).toBeVisible();
    sessionId = (await (await request.get('/api/ui/state')).json()).installation.value.calibration.sessionId;
    await panel.getByRole('button', { name: 'Rotate right', exact: true }).click();
    expectedApiErrors.add('400 /api/installation/layout');
    await panel.getByRole('button', { name: 'Finish and save', exact: true }).click();
    await expect(page.locator('p[role="status"]')).toContainText('outside supported limits');
    sessionId = undefined;
    expect((await (await request.get('/api/ui/state')).json()).installation.value.calibration).toBeNull();
    expect((await (await request.get('/api/installation')).json()).boards[0].rotation).toBe(0);
    await expect(page.locator('.board-tile').first()).toContainText('90°');
    await page.getByLabel('Frames per second', { exact: true }).fill('30');
    await page.getByRole('button', { name: 'Save layout', exact: true }).click();
    await expect(page.locator('p[role="status"]')).toHaveText('Layout saved.');
    expect((await (await request.get('/api/installation')).json()).boards[0].rotation).toBe(90);
  } finally {
    if (sessionId) await request.delete(`/api/installation/calibration/${sessionId}`);
    expect((await request.put('/api/installation', { data: original })).ok()).toBeTruthy();
  }
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
