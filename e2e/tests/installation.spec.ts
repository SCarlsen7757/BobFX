import { test, expect } from './fixtures';

// A 16-LED controller: eight LEDs down the left side of its board and eight down the right.
const strips = { left: { start: 0, count: 8, reversed: false }, right: { start: 8, count: 8, reversed: false } };
const swapped = { left: strips.right, right: strips.left };

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

test('layout persists board order and strip mapping and shows a mapping problem where it is, before saving', async ({ page, request }) => {
  const original = await (await request.get('/api/installation')).json();
  const device = (mac: string, name: string, address: string) => ({ mac, name, address, httpPort: 80, ledCount: 16, udpPort: 21324, selected: true });
  const board = (id: string, deviceMac: string) => ({ id, name: id, deviceMac, ...strips });
  try {
    expect((await request.put('/api/installation', { data: { ...original,
      devices: [device('001122334455', 'North', '192.168.1.20'), device('aabbccddeeff', 'South', '192.168.1.21')],
      boards: [board('North', '001122334455'), board('South', 'aabbccddeeff')],
    } })).ok()).toBeTruthy();
    await page.goto('/layout');
    await expect(page.getByRole('button', { name: 'Enable audio', exact: true })).toBeEnabled();
    const message = page.locator('.layout-page > p[role="status"]');
    const save = page.getByRole('button', { name: 'Save layout', exact: true });
    await page.locator('.board-tile').filter({ hasText: 'South' }).click();
    await page.getByRole('button', { name: 'Move board left', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Move board left', exact: true })).toBeDisabled();
    await expect(page.locator('.unsaved')).toBeVisible();
    await save.click();
    await expect(message).toHaveText('Layout saved.');
    await expect(page.locator('.unsaved')).toBeHidden();
    await page.reload();
    await expect(page.locator('.board-tile').first()).toContainText('1. South');
    await page.locator('.board-tile').first().click();
    await page.getByText('Advanced settings', { exact: true }).click();

    // Two strips sharing an LED: said under the editor and on the board, Save is off, nothing reaches the server.
    await page.getByLabel('First LED', { exact: true }).nth(1).fill('7');
    await expect(page.locator('.mapping-problem')).toHaveText('The left strip (LEDs 0–7) and the right strip (LEDs 7–14) use the same LEDs.');
    await expect(page.locator('.board-item').first().locator('.device-error')).toContainText('use the same LEDs');
    await expect(page.locator('.save-problems')).toContainText('South: The left strip (LEDs 0–7) and the right strip (LEDs 7–14) use the same LEDs.');
    await expect(save).toBeDisabled();
    // A strip longer than the controller, and an emptied field, are each said under their own field.
    await page.getByLabel('First LED', { exact: true }).nth(1).fill('8');
    await page.getByLabel('LED count', { exact: true }).nth(1).fill('51');
    await expect(page.locator('#count-right-problem')).toHaveText('The right strip uses LEDs 8–58, but South has 16 LEDs (0–15).');
    await page.getByLabel('LED count', { exact: true }).nth(1).fill('');
    await expect(page.locator('#count-right-problem')).toHaveText('The LED count must be a whole number, 1 or higher.');
    await expect(save).toBeDisabled();
    expect((await (await request.get('/api/installation')).json()).boards[0].right).toEqual(strips.right);

    // Fixing the number clears every message at once; one button swaps the strips.
    await page.getByLabel('LED count', { exact: true }).nth(1).fill('8');
    await expect(page.locator('.field-problem')).toHaveCount(0);
    await expect(page.locator('.save-problems')).toHaveCount(0);
    await page.getByRole('button', { name: 'Swap left and right', exact: true }).click();
    await expect(page.locator('.board-tile').first()).toContainText('Left 8–15');
    await save.click();
    await expect(message).toHaveText('Layout saved.');
    await page.reload();
    await expect(page.locator('.board-tile').first()).toContainText('Left 8–15');
    const saved = await (await request.get('/api/installation')).json();
    expect(saved.version).toBe(2);
    expect(saved.boards.map((b: { id: string }) => b.id)).toEqual(['South', 'North']);
    expect(saved.boards[0]).toMatchObject(swapped);
    expect(saved.boards[1]).toMatchObject(strips);
  } finally { expect((await request.put('/api/installation', { data: original })).ok()).toBeTruthy(); }
});

test('a rejected save does not leave its error on screen once the draft is changed', async ({ page, request, expectedApiErrors }) => {
  const original = await (await request.get('/api/installation')).json();
  try {
    expect((await request.put('/api/installation', { data: { ...original, idleSync: { ...original.idleSync, enabled: false },
      devices: [{ mac: '001122334455', name: 'North', address: '192.168.1.20', httpPort: 80, ledCount: 16, udpPort: 21324, selected: true }],
      boards: [{ id: 'north', name: 'Board 1', deviceMac: '001122334455', ...strips }],
    } })).ok()).toBeTruthy();
    await page.goto('/layout');
    await expect(page.getByRole('button', { name: 'Enable audio', exact: true })).toBeEnabled();
    const message = page.locator('.layout-page > p[role="status"]');
    // The controller shrinks behind the page's back, so only the server can refuse this save.
    await page.route('**/api/installation/layout', route => route.fulfill({ status: 400, contentType: 'application/problem+json',
      body: JSON.stringify({ detail: 'Board 1: the right strip uses LEDs 8–15, but North has 12 LEDs (0–11).' }) }));
    await page.getByLabel('Name', { exact: true }).fill('Table 1');
    expectedApiErrors.add('400 /api/installation/layout');
    await page.getByRole('button', { name: 'Save layout', exact: true }).click();
    await expect(message).toHaveText('Board 1: the right strip uses LEDs 8–15, but North has 12 LEDs (0–11).');
    await expect(message).toHaveClass(/error-message/);
    // Any edit removes the stale error; so does reloading the saved settings.
    await page.getByLabel('Name', { exact: true }).fill('Table one');
    await expect(message).toBeHidden();
    await page.unroute('**/api/installation/layout');
    await page.getByRole('button', { name: 'Save layout', exact: true }).click();
    await expect(message).toHaveText('Layout saved.');
    expect((await (await request.get('/api/installation')).json()).boards[0].name).toBe('Table one');
  } finally { expect((await request.put('/api/installation', { data: original })).ok()).toBeTruthy(); }
});

test('a controller that does not report 102 LEDs is mapped in halves, explained and saved', async ({ page, request }) => {
  const original = await (await request.get('/api/installation')).json();
  try {
    expect((await request.put('/api/installation', { data: { ...original, idleSync: { ...original.idleSync, enabled: false },
      devices: [{ mac: '001122334455', name: 'Odd strip', address: '192.168.1.20', httpPort: 80, ledCount: 17, udpPort: 21324, selected: true }],
      boards: [{ id: 'north', name: 'Board 1', deviceMac: null, left: null, right: null }],
    } })).ok()).toBeTruthy();
    await page.goto('/layout');
    await expect(page.getByRole('button', { name: 'Enable audio', exact: true })).toBeEnabled();
    const message = page.locator('.layout-page > p[role="status"]');
    await page.getByLabel(/^WLED controller for /).selectOption('001122334455');
    await expect(message).toHaveText('Odd strip reports 17 LEDs, not 102.');
    await expect(page.locator('.board-tile').first()).toContainText('1 LED unused');
    await page.getByRole('button', { name: 'Save layout', exact: true }).click();
    await expect(message).toHaveText('Layout saved.');
    const saved = (await (await request.get('/api/installation')).json()).boards[0];
    expect(saved.deviceMac).toBe('001122334455');
    expect(saved).toMatchObject(strips);
  } finally { expect((await request.put('/api/installation', { data: original })).ok()).toBeTruthy(); }
});

test('simulated boards are added, checked and watched live without any hardware', async ({ page, request }) => {
  const original = await (await request.get('/api/installation')).json();
  let sessionId: string | undefined;
  try {
    expect((await request.put('/api/installation', { data: { ...original, boards: [], devices: [],
      idleSync: { ...original.idleSync, enabled: false, masterMac: null, followers: [] } } })).ok()).toBeTruthy();
    await page.goto('/layout');
    const add = page.getByRole('button', { name: 'Add simulated board', exact: true });
    await expect(add).toBeEnabled();
    await add.click();
    await expect(page.locator('.board-tile')).toHaveCount(1);
    await add.click();
    await expect(page.locator('.board-tile')).toHaveCount(2);
    // A simulated board is a standard board: 51 LEDs down each side, mapped without any typing.
    await expect(page.locator('.board-tile').first()).toContainText('Left 0–50');
    await expect(page.locator('.board-tile').first()).toContainText('Right 51–101');
    await page.getByRole('button', { name: 'Save layout', exact: true }).click();
    await expect(page.locator('.layout-page > p[role="status"]')).toHaveText('Layout saved.');
    const saved = await (await request.get('/api/installation')).json();
    expect(saved.devices.map((d: { simulated: boolean; ledCount: number }) => [d.simulated, d.ledCount])).toEqual([[true, 102], [true, 102]]);
    expect(saved.boards[0]).toMatchObject({ deviceMac: saved.devices[0].mac,
      left: { start: 0, count: 51, reversed: false }, right: { start: 51, count: 51, reversed: false } });

    // Checking the boards lights the simulated board in the live view; the other board stays dark.
    await page.locator('.board-tile').first().click();
    await page.getByRole('button', { name: 'Check on the boards', exact: true }).click();
    const panel = page.getByRole('region', { name: 'Check on the boards', exact: true });
    await expect(panel).toBeVisible();
    sessionId = (await (await request.get('/api/ui/state')).json()).installation.value.calibration.sessionId;
    const states = panel.getByTestId('live-board-state');
    await expect(states).toHaveCount(2);
    await expect(states.first()).toContainText('Simulated · Receiving frames');
    const canvas = panel.getByTestId('live-boards');
    // The light is on the two long borders of the board. These are the colours on the left side and on the right
    // side, a little above the near end, in the middle of the far end, and on the board's surface in between.
    const colours = () => canvas.evaluate(element => {
      const view = element as HTMLCanvasElement, context = view.getContext('2d')!, scale = view.width / (2 * 150);
      const at = (x: number, y: number) => Array.from(context.getImageData(Math.round(x * scale), Math.round(y * scale), 1, 1).data.slice(0, 3));
      return { left: at(27, 200), right: at(123, 200), middle: at(75, 16), surface: at(75, 130) };
    });
    // Blue on the left and amber on the right, as on a correctly wired board. The ends of a board have no LEDs,
    // so its far end and its surface stay unlit, as on a real board.
    await expect.poll(async () => { const c = await colours(); return c.left[2]! > c.left[0]! && c.right[0]! > c.right[2]!; }).toBe(true);
    const unlit = await colours();
    expect(Math.max(...unlit.middle)).toBeLessThan(100);
    expect(Math.max(...unlit.surface)).toBeLessThan(60);
    // A simulated board is wired the standard way, so a wrong mapping shows up as it would on real LEDs.
    await page.getByRole('button', { name: 'Swap left and right', exact: true }).click();
    await expect.poll(async () => { const c = await colours(); return c.left[0]! > c.left[2]! && c.right[2]! > c.right[0]!; }).toBe(true);
    await panel.getByRole('button', { name: 'Cancel check', exact: true }).click();
    sessionId = undefined;
    await expect(panel).toBeHidden();
    await expect(page.locator('.board-tile').first()).toContainText('Left 0–50');

    // The Live boards page shows the saved show playing on them, then idle again once frames stop.
    await page.getByRole('link', { name: 'Live boards', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Live boards', exact: true })).toBeVisible();
    const live = page.getByTestId('live-board-state');
    await expect(live).toHaveText([/1\. Board 1 · Simulated · Idle/, /2\. Board 2 · Simulated · Idle/]);
    const tryIt = page.getByRole('region', { name: 'Try the light show', exact: true });
    await tryIt.getByRole('button', { name: 'Playing', exact: true }).click();
    await expect(page.getByTestId('boards-test-status')).toContainText('Playing “Playing”');
    await expect(live).toHaveText([/Receiving frames/, /Receiving frames/]);
    const picture = () => page.getByTestId('live-boards').evaluate(element => (element as HTMLCanvasElement).toDataURL());
    const first = await picture();
    await expect.poll(picture).not.toBe(first);
    await tryIt.getByRole('button', { name: 'Stop test', exact: true }).click();
    await expect(live).toHaveText([/Idle/, /Idle/]);
    // No device error was reported for a controller that has no network side.
    expect((await (await request.get('/api/ui/state')).json()).installation.value.outputErrors).toEqual({});
  } finally {
    if (sessionId) await request.delete(`/api/installation/calibration/${sessionId}`);
    await request.delete('/api/installation/light-show/test');
    expect((await request.put('/api/installation', { data: original })).ok()).toBeTruthy();
  }
});

test('boards can be added before controllers and checked without saving until finish', async ({ page, request }, testInfo) => {
  const original = await (await request.get('/api/installation')).json();
  let sessionId: string | undefined;
  try {
    expect((await request.put('/api/installation', { data: { ...original, boards: [],
      idleSync: { ...original.idleSync, enabled: false }, devices: [
        { mac: '001122334455', name: 'North', address: '192.168.1.20', httpPort: 80, ledCount: 16, udpPort: 21324, selected: true },
        { mac: 'aabbccddeeff', name: 'South', address: '192.168.1.21', httpPort: 80, ledCount: 16, udpPort: 21324, selected: true },
      ] } })).ok()).toBeTruthy();
    await page.goto('/layout');
    const message = page.locator('.layout-page > p[role="status"]');
    await expect(page.getByRole('button', { name: 'Add board', exact: true })).toBeEnabled();
    for (let i = 0; i < 3; i++) await page.getByRole('button', { name: 'Add board', exact: true }).click();
    await expect(page.locator('.board-tile')).toHaveCount(3);
    const start = page.getByRole('button', { name: 'Check on the boards', exact: true });
    await expect(start).toBeDisabled();
    await page.getByRole('button', { name: 'Save layout', exact: true }).click();
    await expect(message).toHaveText('Layout saved.');
    await page.reload();
    const controllers = page.getByLabel(/^WLED controller for /);
    await expect(controllers).toHaveCount(3);
    await controllers.nth(0).selectOption('001122334455');
    await expect(controllers.nth(2).locator('option[value="001122334455"]')).toHaveCount(0);
    await controllers.nth(2).selectOption('aabbccddeeff');
    await page.locator('.board-tile').first().click();
    await page.screenshot({ path: testInfo.outputPath('board-row.png'), fullPage: true });
    const rowShape = await page.locator('.board-item').evaluateAll(items => items.map(item => Math.round(item.getBoundingClientRect().top)));
    expect(new Set(rowShape).size).toBe(1);
    await start.click();
    const panel = page.getByRole('region', { name: 'Check on the boards', exact: true });
    await expect(panel).toBeVisible();
    const state = await (await request.get('/api/ui/state')).json();
    sessionId = state.installation.value.calibration.sessionId;
    await page.getByRole('button', { name: 'Swap left and right', exact: true }).click();
    await page.getByRole('button', { name: 'Move board right', exact: true }).click();
    await expect(panel.getByRole('heading')).toHaveText('Check 2. Board 1');
    await panel.getByRole('button', { name: 'Next board', exact: true }).click();
    await expect(panel.getByRole('heading')).toHaveText('Check 3. Board 3');
    await expect(panel.getByText('2 / 2', { exact: true })).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath('board-check.png'), fullPage: true });
    const persisted = await (await request.get('/api/installation')).json();
    expect(persisted.boards.every((b: { deviceMac: string | null }) => b.deviceMac === null)).toBeTruthy();
    expect((await request.post('/api/countdown/start', { data: {} })).status()).toBe(409);
    await panel.getByRole('button', { name: 'Cancel check', exact: true }).click();
    sessionId = undefined;
    await expect(panel).toBeHidden();
    await expect(page.locator('.board-tile').first()).toContainText('1. Board 1');
    await expect(page.locator('.board-tile').first()).toContainText('Left 0–7');
    await page.locator('.board-tile').first().click();
    await start.click();
    await expect(panel).toBeVisible();
    sessionId = (await (await request.get('/api/ui/state')).json()).installation.value.calibration.sessionId;
    await page.getByRole('button', { name: 'Swap left and right', exact: true }).click();
    await page.getByRole('button', { name: 'Reverse left strip', exact: true }).click();
    await panel.getByRole('button', { name: 'Finish and save', exact: true }).click();
    sessionId = undefined;
    await expect(panel).toBeHidden();
    await expect(message).toHaveText('Layout saved.');
    const saved = await (await request.get('/api/installation')).json();
    expect(saved.boards.map((b: { deviceMac: string | null }) => b.deviceMac)).toEqual(['001122334455', null, 'aabbccddeeff']);
    expect(saved.boards[0]).toMatchObject({ left: { start: 8, count: 8, reversed: true }, right: strips.left });
    expect(saved.boards[2]).toMatchObject(strips);
    await page.reload();
    await expect(page.locator('.board-tile').first()).toContainText('Left 8–15');
    await start.click();
    await expect(panel).toBeVisible();
    sessionId = (await (await request.get('/api/ui/state')).json()).installation.value.calibration.sessionId;
    await page.getByRole('link', { name: 'WLED devices', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'WLED devices', exact: true })).toBeVisible();
    expect((await (await request.get('/api/ui/state')).json()).installation.value.calibration).toBeNull();
    sessionId = undefined;
  } finally {
    if (sessionId) await request.delete(`/api/installation/calibration/${sessionId}`);
    expect((await request.put('/api/installation', { data: original })).ok()).toBeTruthy();
  }
});

test('a check expires while disconnected and preserves the unsaved draft after reconnect', async ({ page, context, request }) => {
  test.setTimeout(45_000);
  const original = await (await request.get('/api/installation')).json();
  let sessionId: string | undefined;
  try {
    expect((await request.put('/api/installation', { data: { ...original,
      idleSync: { ...original.idleSync, enabled: false },
      devices: [{ mac: '001122334455', name: 'North', address: '192.168.1.20', httpPort: 80, ledCount: 16, udpPort: 21324, selected: true }],
      boards: [{ id: 'north', name: 'Board 1', deviceMac: '001122334455', ...strips }],
    } })).ok()).toBeTruthy();
    await page.goto('/layout');
    await page.getByRole('button', { name: 'Check on the boards', exact: true }).click();
    const panel = page.getByRole('region', { name: 'Check on the boards', exact: true });
    await expect(panel).toBeVisible();
    sessionId = (await (await request.get('/api/ui/state')).json()).installation.value.calibration.sessionId;
    await page.getByRole('button', { name: 'Swap left and right', exact: true }).click();
    await context.setOffline(true);
    await expect(page.getByTestId('connection-status')).toBeVisible();
    await expect.poll(async () => (await (await request.get('/api/ui/state')).json()).installation.value.calibration,
      { timeout: 20_000, intervals: [500, 1000] }).toBeNull();
    sessionId = undefined;
    await context.setOffline(false);
    await expect(page.getByTestId('connection-status')).toBeHidden();
    await expect(panel).toBeHidden();
    await expect(page.locator('.board-tile').first()).toContainText('Left 8–15');
    expect((await (await request.get('/api/installation')).json()).boards[0]).toMatchObject(strips);
    await page.getByRole('button', { name: 'Save layout', exact: true }).click();
    await expect(page.locator('.layout-page > p[role="status"]')).toHaveText('Layout saved.');
    expect((await (await request.get('/api/installation')).json()).boards[0]).toMatchObject(swapped);
  } finally {
    await context.setOffline(false);
    if (sessionId) await request.delete(`/api/installation/calibration/${sessionId}`);
    expect((await request.put('/api/installation', { data: original })).ok()).toBeTruthy();
  }
});

test('a finish that cannot save releases the boards and keeps the adjusted draft for retry', async ({ page, request }) => {
  const original = await (await request.get('/api/installation')).json();
  let sessionId: string | undefined;
  try {
    expect((await request.put('/api/installation', { data: { ...original,
      idleSync: { ...original.idleSync, enabled: false, masterMac: null },
      devices: [{ mac: '001122334455', name: 'North', address: '192.168.1.20', httpPort: 80, ledCount: 16, udpPort: 21324, selected: true }],
      boards: [{ id: 'north', name: 'Board 1', deviceMac: '001122334455', ...strips }],
    } })).ok()).toBeTruthy();
    await page.goto('/layout');
    const message = page.locator('.layout-page > p[role="status"]');
    await page.getByText('Advanced settings', { exact: true }).click();
    // Idle sync without a master cannot be saved. It does not stop the boards from being checked.
    await page.getByLabel('Enable WLED idle sync', { exact: true }).check();
    await expect(page.locator('.save-problems')).toContainText('Idle sync: choose a master, or turn idle sync off.');
    await expect(page.getByRole('button', { name: 'Save layout', exact: true })).toBeDisabled();
    await page.getByRole('button', { name: 'Check on the boards', exact: true }).click();
    const panel = page.getByRole('region', { name: 'Check on the boards', exact: true });
    await expect(panel).toBeVisible();
    sessionId = (await (await request.get('/api/ui/state')).json()).installation.value.calibration.sessionId;
    await page.getByRole('button', { name: 'Swap left and right', exact: true }).click();
    await panel.getByRole('button', { name: 'Finish and save', exact: true }).click();
    await expect(message).toHaveText('Idle sync: choose a master, or turn idle sync off.');
    sessionId = undefined;
    expect((await (await request.get('/api/ui/state')).json()).installation.value.calibration).toBeNull();
    expect((await (await request.get('/api/installation')).json()).boards[0]).toMatchObject(strips);
    await expect(page.locator('.board-tile').first()).toContainText('Left 8–15');
    await page.getByLabel('Enable WLED idle sync', { exact: true }).uncheck();
    await expect(message).toBeHidden();
    await page.getByRole('button', { name: 'Save layout', exact: true }).click();
    await expect(message).toHaveText('Layout saved.');
    expect((await (await request.get('/api/installation')).json()).boards[0]).toMatchObject(swapped);
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
    await expect(page.getByLabel('Device API password', { exact: true })).toHaveAttribute('placeholder', 'Saved · blank keeps it');
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
