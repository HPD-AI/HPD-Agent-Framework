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

/**
 * WebSocket transport implementation.
 * Provides full-duplex communication for both events and bidirectional messages.
 */
export class WebSocketTransport implements AgentTransport {
  private baseUrl: string;
  private httpBaseUrl: string; // HTTP base URL for CRUD operations
  private ws?: WebSocket;
  private eventHandler?: (event: AgentEvent) => void;
  private errorHandler?: (error: Error) => void;
  private closeHandler?: () => void;

  constructor(baseUrl: string) {
    // Store original HTTP URL for CRUD operations
    this.httpBaseUrl = baseUrl.replace(/\/$/, '');

    // Convert http(s) to ws(s) for WebSocket URL
    this.baseUrl = baseUrl
      .replace(/^http:/, 'ws:')
      .replace(/^https:/, 'wss:')
      .replace(/\/$/, '');
  }

  get connected(): boolean {
    return this.ws?.readyState === WebSocket.OPEN;
  }

  connect(options: ConnectOptions): Promise<void> {
    if (this.ws?.readyState === WebSocket.OPEN || this.ws?.readyState === WebSocket.CONNECTING) {
      return Promise.reject(new Error('Already connected. Call disconnect() first.'));
    }

    return new Promise((resolve, reject) => {
      const sessionId = options.sessionId;
      const branchId = options.branchId || 'main';
      const url = `${this.baseUrl}/sessions/${sessionId}/branches/${branchId}/ws`;

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
        // Send initial messages and client tool options once connected
        this.ws!.send(JSON.stringify({
          messages: options.messages,
          clientToolGroups: options.clientToolGroups,
          context: options.context,
          state: options.state,
          expandedContainers: options.expandedContainers,
          hiddenTools: options.hiddenTools,
          resetClientState: options.resetClientState,
          runConfig: options.runConfig,
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

  // ============================================
  // SESSION CRUD (V3)
  // Note: Uses HTTP for CRUD operations (like SSE transport)
  // WebSocket is only used for streaming
  // ============================================

  async listSessions(options?: ListSessionsOptions): Promise<Session[]> {
    const url = new URL(`${this.httpBaseUrl}/sessions`);

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
    const response = await fetch(`${this.httpBaseUrl}/sessions/${sessionId}`, {
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
    const response = await fetch(`${this.httpBaseUrl}/sessions`, {
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
    const response = await fetch(`${this.httpBaseUrl}/sessions/${sessionId}`, {
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
    const response = await fetch(`${this.httpBaseUrl}/sessions/${sessionId}`, {
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
    const response = await fetch(`${this.httpBaseUrl}/sessions/${sessionId}/branches`, {
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
    const response = await fetch(`${this.httpBaseUrl}/sessions/${sessionId}/branches/${branchId}`, {
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
    const response = await fetch(`${this.httpBaseUrl}/sessions/${sessionId}/branches`, {
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
    const response = await fetch(`${this.httpBaseUrl}/sessions/${sessionId}/branches/${branchId}/fork`, {
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
    const url = new URL(`${this.httpBaseUrl}/sessions/${sessionId}/branches/${branchId}`);
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
    const response = await fetch(`${this.httpBaseUrl}/sessions/${sessionId}/branches/${branchId}/messages`, {
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
    const response = await fetch(`${this.httpBaseUrl}/sessions/${sessionId}/branches/${branchId}/siblings`, {
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
