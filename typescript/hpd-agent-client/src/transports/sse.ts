import type { AgentEvent } from '../types/events.js';
import type {
  AgentTransport,
  ClientMessage,
  ConnectOptions,
} from '../types/transport.js';
import type {
  Session,
  Branch,
  SiblingBranch,
  BranchMessage,
  CreateSessionRequest,
  UpdateSessionRequest,
  ListSessionsOptions,
  CreateBranchRequest,
  ForkBranchRequest,
} from '../types/session.js';
import { SseParser } from '../parser.js';

/**
 * SSE (Server-Sent Events) transport implementation.
 * Uses fetch with streaming for event delivery.
 * Bidirectional messages are sent via separate HTTP POST requests.
 */
export class SseTransport implements AgentTransport {
  private baseUrl: string;
  private sessionId?: string;
  private branchId?: string;
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
    if (this._connected) {
      throw new Error('Already connected. Call disconnect() first.');
    }

    this.sessionId = options.sessionId;
    this.branchId = options.branchId || 'main';
    this.abortController = new AbortController();

    // Combine user signal with our internal abort controller
    const signal = options.signal
      ? this.combineSignals(options.signal, this.abortController.signal)
      : this.abortController.signal;

    const url = `${this.baseUrl}/sessions/${this.sessionId}/branches/${this.branchId}/stream`;

    // Build request body with all stream options
    const requestBody: Record<string, unknown> = {
      messages: options.messages,
    };

    // Include client tools configuration
    if (options.clientToolKits && options.clientToolKits.length > 0) {
      requestBody.clientToolKits = options.clientToolKits;
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
    if (options.runConfig !== undefined) {
      requestBody.runConfig = options.runConfig;
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
    if (!this.sessionId || !this.branchId) {
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
        return `/sessions/${this.sessionId}/branches/${this.branchId}/permissions/respond`;
      case 'clarification_response':
        return `/sessions/${this.sessionId}/branches/${this.branchId}/clarifications/respond`;
      case 'continuation_response':
        return `/sessions/${this.sessionId}/branches/${this.branchId}/continuations/respond`;
      case 'client_tool_response':
        return `/sessions/${this.sessionId}/branches/${this.branchId}/client-tools/respond`;
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

  // ============================================
  // SESSION CRUD (V3)
  // ============================================

  async listSessions(options?: ListSessionsOptions): Promise<Session[]> {
    const url = new URL(`${this.baseUrl}/sessions`);

    if (options?.limit) url.searchParams.set('limit', options.limit.toString());
    if (options?.offset) url.searchParams.set('offset', options.offset.toString());
    if (options?.sortBy) url.searchParams.set('sortBy', options.sortBy);
    if (options?.sortDirection) url.searchParams.set('sortDirection', options.sortDirection);

    const response = await fetch(url.toString(), {
      method: 'GET',
      headers: { 'Content-Type': 'application/json' },
    });

    if (!response.ok) {
      const text = await response.text().catch(() => 'Unknown error');
      throw new Error(`Failed to list sessions: HTTP ${response.status}: ${text}`);
    }

    return response.json();
  }

  async getSession(sessionId: string): Promise<Session | null> {
    const response = await fetch(`${this.baseUrl}/sessions/${sessionId}`, {
      method: 'GET',
      headers: { 'Content-Type': 'application/json' },
    });

    if (response.status === 404) {
      return null;
    }

    if (!response.ok) {
      const text = await response.text().catch(() => 'Unknown error');
      throw new Error(`Failed to get session: HTTP ${response.status}: ${text}`);
    }

    return response.json();
  }

  async createSession(options?: CreateSessionRequest): Promise<Session> {
    const response = await fetch(`${this.baseUrl}/sessions`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(options || {}),
    });

    if (!response.ok) {
      const text = await response.text().catch(() => 'Unknown error');
      throw new Error(`Failed to create session: HTTP ${response.status}: ${text}`);
    }

    return response.json();
  }

  async updateSession(sessionId: string, request: UpdateSessionRequest): Promise<Session> {
    const response = await fetch(`${this.baseUrl}/sessions/${sessionId}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    });

    if (!response.ok) {
      const text = await response.text().catch(() => 'Unknown error');
      throw new Error(`Failed to update session: HTTP ${response.status}: ${text}`);
    }

    return response.json();
  }

  async deleteSession(sessionId: string): Promise<void> {
    const response = await fetch(`${this.baseUrl}/sessions/${sessionId}`, {
      method: 'DELETE',
    });

    if (!response.ok) {
      const text = await response.text().catch(() => 'Unknown error');
      throw new Error(`Failed to delete session: HTTP ${response.status}: ${text}`);
    }
  }

  // ============================================
  // BRANCH CRUD (V3)
  // ============================================

  async listBranches(sessionId: string): Promise<Branch[]> {
    const response = await fetch(`${this.baseUrl}/sessions/${sessionId}/branches`, {
      method: 'GET',
      headers: { 'Content-Type': 'application/json' },
    });

    if (!response.ok) {
      const text = await response.text().catch(() => 'Unknown error');
      throw new Error(`Failed to list branches: HTTP ${response.status}: ${text}`);
    }

    return response.json();
  }

  async getBranch(sessionId: string, branchId: string): Promise<Branch | null> {
    const response = await fetch(`${this.baseUrl}/sessions/${sessionId}/branches/${branchId}`, {
      method: 'GET',
      headers: { 'Content-Type': 'application/json' },
    });

    if (response.status === 404) {
      return null;
    }

    if (!response.ok) {
      const text = await response.text().catch(() => 'Unknown error');
      throw new Error(`Failed to get branch: HTTP ${response.status}: ${text}`);
    }

    return response.json();
  }

  async createBranch(sessionId: string, options?: CreateBranchRequest): Promise<Branch> {
    const response = await fetch(`${this.baseUrl}/sessions/${sessionId}/branches`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(options || {}),
    });

    if (!response.ok) {
      const text = await response.text().catch(() => 'Unknown error');
      throw new Error(`Failed to create branch: HTTP ${response.status}: ${text}`);
    }

    return response.json();
  }

  async forkBranch(
    sessionId: string,
    branchId: string,
    options: ForkBranchRequest
  ): Promise<Branch> {
    const response = await fetch(`${this.baseUrl}/sessions/${sessionId}/branches/${branchId}/fork`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(options),
    });

    if (!response.ok) {
      const text = await response.text().catch(() => 'Unknown error');
      throw new Error(`Failed to fork branch: HTTP ${response.status}: ${text}`);
    }

    return response.json();
  }

  async deleteBranch(sessionId: string, branchId: string, options?: { recursive?: boolean }): Promise<void> {
    const url = new URL(`${this.baseUrl}/sessions/${sessionId}/branches/${branchId}`);
    if (options?.recursive) url.searchParams.set('recursive', 'true');
    const response = await fetch(url.toString(), {
      method: 'DELETE',
    });

    if (!response.ok) {
      const text = await response.text().catch(() => 'Unknown error');
      throw new Error(`Failed to delete branch: HTTP ${response.status}: ${text}`);
    }
  }

  async getBranchMessages(sessionId: string, branchId: string): Promise<BranchMessage[]> {
    const response = await fetch(`${this.baseUrl}/sessions/${sessionId}/branches/${branchId}/messages`, {
      method: 'GET',
      headers: { 'Content-Type': 'application/json' },
    });

    if (!response.ok) {
      const text = await response.text().catch(() => 'Unknown error');
      throw new Error(`Failed to get branch messages: HTTP ${response.status}: ${text}`);
    }

    return response.json();
  }

  // ============================================
  // SIBLING NAVIGATION (V3)
  // ============================================

  async getBranchSiblings(sessionId: string, branchId: string): Promise<SiblingBranch[]> {
    const response = await fetch(`${this.baseUrl}/sessions/${sessionId}/branches/${branchId}/siblings`, {
      method: 'GET',
      headers: { 'Content-Type': 'application/json' },
    });

    if (!response.ok) {
      const text = await response.text().catch(() => 'Unknown error');
      throw new Error(`Failed to get siblings: HTTP ${response.status}: ${text}`);
    }

    // Backend returns ordered SiblingBranchDto[] (already sorted by siblingIndex)
    return response.json();
  }

  async getNextSibling(sessionId: string, branchId: string): Promise<Branch | null> {
    const branch = await this.getBranch(sessionId, branchId);
    if (!branch?.nextSiblingId) {
      return null;
    }

    return this.getBranch(sessionId, branch.nextSiblingId);
  }

  async getPreviousSibling(sessionId: string, branchId: string): Promise<Branch | null> {
    const branch = await this.getBranch(sessionId, branchId);
    if (!branch?.previousSiblingId) {
      return null;
    }

    return this.getBranch(sessionId, branch.previousSiblingId);
  }
}
