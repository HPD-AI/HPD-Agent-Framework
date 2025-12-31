/**
 * HPD Agent Headless UI - Main Entry Point
 *
 * Phase 2: Core AI components
 */

// Agent state and utilities (includes Message type)
export * from './bits/agent/index.js';

// Message component (explicit exports to avoid conflicts)
export { Message, MessageState, createMessageState } from './bits/message/index.js';
export type { MessageProps, MessageHTMLProps, MessageSnippetProps } from './bits/message/index.js';

// MessageList component
export * as MessageList from './bits/message-list/index.js';
export { MessageListState } from './bits/message-list/index.js';
export type { MessageListProps, MessageListSnippetProps } from './bits/message-list/index.js';

// Input component
export * as Input from './bits/input/index.js';

// ToolExecution component
export { ToolExecution } from './bits/tool-execution/index.js';

// PermissionDialog component
export * as PermissionDialog from './bits/permission-dialog/index.js';

// Audio components (Phase 3A)
export * as AudioPlaybackGate from './bits/audio-playback-gate/index.js';
export * as AudioPlayer from './bits/audio-player/index.js';
export * as Transcription from './bits/transcription/index.js';
export * as VoiceActivityIndicator from './bits/voice-activity-indicator/index.js';

// Audio components (Phase 3B)
export * as InterruptionIndicator from './bits/interruption-indicator/index.js';
export * as TurnIndicator from './bits/turn-indicator/index.js';
export * as AudioVisualizer from './bits/audio-visualizer/index.js';

// Testing utilities (mock agent)
export { createMockAgent } from './testing/mock-agent.js';
export type { MockAgentOptions } from './testing/mock-agent.js';
