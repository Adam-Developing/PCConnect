import React, { useEffect, useState } from 'react';
import ReactDOM from 'react-dom/client';
import './styles.css';

interface Reminder {
  ID: number;
  Reminder: string;
  Date: string;
  Time: string;
  Completed: number;
}

function ReminderApp() {
  const [reminderId, setReminderId] = useState<number | null>(null);
  const [reminders, setReminders] = useState<Reminder[]>([]);
  const [reminder, setReminder] = useState<Reminder | null>(null);

  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const idParam = params.get('id');
    if (idParam) {
      setReminderId(parseInt(idParam, 10));
    }

    const appBinding = (window as any).wails?.Call;
    if (appBinding) {
      appBinding("app.App.GetCachedReminders").then((cached: any) => {
        if (cached && Array.isArray(cached)) {
          setReminders(cached);
        }
      });
    }
  }, []);

  useEffect(() => {
    if (reminderId !== null && reminders.length > 0) {
      const found = reminders.find(r => r.ID === reminderId);
      if (found) {
        setReminder(found);
      }
    }
  }, [reminderId, reminders]);

  const handleDismiss = () => {
    if ((window as any).wails?.Events?.Emit) {
      (window as any).wails.Events.Emit("close_reminder_window");
    }
  };

  return (
    <div style={{ color: 'white', textAlign: 'center', maxWidth: '600px', width: '100%', pointerEvents: 'auto' }}>
      {reminder ? (
        <>
          <h1 style={{ fontSize: '4rem', marginBottom: '1rem', fontWeight: 800 }}>{reminder.Time}</h1>
          <h2 style={{ fontSize: '2.5rem', marginBottom: '3rem', fontWeight: 500, opacity: 0.9 }}>{reminder.Reminder}</h2>
          <div style={{ display: 'flex', gap: '20px', justifyContent: 'center' }}>
            <button
              onClick={handleDismiss}
              style={{
                padding: '15px 40px',
                fontSize: '1.2rem',
                backgroundColor: 'rgba(255, 255, 255, 0.2)',
                color: 'white',
                border: '2px solid white',
                borderRadius: '8px',
                cursor: 'pointer',
                fontWeight: 600,
                transition: 'all 0.2s ease'
              }}
              onMouseOver={(e) => {
                e.currentTarget.style.backgroundColor = 'white';
                e.currentTarget.style.color = 'black';
              }}
              onMouseOut={(e) => {
                e.currentTarget.style.backgroundColor = 'rgba(255, 255, 255, 0.2)';
                e.currentTarget.style.color = 'white';
              }}
            >
              Dismiss
            </button>
          </div>
        </>
      ) : (
        <h2>Loading reminder...</h2>
      )}
    </div>
  );
}

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <ReminderApp />
  </React.StrictMode>,
);
