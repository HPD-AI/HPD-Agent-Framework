import type { AgentEvent } from '../types/events.js';
import type { AgentTransport, ClientMessage, ConnectOptions } from '../types/transport.js';

/**
 * WebSocket transport implementation.
 * Provides full-duplex communication for both events and bidirectional messages.
 */
export class WebSocketTransport implements AgentTransport {
  private baseUrl: string;
  private ws?: WebSocket;
  private eventHandler?: (event: AgentEvent) => void;
  private errorHandler?: (error: Error) => void;
  private closeHandler?: () => void;

  constructor(baseUrl: string) {
    // Convert http(s) to ws(s)
    this.baseUrl = baseUrl
      .replace(/^http:/, 'ws:')
      .replace(/^https:/, 'wss:')
      .replace(/\/$/, '');
  }

  get connected(): boolean {
    return this.ws?.readyState === WebSocket.OPEN;
  }

  connect(options: ConnectOptions): Promise<void> {
    return new Promise((resolve, reject) => {
      const url = `${this.baseUrl}/agent/conversations/${options.conversationId}/ws`;

      try {
        this.ws = new WebSocket(url);
      } catch (error) {
        reject(new Error(`Failed to create WebSocket: ${error}`));
        return;
      }

      // Cleanup function for event listeners
      const cleanup = () => {
        options.signal?.removeEventListener('abort', onAbort);
      };

      // Handle abort signal
      const onAbort = () => {
        cleanup();
        this.ws?.close();
        reject(new DOMException('Aborted', 'AbortError'));
      };

      if (options.signal?.aborted) {
        reject(new DOMException('Aborted', 'AbortError'));
        return;
      }

      options.signal?.addEventListener('abort', onAbort, { once: true });

      this.ws.onopen = () => {
        cleanup();
        // Send initial messages and frontend options once connected
        this.ws!.send(JSON.stringify({
          messages: options.messages,
          frontendPlugins: options.frontendPlugins,
          context: options.context,
          state: options.state,
          expandedContainers: options.expandedContainers,
          hiddenTools: options.hiddenTools,
          resetFrontendState: options.resetFrontendState,
        }));
        resolve();
      };

      this.ws.onmessage = (event) => {
        try {
          const agentEvent = JSON.parse(event.data as string) as AgentEvent;
          this.eventHandler?.(agentEvent);
        } catch {
          // Ignore parse errors - invalid JSON
        }
      };

      this.ws.onerror = () => {
        cleanup();
        const error = new Error('WebSocket error');
        this.errorHandler?.(error);
        reject(error);
      };

      this.ws.onclose = () => {
        cleanup();
        this.closeHandler?.();
      };
    });
  }

  async send(message: ClientMessage): Promise<void> {
    if (this.ws?.readyState !== WebSocket.OPEN) {
      throw new Error('WebSocket not connected');
    }
    this.ws.send(JSON.stringify(message));
  }

  onEvent(handler: (event: AgentEvent) => void): void {
    this.eventHandler = handler;
  }

  onError(handler: (error: Error) => void): void {
    this.errorHandler = handler;
  }

  onClose(handler: () => void): void {
    this.closeHandler = handler;
  }

  disconnect(): void {
    this.ws?.close();
  }
}
