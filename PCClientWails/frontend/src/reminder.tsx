import React, { useEffect, useState } from 'react';
import ReactDOM from 'react-dom/client';
import FullscreenReminder from './components/FullscreenReminder';
import { completeReminder } from './lib/api';
import type { Reminder, Session } from './types';
import './styles.css';

function ReminderApp() {
  const [reminder, setReminder] = useState<Reminder | null>(null);
  const [session, setSession] = useState<Session | null>(null);

  useEffect(() => {
    const appBinding = window.go?.app?.App;
    if (appBinding?.GetSession) {
      appBinding.GetSession().then((s) => {
        if (s?.baseUrl && s?.apiKey && s?.pcName) {
          setSession(s);
        }
      }).catch(() => {});
    }

    const runtime = window.runtime;

    // Safety timeout: If after 5 seconds we still have no reminder, hide the window
    // to prevent a "stuck" invisible window.
    const safetyTimer = setTimeout(async () => {
      if (!reminder) {
        console.warn('Safety timeout: No active reminder found after 5s. Hiding window.');
        (runtime as any).WindowHide();
      }
    }, 5000);

    // Listen for future reminder events
    const unsub = runtime.EventsOn('fullscreen_reminder_data', (eventData: Reminder) => {
      console.log('Received fullscreen reminder event:', eventData);
      if (eventData) {
        setReminder(eventData);
        // If we get an event, we don't need the safety timer anymore
        clearTimeout(safetyTimer);
      }
    });

    return () => {
      unsub?.();
      clearTimeout(safetyTimer);
    };
  }, [reminder]);

  const handleComplete = async (r: Reminder) => {
    if (session) {
      try {
        await completeReminder(session, r.ID, 1);
        // Clear state and hide window via Go to ensure consistency
        setReminder(null);
        (window.runtime as any).WindowHide();
      } catch (err) {
        console.error('Failed to complete reminder:', err);
      }
    }
  };

  if (!session) {
    return (
      <div style={{
        minHeight: '100vh',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        background: 'rgba(0, 0, 0, 0.1)',
        color: '#ffffff',
        fontFamily: 'Inter, sans-serif',
        textAlign: 'center',
        padding: '2rem',
      }}>
        <div>
          <div style={{ fontSize: '2rem', fontWeight: 700, marginBottom: '0.5rem' }}>PCConnect</div>
          <div style={{ fontSize: '1rem', opacity: 0.85 }}>Loading reminder session…</div>
        </div>
      </div>
    );
  }

  if (!reminder) {
    return (
      <div style={{
        minHeight: '100vh',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        background: 'rgba(0, 0, 0, 0.1)',
        color: '#ffffff',
        fontFamily: 'Inter, sans-serif',
        textAlign: 'center',
        padding: '2rem',
      }}>
        <div>
          <div style={{ fontSize: '2rem', fontWeight: 700, marginBottom: '0.5rem' }}>PCConnect</div>
          <div style={{ fontSize: '1rem', opacity: 0.85 }}>Waiting for the next full-screen reminder…</div>
        </div>
      </div>
    );
  }

  return (
    <FullscreenReminder 
      reminder={reminder} 
      session={session} 
      onComplete={handleComplete} 
    />
  );
}

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <ReminderApp />
  </React.StrictMode>
);
