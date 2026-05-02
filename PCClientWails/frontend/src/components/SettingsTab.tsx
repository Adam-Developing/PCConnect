import { useEffect, useState } from 'react';
import type { Session, Theme, ToastType } from '../types';
import { fetchProfile, updateProfile, sha256 } from '../lib/api';
import { DEFAULT_FULLSCREEN_BG, DEFAULT_FULLSCREEN_TEXT } from '../lib/reminderDefaults';

type Props = {
  theme: Theme;
  session: Session;
  addToast: (type: ToastType, message: string) => void;
  onToggleTheme: () => void;
  onLogout: () => void;
  onSessionUpdate?: (newSession: Session) => void;
};

const normalizeColorValue = (value: string | undefined, fallback: string) => {
  if (!value) return fallback;
  if (value.startsWith('#') && value.length === 9) return value.slice(0, 7);
  return value;
};

function AutoStartToggle({ addToast }: { addToast: Props['addToast'] }) {
  const [enabled, setEnabled] = useState(false);

  useEffect(() => {
    if (window.go?.app?.App?.IsAutoStartEnabled) {
      window.go.app.App.IsAutoStartEnabled().then(setEnabled);
    }
  }, []);

  const toggle = async () => {
    const next = !enabled;
    const appBinding = window.go?.app?.App;
    if (!appBinding) return;
    try {
      await appBinding.SetAutoStart(next);
      setEnabled(next);
      addToast('success', next ? 'Auto-start enabled' : 'Auto-start disabled');
    } catch (err) {
      addToast('error', `Failed to update auto-start: ${(err as Error).message}`);
    }
  };

  return (
    <div className="setting-row">
      <div>
        <div className="setting-label">Launch on Startup</div>
        <div className="setting-desc">Open PCConnect when Windows starts</div>
      </div>
      <button className={`toggle ${enabled ? 'on' : ''}`} onClick={toggle} aria-label="Toggle auto-start" />
    </div>
  );
}

export default function SettingsTab({ theme, session, addToast, onToggleTheme, onLogout, onSessionUpdate }: Props) {
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [profile, setProfile] = useState({ Name: '', Username: '', Email: '' });

  const [name, setName] = useState('');
  const [email, setEmail] = useState('');

  const [oldPw, setOldPw] = useState('');
  const [newPw, setNewPw] = useState('');
  const [confirmPw, setConfirmPw] = useState('');

  const [notificationStyle, setNotificationStyle] = useState<'toast' | 'fullscreen'>(session.notificationStyle || 'toast');
  const [bgColor, setBgColor] = useState(normalizeColorValue(session.fullscreenBgColor, DEFAULT_FULLSCREEN_BG));
  const [textColor, setTextColor] = useState(normalizeColorValue(session.fullscreenTextColor, DEFAULT_FULLSCREEN_TEXT));

  useEffect(() => {
    fetchProfile(session)
      .then(p => {
        setProfile(p);
        setName(p.Name);
        setEmail(p.Email);
      })
      .catch(err => addToast('error', `Failed to load profile: ${err.message}`))
      .finally(() => setLoading(false));
  }, [session]);

  const handleUpdateProfile = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true);
    try {
      await updateProfile(session, { name, email });
      setProfile(prev => ({ ...prev, Name: name, Email: email }));
      addToast('success', 'Profile updated successfully');
    } catch (err) {
      addToast('error', (err as Error).message);
    } finally {
      setSaving(false);
    }
  };

  const handleChangePassword = async (e: React.FormEvent) => {
    e.preventDefault();

    // Enforce minimum requirements
    if (newPw.length < 8) {
      addToast('error', 'Password must be at least 8 characters long');
      return;
    }
    if (!/[A-Za-z]/.test(newPw) || !/[0-9]/.test(newPw) || !/[!@#$%^&*(),.?":{}|<>]/.test(newPw)) {
      addToast('error', 'Password must contain letters, numbers, and at least one special character');
      return;
    }

    if (newPw !== confirmPw) {
      addToast('error', 'New passwords do not match');
      return;
    }
    setSaving(true);
    try {
      const oldHash = await sha256(oldPw);
      const newHash = await sha256(newPw);
      await updateProfile(session, { oldPassword: oldHash, newPassword: newHash });
      setOldPw('');
      setNewPw('');
      setConfirmPw('');
      addToast('success', 'Password changed successfully');
    } catch (err) {
      addToast('error', (err as Error).message);
    } finally {
      setSaving(false);
    }
  };

  const handleNotificationStyleChange = async (style: 'toast' | 'fullscreen') => {
    try {
      if (window.go?.app?.App?.SaveNotificationStyle) {
        await window.go.app.App.SaveNotificationStyle(style, bgColor, textColor);
        setNotificationStyle(style);
        if (onSessionUpdate) {
          onSessionUpdate({ ...session, notificationStyle: style, fullscreenBgColor: bgColor, fullscreenTextColor: textColor });
        }
        addToast('success', 'Notification style updated');
      }
    } catch (err) {
      addToast('error', `Failed to update style: ${(err as Error).message}`);
    }
  };

  const handleColorChange = async (type: 'bg' | 'text', value: string) => {
    const newBg = type === 'bg' ? value : bgColor;
    const newText = type === 'text' ? value : textColor;
    if (type === 'bg') setBgColor(value);
    else setTextColor(value);

    try {
      if (window.go?.app?.App?.SaveNotificationStyle) {
        await window.go.app.App.SaveNotificationStyle(notificationStyle, newBg, newText);
        if (onSessionUpdate) {
          onSessionUpdate({ ...session, notificationStyle, fullscreenBgColor: newBg, fullscreenTextColor: newText });
        }
      }
    } catch (err) {
      // Background save, silently fail or toast
    }
  };

  if (loading) {
    return <div className="animate-in"><div className="page-title"><h2>Settings</h2><p className="text-muted">Loading your preferences…</p></div></div>;
  }

  return (
    <div className="animate-in">
      <div className="page-title"><h2>Settings</h2><p className="text-muted">Preferences and account configuration</p></div>

      <div className="settings-grid">
        <div className="card">
          <div className="settings-group">
            <h3>Account Profile</h3>
            <p className="text-muted text-sm mb-4">Update your personal details and contact information.</p>
            <form className="form-grid" onSubmit={handleUpdateProfile}>
              <label>
                Display Name
                <input value={name} onChange={e => setName(e.target.value)} placeholder="Your name" required />
              </label>
              <label>
                Username
                <input value={profile.Username} disabled className="bg-muted" title="Username cannot be changed" />
              </label>
              <label>
                Email Address
                <input type="email" value={email} onChange={e => setEmail(e.target.value)} placeholder="your@email.com" required />
              </label>
              <div className="form-actions">
                <button type="submit" className="btn btn-primary btn-sm" disabled={saving || (name === profile.Name && email === profile.Email)}>
                  {saving ? 'Saving...' : 'Save Changes'}
                </button>
              </div>
            </form>
          </div>
        </div>

        <div className="card">
          <div className="settings-group">
            <h3>Security</h3>
            <p className="text-muted text-sm mb-4">Change your password to keep your account secure. (Min 8 chars, letters, numbers & special char)</p>
            <form className="form-grid" onSubmit={handleChangePassword}>
              <label>
                Current Password
                <input type="password" value={oldPw} onChange={e => setOldPw(e.target.value)} placeholder="••••••••" required />
              </label>
              <label>
                New Password
                <input type="password" value={newPw} onChange={e => setNewPw(e.target.value)} placeholder="••••••••" required />
              </label>
              <label>
                Confirm New Password
                <input type="password" value={confirmPw} onChange={e => setConfirmPw(e.target.value)} placeholder="••••••••" required />
              </label>
              <div className="form-actions">
                <button type="submit" className="btn btn-primary btn-sm" disabled={saving || !newPw}>
                  {saving ? 'Updating...' : 'Update Password'}
                </button>
              </div>
            </form>
          </div>
        </div>

        <div className="card">
          <div className="settings-group">
            <h3>App Preferences</h3>
            <div className="setting-row">
              <div>
                <div className="setting-label">Dark Mode</div>
                <div className="setting-desc">Switch between light and dark theme</div>
              </div>
              <button className={`toggle ${theme === 'dark' ? 'on' : ''}`} onClick={onToggleTheme} aria-label="Toggle theme" />
            </div>

            <div className="setting-row">
              <div>
                <div className="setting-label">Full-Screen Reminders</div>
                <div className="setting-desc">Show a full-page overlay instead of a system toast</div>
              </div>
              <button 
                className={`toggle ${notificationStyle === 'fullscreen' ? 'on' : ''}`} 
                onClick={() => handleNotificationStyleChange(notificationStyle === 'toast' ? 'fullscreen' : 'toast')} 
                aria-label="Toggle full-screen reminders" 
              />
            </div>

            {notificationStyle === 'fullscreen' && (
              <>
                <div className="setting-row" style={{ paddingLeft: '2rem' }}>
                  <div>
                    <div className="setting-label">Background Color</div>
                  </div>
                  <input type="color" value={bgColor} onChange={(e) => handleColorChange('bg', e.target.value)} />
                </div>
                <div className="setting-row" style={{ paddingLeft: '2rem' }}>
                  <div>
                    <div className="setting-label">Text Color</div>
                  </div>
                  <input type="color" value={textColor} onChange={(e) => handleColorChange('text', e.target.value)} />
                </div>
              </>
            )}
            
            <AutoStartToggle addToast={addToast} />
          </div>



          <div className="settings-group">
            <h3>Session</h3>
            <div className="setting-row">
              <div><div className="setting-label">Sign Out</div><div className="setting-desc">Disconnect and clear session from this device</div></div>
              <button className="btn btn-danger btn-sm" onClick={onLogout}>Log Out</button>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
