/** Every server address the app calls, so that an API route is named in one place. */
const installation = '/api/installation';

export const endpoints = {
  state: '/api/ui/state',
  hub: '/hubs/ui',
  countdown: { start: '/api/countdown/start', stop: '/api/countdown/stop' },
  effects: { list: '/api/drgb/effects', start: '/api/drgb/start', stop: '/api/drgb/stop' },
  discovery: {
    scan: '/api/wled/scan',
    devices: '/api/wled/devices',
    device: (mac: string) => `/api/wled/devices/${mac}`,
  },
  installation: {
    layout: `${installation}/layout`,
    countdown: `${installation}/countdown`,
    controllers: `${installation}/controllers`,
    controllerTest: `${installation}/controllers/test`,
    lightShow: `${installation}/light-show`,
    lightShowTest: `${installation}/light-show/test`,
    lightShowPreview: `${installation}/light-show/preview`,
    lightShowThemes: `${installation}/light-show/themes`,
    calibration: `${installation}/calibration`,
    calibrationSession: (id: string) => `${installation}/calibration/${id}`,
    manualDevice: `${installation}/devices/manual`,
    simulatedDevice: `${installation}/devices/simulated`,
    device: (mac: string) => `${installation}/devices/${mac}`,
    /** `select` registers a discovered device; the two locate actions light a registered or a discovered one. */
    deviceAction: (mac: string, action: 'select' | 'locate' | 'locate-discovered') => `${installation}/devices/${mac}/${action}`,
  },
} as const;
