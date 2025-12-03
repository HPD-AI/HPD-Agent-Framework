import type { AgentEvent, PermissionChoice } from './events.js';
import type {
  FrontendPluginDefinition,
  ContextItem,
  ToolResultContent,
  FrontendToolAugmentation,
} from './frontend-tools.js';

/**
 * Options for connecting to an agent stream.
 */
export interface ConnectOptions {
  /** Conversation ID to stream from */
  conversationId: string;
  /** Messages to send to the agent */
  messages: Array<{ content: string; role?: string }>;
  /** Optional AbortSignal for cancellation */
  signal?: AbortSignal;
  /** Frontend plugins to register */
  frontendPlugins?: FrontendPluginDefinition[];
  /** Context items to pass to the agent */
  context?: ContextItem[];
  /** Application state (opaque to agent) */
  state?: unknown;
  /** Plugins to start expanded */
  expandedContainers?: string[];
  /** Tools to start hidden */
  hiddenTools?: string[];
  /** Reset frontend state (clear all registered plugins) */
  resetFrontendState?: boolean;
}

/**
 * Messages sent from client to server (bidirectional communication).
 */
export type ClientMessage =
  | PermissionResponseMessage
  | ClarificationResponseMessage
  | ContinuationResponseMessage
  | FrontendToolResponseMessage;

export interface PermissionResponseMessage {
  type: 'permission_response';
  permissionId: string;
  approved: boolean;
  choice?: PermissionChoice;
  reason?: string;
}

export interface ClarificationResponseMessage {
  type: 'clarification_response';
  clarificationId: string;
  response: string;
}

export interface ContinuationResponseMessage {
  type: 'continuation_response';
  continuationId: string;
  shouldContinue: boolean;
}

export interface FrontendToolResponseMessage {
  type: 'frontend_tool_response';
  requestId: string;
  content: ToolResultContent[];
  success: boolean;
  errorMessage?: string;
  augmentation?: FrontendToolAugmentation;
}

/**
 * Abstract transport interface.
 * Implementations handle the specifics of SSE, WebSocket, etc.
 */
export interface AgentTransport {
  /** Connect and start streaming */
  connect(options: ConnectOptions): Promise<void>;

  /** Send a message (for bidirectional transports) */
  send(message: ClientMessage): Promise<void>;

  /** Register event handler */
  onEvent(handler: (event: AgentEvent) => void): void;

  /** Register error handler */
  onError(handler: (error: Error) => void): void;

  /** Register close handler */
  onClose(handler: () => void): void;

  /** Disconnect */
  disconnect(): void;

  /** Connection state */
  readonly connected: boolean;
}
