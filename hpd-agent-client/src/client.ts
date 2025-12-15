import type {
  AgentEvent,
  ReasoningPhase,
  PermissionRequestEvent,
  ClarificationRequestEvent,
  ContinuationRequestEvent,
  ClientToolInvokeRequestEvent,
  ClientToolGroupsRegisteredEvent,
  PermissionChoice,
  BranchCreatedEvent,
  BranchSwitchedEvent,
  BranchDeletedEvent,
  BranchRenamedEvent,
} from './types/events.js';
import { EventTypes } from './types/events.js';
import type { AgentTransport } from './types/transport.js';
import type {
  ClientToolGroupDefinition,
  ContextItem,
  ClientToolInvokeResponse,
} from './types/client-tools.js';
import { SseTransport } from './transports/sse.js';
import { WebSocketTransport } from './transports/websocket.js';

// ============================================
// Event Handler Interfaces
// ============================================

/**
 * Response to a permission request.
 */
export interface PermissionResponse {
  approved: boolean;
  choice?: PermissionChoice;
  reason?: string;
}

/**
 * Options for streaming.
 */
export interface StreamOptions {
  signal?: AbortSignal;
  /** Client tool groups to register */
  clientToolGroups?: ClientToolGroupDefinition[];
  /** Context items to pass to the agent */
  context?: ContextItem[];
  /** Application state (opaque to agent) */
  state?: unknown;
  /** Tool groups to start expanded */
  expandedContainers?: string[];
  /** Tools to start hidden */
  hiddenTools?: string[];
  /** Reset client state (clear all registered tool groups) */
  resetClientState?: boolean;
}

/**
 * Event handlers for the agent client.
 * All handlers are optional - only implement what you need.
 */
export interface EventHandlers {
  // ============================================
  // Content Handlers
  // ============================================

  /** Called for each text chunk */
  onTextDelta?: (text: string, messageId: string) => void;

  /** Called when a new message starts */
  onTextMessageStart?: (messageId: string, role: string) => void;

  /** Called when a message completes */
  onTextMessageEnd?: (messageId: string) => void;

  // ============================================
  // Tool Handlers
  // ============================================

  /** Called when a tool invocation starts */
  onToolCallStart?: (callId: string, name: string, messageId: string) => void;

  /** Called with tool arguments (JSON string) */
  onToolCallArgs?: (callId: string, argsJson: string) => void;

  /** Called when tool execution completes */
  onToolCallEnd?: (callId: string) => void;

  /** Called with tool result */
  onToolCallResult?: (callId: string, result: string) => void;

  // ============================================
  // Reasoning Handlers
  // ============================================

  /** Called for reasoning/thinking content */
  onReasoning?: (text: string, phase: ReasoningPhase, messageId: string) => void;

  // ============================================
  // Bidirectional Handlers (async for user interaction)
  // ============================================

  /**
   * Called when permission is required.
   * Return a PermissionResponse to approve/deny.
   */
  onPermissionRequest?: (request: PermissionRequestEvent) => Promise<PermissionResponse>;

  /**
   * Called when clarification is needed.
   * Return user's clarifying response.
   */
  onClarificationRequest?: (request: ClarificationRequestEvent) => Promise<string>;

  /**
   * Called when agent wants to continue (e.g., iteration limit).
   * Return true to continue, false to stop.
   */
  onContinuationRequest?: (request: ContinuationRequestEvent) => Promise<boolean>;

  // ============================================
  // Client Tool Handlers
  // ============================================

  /**
   * Called when a client tool needs to be invoked.
   * Implement this to execute tools in the browser/client context.
   * Return the tool result to send back to the agent.
   */
  onClientToolInvoke?: (request: ClientToolInvokeRequestEvent) => Promise<ClientToolInvokeResponse>;

  /**
   * Called when client tool groups are successfully registered.
   * Useful for debugging and UI updates.
   */
  onClientToolGroupsRegistered?: (event: ClientToolGroupsRegisteredEvent) => void;

  // ============================================
  // Lifecycle Handlers
  // ============================================

  /** Called when an agent iteration starts */
  onTurnStart?: (iteration: number) => void;

  /** Called when an agent iteration ends */
  onTurnEnd?: (iteration: number) => void;

  /** Called when the entire message turn completes */
  onComplete?: () => void;

  /** Called on error */
  onError?: (message: string) => void;

  /** Called on middleware progress */
  onProgress?: (sourceName: string, message: string, percentComplete?: number) => void;

  // ============================================
  // Branch Handlers
  // ============================================

  /** Called when a new conversation branch is created */
  onBranchCreated?: (event: BranchCreatedEvent) => void;

  /** Called when the active branch is switched */
  onBranchSwitched?: (event: BranchSwitchedEvent) => void;

  /** Called when a branch is deleted */
  onBranchDeleted?: (event: BranchDeletedEvent) => void;

  /** Called when a branch is renamed */
  onBranchRenamed?: (event: BranchRenamedEvent) => void;

  // ============================================
  // Raw Event Access
  // ============================================

  /** Called for every event (for logging, custom handling) */
  onEvent?: (event: AgentEvent) => void;
}

// ============================================
// Client Configuration
// ============================================

export type TransportType = 'sse' | 'websocket';

export interface AgentClientConfig {
  /** Base URL of the HPD-Agent API */
  baseUrl: string;

  /** Transport type (default: 'sse') */
  transport?: TransportType;

  /** Custom headers for requests (SSE only) */
  headers?: Record<string, string>;

  /** Client tool groups to register automatically on every stream */
  clientToolGroups?: ClientToolGroupDefinition[];

  /** Default context items to include on every stream */
  defaulTMetadata?: ContextItem[];

  /** Handler for client tool invocations */
  onClientToolInvoke?: (request: ClientToolInvokeRequestEvent) => Promise<ClientToolInvokeResponse>;
}

// ============================================
// Agent Client
// ============================================

/**
 * Main client for interacting with HPD-Agent.
 * Provides typed event handlers and automatic transport management.
 */
export class AgentClient {
  private config: AgentClientConfig;
  private transport: AgentTransport;

  /**
   * Create a new AgentClient.
   * @param config Configuration object or base URL string
   */
  constructor(config: AgentClientConfig | string) {
    this.config = typeof config === 'string' ? { baseUrl: config } : config;
    this.transport = this.createTransport();
  }

  private createTransport(): AgentTransport {
    const type = this.config.transport ?? 'sse';

    switch (type) {
      case 'websocket':
        return new WebSocketTransport(this.config.baseUrl);
      case 'sse':
      default:
        return new SseTransport(this.config.baseUrl);
    }
  }

  /**
   * Stream agent events with typed handlers.
   * Returns a promise that resolves on completion or rejects on error.
   *
   * @param conversationId The conversation to stream from
   * @param messages Messages to send to the agent
   * @param handlers Event handlers
   * @param options Streaming options (e.g., abort signal)
   */
  async stream(
    conversationId: string,
    messages: Array<{ content: string; role?: string }>,
    handlers: EventHandlers,
    options?: StreamOptions
  ): Promise<void> {
    return new Promise((resolve, reject) => {
      let completed = false;

      const complete = () => {
        if (!completed) {
          completed = true;
          resolve();
        }
      };

      const fail = (error: Error) => {
        if (!completed) {
          completed = true;
          reject(error);
        }
      };

      // Set up event dispatching
      this.transport.onEvent((event) => {
        // Always call raw handler first
        handlers.onEvent?.(event);

        // Dispatch to typed handlers (async but we don't await)
        this.dispatchEvent(event, handlers, complete, fail).catch(fail);
      });

      this.transport.onError((error) => {
        handlers.onError?.(error.message);
        fail(error);
      });

      this.transport.onClose(() => {
        complete();
      });

      // Merge config defaults with stream options
      const mergedToolGroups = [
        ...(this.config.clientToolGroups ?? []),
        ...(options?.clientToolGroups ?? []),
      ];
      const mergedContext = [
        ...(this.config.defaulTMetadata ?? []),
        ...(options?.context ?? []),
      ];

      // Connect
      this.transport
        .connect({
          conversationId,
          messages,
          signal: options?.signal,
          clientToolGroups: mergedToolGroups.length > 0 ? mergedToolGroups : undefined,
          context: mergedContext.length > 0 ? mergedContext : undefined,
          state: options?.state,
          expandedContainers: options?.expandedContainers,
          hiddenTools: options?.hiddenTools,
          resetClientState: options?.resetClientState,
        })
        .catch(fail);
    });
  }

  private async dispatchEvent(
    event: AgentEvent,
    handlers: EventHandlers,
    complete: () => void,
    fail: (error: Error) => void
  ): Promise<void> {
    try {
      switch (event.type) {
        // Content events
        case EventTypes.TEXT_DELTA:
          handlers.onTextDelta?.(event.text, event.messageId);
          break;

        case EventTypes.TEXT_MESSAGE_START:
          handlers.onTextMessageStart?.(event.messageId, event.role);
          break;

        case EventTypes.TEXT_MESSAGE_END:
          handlers.onTextMessageEnd?.(event.messageId);
          break;

        // Tool events
        case EventTypes.TOOL_CALL_START:
          handlers.onToolCallStart?.(event.callId, event.name, event.messageId);
          break;

        case EventTypes.TOOL_CALL_ARGS:
          handlers.onToolCallArgs?.(event.callId, event.argsJson);
          break;

        case EventTypes.TOOL_CALL_END:
          handlers.onToolCallEnd?.(event.callId);
          break;

        case EventTypes.TOOL_CALL_RESULT:
          handlers.onToolCallResult?.(event.callId, event.result);
          break;

        // Reasoning
        case EventTypes.REASONING:
          if (event.text) {
            handlers.onReasoning?.(event.text, event.phase, event.messageId);
          }
          break;

        // Bidirectional - Permission
        case EventTypes.PERMISSION_REQUEST:
          if (handlers.onPermissionRequest) {
            const response = await handlers.onPermissionRequest(event);
            await this.transport.send({
              type: 'permission_response',
              permissionId: event.permissionId,
              approved: response.approved,
              choice: response.choice,
              reason: response.reason,
            });
          }
          break;

        // Bidirectional - Clarification
        case EventTypes.CLARIFICATION_REQUEST:
          if (handlers.onClarificationRequest) {
            const answer = await handlers.onClarificationRequest(event);
            await this.transport.send({
              type: 'clarification_response',
              clarificationId: event.requestId,
              response: answer,
            });
          }
          break;

        // Bidirectional - Continuation
        case EventTypes.CONTINUATION_REQUEST:
          if (handlers.onContinuationRequest) {
            const shouldContinue = await handlers.onContinuationRequest(event);
            await this.transport.send({
              type: 'continuation_response',
              continuationId: event.continuationId,
              shouldContinue,
            });
          }
          break;

        // Bidirectional - Client Tool Invocation
        case EventTypes.CLIENT_TOOL_INVOKE_REQUEST:
          // Use handler from stream() or fall back to config handler
          const toolHandler = handlers.onClientToolInvoke ?? this.config.onClientToolInvoke;
          if (toolHandler) {
            const toolResponse = await toolHandler(event);
            await this.transport.send({
              type: 'client_tool_response',
              requestId: toolResponse.requestId,
              content: toolResponse.content,
              success: toolResponse.success,
              errorMessage: toolResponse.errorMessage,
              augmentation: toolResponse.augmentation,
            });
          }
          break;

        // Client Tool Groups Registered (informational)
        case EventTypes.CLIENT_TOOL_GROUPS_REGISTERED:
          handlers.onClientToolGroupsRegistered?.(event);
          break;

        // Lifecycle
        case EventTypes.AGENT_TURN_STARTED:
          handlers.onTurnStart?.(event.iteration);
          break;

        case EventTypes.AGENT_TURN_FINISHED:
          handlers.onTurnEnd?.(event.iteration);
          break;

        // Middleware progress
        case EventTypes.MIDDLEWARE_PROGRESS:
          handlers.onProgress?.(event.sourceName, event.message, event.percentComplete);
          break;

        // Terminal events
        case EventTypes.MESSAGE_TURN_FINISHED:
          handlers.onComplete?.();
          complete();
          break;

        case EventTypes.MESSAGE_TURN_ERROR:
          handlers.onError?.(event.message);
          fail(new Error(event.message));
          break;

        // Branch events
        case EventTypes.BRANCH_CREATED:
          handlers.onBranchCreated?.(event);
          break;

        case EventTypes.BRANCH_SWITCHED:
          handlers.onBranchSwitched?.(event);
          break;

        case EventTypes.BRANCH_DELETED:
          handlers.onBranchDeleted?.(event);
          break;

        case EventTypes.BRANCH_RENAMED:
          handlers.onBranchRenamed?.(event);
          break;
      }
    } catch (error) {
      fail(error as Error);
    }
  }

  /**
   * Abort the current stream.
   */
  abort(): void {
    this.transport.disconnect();
  }

  /**
   * Check if currently streaming.
   */
  get streaming(): boolean {
    return this.transport.connected;
  }

  // ============================================
  // Client Tool Group Management
  // ============================================

  /**
   * Register a client tool group. It will be automatically included in all future streams.
   */
  registerToolGroup(toolGroup: ClientToolGroupDefinition): void {
    if (!this.config.clientToolGroups) {
      this.config.clientToolGroups = [];
    }
    // Remove existing tool group with same name (update)
    this.config.clientToolGroups = this.config.clientToolGroups.filter(g => g.name !== toolGroup.name);
    this.config.clientToolGroups.push(toolGroup);
  }

  /**
   * Register multiple client tool groups.
   */
  registerToolGroups(toolGroups: ClientToolGroupDefinition[]): void {
    toolGroups.forEach(g => this.registerToolGroup(g));
  }

  /**
   * Unregister a client tool group by name.
   */
  unregisterToolGroup(toolGroupName: string): void {
    if (this.config.clientToolGroups) {
      this.config.clientToolGroups = this.config.clientToolGroups.filter(g => g.name !== toolGroupName);
    }
  }

  /**
   * Get all registered tool groups.
   */
  get toolGroups(): ClientToolGroupDefinition[] {
    return this.config.clientToolGroups ?? [];
  }

  /**
   * Set the handler for client tool invocations.
   */
  setToolHandler(handler: (request: ClientToolInvokeRequestEvent) => Promise<ClientToolInvokeResponse>): void {
    this.config.onClientToolInvoke = handler;
  }
}
