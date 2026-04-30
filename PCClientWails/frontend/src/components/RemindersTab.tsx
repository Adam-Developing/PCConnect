import { useState } from 'react';
import type { Reminder } from '../types';

type Filter = 'all' | 'pending' | 'completed' | 'today';

type Props = {
  reminders: Reminder[];
  onToggle: (r: Reminder) => void;
  onCreate: (date: string, time: string, text: string) => void;
};

function matchFilter(r: Reminder, filter: Filter): boolean {
  if (filter === 'pending') return !r.Completed;
  if (filter === 'completed') return !!r.Completed;
  if (filter === 'today') {
    const today = new Date();
    const dd = String(today.getDate()).padStart(2, '0');
    const mm = String(today.getMonth() + 1).padStart(2, '0');
    const yy = String(today.getFullYear()).slice(-2);
    return r.Date === `${dd}/${mm}/${yy}`;
  }
  return true;
}

export default function RemindersTab({ reminders, onToggle, onCreate }: Props) {
  const [filter, setFilter] = useState<Filter>('all');
  const [date, setDate] = useState(new Date().toISOString().slice(0, 10));
  const [time, setTime] = useState('09:00');
  const [text, setText] = useState('');
  const [showComposer, setShowComposer] = useState(false);

  const filtered = reminders.filter(r => matchFilter(r, filter));
  const counts = {
    all: reminders.length,
    pending: reminders.filter(r => !r.Completed).length,
    completed: reminders.filter(r => !!r.Completed).length,
  };

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!text.trim()) return;
    onCreate(date, time, text.trim());
    setText('');
    setShowComposer(false);
  };

  return (
    <div className="animate-in">
      <div className="page-title" style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
        <div><h2>Reminders</h2><p className="text-muted">Manage your tasks and reminders</p></div>
        <button className="btn btn-primary btn-sm" onClick={() => setShowComposer(!showComposer)}>
          {showComposer ? '✕ Close' : '＋ New Reminder'}
        </button>
      </div>

      {showComposer && (
        <form className="composer animate-in" onSubmit={handleSubmit}>
          <h3>Create Reminder</h3>
          <div className="composer-fields">
            <label>Date<input type="date" value={date} onChange={e => setDate(e.target.value)} required /></label>
            <label>Time<input type="time" value={time} onChange={e => setTime(e.target.value)} required /></label>
            <label>What to remember?<input value={text} onChange={e => setText(e.target.value)} placeholder="Enter reminder text..." required /></label>
            <button type="submit" className="btn btn-primary">Save</button>
          </div>
        </form>
      )}

      <div className="reminder-filters">
        {(['all', 'pending', 'completed', 'today'] as Filter[]).map(f => (
          <button key={f} className={`filter-chip ${filter === f ? 'active' : ''}`} onClick={() => setFilter(f)}>
            {f.charAt(0).toUpperCase() + f.slice(1)}
            {f !== 'today' && <span> ({counts[f as keyof typeof counts] ?? ''})</span>}
          </button>
        ))}
      </div>

      {filtered.length > 0 ? (
        <div className="reminder-list">
          {filtered.map(r => (
            <div key={r.ID} className={`reminder-item ${r.Completed ? 'completed' : ''}`}>
              <button className={`reminder-check ${r.Completed ? 'checked' : ''}`} onClick={() => onToggle(r)} title={r.Completed ? 'Mark pending' : 'Mark complete'}>
                {r.Completed ? '✓' : ''}
              </button>
              <div className="reminder-body">
                <div className="reminder-text">{r.Reminder}</div>
                <div className="reminder-meta">
                  <span>📅 {r.Date || 'No date'}</span>
                  <span>🕐 {r.Time || 'No time'}</span>
                </div>
              </div>
            </div>
          ))}
        </div>
      ) : (
        <div className="empty-state">
          <div className="empty-icon">📝</div>
          <h3>No reminders {filter !== 'all' ? `(${filter})` : ''}</h3>
          <p>{filter === 'all' ? 'Create your first reminder to get started.' : 'No reminders match this filter.'}</p>
        </div>
      )}
    </div>
  );
}
