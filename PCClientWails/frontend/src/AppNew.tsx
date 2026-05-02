import { useEffect, useMemo, useRef, useState } from 'react';
import { addReminder, clearCommand, completeReminder, fetchDevices, fetchReminders, login, pollCommand, registerDevice, sha256 } from './lib/api';
import { enqueue, loadQueue, removeById } from './lib/offlineQueue';
import { useToasts } from './lib/useToasts';
import { useTheme } from './lib/useTheme';
import type { CommandLog, QueueItem, Reminder, Session } from './types';
import ToastContainer from './components/ToastContainer';
import LoginPage from './components/LoginPage';
import DevicePage from './components/DevicePage';
import DashboardTab from './components/DashboardTab';
import RemindersTab from './components/RemindersTab';
import DevicesTab from './components/DevicesTab';
import SettingsTab from './components/SettingsTab';

type Tab = 'dashboard' | 'reminders' | 'devices' | 'settings';
type Stage = 'login' | 'device' | 'app';

function makeId(): string { return `${Date.now()}-${Math.random().toString(16).slice(2)}`; }

function normalizeSocketReminders(payload: unknown): Reminder[] {
  if (Array.isArray(payload)) return payload as Reminder[];
  if (payload && typeof payload === 'object' && Array.isArray((payload as any).reminders)) return (payload as any).reminders;
  return [];
}

const NAV: { key: Tab; icon: string; label: string }[] = [
  { key: 'dashboard', icon: '📊', label: 'Dashboard' },
  { key: 'reminders', icon: '📋', label: 'Reminders' },
  { key: 'devices', icon: '🖥', label: 'Devices' },
  { key: 'settings', icon: '⚙', label: 'Settings' },
];

export default function AppNew() {
  const [theme, toggleTheme] = useTheme();
  const { toasts, addToast, removeToast } = useToasts();
  const [stage, setStage] = useState<Stage>('login');
  const [tab, setTab] = useState<Tab>('dashboard');
  const [session, setSession] = useState<Session | null>(null);
  const [authApiKey, setAuthApiKey] = useState('');
  const [devices, setDevices] = useState<string[]>([]);
  const [socketHealthy, setSocketHealthy] = useState(false);
  const [mode, setMode] = useState<'realtime' | 'degraded' | 'offline'>('offline');
  const [statusText, setStatusText] = useState('Not connected');
  const [reminders, setReminders] = useState<Reminder[]>([]);
  const [commandLog, setCommandLog] = useState<CommandLog[]>([]);
  const [queueSize, setQueueSize] = useState(loadQueue().length);
  const [busy, setBusy] = useState(false);
  const [errorMessage, setErrorMessage] = useState('');
  const fallbackTimerRef = useRef<number | null>(null);
  const fallbackIntervalRef = useRef<number>(5000);

  const pill = useMemo(() => {
    if (socketHealthy) return { label: 'Online', className: 'pill online' };
    if (mode === 'degraded') return { label: 'Reconnecting', className: 'pill degraded' };
    return { label: 'Offline', className: 'pill offline' };
  }, [mode, socketHealthy]);

  // Restore session
  useEffect(() => {
    const binding = window.go?.app?.App;
    if (!binding) return;
    binding.GetSession().then((s: any) => {
      if (s?.baseUrl && s?.apiKey && s?.pcName) { setSession(s); setStage('app'); }
    }).catch(() => { });
  }, []);

  // Connect on session change
  useEffect(() => {
    if (!session) return;
    setStage('app');
    bootstrap(session);
    
    // Listen for events from Go
    const unsubReminders = window.runtime.EventsOn('reminders_updated', (data: any) => {
      const items = normalizeSocketReminders(data);
      if (items.length || Array.isArray(data)) setReminders(items);
    });

    const unsubStatus = window.runtime.EventsOn('connection_status', (status: { connected: boolean, mode: 'realtime' | 'degraded' | 'offline' }) => {
      setSocketHealthy(status.connected);
      setMode(status.mode);
      setStatusText(status.connected ? 'Connected' : 'Reconnecting…');
    });

    const unsubLogout = window.runtime.EventsOn('logout', () => {
      handleLogout();
    });

    // Check initial status
    const appBinding = window.go?.app?.App;
    if (appBinding) {
      appBinding.GetConnectionStatus().then((status: any) => {
        setSocketHealthy(status.socketHealthy);
        setMode(status.mode as any);
        setStatusText(status.socketHealthy ? 'Connected' : 'Waiting…');
      });
      if (appBinding.FrontendReady) {
        appBinding.FrontendReady();
      }
    }

    return () => {
      unsubReminders();
      unsubStatus();
      unsubLogout();
    };
  }, [session]);

  async function bootstrap(s: Session) {
    try {
      // Try to load from cache first for instant feedback
      if (window.go?.app?.App?.GetCachedReminders) {
        const cached = await window.go.app.App.GetCachedReminders();
        if (cached) setReminders(normalizeSocketReminders(cached));
      }

      const data = await fetchReminders(s);
      setReminders(data);
      if (window.go?.app?.App?.SyncReminders) {
        await window.go.app.App.SyncReminders(data);
      }
      await flushQueue(s);
    } catch (e) { 
      console.warn('Bootstrap fetch failed, using cache if available', e);
      // If fetch fails, we already have cache loaded from above
    }
  }

  function stopFallback() { if (fallbackTimerRef.current !== null) { clearTimeout(fallbackTimerRef.current); fallbackTimerRef.current = null; } fallbackIntervalRef.current = 5000; }
  function startFallback(s: Session) {
    if (fallbackTimerRef.current !== null) return;
    const tick = async () => {
      try {
        if (socketHealthy) { stopFallback(); return; }
        const cmd = await pollCommand(s);
        if (cmd) { await execCmd(cmd); await clearCommand(s); }
        fallbackIntervalRef.current = Math.min(fallbackIntervalRef.current * 2, 30000);
      } catch { fallbackIntervalRef.current = Math.min(fallbackIntervalRef.current * 2, 30000); }
      finally { fallbackTimerRef.current = window.setTimeout(tick, fallbackIntervalRef.current); }
    };
    fallbackTimerRef.current = window.setTimeout(tick, fallbackIntervalRef.current);
  }

  async function execCmd(command: string) {
    const log: CommandLog = { id: makeId(), command, status: 'received', at: new Date().toISOString(), message: 'Received' };
    setCommandLog(prev => [log, ...prev].slice(0, 30));
    try {
      if (window.go?.app?.App?.ExecuteSystemCommand) await window.go.app.App.ExecuteSystemCommand(command);
      setCommandLog(prev => prev.map(e => e.id === log.id ? { ...e, status: 'executed', message: 'Done' } : e));
      addToast('info', `Command executed: ${command}`);
    } catch (err) {
      setCommandLog(prev => prev.map(e => e.id === log.id ? { ...e, status: 'failed', message: (err as Error).message } : e));
      addToast('error', `Command failed: ${command}`);
    }
  }

  async function handleLogin(username: string, password: string) {
    setErrorMessage(''); setBusy(true);
    try {
      const hash = await sha256(password);
      const apiKey = await login(username.trim(), hash);
      setAuthApiKey(apiKey);
      const devs = await fetchDevices({ apiKey });
      setDevices(devs);
      setStage('device');
    } catch (e) { setErrorMessage((e as Error).message || 'Login failed'); }
    finally { setBusy(false); }
  }

  async function handleDeviceSelect(pcName: string) {
    if (!pcName.trim() || !authApiKey) return;
    setBusy(true); setErrorMessage('');
    try {
      await registerDevice({ apiKey: authApiKey }, pcName.trim());
      const s: Session = { baseUrl: 'http://localhost:3000/api_node', apiKey: authApiKey, pcName: pcName.trim() };
      if (window.go?.app?.App?.SaveSession) await window.go.app.App.SaveSession(s);
      setSession(s);
    } catch (e) { setErrorMessage((e as Error).message || 'Failed to connect'); }
    finally { setBusy(false); }
  }

  async function handleLogout() {
    if (window.go?.app?.App?.ClearSession) await window.go.app.App.ClearSession();
    setSession(null); setStage('login'); setReminders([]); setCommandLog([]); setDevices([]);
    addToast('info', 'Signed out');
  }

  async function queueOrRun(item: QueueItem, runner: () => Promise<void>) {
    if (!session) return;
    if (!socketHealthy && mode !== 'realtime') { setQueueSize(enqueue(item).length); addToast('warning', 'Saved offline — will sync later'); return; }
    try { await runner(); } catch { setQueueSize(enqueue(item).length); addToast('warning', 'Queued for sync'); }
  }

  async function flushQueue(s: Session) {
    const queue = loadQueue();
    if (!queue.length) { setQueueSize(0); return; }
    for (const item of queue) {
      try {
        if (item.type === 'createReminder') await addReminder(s, { date: String(item.payload.date), time: String(item.payload.time), text: String(item.payload.text) });
        if (item.type === 'completeReminder') await completeReminder(s, Number(item.payload.id), Number(item.payload.completed));
        removeById(item.id);
      } catch { break; }
    }
    setQueueSize(loadQueue().length);
  }

  async function handleCreateReminder(date: string, time: string, text: string) {
    if (!session) return;
    const item: QueueItem = { id: makeId(), type: 'createReminder', payload: { date, time, text }, createdAt: new Date().toISOString() };
    await queueOrRun(item, async () => {
      await addReminder(session, { date, time, text });
      setReminders(await fetchReminders(session));
      addToast('success', 'Reminder created');
    });
  }

  async function handleToggleReminder(r: Reminder) {
    if (!session) return;
    const next = r.Completed ? 0 : 1;
    setReminders(prev => prev.map(i => i.ID === r.ID ? { ...i, Completed: next } : i));
    const item: QueueItem = { id: makeId(), type: 'completeReminder', payload: { id: r.ID, completed: next }, createdAt: new Date().toISOString() };
    await queueOrRun(item, async () => { await completeReminder(session, r.ID, next); });
  }

  // Render
  if (stage === 'login') return (<><ToastContainer toasts={toasts} onRemove={removeToast} /><LoginPage busy={busy} error={errorMessage} onLogin={handleLogin} /></>);
  if (stage === 'device') return (<><ToastContainer toasts={toasts} onRemove={removeToast} /><DevicePage devices={devices} busy={busy} error={errorMessage} onSelect={handleDeviceSelect} /></>);
  if (!session) return null;

  return (
    <>
      <ToastContainer toasts={toasts} onRemove={removeToast} />
      <div className="app-shell">
        <aside className="sidebar">
          <div className="sidebar-brand">
            <div className="sidebar-brand-icon">PC</div>
            <div className="sidebar-brand-text"><h2>PCConnect</h2><p>{session.pcName}</p></div>
          </div>
          {NAV.map(n => (
            <button key={n.key} className={`nav-btn ${tab === n.key ? 'active' : ''}`} onClick={() => setTab(n.key)}>
              <span className="nav-icon">{n.icon}</span><span className="nav-label">{n.label}</span>
            </button>
          ))}
          <div className="sidebar-spacer" />
          <div className="sidebar-footer">
            <button className="nav-btn" onClick={handleLogout}><span className="nav-icon">🚪</span><span className="nav-label">Log Out</span></button>
          </div>
        </aside>
        <div className="main-content">
          <header className="topbar">
            <div className={pill.className}>{pill.label}</div>
            <span className="text-dim">{statusText}</span>
            <div className="topbar-right">
              {queueSize > 0 && <span className="queue-badge">⏳ {queueSize} queued</span>}
              <button className="theme-toggle" onClick={toggleTheme} title="Toggle theme">{theme === 'dark' ? '☀️' : '🌙'}</button>
            </div>
          </header>
          <div className="page-content">
            {tab === 'dashboard' && <DashboardTab mode={mode} socketHealthy={socketHealthy} reminders={reminders} commandLog={commandLog} />}
            {tab === 'reminders' && <RemindersTab reminders={reminders} onToggle={handleToggleReminder} onCreate={handleCreateReminder} />}
            {tab === 'devices' && <DevicesTab session={session} />}
            {tab === 'settings' && <SettingsTab theme={theme} session={session} addToast={addToast} onToggleTheme={toggleTheme} onLogout={handleLogout} onSessionUpdate={setSession} />}
          </div>
        </div>
      </div>
    </>
  );
}
