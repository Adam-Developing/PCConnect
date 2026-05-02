import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './AppNew';
import ReminderWindow from './ReminderWindow';
import './styles.css';

const search = new URLSearchParams(window.location.search);
const isReminderWindow = search.get('window') === 'reminder';

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    {isReminderWindow ? <ReminderWindow /> : <App />}
  </React.StrictMode>,
);
