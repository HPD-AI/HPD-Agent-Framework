/**
 * HPD Agent Headless UI - Main Entry Point
 *
 * Phase 2: Core AI components
 */

// Agent state and utilities (includes Message type)
export * from './agent/index.js';

// Message component (explicit exports to avoid conflicts)
export { Message, MessageState, createMessageState } from './message/index.js';
export type { MessageProps, MessageHTMLProps, MessageSnippetProps } from './message/index.js';

// MessageList component
export * as MessageList from './message-list/index.js';
export { MessageListState } from './message-list/index.js';
export type { MessageListProps, MessageListSnippetProps } from './message-list/index.js';

// Input component
export * as Input from './input/index.js';

// ChatInput component (compositional input with accessories)
export * as ChatInput from './chat-input/index.js';
export { ChatInputRootState } from './chat-input/index.js';
export type {ChatInputRootProps, ChatInputInputProps, ChatInputLeadingProps, ChatInputTrailingProps,
	ChatInputTopProps, ChatInputBottomProps
} from './chat-input/index.js';

// ToolExecution component
export { ToolExecution } from './tool-execution/index.js';

// PermissionDialog component
export * as PermissionDialog from './permission-dialog/index.js';

// Audio components (Phase 3A)
export * as AudioPlaybackGate from './audio-playback-gate/index.js';
export * as AudioPlayer from './audio-player/index.js';
export * as Transcription from './transcription/index.js';
export * as VoiceActivityIndicator from './voice-activity-indicator/index.js';

// Audio components (Phase 3B)
export * as InterruptionIndicator from './interruption-indicator/index.js';
export * as TurnIndicator from './turn-indicator/index.js';
export * as AudioVisualizer from './audio-visualizer/index.js';

// Testing utilities (mock agent)
export { createMockAgent } from './testing/mock-agent.js';
export type { MockAgentOptions } from './testing/mock-agent.js';

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
} from './internal/attrs.js';
export type { CreateHPDAttrsReturn } from './internal/attrs.js';

// Keyboard constants
export { kbd } from './internal/kbd.js';
export type { KbdKey } from './internal/kbd.js';

// Common utilities
export { noop } from './internal/noop.js';
export { createId } from './internal/create-id.js';
export { debounce } from './internal/debounce.js';

// Type utilities
export type {
	WithChild,
	Without,
	OnChangeFn,
	HPDKeyboardEvent,
	HPDMouseEvent,
	WithRefOpts,
	RefAttachment
} from './internal/types.js';

// Focus management
export { RovingFocusGroup } from './internal/roving-focus-group.js';
export {
	focus,
	focusFirst,
	focusWithoutScroll,
	getTabbableCandidates,
	getTabbableEdges,
	findVisible,
	handleCalendarInitialFocus
} from './internal/focus.js';
export type { FocusableTarget } from './internal/focus.js';

export { getTabbableFrom, getTabbableFromFocusable, isTabbable, isFocusable, tabbable, focusable } from './internal/tabbable.js';

// Resize observer
export { HPDResizeObserver } from './internal/svelte-resize-observer.svelte.js';

// Animation utilities
export { PresenceManager } from './internal/presence-manager.svelte.js';
export { AnimationsComplete } from './internal/animations-complete.js';

// DOM utilities
export { getFirstNonCommentChild, isClickTrulyOutside } from './internal/dom.js';

// Event utilities
export { CustomEventDispatcher } from './internal/events.js';
export type { EventCallback } from './internal/events.js';

// Locale and direction
export { getElemDirection } from './internal/locale.js';
export type { Direction } from './internal/locale.js';

// Directional keys
export {
	getNextKey,
	getPrevKey,
	getDirectionalKeys,
	FIRST_KEYS,
	LAST_KEYS,
	FIRST_LAST_KEYS,
	SELECTION_KEYS
} from './internal/get-directional-keys.js';
export type { Orientation } from './internal/get-directional-keys.js';

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
} from './internal/is.js';
