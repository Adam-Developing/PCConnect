export {};

declare global {
  interface Window {
    wails?: {
      Events: {
        On: (eventName: string, callback: (event: { name: string; data?: any; sender?: string }) => void) => () => void;
        Emit: (eventName: string, data?: any) => Promise<boolean>;
      };
      Call: {
        ByName: (methodName: string, ...args: any[]) => Promise<any> & { cancel?: () => void };
      };
      Window: {
        Show: () => Promise<void>;
        Hide: () => Promise<void>;
        Fullscreen: () => Promise<void>;
        UnFullscreen: () => Promise<void>;
        SetAlwaysOnTop: (enabled: boolean) => Promise<void>;
        Name: () => Promise<string>;
      };
    };
  }
}
