import type { Reminder, Session } from '../types';

const appBinding = 'github.com/Adam-Developing/PCConnect/PCClientWails/app.App';

function hasWailsRuntime(): boolean {
  return !!window.wails?.Call?.ByName;
}

function callApp<T>(method: string, ...args: any[]): Promise<T> {
  const caller = window.wails?.Call?.ByName;
  if (!caller) {
    return Promise.reject(new Error('Wails runtime unavailable'));
  }
  return caller(`${appBinding}.${method}`, ...args) as Promise<T>;
}

export async function getSession(): Promise<Session | null> {
  if (!hasWailsRuntime()) return null;
  return callApp<Session>('GetSession');
}

export async function saveSession(session: Session): Promise<Session | null> {
  if (!hasWailsRuntime()) return session;
  return callApp<Session>('SaveSession', session);
}

export async function clearSession(): Promise<void> {
  if (!hasWailsRuntime()) return;
  await callApp<void>('ClearSession');
}

export async function executeSystemCommand(command: string): Promise<string> {
  return callApp<string>('ExecuteSystemCommand', command);
}

export async function getConnectionStatus(): Promise<{ socketHealthy: boolean; mode: string } | null> {
  if (!hasWailsRuntime()) return null;
  return callApp<{ socketHealthy: boolean; mode: string }>('GetConnectionStatus');
}

export async function getCachedReminders(): Promise<Reminder[] | null> {
  if (!hasWailsRuntime()) return null;
  return callApp<Reminder[]>('GetCachedReminders');
}

export async function syncReminders(items: Reminder[]): Promise<void> {
  if (!hasWailsRuntime()) return;
  await callApp<void>('SyncReminders', items);
}

export async function frontendReady(): Promise<void> {
  if (!hasWailsRuntime()) return;
  await callApp<void>('FrontendReady');
}

export async function saveNotificationStyle(style: string, bgColor: string, textColor: string): Promise<void> {
  if (!hasWailsRuntime()) return;
  await callApp<void>('SaveNotificationStyle', style, bgColor, textColor);
}

export async function isAutoStartEnabled(): Promise<boolean | null> {
  if (!hasWailsRuntime()) return null;
  return callApp<boolean>('IsAutoStartEnabled');
}

export async function setAutoStart(enabled: boolean): Promise<void> {
  if (!hasWailsRuntime()) return;
  await callApp<void>('SetAutoStart', enabled);
}
