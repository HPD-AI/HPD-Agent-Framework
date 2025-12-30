/**
 * createAgent() - Central Agent Factory
 *
 * Creates a reactive agent instance that automatically wires HPD protocol events
 * to AgentState updates. This is the main entry point for the library.
 *
 * @example
 * ```ts
 * import { createAgent } from '@hpd/hpd-agent-headless-ui';
 * import { createExpandedToolGroup } from '@hpd/hpd-agent-client';
 *
 * const locationTools = createExpandedToolGroup('location-tools', [
 *   {
 *     name: 'getCurrentLocation',
 *     description: 'Get user location',
 *     parametersSchema: { type: 'object', properties: {} }
 *   }
 * ], {
 *   description: 'Tools for accessing user location'
 * });
 *
 * const agent = createAgent({
 *   baseUrl: 'http://localhost:5135',
 *   conversationId: 'conv-123',
 *   clientToolGroups: [locationTools]
 * });
 *
 * // Reactive state (automatic UI updates via Svelte runes)
 * const { messages, streaming, pendingPermissions } = agent.state;
 *
 * // Simple send API
 * await agent.send('Get my location');
 * ```
 */

import {
	AgentClient,
	type EventHandlers,
	type PermissionChoice,
	createErrorResponse
} from '@hpd/hpd-agent-client';
import { AgentState } from './agent.svelte.js';
import type { CreateAgentOptions } from './types.js';

// ============================================
// Agent Instance Interface
// ============================================

export interface Agent {
	/** Reactive state - updates trigger UI re-renders automatically */
	readonly state: AgentState;

	/** Send a message to the agent */
	send(content: string): Promise<void>;

	/** Approve a permission request */
	approve(permissionId: string, choice?: PermissionChoice): Promise<void>;

	/** Deny a permission request */
	deny(permissionId: string, reason?: string): Promise<void>;

	/** Respond to a clarification request */
	clarify(clarificationId: string, answer: string): Promise<void>;

	/** Abort the current stream */
	abort(): void;

	/** Clear all messages and reset state */
	clear(): void;
}


// ============================================
// createAgent() - Main Factory Function
// ============================================

/**
 * Create a reactive agent instance with automatic event handling.
 *
 * This function:
 * 1. Creates an AgentClient (transport layer)
 * 2. Creates an AgentState (reactive state)
 * 3. Auto-wires all 67 HPD protocol events
 * 4. Returns a clean API with send(), approve(), etc.
 *
 * @param options Configuration options
 * @returns Agent instance with reactive state and convenience methods
 */
export function createAgent(options: CreateAgentOptions): Agent {
	const conversationId = options.conversationId ?? `conv-${Date.now()}`;

	// 1. Create AgentClient (transport layer)
	const client = new AgentClient({
		baseUrl: options.baseUrl,
		transport: options.transport ?? 'sse',
		headers: options.headers,
		clientToolGroups: options.clientToolGroups
	});

	// 2. Create AgentState (reactive state layer)
	const state = new AgentState();

	// 3. Build event handlers that auto-wire to AgentState
	const eventHandlers: EventHandlers = {
		// ============================================
		// Content Events → AgentState
		// ============================================

		onTextMessageStart: (messageId, role) => {
			state.onTextMessageStart(messageId, role);
		},

		onTextDelta: (text, messageId) => {
			state.onTextDelta(text, messageId);
		},

		onTextMessageEnd: (messageId) => {
			state.onTextMessageEnd(messageId);
		},

		// ============================================
		// Reasoning Events → AgentState
		// ============================================

		onReasoning: (text, messageId) => {
			// Map to reasoning delta
			state.onReasoningDelta(text, messageId);
		},

		// ============================================
		// Tool Events → AgentState
		// ============================================

		onToolCallStart: (callId, name, messageId) => {
			state.onToolCallStart(callId, name, messageId);
		},

		onToolCallArgs: (callId, argsJson) => {
			state.onToolCallArgs(callId, argsJson);
		},

		onToolCallEnd: (callId) => {
			state.onToolCallEnd(callId);
		},

		onToolCallResult: (callId, result) => {
			state.onToolCallResult(callId, result);
		},

		// ============================================
		// Permission Events → AgentState
		// ============================================

		onPermissionRequest: async (request) => {
			// Add to state for UI to display
			state.onPermissionRequest({
				permissionId: request.permissionId,
				sourceName: request.sourceName,
				functionName: request.functionName,
				description: request.description,
				callId: request.callId,
				arguments: request.arguments
			});

			// Wait for user to approve/deny via agent.approve() or agent.deny()
			// This is handled by returning a pending promise that the send() method waits for
			return new Promise((resolve) => {
				// Store resolver so approve()/deny() can resolve it
				pendingPermissionResolvers.set(request.permissionId, resolve);
			});
		},

		// ============================================
		// Clarification Events → AgentState
		// ============================================

		onClarificationRequest: async (request) => {
			// Add to state for UI to display
			state.onClarificationRequest({
				requestId: request.requestId,
				sourceName: request.sourceName,
				question: request.question,
				agentName: request.agentName,
				options: request.options
			});

			// Wait for user to respond via agent.clarify()
			return new Promise((resolve) => {
				pendingClarificationResolvers.set(request.requestId, resolve);
			});
		},

		// ============================================
		// Client Tool Events → User Handler
		// ============================================

		onClientToolInvoke: async (request) => {
			// Call user's handler if provided
			if (options.onClientToolInvoke) {
				return await options.onClientToolInvoke(request);
			}

			// Fallback error if no handler provided
			return createErrorResponse(
				request.requestId,
				`No onClientToolInvoke handler registered for tool: ${request.toolName}`
			);
		},

		onClientToolGroupsRegistered: (event) => {
			state.onClientToolGroupsRegistered(
				event.registeredToolGroups,
				event.totalTools,
				event.timestamp
			);
		},

		// ============================================
		// Lifecycle Events → AgentState
		// ============================================

		onTurnStart: (iteration) => {
			// TODO: Add to AgentState if needed
		},

		onTurnEnd: (iteration) => {
			// TODO: Add to AgentState if needed
		},

		onComplete: () => {
			options.onComplete?.();
		},

		onError: (message) => {
			state.onMessageTurnError(message);
			options.onError?.(message);
		},

		// ============================================
		// Audio Events (TTS) → AgentState
		// ============================================

		onSynthesisStarted: (event) => {
			state.onSynthesisStarted(
				event.synthesisId,
				event.modelId,
				event.voice,
				event.streamId
			);
		},

		onAudioChunk: (event) => {
			state.onAudioChunk(
				event.synthesisId,
				event.base64Audio,
				event.mimeType,
				event.chunkIndex,
				event.duration,
				event.isLast,
				event.streamId
			);
		},

		onSynthesisCompleted: (event) => {
			state.onSynthesisCompleted(
				event.synthesisId,
				event.wasInterrupted,
				event.totalChunks,
				event.deliveredChunks,
				event.streamId
			);
		},

		// ============================================
		// Audio Events (STT) → AgentState
		// ============================================

		onTranscriptionDelta: (event) => {
			state.onTranscriptionDelta(
				event.transcriptionId,
				event.text,
				event.isFinal,
				event.confidence
			);
		},

		onTranscriptionCompleted: (event) => {
			state.onTranscriptionCompleted(
				event.transcriptionId,
				event.finalText,
				event.processingDuration
			);
		},

		// ============================================
		// Audio Events (Interruption) → AgentState
		// ============================================

		onUserInterrupted: (event) => {
			state.onUserInterrupted(event.transcribedText);
		},

		onSpeechPaused: (event) => {
			state.onSpeechPaused(event.synthesisId, event.reason);
		},

		onSpeechResumed: (event) => {
			state.onSpeechResumed(event.synthesisId, event.pauseDuration);
		},

		// ============================================
		// Audio Events (Preemptive Generation) → AgentState
		// ============================================

		onPreemptiveGenerationStarted: (event) => {
			state.onPreemptiveGenerationStarted(event.generationId, event.turnCompletionProbability);
		},

		onPreemptiveGenerationDiscarded: (event) => {
			state.onPreemptiveGenerationDiscarded(event.generationId, event.reason);
		},

		// ============================================
		// Audio Events (VAD) → AgentState
		// ============================================

		onVadStartOfSpeech: (event) => {
			state.onVadStartOfSpeech(event.timestamp, event.speechProbability);
		},

		onVadEndOfSpeech: (event) => {
			state.onVadEndOfSpeech(
				event.timestamp,
				event.speechDuration,
				event.speechProbability
			);
		},

		// ============================================
		// Audio Events (Metrics) → AgentState
		// ============================================

		onAudioPipelineMetrics: (event) => {
			state.onAudioPipelineMetrics(
				event.metricType,
				event.metricName,
				event.value,
				event.unit
			);
		},

		// ============================================
		// Audio Events (Turn Detection) → AgentState
		// ============================================

		onTurnDetected: (event) => {
			state.onTurnDetected(
				event.transcribedText,
				event.completionProbability,
				event.silenceDuration,
				event.detectionMethod
			);
		},

		// ============================================
		// Audio Events (Filler) → AgentState
		// ============================================

		onFillerAudioPlayed: (event) => {
			state.onFillerAudioPlayed(event.phrase, event.duration);
		},

		// Optional: Log all events for debugging
		// onEvent: (event) => {
		// 	console.log('[HPD EVENT]', event.type, event);
		// }
	};

	// Permission resolver storage
	const pendingPermissionResolvers = new Map<
		string,
		(response: { approved: boolean; choice?: PermissionChoice; reason?: string }) => void
	>();

	// Clarification resolver storage
	const pendingClarificationResolvers = new Map<string, (answer: string) => void>();

	// 4. Return Agent API
	return {
		state,

		async send(content: string): Promise<void> {
			// Add user message to state immediately
			state.addUserMessage(content);

			// Stream with auto-wired handlers
			await client.stream(conversationId, [{ content }], eventHandlers);
		},

		async approve(permissionId: string, choice: PermissionChoice = 'ask'): Promise<void> {
			const resolver = pendingPermissionResolvers.get(permissionId);
			if (resolver) {
				resolver({ approved: true, choice });
				pendingPermissionResolvers.delete(permissionId);

				// Update state
				state.onPermissionApproved(permissionId, '');
			}
		},

		async deny(permissionId: string, reason?: string): Promise<void> {
			const resolver = pendingPermissionResolvers.get(permissionId);
			if (resolver) {
				resolver({ approved: false, reason });
				pendingPermissionResolvers.delete(permissionId);

				// Update state
				state.onPermissionDenied(permissionId, '', reason ?? 'User denied');
			}
		},

		async clarify(clarificationId: string, answer: string): Promise<void> {
			const resolver = pendingClarificationResolvers.get(clarificationId);
			if (resolver) {
				resolver(answer);
				pendingClarificationResolvers.delete(clarificationId);

				// Remove from pending in state
				// TODO: Add onClarificationResolved to AgentState
			}
		},

		abort(): void {
			client.abort();
		},

		clear(): void {
			state.clearMessages();
		}
	};
}
