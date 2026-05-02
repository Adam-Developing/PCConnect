import { useEffect, useMemo, useState } from 'react';
import type { Reminder, Session } from './types';
import FullscreenReminder from './components/FullscreenReminder';
import { completeReminder } from './lib/api';
import { DEFAULT_FULLSCREEN_BG, DEFAULT_FULLSCREEN_TEXT } from './lib/reminderDefaults';

const fallbackSession: Session = {
  baseUrl: '',
  apiKey: '',
  pcName: '',
  notificationStyle: 'fullscreen',
  fullscreenBgColor: DEFAULT_FULLSCREEN_BG,
  fullscreenTextColor: DEFAULT_FULLSCREEN_TEXT
};

export default function ReminderWindow() {
  const [reminder, setReminder] = useState<Reminder | null>(null);
  const [session, setSession] = useState<Session | null>(null);

  const displaySession = useMemo(() => session ?? fallbackSession, [session]);

  const loadSession = async () => {
    const binding = window.go?.app?.App;
    if (!binding?.GetSession) return;
    const next = await binding.GetSession();
    if (next?.baseUrl || next?.apiKey || next?.pcName) {
      setSession(next);
    }
  };

  useEffect(() => {
    loadSession();
    if (!window.runtime?.EventsOn) return;
    const unsubscribe = window.runtime.EventsOn('show_fullscreen_reminder', (data?: Reminder) => {
      if (!data) return;
      setReminder(data);
      loadSession();
      window.runtime?.WindowShow?.();
      window.runtime?.WindowFullscreen?.();
      window.runtime?.WindowSetAlwaysOnTop?.(true);
    });
    return () => {
      unsubscribe();
    };
  }, []);

  const handleComplete = async (item: Reminder) => {
    try {
      if (session) {
        await completeReminder(session, item.ID, 1);
      }
    } catch {
      // ignore
    } finally {
      setReminder(null);
      window.runtime?.WindowHide?.();
    }
  };

  if (!reminder) {
    return null;
  }

  return (
    <FullscreenReminder reminder={reminder} session={displaySession} onComplete={handleComplete} />
  );
}
