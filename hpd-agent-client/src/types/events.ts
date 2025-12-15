/**
 * Event type constants matching C# EventTypes.cs
 * Uses SCREAMING_SNAKE_CASE for JSON discriminators
 */
export const EventTypes = {
  // Message Turn Lifecycle
  MESSAGE_TURN_STARTED: 'MESSAGE_TURN_STARTED',
  MESSAGE_TURN_FINISHED: 'MESSAGE_TURN_FINISHED',
  MESSAGE_TURN_ERROR: 'MESSAGE_TURN_ERROR',

  // Agent Turn (iteration within a message turn)
  AGENT_TURN_STARTED: 'AGENT_TURN_STARTED',
  AGENT_TURN_FINISHED: 'AGENT_TURN_FINISHED',
  STATE_SNAPSHOT: 'STATE_SNAPSHOT',

  // Content Streaming
  TEXT_MESSAGE_START: 'TEXT_MESSAGE_START',
  TEXT_DELTA: 'TEXT_DELTA',
  TEXT_MESSAGE_END: 'TEXT_MESSAGE_END',

  // Reasoning (extended thinking)
  REASONING: 'REASONING',

  // Tool Execution
  TOOL_CALL_START: 'TOOL_CALL_START',
  TOOL_CALL_ARGS: 'TOOL_CALL_ARGS',
  TOOL_CALL_END: 'TOOL_CALL_END',
  TOOL_CALL_RESULT: 'TOOL_CALL_RESULT',

  // Permissions (bidirectional)
  PERMISSION_REQUEST: 'PERMISSION_REQUEST',
  PERMISSION_RESPONSE: 'PERMISSION_RESPONSE',
  PERMISSION_APPROVED: 'PERMISSION_APPROVED',
  PERMISSION_DENIED: 'PERMISSION_DENIED',

  // Continuation (for long-running tasks)
  CONTINUATION_REQUEST: 'CONTINUATION_REQUEST',
  CONTINUATION_RESPONSE: 'CONTINUATION_RESPONSE',

  // Clarification (bidirectional)
  CLARIFICATION_REQUEST: 'CLARIFICATION_REQUEST',
  CLARIFICATION_RESPONSE: 'CLARIFICATION_RESPONSE',

  // Middleware
  MIDDLEWARE_PROGRESS: 'MIDDLEWARE_PROGRESS',
  MIDDLEWARE_ERROR: 'MIDDLEWARE_ERROR',

  // Frontend Tools (bidirectional)
  FRONTEND_TOOL_INVOKE_REQUEST: 'FRONTEND_TOOL_INVOKE_REQUEST',
  FRONTEND_TOOL_INVOKE_RESPONSE: 'FRONTEND_TOOL_INVOKE_RESPONSE',
  FRONTEND_PLUGINS_REGISTERED: 'FRONTEND_PLUGINS_REGISTERED',

  // Observability (optional, for debugging)
  COLLAPSED_TOOLS_VISIBLE: 'COLLAPSED_TOOLS_VISIBLE',
  CONTAINER_EXPANDED: 'CONTAINER_EXPANDED',
  MIDDLEWARE_PIPELINE_START: 'MIDDLEWARE_PIPELINE_START',
  MIDDLEWARE_PIPELINE_END: 'MIDDLEWARE_PIPELINE_END',
  PERMISSION_CHECK: 'PERMISSION_CHECK',
  ITERATION_START: 'ITERATION_START',
  CIRCUIT_BREAKER_TRIGGERED: 'CIRCUIT_BREAKER_TRIGGERED',
  HISTORY_REDUCTION_CACHE: 'HISTORY_REDUCTION_CACHE',
  CHECKPOINT: 'CHECKPOINT',
  INTERNAL_PARALLEL_TOOL_EXECUTION: 'INTERNAL_PARALLEL_TOOL_EXECUTION',
  INTERNAL_RETRY: 'INTERNAL_RETRY',
  FUNCTION_RETRY: 'FUNCTION_RETRY',
  DELTA_SENDING_ACTIVATED: 'DELTA_SENDING_ACTIVATED',
  PLAN_MODE_ACTIVATED: 'PLAN_MODE_ACTIVATED',
  NESTED_AGENT_INVOKED: 'NESTED_AGENT_INVOKED',
  DOCUMENT_PROCESSED: 'DOCUMENT_PROCESSED',
  INTERNAL_MESSAGE_PREPARED: 'INTERNAL_MESSAGE_PREPARED',
  BIDIRECTIONAL_EVENT_PROCESSED: 'BIDIRECTIONAL_EVENT_PROCESSED',
  AGENT_DECISION: 'AGENT_DECISION',
  AGENT_COMPLETION: 'AGENT_COMPLETION',
  ITERATION_MESSAGES: 'ITERATION_MESSAGES',
  SCHEMA_CHANGED: 'SCHEMA_CHANGED',
  COLLAPSING_STATE: 'COLLAPSING_STATE',

  // Branch Events
  BRANCH_CREATED: 'BRANCH_CREATED',
  BRANCH_SWITCHED: 'BRANCH_SWITCHED',
  BRANCH_DELETED: 'BRANCH_DELETED',
  BRANCH_RENAMED: 'BRANCH_RENAMED',
} as const;

export type EventType = (typeof EventTypes)[keyof typeof EventTypes];

// ============================================
// Execution Context
// ============================================

export interface AgentExecutionContext {
  agentName: string;
  agentId: string;
  parentAgentId?: string;
  agentChain: string[];
  depth: number;
  isSubAgent: boolean;
}

// ============================================
// Base Event
// ============================================

export interface BaseEvent {
  version: string;
  type: string;
  executionContext?: AgentExecutionContext;
}

// ============================================
// Message Turn Events
// ============================================

export interface MessageTurnStartedEvent extends BaseEvent {
  type: typeof EventTypes.MESSAGE_TURN_STARTED;
  messageTurnId: string;
  conversationId: string;
  agentName: string;
  timestamp: string;
}

export interface MessageTurnFinishedEvent extends BaseEvent {
  type: typeof EventTypes.MESSAGE_TURN_FINISHED;
  messageTurnId: string;
  conversationId: string;
  agentName: string;
  duration: string;
  timestamp: string;
}

export interface MessageTurnErrorEvent extends BaseEvent {
  type: typeof EventTypes.MESSAGE_TURN_ERROR;
  message: string;
}

// ============================================
// Agent Turn Events
// ============================================

export interface AgentTurnStartedEvent extends BaseEvent {
  type: typeof EventTypes.AGENT_TURN_STARTED;
  iteration: number;
}

export interface AgentTurnFinishedEvent extends BaseEvent {
  type: typeof EventTypes.AGENT_TURN_FINISHED;
  iteration: number;
}

export interface StateSnapshotEvent extends BaseEvent {
  type: typeof EventTypes.STATE_SNAPSHOT;
  currentIteration: number;
  maxIterations: number;
  isTerminated: boolean;
  terminationReason?: string;
  consecutiveErrorCount: number;
  completedFunctions: string[];
  agentName: string;
  timestamp: string;
}

// ============================================
// Content Events
// ============================================

export interface TextMessageStartEvent extends BaseEvent {
  type: typeof EventTypes.TEXT_MESSAGE_START;
  messageId: string;
  role: string;
}

export interface TextDeltaEvent extends BaseEvent {
  type: typeof EventTypes.TEXT_DELTA;
  text: string;
  messageId: string;
}

export interface TextMessageEndEvent extends BaseEvent {
  type: typeof EventTypes.TEXT_MESSAGE_END;
  messageId: string;
}

// ============================================
// Reasoning Events
// ============================================

export type ReasoningPhase =
  | 'SessionStart'
  | 'MessageStart'
  | 'Delta'
  | 'MessageEnd'
  | 'SessionEnd';

export interface ReasoningEvent extends BaseEvent {
  type: typeof EventTypes.REASONING;
  phase: ReasoningPhase;
  messageId: string;
  role?: string;
  text?: string;
}

// ============================================
// Tool Events
// ============================================

export interface ToolCallStartEvent extends BaseEvent {
  type: typeof EventTypes.TOOL_CALL_START;
  callId: string;
  name: string;
  messageId: string;
}

export interface ToolCallArgsEvent extends BaseEvent {
  type: typeof EventTypes.TOOL_CALL_ARGS;
  callId: string;
  argsJson: string;
}

export interface ToolCallEndEvent extends BaseEvent {
  type: typeof EventTypes.TOOL_CALL_END;
  callId: string;
}

export interface ToolCallResultEvent extends BaseEvent {
  type: typeof EventTypes.TOOL_CALL_RESULT;
  callId: string;
  result: string;
}

// ============================================
// Permission Events (Bidirectional)
// ============================================

export type PermissionChoice = 'ask' | 'allow_always' | 'deny_always';

export interface PermissionRequestEvent extends BaseEvent {
  type: typeof EventTypes.PERMISSION_REQUEST;
  permissionId: string;
  sourceName: string;
  functionName: string;
  description?: string;
  callId: string;
  arguments?: Record<string, unknown>;
}

export interface PermissionResponseEvent extends BaseEvent {
  type: typeof EventTypes.PERMISSION_RESPONSE;
  permissionId: string;
  sourceName: string;
  approved: boolean;
  reason?: string;
  choice?: PermissionChoice;
}

export interface PermissionApprovedEvent extends BaseEvent {
  type: typeof EventTypes.PERMISSION_APPROVED;
  permissionId: string;
  sourceName: string;
}

export interface PermissionDeniedEvent extends BaseEvent {
  type: typeof EventTypes.PERMISSION_DENIED;
  permissionId: string;
  sourceName: string;
  reason: string;
}

// ============================================
// Continuation Events (Bidirectional)
// ============================================

export interface ContinuationRequestEvent extends BaseEvent {
  type: typeof EventTypes.CONTINUATION_REQUEST;
  continuationId: string;
  sourceName: string;
  currentIteration: number;
  maxIterations: number;
}

export interface ContinuationResponseEvent extends BaseEvent {
  type: typeof EventTypes.CONTINUATION_RESPONSE;
  continuationId: string;
  sourceName: string;
  approved: boolean;
  extensionAmount?: number;
}

// ============================================
// Clarification Events (Bidirectional)
// ============================================

export interface ClarificationRequestEvent extends BaseEvent {
  type: typeof EventTypes.CLARIFICATION_REQUEST;
  requestId: string;
  sourceName: string;
  question: string;
  agentName?: string;
  options?: string[];
}

export interface ClarificationResponseEvent extends BaseEvent {
  type: typeof EventTypes.CLARIFICATION_RESPONSE;
  requestId: string;
  sourceName: string;
  question: string;
  answer: string;
}

// ============================================
// Middleware Events
// ============================================

export interface MiddlewareProgressEvent extends BaseEvent {
  type: typeof EventTypes.MIDDLEWARE_PROGRESS;
  sourceName: string;
  message: string;
  percentComplete?: number;
}

export interface MiddlewareErrorEvent extends BaseEvent {
  type: typeof EventTypes.MIDDLEWARE_ERROR;
  sourceName: string;
  errorMessage: string;
}

// ============================================
// Frontend Tool Events (Bidirectional)
// ============================================

export interface FrontendToolInvokeRequestEvent extends BaseEvent {
  type: typeof EventTypes.FRONTEND_TOOL_INVOKE_REQUEST;
  requestId: string;
  toolName: string;
  callId: string;
  arguments: Record<string, unknown>;
  description?: string;
}

export interface FrontendToolInvokeResponseEvent extends BaseEvent {
  type: typeof EventTypes.FRONTEND_TOOL_INVOKE_RESPONSE;
  requestId: string;
  content: Array<{ type: string; [key: string]: unknown }>;
  success: boolean;
  errorMessage?: string;
  augmentation?: Record<string, unknown>;
}

export interface FrontendPluginsRegisteredEvent extends BaseEvent {
  type: typeof EventTypes.FRONTEND_PLUGINS_REGISTERED;
  registeredPlugins: string[];
  totalTools: number;
  timestamp: string;
}

// ============================================
// Branch Events
// ============================================

export interface BranchCreatedEvent extends BaseEvent {
  type: typeof EventTypes.BRANCH_CREATED;
  threadId: string;
  branchName: string;
  checkpointId: string;
  parentCheckpointId: string;
  forkMessageIndex: number;
  createdAt: string;
}

export interface BranchSwitchedEvent extends BaseEvent {
  type: typeof EventTypes.BRANCH_SWITCHED;
  threadId: string;
  previousBranch?: string;
  newBranch?: string;
  checkpointId: string;
  switchedAt: string;
}

export interface BranchDeletedEvent extends BaseEvent {
  type: typeof EventTypes.BRANCH_DELETED;
  threadId: string;
  branchName: string;
  checkpointsPruned: number;
  deletedAt: string;
}

export interface BranchRenamedEvent extends BaseEvent {
  type: typeof EventTypes.BRANCH_RENAMED;
  threadId: string;
  oldName: string;
  newName: string;
  renamedAt: string;
}

// ============================================
// Union Type (Core Events)
// ============================================

/**
 * Union of all core agent events that clients typically handle.
 * Does not include observability events (which are for debugging).
 */
export type AgentEvent =
  // Message Turn Events
  | MessageTurnStartedEvent
  | MessageTurnFinishedEvent
  | MessageTurnErrorEvent
  // Agent Turn Events
  | AgentTurnStartedEvent
  | AgentTurnFinishedEvent
  | StateSnapshotEvent
  // Content Events
  | TextMessageStartEvent
  | TextDeltaEvent
  | TextMessageEndEvent
  // Reasoning Events
  | ReasoningEvent
  // Tool Events
  | ToolCallStartEvent
  | ToolCallArgsEvent
  | ToolCallEndEvent
  | ToolCallResultEvent
  // Permission Events
  | PermissionRequestEvent
  | PermissionResponseEvent
  | PermissionApprovedEvent
  | PermissionDeniedEvent
  // Continuation Events
  | ContinuationRequestEvent
  | ContinuationResponseEvent
  // Clarification Events
  | ClarificationRequestEvent
  | ClarificationResponseEvent
  // Middleware Events
  | MiddlewareProgressEvent
  | MiddlewareErrorEvent
  // Frontend Tool Events
  | FrontendToolInvokeRequestEvent
  | FrontendToolInvokeResponseEvent
  | FrontendPluginsRegisteredEvent
  // Branch Events
  | BranchCreatedEvent
  | BranchSwitchedEvent
  | BranchDeletedEvent
  | BranchRenamedEvent;

// ============================================
// Type Guards
// ============================================

export function isTextDeltaEvent(event: BaseEvent): event is TextDeltaEvent {
  return event.type === EventTypes.TEXT_DELTA;
}

export function isToolCallStartEvent(event: BaseEvent): event is ToolCallStartEvent {
  return event.type === EventTypes.TOOL_CALL_START;
}

export function isPermissionRequestEvent(event: BaseEvent): event is PermissionRequestEvent {
  return event.type === EventTypes.PERMISSION_REQUEST;
}

export function isReasoningEvent(event: BaseEvent): event is ReasoningEvent {
  return event.type === EventTypes.REASONING;
}

export function isMessageTurnFinishedEvent(event: BaseEvent): event is MessageTurnFinishedEvent {
  return event.type === EventTypes.MESSAGE_TURN_FINISHED;
}

export function isMessageTurnErrorEvent(event: BaseEvent): event is MessageTurnErrorEvent {
  return event.type === EventTypes.MESSAGE_TURN_ERROR;
}

export function isClarificationRequestEvent(event: BaseEvent): event is ClarificationRequestEvent {
  return event.type === EventTypes.CLARIFICATION_REQUEST;
}

export function isContinuationRequestEvent(event: BaseEvent): event is ContinuationRequestEvent {
  return event.type === EventTypes.CONTINUATION_REQUEST;
}

export function isFrontendToolInvokeRequestEvent(
  event: BaseEvent
): event is FrontendToolInvokeRequestEvent {
  return event.type === EventTypes.FRONTEND_TOOL_INVOKE_REQUEST;
}

export function isFrontendPluginsRegisteredEvent(
  event: BaseEvent
): event is FrontendPluginsRegisteredEvent {
  return event.type === EventTypes.FRONTEND_PLUGINS_REGISTERED;
}

export function isBranchCreatedEvent(event: BaseEvent): event is BranchCreatedEvent {
  return event.type === EventTypes.BRANCH_CREATED;
}

export function isBranchSwitchedEvent(event: BaseEvent): event is BranchSwitchedEvent {
  return event.type === EventTypes.BRANCH_SWITCHED;
}

export function isBranchDeletedEvent(event: BaseEvent): event is BranchDeletedEvent {
  return event.type === EventTypes.BRANCH_DELETED;
}

export function isBranchRenamedEvent(event: BaseEvent): event is BranchRenamedEvent {
  return event.type === EventTypes.BRANCH_RENAMED;
}
