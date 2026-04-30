import type { CommandLog, Reminder } from '../types';

type Props = {
  mode: string;
  socketHealthy: boolean;
  reminders: Reminder[];
  commandLog: CommandLog[];
};

export default function DashboardTab({ mode, socketHealthy, reminders, commandLog }: Props) {
  const nextReminder = reminders.find(r => !r.Completed);

  return (
    <div className="animate-in">
      <div className="page-title"><h2>Dashboard</h2><p className="text-muted">System overview at a glance</p></div>
      <div className="grid-3">
        <div className="card metric-card">
          <span className="metric-icon">🔗</span>
          <div className="card-header"><h3>System Status</h3></div>
          <div className="metric-value" style={{ color: socketHealthy ? 'var(--success)' : 'var(--warning)', fontSize: '1.1rem' }}>
            {socketHealthy ? '● Connected' : '○ ' + (mode === 'degraded' ? 'Reconnecting' : 'Offline')}
          </div>

        </div>
        <div className="card metric-card">
          <span className="metric-icon">📋</span>
          <div className="card-header"><h3>Reminders</h3></div>
          <div className="metric-value">{reminders.filter(r => !r.Completed).length}</div>
          <p className="metric-label">pending of {reminders.length} total</p>
        </div>
        <div className="card metric-card">
          <span className="metric-icon">⚡</span>
          <div className="card-header"><h3>Commands</h3></div>
          <div className="metric-value">{commandLog.length}</div>
          <p className="metric-label">received this session</p>
        </div>
      </div>

      <div style={{ marginTop: 16 }} className="grid-2">
        <div className="card">
          <div className="card-header"><h3>📌 Next Reminder</h3></div>
          {nextReminder ? (
            <div>
              <p style={{ fontWeight: 500 }}>{nextReminder.Reminder}</p>
              <p className="text-dim" style={{ marginTop: 4 }}>{nextReminder.Date} at {nextReminder.Time}</p>
            </div>
          ) : (
            <div className="empty-state" style={{ padding: '24px 0' }}>
              <p>No pending reminders</p>
            </div>
          )}
        </div>
        <div className="card">
          <div className="card-header"><h3>⚡ Recent Activity</h3></div>
          {commandLog.length > 0 ? (
            <div className="event-log">
              {commandLog.slice(0, 5).map(log => (
                <div key={log.id} className="event-item">
                  <div className={`event-dot ${log.status}`} />
                  <div className="event-body">
                    <span className="event-cmd">{log.command}</span>
                    <span className="event-time"> · {log.status} · {new Date(log.at).toLocaleTimeString()}</span>
                  </div>
                </div>
              ))}
            </div>
          ) : (
            <div className="empty-state" style={{ padding: '24px 0' }}>
              <p>No commands received yet</p>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
