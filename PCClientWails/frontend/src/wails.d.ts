export {};

declare global {
  interface Window {
    runtime: {
      EventsOn: (eventName: string, callback: (data?: any) => void) => () => void;
      EventsEmit: (eventName: string, ...args: any[]) => void;
      WindowShow: () => void;
      WindowFullscreen: () => void;
      WindowUnfullscreen: () => void;
      WindowSetAlwaysOnTop: (b: boolean) => void;
      Quit: () => void;
    };
    go?: {
      app?: {
        App?: {
          GetSession: () => Promise<{ baseUrl: string; apiKey: string; pcName: string }>;
          SaveSession: (session: { baseUrl: string; apiKey: string; pcName: string }) => Promise<{ baseUrl: string; apiKey: string; pcName: string }>;
          ClearSession: () => Promise<void>;
          ExecuteSystemCommand: (command: string) => Promise<string>;
          GetConnectionStatus: () => Promise<{ socketHealthy: boolean; mode: string }>;
          GetCachedReminders: () => Promise<any>;
          SetAutoStart: (enabled: boolean) => Promise<void>;
          IsAutoStartEnabled: () => Promise<boolean>;
          Notify: (title: string, body: string) => Promise<void>;
          SaveNotificationStyle: (style: string, bgColor: string, textColor: string) => Promise<void>;
          SyncReminders: (items: any[]) => Promise<void>;
          FrontendReady: () => Promise<void>;
        };
      };
    };
  }
}
