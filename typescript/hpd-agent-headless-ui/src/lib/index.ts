/**
 * HPD Agent Headless UI - Main Entry Point
 *
 * Phase 2: Core AI components
 */

// Agent state and utilities (includes Message type)
export * from './agent/index.ts';

// Workspace (V3 - Unified session/branch/streaming factory)
export { createWorkspace } from './workspace/index.ts';
export type { Workspace, CreateWorkspaceOptions } from './workspace/index.ts';

// Re-exports from hpd-agent-client — users only need to import from this package
export type {
	Session,
	Branch,
	BranchMessage,
	CreateSessionRequest,
	CreateBranchRequest,
	clientToolKitDefinition,
	ClientToolDefinition,
	ClientSkillDefinition,
	ClientToolInvokeResponse,
	ClientToolInvokeRequestEvent,
	PermissionChoice,
	PermissionResponse,
} from '@hpd/hpd-agent-client';
export {
	createSuccessResponse,
	createErrorResponse,
	createExpandedToolKit,
} from '@hpd/hpd-agent-client';

// BranchSwitcher component (V3 - Sibling navigation UI)
export * as BranchSwitcher from './branch-switcher/index.ts';
export {
	BranchSwitcherRootState,
	BranchSwitcherPrevState,
	BranchSwitcherNextState,
	BranchSwitcherPositionState,
	branchSwitcherAttrs,
} from './branch-switcher/index.ts';

// SessionList component (V3 - Session management UI)
export * as SessionList from './session-list/index.ts';
export {
	SessionListRootState,
	SessionListItemState,
	SessionListEmptyState,
	SessionListCreateButtonState,
	sessionListAttrs,
} from './session-list/index.ts';
export type {
	SessionListRootProps,
	SessionListItemProps,
	SessionListEmptyProps,
	SessionListCreateButtonProps,
	SessionListRootSnippetProps,
	SessionListItemSnippetProps,
} from './session-list/index.ts';

// Message component (explicit exports to avoid conflicts)
export { Message, MessageState, createMessageState } from './message/index.ts';
export type { MessageProps, MessageHTMLProps, MessageSnippetProps } from './message/index.ts';

// MessageActions component (edit + retry buttons)
export * as MessageActions from './message-actions/index.ts';
export {
	MessageActionsRootState,
	MessageActionsEditButtonState,
	MessageActionsRetryButtonState,
	MessageActionsCopyButtonState,
	MessageActionsPrevState,
	MessageActionsNextState,
	MessageActionsPositionState,
	messageActionsAttrs,
} from './message-actions/index.ts';
export type {
	MessageActionsRootProps,
	MessageActionsEditButtonProps,
	MessageActionsRetryButtonProps,
	MessageActionsCopyButtonProps,
	MessageActionsPrevProps,
	MessageActionsNextProps,
	MessageActionsPositionProps,
	MessageActionsRootSnippetProps,
	MessageActionsEditButtonSnippetProps,
	MessageActionsRetryButtonSnippetProps,
	MessageActionsCopyButtonSnippetProps,
	MessageActionsPositionSnippetProps,
	MessageActionStatus,
} from './message-actions/index.ts';

// MessageEdit component (inline message editing)
export * as MessageEdit from './message-edit/index.ts';
export {
	MessageEditRootState,
	MessageEditTextareaState,
	MessageEditSaveButtonState,
	MessageEditCancelButtonState,
	messageEditAttrs,
} from './message-edit/index.ts';
export type {
	MessageEditRootProps,
	MessageEditRootHTMLProps,
	MessageEditRootSnippetProps,
	MessageEditTextareaProps,
	MessageEditTextareaSnippetProps,
	MessageEditSaveButtonProps,
	MessageEditSaveButtonSnippetProps,
	MessageEditCancelButtonProps,
	MessageEditCancelButtonSnippetProps,
} from './message-edit/index.ts';

// MessageList component
export * as MessageList from './message-list/index.ts';
export { MessageListState } from './message-list/index.ts';
export type { MessageListProps, MessageListSnippetProps } from './message-list/index.ts';

// Input component
export * as Input from './input/index.ts';

// ChatInput component (compositional input with accessories)
export * as ChatInput from './chat-input/index.ts';
export { ChatInputRootState } from './chat-input/index.ts';
export type {ChatInputRootProps, ChatInputInputProps, ChatInputLeadingProps, ChatInputTrailingProps,
	ChatInputTopProps, ChatInputBottomProps
} from './chat-input/index.ts';

// ToolExecution component
export { ToolExecution } from './tool-execution/index.ts';

// PermissionDialog component
export * as PermissionDialog from './permission-dialog/index.ts';

// Audio components (Phase 3A)
export * as AudioPlaybackGate from './audio-playback-gate/index.ts';
export * as AudioPlayer from './audio-player/index.ts';
export * as Transcription from './transcription/index.ts';
export * as VoiceActivityIndicator from './voice-activity-indicator/index.ts';

// Audio components (Phase 3B)
export * as InterruptionIndicator from './interruption-indicator/index.ts';
export * as TurnIndicator from './turn-indicator/index.ts';
export * as AudioVisualizer from './audio-visualizer/index.ts';

// Testing utilities (mock workspace)
export { createMockWorkspace } from './testing/mock-agent.svelte.ts';
export type { MockWorkspaceOptions } from './testing/mock-agent.svelte.ts';

// ========================================
// Storage System
// ========================================
export * from './storage/index.ts';

// ========================================
// SplitPanel Component
// ========================================
export * as SplitPanel from './split-panel/index.ts';

// ========================================
// Artifact Component
// ========================================
export * as Artifact from './artifact/index.ts';
export { ArtifactProviderState, ArtifactRootState, ArtifactPanelState } from './artifact/index.ts';
export type {
	ArtifactProviderProps,
	ArtifactRootProps,
	ArtifactSlotProps,
	ArtifactTriggerProps,
	ArtifactPanelProps,
	ArtifactTitleProps,
	ArtifactContentProps,
	ArtifactCloseProps,
	ArtifactPanelSnippetProps,
	ArtifactRootSnippetProps
} from './artifact/index.ts';

// ========================================
// Utilities (for extending the library)
// ========================================

// Data attributes and styling
export {
	createHPDAttrs,
	boolToStr,
	boolToStrTrueOrUndef,
	boolToEmptyStrOrUndef,
	boolToTrueOrUndef,
	getDataOpenClosed,
	getDataChecked,
	getAriaChecked
} from './internal/attrs.ts';
export type { CreateHPDAttrsReturn } from './internal/attrs.ts';

// Keyboard constants
export { kbd } from './internal/kbd.ts';
export type { KbdKey } from './internal/kbd.ts';

// Common utilities
export { noop } from './internal/noop.ts';
export { createId } from './internal/create-id.ts';
export { debounce } from './internal/debounce.ts';

// Type utilities
export type {
	WithChild,
	Without,
	OnChangeFn,
	HPDKeyboardEvent,
	HPDMouseEvent,
	WithRefOpts,
	RefAttachment
} from './internal/types.ts';

// Focus management
export { RovingFocusGroup } from './internal/roving-focus-group.ts';
export {
	focus,
	focusFirst,
	focusWithoutScroll,
	getTabbableCandidates,
	getTabbableEdges,
	findVisible,
	handleCalendarInitialFocus
} from './internal/focus.ts';
export type { FocusableTarget } from './internal/focus.ts';

export { getTabbableFrom, getTabbableFromFocusable, isTabbable, isFocusable, tabbable, focusable } from './internal/tabbable.ts';

// Resize observer
export { HPDResizeObserver } from './internal/svelte-resize-observer.svelte.ts';

// Animation utilities
export { PresenceManager } from './internal/presence-manager.svelte.ts';
export { AnimationsComplete } from './internal/animations-complete.ts';

// DOM utilities
export { getFirstNonCommentChild, isClickTrulyOutside } from './internal/dom.ts';

// Event utilities
export { CustomEventDispatcher } from './internal/events.ts';
export type { EventCallback } from './internal/events.ts';

// Locale and direction
export { getElemDirection } from './internal/locale.ts';
export type { Direction } from './internal/locale.ts';

// Directional keys
export {
	getNextKey,
	getPrevKey,
	getDirectionalKeys,
	FIRST_KEYS,
	LAST_KEYS,
	FIRST_LAST_KEYS,
	SELECTION_KEYS
} from './internal/get-directional-keys.ts';
export type { Orientation } from './internal/get-directional-keys.ts';

// Type checking utilities
export {
	isBrowser,
	isIOS,
	isFunction,
	isHTMLElement,
	isElement,
	isElementOrSVGElement,
	isNumberString,
	isNull,
	isTouch,
	isFocusVisible,
	isNotNull,
	isSelectableInput,
	isElementHidden
} from './internal/is.ts';
