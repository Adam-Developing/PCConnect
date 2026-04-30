import type { Reminder, Session } from '../types';

export async function sha256(input: string): Promise<string> {
  const data = new TextEncoder().encode(input);
  const hash = await crypto.subtle.digest('SHA-256', data);
  return Array.from(new Uint8Array(hash)).map(b => b.toString(16).padStart(2, '0')).join('');
}

const API_BASE_URL = 'http://localhost:3000/api_node';

function apiPath(path: string): string {
  const normalized = path.startsWith('/') ? path : `/${path}`;
  return `${API_BASE_URL}${normalized}`;
}

async function parseError(response: Response, fallbackMessage: string): Promise<Error> {
  try {
    const text = await response.text();
    if (!text) {
      return new Error(fallbackMessage);
    }

    try {
      const body = JSON.parse(text);
      const message = body?.message || body?.error || body?.data?.message;
      if (typeof message === 'string' && message.trim().length > 0) {
        return new Error(message.trim());
      }
    } catch {
      if (!text.trim().startsWith('{') && !text.trim().startsWith('[')) {
        return new Error(text.trim());
      }
    }
  } catch {
  }

  return new Error(fallbackMessage);
}

function normalizeReminder(item: Reminder): Reminder {
  const text = String(item.Reminder ?? '').trim();
  if (text.toLowerCase().includes('error decrypting')) {
    return {
      ...item,
      Reminder: 'Reminder text unavailable',
    };
  }

  return item;
}

type BasicSession = Pick<Session, 'apiKey'>;

export function getApiBaseUrl(): string {
  return API_BASE_URL;
}

export async function login(username: string, passwordHash: string): Promise<string> {
  const response = await fetch(apiPath('/auth/login'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      loginUsername: username,
      loginPassword: passwordHash,
    }),
  });

  if (!response.ok) {
    throw await parseError(response, 'Login failed. Please verify your credentials and try again.');
  }

  const data = await response.json();
  const apiKey = data?.data?.api_key;
  if (!apiKey) {
    throw new Error('API key missing in response');
  }
  return apiKey;
}

export async function fetchDevices(session: BasicSession): Promise<string[]> {
  const response = await fetch(apiPath('/v1/devices'), {
    headers: {
      'X-API-Key': session.apiKey,
    },
  });

  if (!response.ok) {
    throw await parseError(response, `Failed to load device list (${response.status})`);
  }

  const body = await response.json();
  const names = body?.data?.PCNames;
  if (!Array.isArray(names)) {
    return [];
  }

  return names
    .map((value: unknown) => String(value).trim())
    .filter((value: string) => value.length > 0);
}

export async function registerDevice(session: BasicSession, pcName: string): Promise<void> {
  const response = await fetch(apiPath('/v1/devices'), {
    method: 'POST',
    headers: {
      'X-API-Key': session.apiKey,
      'PCName': pcName,
    },
  });

  if (!response.ok) {
    throw await parseError(response, `Failed to register device (${response.status})`);
  }
}

export async function fetchReminders(session: Session): Promise<Reminder[]> {
  const response = await fetch(apiPath('/v1/reminders'), {
    headers: {
      'X-API-Key': session.apiKey,
      'PCName': session.pcName,
    },
  });

  if (!response.ok) {
    throw await parseError(response, `Failed to load reminders (${response.status})`);
  }

  const body = await response.json();

  // Handle both flat array and wrapped { data: [...] } responses
  let items: Reminder[];
  if (Array.isArray(body)) {
    items = body as Reminder[];
  } else if (Array.isArray(body?.data)) {
    items = body.data as Reminder[];
  } else if (Array.isArray(body?.reminders)) {
    items = body.reminders as Reminder[];
  } else {
    items = [];
  }

  return items.map(normalizeReminder);
}

export async function addReminder(session: Session, reminder: { date: string; time: string; text: string }): Promise<void> {
  const response = await fetch(apiPath('/v1/reminders'), {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'X-API-Key': session.apiKey,
      'PCName': session.pcName,
    },
    body: JSON.stringify({
      date: reminder.date,
      time: reminder.time,
      reminder: reminder.text,
      completed: 0,
    }),
  });

  if (!response.ok) {
    throw await parseError(response, `Failed to add reminder (${response.status})`);
  }
}

export async function completeReminder(session: Session, reminderId: number, completed: number): Promise<void> {
  const response = await fetch(apiPath(`/v1/reminders/${reminderId}/complete`), {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'X-API-Key': session.apiKey,
      'PCName': session.pcName,
    },
    body: JSON.stringify({ completed }),
  });

  if (!response.ok) {
    throw await parseError(response, `Failed to update reminder (${response.status})`);
  }
}

export async function pollCommand(session: Session): Promise<string | null> {
  const response = await fetch(apiPath('/v1/devices/requests'), {
    headers: {
      'X-API-Key': session.apiKey,
      'PCName': session.pcName,
    },
  });

  if (!response.ok) {
    throw await parseError(response, `Fallback command poll failed (${response.status})`);
  }

  const body = await response.json();
  return body?.data?.request ?? null;
}

export async function clearCommand(session: Session): Promise<void> {
  const response = await fetch(apiPath('/v1/devices/requests/clear'), {
    method: 'POST',
    headers: {
      'X-API-Key': session.apiKey,
      'PCName': session.pcName,
    },
  });

  if (!response.ok) {
    throw await parseError(response, `Fallback command clear failed (${response.status})`);
  }
}

export async function fetchProfile(session: Session): Promise<{ Name: string, Username: string, Email: string }> {
  const response = await fetch(apiPath('/v1/account/profile'), {
    headers: {
      'X-API-Key': session.apiKey,
    },
  });

  if (!response.ok) {
    throw await parseError(response, `Failed to load profile (${response.status})`);
  }

  const body = await response.json();
  return body.data;
}

export async function updateProfile(session: Session, data: { name?: string, email?: string, oldPassword?: string, newPassword?: string }): Promise<void> {
  const response = await fetch(apiPath('/v1/account/profile'), {
    method: 'PUT',
    headers: {
      'Content-Type': 'application/json',
      'X-API-Key': session.apiKey,
    },
    body: JSON.stringify(data),
  });

  if (!response.ok) {
    throw await parseError(response, `Failed to update profile (${response.status})`);
  }
}
