import type { QueueItem } from '../types';

const queueStorageKey = 'pcconnect.offlineQueue.v1';

export function loadQueue(): QueueItem[] {
  const serialized = localStorage.getItem(queueStorageKey);
  if (!serialized) {
    return [];
  }

  try {
    const parsed = JSON.parse(serialized) as QueueItem[];
    if (!Array.isArray(parsed)) {
      return [];
    }
    return parsed;
  } catch {
    return [];
  }
}

export function saveQueue(items: QueueItem[]): void {
  localStorage.setItem(queueStorageKey, JSON.stringify(items));
}

export function enqueue(item: QueueItem): QueueItem[] {
  const next = [...loadQueue(), item];
  saveQueue(next);
  return next;
}

export function removeById(id: string): QueueItem[] {
  const next = loadQueue().filter((item) => item.id !== id);
  saveQueue(next);
  return next;
}
