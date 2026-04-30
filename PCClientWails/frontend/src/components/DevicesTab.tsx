import type { Session } from '../types';

export default function DevicesTab({ session }: { session: Session }) {
  return (
    <div className="animate-in">
      <div className="page-title"><h2>Devices</h2><p className="text-muted">Connected device information</p></div>
      <div className="grid-2">
        <div className="card">
          <div className="card-header"><h3>🖥 Current Device</h3></div>
          <div className="setting-row"><div><div className="setting-label">PC Name</div></div><div className="setting-value">{session.pcName}</div></div>

        </div>
        <div className="card">
          <div className="card-header"><h3>ℹ️ Information</h3></div>
          <p className="text-muted" style={{ fontSize: '.875rem' }}>
            This device is registered and connected to the PCConnect gateway. Remote commands sent from your phone will be executed in real-time.
          </p>
          <p className="text-dim" style={{ marginTop: 8 }}>To switch devices, log out and select a different PC.</p>
        </div>
      </div>
    </div>
  );
}
