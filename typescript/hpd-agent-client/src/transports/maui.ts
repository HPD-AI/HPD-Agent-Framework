import type { AgentEvent } from '../types/events.js';
import type { AgentTransport, ClientMessage, ConnectOptions } from '../types/transport.js';
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

declare global {
  interface Window {
    HybridWebView?: {
      InvokeDotNet<TResult = unknown>(methodName: string, params?: unknown[]): Promise<TResult>;
      SendRawMessage(message: string): void;
    };
  }
}

export class MauiTransport implements AgentTransport {
  private eventHandler?: (event: AgentEvent) => void;
  private errorHandler?: (error: Error) => void;
  private closeHandler?: () => void;
  private _connected = false;
  private currentStreamId?: string;
  private currentSessionId?: string;
  private messageListener?: (event: Event) => void;

  get connected(): boolean {
    return this._connected;
  }

  async connect(options: ConnectOptions): Promise<void> {
    if (!window.HybridWebView) {
      throw new Error('MAUI HybridWebView not available');
    }

    // Always clean up any previous listener before registering a new one.
    // Handles the case where a prior stream ended without a clean close event.
    this.cleanup();

    this.messageListener = (event: Event) => {
      const customEvent = event as CustomEvent<{ message: string }>;
      const message = customEvent.detail.message;

      const [type, streamId, ...jsonParts] = message.split(':');

      if (type === 'agent_event' && streamId === this.currentStreamId) {
        try {
          const eventJson = jsonParts.join(':');
          const agentEvent = JSON.parse(eventJson) as AgentEvent;
          this.eventHandler?.(agentEvent);
        } catch (error) {
          this.errorHandler?.(new Error(`Failed to parse event: ${error}`));
        }
      } else if (type === 'agent_complete' && streamId === this.currentStreamId) {
        this._connected = false;
        this.closeHandler?.();
      } else if (type === 'agent_error' && streamId === this.currentStreamId) {
        const errorMessage = jsonParts.join(':');
        this.errorHandler?.(new Error(errorMessage));
        this._connected = false;
        this.closeHandler?.();
      }
    };

    window.addEventListener('HybridWebViewMessageReceived', this.messageListener);

    try {
      this.currentSessionId = options.sessionId;
      this.currentStreamId = await window.HybridWebView.InvokeDotNet<string>(
        'StartStream',
        [
          options.messages[0]?.content ?? '',
          options.sessionId,
          options.branchId || 'main',
          options.runConfig ? JSON.stringify(options.runConfig) : undefined,
        ]
      );

      this._connected = true;

      if (options.signal) {
        options.signal.addEventListener('abort', () => {
          this.disconnect();
        }, { once: true });
      }
    } catch (error) {
      this.cleanup();
      throw new Error(`Failed to start stream: ${error}`);
    }
  }

  async send(message: ClientMessage): Promise<void> {
    if (!window.HybridWebView) {
      throw new Error('MAUI HybridWebView not available');
    }

    switch (message.type) {
      case 'permission_response':
        {
          const request = {
            SessionId: this.currentSessionId,
            PermissionId: message.permissionId,
            Approved: message.approved,
            Reason: message.reason,
            Choice: message.choice,
          };
          await window.HybridWebView.InvokeDotNet('RespondToPermission', [
            JSON.stringify(request),
          ]);
        }
        break;

      case 'client_tool_response':
        {
          const request = {
            SessionId: this.currentSessionId,
            RequestId: message.requestId,
            Success: message.success,
            Content: message.content,
            ErrorMessage: message.errorMessage,
          };
          await window.HybridWebView.InvokeDotNet('RespondToClientTool', [
            JSON.stringify(request),
          ]);
        }
        break;

      default:
        throw new Error(`Unsupported message type: ${(message as { type: string }).type}`);
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
    if (this.currentStreamId && window.HybridWebView) {
      try {
        window.HybridWebView.InvokeDotNet('StopStream', [this.currentStreamId]);
      } catch {
        // Ignore errors on disconnect
      }
    }
    this.cleanup();
  }

  private cleanup(): void {
    if (this.messageListener) {
      window.removeEventListener('HybridWebViewMessageReceived', this.messageListener);
      this.messageListener = undefined;
    }
    this._connected = false;
    this.currentStreamId = undefined;
    this.currentSessionId = undefined;
  }

  // Session CRUD
  async listSessions(options?: ListSessionsOptions): Promise<Session[]> {
    if (!window.HybridWebView) throw new Error('MAUI HybridWebView not available');
    const request = options ? JSON.stringify({ offset: options.offset, limit: options.limit }) : undefined;
    const json = await window.HybridWebView.InvokeDotNet<string>('SearchSessions', [request]);
    return JSON.parse(json);
  }

  async getSession(sessionId: string): Promise<Session | null> {
    if (!window.HybridWebView) throw new Error('MAUI HybridWebView not available');
    try {
      const json = await window.HybridWebView.InvokeDotNet<string>('GetSession', [sessionId]);
      return JSON.parse(json);
    } catch {
      return null;
    }
  }

  async createSession(options?: CreateSessionRequest): Promise<Session> {
    if (!window.HybridWebView) throw new Error('MAUI HybridWebView not available');
    const json = await window.HybridWebView.InvokeDotNet<string>('CreateSession', [
      options?.sessionId,
      options?.metadata ? JSON.stringify(options.metadata) : undefined,
    ]);
    return JSON.parse(json);
  }

  async updateSession(sessionId: string, request: UpdateSessionRequest): Promise<Session> {
    if (!window.HybridWebView) throw new Error('MAUI HybridWebView not available');
    const json = await window.HybridWebView.InvokeDotNet<string>('UpdateSession', [
      sessionId,
      request.metadata ? JSON.stringify(request.metadata) : undefined,
    ]);
    return JSON.parse(json);
  }

  async deleteSession(sessionId: string): Promise<void> {
    if (!window.HybridWebView) throw new Error('MAUI HybridWebView not available');
    await window.HybridWebView.InvokeDotNet('DeleteSession', [sessionId]);
  }

  // Branch CRUD
  async listBranches(sessionId: string): Promise<Branch[]> {
    if (!window.HybridWebView) throw new Error('MAUI HybridWebView not available');
    const json = await window.HybridWebView.InvokeDotNet<string>('ListBranches', [sessionId]);
    return JSON.parse(json);
  }

  async getBranch(sessionId: string, branchId: string): Promise<Branch | null> {
    if (!window.HybridWebView) throw new Error('MAUI HybridWebView not available');
    try {
      const json = await window.HybridWebView.InvokeDotNet<string>('GetBranch', [sessionId, branchId]);
      return JSON.parse(json);
    } catch {
      return null;
    }
  }

  async createBranch(sessionId: string, options?: CreateBranchRequest): Promise<Branch> {
    if (!window.HybridWebView) throw new Error('MAUI HybridWebView not available');
    const json = await window.HybridWebView.InvokeDotNet<string>('CreateBranch', [
      sessionId,
      options?.branchId,
      options?.name,
      options?.description,
    ]);
    return JSON.parse(json);
  }

  async forkBranch(sessionId: string, branchId: string, options: ForkBranchRequest): Promise<Branch> {
    if (!window.HybridWebView) throw new Error('MAUI HybridWebView not available');
    const json = await window.HybridWebView.InvokeDotNet<string>('ForkBranch', [
      sessionId,
      branchId,
      options.newBranchId,
      options.fromMessageIndex,
      options.name,
      options.description,
    ]);
    return JSON.parse(json);
  }

  async deleteBranch(sessionId: string, branchId: string, options?: { recursive?: boolean }): Promise<void> {
    if (!window.HybridWebView) throw new Error('MAUI HybridWebView not available');
    await window.HybridWebView.InvokeDotNet('DeleteBranch', [sessionId, branchId, options?.recursive ?? false]);
  }

  async getBranchMessages(sessionId: string, branchId: string): Promise<BranchMessage[]> {
    if (!window.HybridWebView) throw new Error('MAUI HybridWebView not available');
    const json = await window.HybridWebView.InvokeDotNet<string>('GetBranchMessages', [sessionId, branchId]);
    return JSON.parse(json);
  }

  // ============================================
  // SIBLING NAVIGATION (V3)
  // ============================================

  async getBranchSiblings(sessionId: string, branchId: string): Promise<SiblingBranch[]> {
    if (!window.HybridWebView) throw new Error('MAUI HybridWebView not available');
    const json = await window.HybridWebView.InvokeDotNet<string>('GetBranchSiblings', [sessionId, branchId]);
    return JSON.parse(json);
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

  // Asset CRUD (not part of AgentTransport interface)
  async uploadAsset(sessionId: string, file: File): Promise<unknown> {
    if (!window.HybridWebView) throw new Error('MAUI HybridWebView not available');

    // Convert file to base64
    const arrayBuffer = await file.arrayBuffer();
    const base64 = btoa(String.fromCharCode(...new Uint8Array(arrayBuffer)));

    const json = await window.HybridWebView.InvokeDotNet<string>('UploadAsset', [
      sessionId,
      base64,
      file.type,
      file.name,
    ]);
    return JSON.parse(json);
  }

  async listAssets(sessionId: string): Promise<unknown[]> {
    if (!window.HybridWebView) throw new Error('MAUI HybridWebView not available');
    const json = await window.HybridWebView.InvokeDotNet<string>('ListAssets', [sessionId]);
    return JSON.parse(json);
  }

  async deleteAsset(sessionId: string, assetId: string): Promise<void> {
    if (!window.HybridWebView) throw new Error('MAUI HybridWebView not available');
    await window.HybridWebView.InvokeDotNet('DeleteAsset', [sessionId, assetId]);
  }
}
