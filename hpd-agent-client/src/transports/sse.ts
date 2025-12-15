import type { AgentEvent } from '../types/events.js';
import type { AgentTransport, ClientMessage, ConnectOptions } from '../types/transport.js';
import { SseParser } from '../parser.js';

/**
 * SSE (Server-Sent Events) transport implementation.
 * Uses fetch with streaming for event delivery.
 * Bidirectional messages are sent via separate HTTP POST requests.
 */
export class SseTransport implements AgentTransport {
  private baseUrl: string;
  private conversationId?: string;
  private abortController?: AbortController;
  private eventHandler?: (event: AgentEvent) => void;
  private errorHandler?: (error: Error) => void;
  private closeHandler?: () => void;
  private _connected = false;

  constructor(baseUrl: string) {
    // Remove trailing slash for consistent URL building
    this.baseUrl = baseUrl.replace(/\/$/, '');
  }

  get connected(): boolean {
    return this._connected;
  }

  async connect(options: ConnectOptions): Promise<void> {
    this.conversationId = options.conversationId;
    this.abortController = new AbortController();

    // Combine user signal with our internal abort controller
    const signal = options.signal
      ? this.combineSignals(options.signal, this.abortController.signal)
      : this.abortController.signal;

    const url = `${this.baseUrl}/agent/conversations/${options.conversationId}/stream`;

    // Build request body with all stream options
    const requestBody: Record<string, unknown> = {
      messages: options.messages,
    };

    // Include client tools configuration
    if (options.clientToolGroups && options.clientToolGroups.length > 0) {
      requestBody.clientToolGroups = options.clientToolGroups;
    }
    if (options.context && options.context.length > 0) {
      requestBody.context = options.context;
    }
    if (options.state !== undefined) {
      requestBody.state = options.state;
    }
    if (options.expandedContainers && options.expandedContainers.length > 0) {
      requestBody.expandedContainers = options.expandedContainers;
    }
    if (options.hiddenTools && options.hiddenTools.length > 0) {
      requestBody.hiddenTools = options.hiddenTools;
    }
    if (options.resetClientState) {
      requestBody.resetClientState = options.resetClientState;
    }

    const response = await fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'text/event-stream',
      },
      body: JSON.stringify(requestBody),
      signal,
    });

    if (!response.ok) {
      const text = await response.text().catch(() => 'Unknown error');
      throw new Error(`HTTP ${response.status}: ${text}`);
    }

    if (!response.body) {
      throw new Error('No response body');
    }

    this._connected = true;
    await this.processStream(response.body);
  }

  private async processStream(body: ReadableStream<Uint8Array>): Promise<void> {
    const reader = body.getReader();
    const parser = new SseParser();

    try {
      while (true) {
        const { done, value } = await reader.read();

        if (done) {
          // Process any remaining data in the buffer
          const finalEvents = parser.flush();
          for (const event of finalEvents) {
            this.eventHandler?.(event);
          }
          break;
        }

        const events = parser.processChunk(value);
        for (const event of events) {
          this.eventHandler?.(event);
        }
      }
    } catch (error) {
      // Don't treat abort as an error
      if ((error as DOMException)?.name !== 'AbortError') {
        this.errorHandler?.(error as Error);
      }
    } finally {
      reader.releaseLock();
      this._connected = false;
      this.closeHandler?.();
    }
  }

  async send(message: ClientMessage): Promise<void> {
    if (!this.conversationId) {
      throw new Error('Not connected');
    }

    // SSE is unidirectional - send via separate HTTP request
    const endpoint = this.getEndpointForMessage(message);

    const response = await fetch(`${this.baseUrl}${endpoint}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(message),
    });

    if (!response.ok) {
      const text = await response.text().catch(() => 'Unknown error');
      throw new Error(`Failed to send message: HTTP ${response.status}: ${text}`);
    }
  }

  private getEndpointForMessage(message: ClientMessage): string {
    switch (message.type) {
      case 'permission_response':
        return `/agent/conversations/${this.conversationId}/permissions/respond`;
      case 'clarification_response':
        return `/agent/conversations/${this.conversationId}/clarifications/respond`;
      case 'continuation_response':
        return `/agent/conversations/${this.conversationId}/continuations/respond`;
      case 'client_tool_response':
        return `/agent/conversations/${this.conversationId}/client-tools/respond`;
      default:
        throw new Error(`Unknown message type: ${(message as { type: string }).type}`);
    }
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
    this.abortController?.abort();
    this._connected = false;
  }

  /**
   * Combine multiple AbortSignals into one.
   * Aborts when any of the input signals abort.
   */
  private combineSignals(...signals: AbortSignal[]): AbortSignal {
    const controller = new AbortController();

    for (const signal of signals) {
      if (signal.aborted) {
        controller.abort(signal.reason);
        return controller.signal;
      }
      signal.addEventListener('abort', () => controller.abort(signal.reason), { once: true });
    }

    return controller.signal;
  }
}
