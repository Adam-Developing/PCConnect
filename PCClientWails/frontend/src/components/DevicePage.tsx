import { useState } from 'react';

type Props = {
  devices: string[];
  busy: boolean;
  error: string;
  onSelect: (name: string) => void;
};

export default function DevicePage({ devices, busy, error, onSelect }: Props) {
  const [selected, setSelected] = useState(devices[0] ?? '');
  const [newName, setNewName] = useState('');

  return (
    <div className="auth-page">
      <div className="auth-container device-card">
        <div className="auth-logo">
          <div className="auth-logo-icon">PC</div>
          <h1>PCConnect</h1>
        </div>
        <div className="auth-card">
          <h2>Select your device</h2>
          <p className="text-muted">Choose an existing PC or register a new one.</p>
          <div className="device-grid">
            <div className="device-section">
              <h3>📟 Existing PCs</h3>
              {devices.length > 0 ? (
                <div className="device-chips">
                  {devices.map(d => (
                    <button key={d} className={`device-chip ${selected === d ? 'active' : ''}`} onClick={() => setSelected(d)}>{d}</button>
                  ))}
                </div>
              ) : (
                <p className="text-dim">No devices registered yet.</p>
              )}
              <button className="btn btn-primary btn-sm" disabled={!selected || busy} onClick={() => onSelect(selected)}>
                {busy ? 'Connecting…' : 'Use selected'}
              </button>
            </div>
            <div className="device-section">
              <h3>➕ New Device</h3>
              <label>
                PC Name
                <input value={newName} onChange={e => setNewName(e.target.value)} placeholder="e.g. WorkStation-1" />
              </label>
              <button className="btn btn-secondary btn-sm" disabled={!newName.trim() || busy} onClick={() => onSelect(newName.trim())}>
                {busy ? 'Creating…' : 'Create & connect'}
              </button>
            </div>
          </div>
          {error && <p className="inline-error">{error}</p>}
        </div>
      </div>
    </div>
  );
}
