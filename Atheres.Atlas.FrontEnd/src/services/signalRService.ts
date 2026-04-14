import * as signalR from '@microsoft/signalr'
import type { Notification } from '../types'

type NotificationHandler = (notification: Notification) => void

class AtlasSignalRService {
  private connection: signalR.HubConnection | null = null
  private handlers: NotificationHandler[] = []

  async start(): Promise<void> {
    if (this.connection) return

    this.connection = new signalR.HubConnectionBuilder()
      .withUrl('/api/negotiate', {
        accessTokenFactory: () => localStorage.getItem('atlas_access_token') ?? '',
      })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(signalR.LogLevel.Warning)
      .build()

    this.connection.on('notification', (data: Notification) => {
      this.handlers.forEach((h) => h(data))
    })

    this.connection.onreconnecting(() => {
      console.info('[SignalR] Reconnecting...')
    })

    this.connection.onreconnected(() => {
      console.info('[SignalR] Reconnected.')
    })

    this.connection.onclose(() => {
      console.warn('[SignalR] Connection closed.')
    })

    await this.connection.start()
    console.info('[SignalR] Connected to atlas-hub.')
  }

  onNotification(handler: NotificationHandler): () => void {
    this.handlers.push(handler)
    return () => {
      this.handlers = this.handlers.filter((h) => h !== handler)
    }
  }

  async stop(): Promise<void> {
    await this.connection?.stop()
    this.connection = null
  }

  get state(): signalR.HubConnectionState {
    return this.connection?.state ?? signalR.HubConnectionState.Disconnected
  }
}

// Singleton
export const signalRService = new AtlasSignalRService()
