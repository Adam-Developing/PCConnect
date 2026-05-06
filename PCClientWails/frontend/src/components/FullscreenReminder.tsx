import { useEffect } from 'react';
import { WindowFullscreen, WindowSetAlwaysOnTop, WindowUnfullscreen } from '../../wailsjs/runtime/runtime';
import type { Reminder, Session } from '../types';

type Props = {
  reminder: Reminder;
  session: Session;
  onComplete: (r: Reminder) => void;
};

export default function FullscreenReminder({ reminder, session, onComplete }: Props) {
  useEffect(() => {
    // Play a gentle alert sound if needed
    const audio = new Audio('/alert.mp3'); 
    audio.play().catch(() => {});

    // Try to go true fullscreen
    const wailsBridge = window.wails;
    if (wailsBridge) {
      WindowFullscreen();
      WindowSetAlwaysOnTop(true);
    }

    const blockKeys = (e: KeyboardEvent) => {
      e.preventDefault();
      e.stopPropagation();
      // Optionally allow Enter to mark complete
      if (e.key === 'Enter') {
        onComplete(reminder);
      }
    };

    window.addEventListener('keydown', blockKeys, { capture: true });

    return () => {
      window.removeEventListener('keydown', blockKeys, { capture: true });
      // Restore normal window
      if (wailsBridge) {
        WindowUnfullscreen();
        WindowSetAlwaysOnTop(false);
      }
    };
  }, [reminder, onComplete]);

  const bgColor = session.fullscreenBgColor || '#ff0000cc';
  const textColor = session.fullscreenTextColor || '#ffffff';

  // Ensure background is translucent if it's a hex code from the picker
  const displayBgColor = (bgColor.startsWith('#') && bgColor.length === 7) ? bgColor + 'cc' : bgColor;

  return (
    <div 
      style={{
        position: 'fixed',
        inset: 0,
        zIndex: 99999, // Ensure it covers everything
        backgroundColor: displayBgColor,
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        padding: '2rem',
        animation: 'fadeIn 0.3s ease-out'
      }}
    >
      <div 
        style={{
          padding: '3rem',
          maxWidth: '800px',
          width: '100%',
          textAlign: 'center',
          animation: 'slideUp 0.4s cubic-bezier(0.16, 1, 0.3, 1)'
        }}
      >
        <div style={{ fontSize: '4rem', marginBottom: '1rem' }}>⏰</div>
        <h2 style={{ 
          fontSize: '3rem', 
          fontWeight: 700, 
          marginBottom: '1rem',
          color: textColor
        }}>
          Reminder
        </h2>
        <p style={{ 
          fontSize: '2rem', 
          color: textColor, 
          marginBottom: '3rem',
          lineHeight: 1.5,
          opacity: 0.9
        }}>
          {reminder.Reminder}
        </p>

        <div style={{
          display: 'flex',
          gap: '1rem',
          justifyContent: 'center'
        }}>
          <button 
            style={{ 
              padding: '1rem 3rem', 
              fontSize: '1.5rem',
              fontWeight: 600,
              backgroundColor: textColor,
              color: bgColor,
              border: 'none',
              borderRadius: '8px',
              cursor: 'pointer',
              boxShadow: '0 10px 25px -5px rgba(0,0,0,0.2)'
            }}
            onClick={() => onComplete(reminder)}
          >
            Mark Completed
          </button>
        </div>
      </div>
      <style>{`
        @keyframes fadeIn {
          from { opacity: 0; }
          to { opacity: 1; }
        }
        @keyframes slideUp {
          from { opacity: 0; transform: translateY(20px) scale(0.95); }
          to { opacity: 1; transform: translateY(0) scale(1); }
        }
      `}</style>
    </div>
  );
}
