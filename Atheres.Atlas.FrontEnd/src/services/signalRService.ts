import * as signalR from '@microsoft/signalr'
import type { Notification } from '../types'

type NotificationHandler = (notification: Notification) => void

class AtlasSignalRService {
  private connection: signalR.HubConnection | null = null
  private handlers: NotificationHandler[] = []
  // Tracks the most recent start/stop call so concurrent invocations (React
  // StrictMode double-mount, rapid re-renders) serialize cleanly instead of
  // aborting each other mid-negotiation.
  private pending: Promise<void> = Promise.resolve()

  async start(): Promise<void> {
    this.pending = this.pending.then(() => this._startImpl()).catch(() => {})
    return this.pending
  }

  async stop(): Promise<void> {
    this.pending = this.pending.then(() => this._stopImpl()).catch(() => {})
    return this.pending
  }

  private async _startImpl(): Promise<void> {
    if (this.connection) return

    // The SignalR client appends "/negotiate" to this base URL, so point it at
    // "/api" — then the actual request lands at POST /api/negotiate, which the
    // NotificationAgent handles. Passing "/api/negotiate" here produced
    // /api/negotiate/negotiate and 404'd.
    const connection = new signalR.HubConnectionBuilder()
      .withUrl('/api', {
        accessTokenFactory: () => localStorage.getItem('atlas_access_token') ?? '',
      })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(signalR.LogLevel.Warning)
      .build()

    connection.on('notification', (data: Notification) => {
      this.handlers.forEach((h) => h(data))
    })
    connection.onreconnecting(() => console.info('[SignalR] Reconnecting...'))
    connection.onreconnected(() => console.info('[SignalR] Reconnected.'))
    connection.onclose(() => console.warn('[SignalR] Connection closed.'))

    this.connection = connection
    try {
      await connection.start()
      console.info('[SignalR] Connected to atlashub.')
    } catch (err) {
      this.connection = null
      // AbortError is expected when stop() fires before negotiate finishes —
      // e.g. React StrictMode double-mounting in development. Not a real failure.
      if ((err as Error)?.name === 'AbortError') return
      throw err
    }
  }

  private async _stopImpl(): Promise<void> {
    const c = this.connection
    this.connection = null
    if (c) await c.stop()
  }

  onNotification(handler: NotificationHandler): () => void {
    this.handlers.push(handler)
    return () => {
      this.handlers = this.handlers.filter((h) => h !== handler)
    }
  }

  get state(): signalR.HubConnectionState {
    return this.connection?.state ?? signalR.HubConnectionState.Disconnected
  }
}

// Singleton
export const signalRService = new AtlasSignalRService()
