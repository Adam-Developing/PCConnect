import { useState } from 'react';

type Props = {
  busy: boolean;
  error: string;
  onLogin: (username: string, password: string) => Promise<void>;
};

export default function LoginPage({ busy, error, onLogin }: Props) {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [showPw, setShowPw] = useState(false);

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    onLogin(username, password);
  };

  return (
    <div className="auth-page">
      <div className="auth-container">
        <div className="auth-logo">
          <div className="auth-logo-icon">PC</div>
          <h1>PCConnect</h1>
        </div>
        <div className="auth-card">
          <h2>Welcome back</h2>
          <p className="text-muted">Sign in to manage your devices and reminders.</p>
          <form className="form-grid" onSubmit={handleSubmit}>
            <label>
              Username or Email
              <input value={username} onChange={e => setUsername(e.target.value)} placeholder="Enter your username" required autoFocus />
            </label>
            <label>
              Password
              <div style={{ position: 'relative' }}>
                <input type={showPw ? 'text' : 'password'} value={password} onChange={e => setPassword(e.target.value)} placeholder="Enter your password" required />
                <button type="button" className="btn-ghost btn-sm" style={{ position: 'absolute', right: 4, top: '50%', transform: 'translateY(-50%)' }} onClick={() => setShowPw(!showPw)}>
                  {showPw ? '🙈' : '👁'}
                </button>
              </div>
            </label>
            <button type="submit" className="btn btn-primary" disabled={busy}>
              {busy ? <><span className="spinner" /> Signing in…</> : 'Sign In'}
            </button>
          </form>
          {error && <p className="inline-error">{error}</p>}
        </div>
        <p className="auth-footer">Secure desktop companion · End-to-end encrypted</p>
      </div>
    </div>
  );
}
