export type Phase = 'Idle' | 'PreCountdown' | 'Running' | 'Ending';
export interface Versioned<T> { serverInstanceId: string; revision: number; serverUtc: string; value: T }
export interface GameState {
  phase: Phase; runId: number; running: boolean; preCountdownRunning: boolean;
  remainingSeconds: number; preRemainingSeconds: number;
  startedUtc: string | null; endedUtc: string | null; opening: boolean; gameOver: boolean;
}
export interface EffectState { currentEffect: string; speed: number; ledCount: number; streaming: boolean; error: string | null }
export interface Device {
  mac: string; address: string; port: number; name: string; hostName: string;
  version: string; lastSeen: string; isOnline: boolean; webUrl: string;
}
export interface DiscoveryState { scanning: boolean; error: string | null; lastScanUtc: string | null; devices: Device[] }
export interface Defaults { durationSeconds: number; deviationSeconds: number }
export interface UiState {
  game: Versioned<GameState>; effect: Versioned<EffectState>;
  discovery: Versioned<DiscoveryState>; installation: Versioned<InstallationState>; defaults: Defaults;
}
/** A WLED controller. A simulated one exists only in BobFX: its frames are shown on the Live boards view. */
export interface SavedDevice { mac: string; name: string; address: string; httpPort: number; ledCount: number; udpPort: number; selected: boolean; simulated: boolean }
/** One LED strip along a side of a board. Its first LED is at the near end unless reversed. */
export interface Strip { start: number; count: number; reversed: boolean }
/** A board has a strip down its left side and one down its right, as seen from the near end; both are null until mapped. */
export interface Board { id: string; name: string; deviceMac: string | null; left: Strip | null; right: Strip | null }
export interface IdleSync { enabled: boolean; masterMac: string | null; followers: string[]; group: number }
export interface AnimationSettings { framesPerSecond: number; flashSeconds: number; dotSeconds: number; dotLapsPerSecond: number }
export interface RemoteController { id: string; name: string; address: string; port: number; enabled: boolean; remoteEnabled: boolean; secretName: string }
export interface LightShowColors { primary: string; secondary: string; tertiary: string; accent: string }
export interface LightShowSettings { theme: string; custom: LightShowColors; brightness: number; speed: number; intensity: 'calm' | 'balanced' | 'lively' }
export interface LightShowTheme extends LightShowColors { id: string; name: string; highlight: string }
export type LightShowSegment = 'countdown' | 'playing' | 'gameOver';
export interface LightShowTest { segment: LightShowSegment; endsUtc: string }
export interface InstallationSettings { version: number; devices: SavedDevice[]; boards: Board[]; idleSync: IdleSync; animation: AnimationSettings; lightShow: LightShowSettings; controllers: RemoteController[]; countdown: Defaults | null }
export interface RemoteStatus { state: string; apiVersion: string | null; inputSupported: boolean; selector: number }
export interface InstallationState {
  settings: InstallationSettings; locating: boolean; outputErrors: Record<string, string>;
  controllers: Record<string, RemoteStatus>; passwordsSaved: Record<string, boolean>;
  calibration: CalibrationSession | null; lightShowTest: LightShowTest | null;
}
export interface CalibrationSession { sessionId: string; activeBoardId: string; expiresUtc: string }
export interface EffectDescriptor {
  type: string; info: { requiredColors: number; optionalColors: number; totalColors: number;
    description: string; defaultSpeed: string; colorLabels: string[]; customParameters: Record<string, string> };
}
export interface SoundEvent { eventId: string; runId: number; key: string }
export interface DeviceDetails {
  device: Device;
  details: { wifiStrength: number | null; ledCount: number | null; brand: string; product: string;
    architecture: string; coreVersion: string; freeHeap: number; uptimeSeconds: number;
    dataSourceType: string; dataSourceAddress: string } | null;
}
export interface LightShowPreviewBoard { id: string; name: string; left: Strip | null; right: Strip | null; ledCount: number; assigned: boolean }
/** Server-rendered frames of one segment: each frame is base64 RGB for every board in row order. */
export interface LightShowPreview { framesPerSecond: number; seconds: number; exampleBoards: boolean; boards: LightShowPreviewBoard[]; frames: string[] }
/** What a controller's LEDs show right now: base64 RGB bytes, or null when it receives no frames and is idle. */
export interface BoardFrame { mac: string; pixels: string | null }
