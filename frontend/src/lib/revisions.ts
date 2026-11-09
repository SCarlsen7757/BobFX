import type { Versioned } from './contracts';

export function newer<T>(current: Versioned<T> | undefined, incoming: Versioned<T>, instance: string): boolean {
  return incoming.serverInstanceId === instance &&
    (!current || current.serverInstanceId !== instance || incoming.revision > current.revision);
}
