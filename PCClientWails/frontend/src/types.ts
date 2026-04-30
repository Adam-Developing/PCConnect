export type Session = {
  baseUrl: string;
  apiKey: string;
  pcName: string;
  notificationStyle?: 'toast' | 'fullscreen';
  fullscreenBgColor?: string;
  fullscreenTextColor?: string;
};

export type Reminder = {
  ID: number;
  Date: string;
  Time: string;
  Reminder: string;
  Completed: number;
};

export type CommandLog = {
  id: string;
  command: string;
  status: 'received' | 'executed' | 'failed';
  at: string;
  message: string;
};

export type QueueItem = {
  id: string;
  type: 'createReminder' | 'completeReminder';
  payload: Record<string, unknown>;
  createdAt: string;
};

export type ToastType = 'success' | 'error' | 'info' | 'warning';

export type Toast = {
  id: string;
  type: ToastType;
  message: string;
  duration?: number;
};

export type Theme = 'light' | 'dark';
