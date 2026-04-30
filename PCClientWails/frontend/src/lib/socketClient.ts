import { io, type Socket } from 'socket.io-client';
import type { Session } from '../types';

const SOCKET_URL = 'http://localhost:3000';

export function connectSocket(session: Session): Socket {
  return io(SOCKET_URL, {
    transports: ['websocket', 'polling'],
    reconnection: true,
    reconnectionAttempts: Infinity,
    reconnectionDelay: 1000,
    reconnectionDelayMax: 8000,
    timeout: 8000,
  });
}
