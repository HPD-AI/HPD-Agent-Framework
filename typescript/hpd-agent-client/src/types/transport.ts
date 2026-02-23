import type { AgentEvent, PermissionChoice } from './events.js';
import type {
  clientToolKitDefinition,
  ContextItem,
  ToolResultContent,
  ClientToolAugmentation,
} from './client-tools.js';
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
} from './session.js';

/**
 * Options for connecting to an agent stream.
 * V3 API uses session + branch instead of conversation.
 */
export interface ConnectOptions {
  /** Session ID to stream from */
  sessionId: string;
  /** Branch ID to stream from (default: 'main') */
  branchId?: string;
  /** Messages to send to the agent */
  messages: Array<{ content: string; role?: string }>;
  /** Optional AbortSignal for cancellation */
  signal?: AbortSignal;
  /** Client tool groups to register */
  clientToolKits?: clientToolKitDefinition[];
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
  /** Run configuration (temperature, model overrides, etc.) */
  runConfig?: unknown;
}

/**
 * Messages sent from client to server (bidirectional communication).
 */
export type ClientMessage =
  | PermissionResponseMessage
  | ClarificationResponseMessage
  | ContinuationResponseMessage
  | ClientToolResponseMessage;

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

export interface ClientToolResponseMessage {
  type: 'client_tool_response';
  requestId: string;
  content: ToolResultContent[];
  success: boolean;
  errorMessage?: string;
  augmentation?: ClientToolAugmentation;
}

/**
 * Abstract transport interface.
 * Implementations handle the specifics of SSE, WebSocket, etc.
 */
export interface AgentTransport {
  // ============================================
  // STREAMING (existing)
  // ============================================

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

  // ============================================
  // SESSION CRUD (V3)
  // ============================================

  /** List all sessions */
  listSessions(options?: ListSessionsOptions): Promise<Session[]>;

  /** Get a session by ID */
  getSession(sessionId: string): Promise<Session | null>;

  /** Create a new session */
  createSession(options?: CreateSessionRequest): Promise<Session>;

  /** Update session metadata */
  updateSession(sessionId: string, request: UpdateSessionRequest): Promise<Session>;

  /** Delete a session and all its branches */
  deleteSession(sessionId: string): Promise<void>;

  // ============================================
  // BRANCH CRUD (V3)
  // ============================================

  /** List all branches in a session */
  listBranches(sessionId: string): Promise<Branch[]>;

  /** Get a branch by ID */
  getBranch(sessionId: string, branchId: string): Promise<Branch | null>;

  /** Create a new branch in a session */
  createBranch(sessionId: string, options?: CreateBranchRequest): Promise<Branch>;

  /** Fork a branch at a specific message index */
  forkBranch(sessionId: string, branchId: string, options: ForkBranchRequest): Promise<Branch>;

  /** Delete a branch. Pass recursive: true to delete the entire subtree (must be enabled server-side via AllowRecursiveBranchDelete). */
  deleteBranch(sessionId: string, branchId: string, options?: { recursive?: boolean }): Promise<void>;

  /** Get all messages in a branch */
  getBranchMessages(sessionId: string, branchId: string): Promise<BranchMessage[]>;

  // ============================================
  // SIBLING NAVIGATION (V3)
  // ============================================

  /** Get sibling branches (ordered by siblingIndex) */
  getBranchSiblings(sessionId: string, branchId: string): Promise<SiblingBranch[]>;

  /** Get the next sibling branch (null if last) */
  getNextSibling(sessionId: string, branchId: string): Promise<Branch | null>;

  /** Get the previous sibling branch (null if first) */
  getPreviousSibling(sessionId: string, branchId: string): Promise<Branch | null>;
}
