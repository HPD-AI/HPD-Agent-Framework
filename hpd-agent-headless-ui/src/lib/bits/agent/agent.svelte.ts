/**
 * AgentState - Reactive State Manager for HPD Agent
 *
 * This is the core state manager that holds all chat data and implements
 * the event handler methods that EventMapper calls when HPD protocol events arrive.
 */

import type {
	Message,
	MessageRole,
	ToolCall,
	ToolCallStatus,
	PermissionRequest,
	ClarificationRequest,
	ClientToolInvokeRequest
} from './types.js';

export class AgentState {
	// ============================================
	// Reactive State ($state runes)
	// ============================================

	#messages = $state<Message[]>([]);
	#streaming = $state(false);
	#reasoning = $state(false);
	#error = $state<string | null>(null);

	// Tool execution tracking
	#activeTools = $state<ToolCall[]>([]);

	// Bidirectional request tracking
	#pendingPermissions = $state<PermissionRequest[]>([]);
	#pendingClarifications = $state<ClarificationRequest[]>([]);
	#pendingClientToolRequests = $state<ClientToolInvokeRequest[]>([]);

	// Turn tracking
	#currentTurnId = $state<string | null>(null);
	#currentConversationId = $state<string | null>(null);

	// ============================================
	// Derived State ($derived)
	// ============================================

	readonly isWaitingForUser = $derived(
		this.#pendingPermissions.length > 0 || this.#pendingClarifications.length > 0
	);

	readonly lastMessage = $derived(this.#messages[this.#messages.length - 1]);

	readonly canSend = $derived(!this.#streaming && !this.isWaitingForUser && !this.#error);

	readonly hasMessages = $derived(this.#messages.length > 0);

	// ============================================
	// Public Getters (expose reactive state)
	// ============================================

	get messages() {
		return this.#messages;
	}

	get streaming() {
		return this.#streaming;
	}

	get reasoning() {
		return this.#reasoning;
	}

	get error() {
		return this.#error;
	}

	get activeTools() {
		return this.#activeTools;
	}

	get pendingPermissions() {
		return this.#pendingPermissions;
	}

	get pendingClarifications() {
		return this.#pendingClarifications;
	}

	// ============================================
	// Event Handlers (called by EventMapper)
	// ============================================

	// --- Text Content Events ---

	onTextMessageStart(messageId: string, role: string) {
		const message: Message = {
			id: messageId,
			role: role as MessageRole,
			content: '',
			streaming: true,
			thinking: false,
			timestamp: new Date(),
			toolCalls: []
		};

		this.#messages.push(message);
		this.#streaming = true;
	}

	onTextDelta(text: string, messageId: string) {
		const index = this.#messages.findIndex((m) => m.id === messageId);
		if (index !== -1) {
			// Create new message object to trigger reactivity
			this.#messages[index] = {
				...this.#messages[index],
				content: this.#messages[index].content + text
			};
		}
	}

	onTextMessageEnd(messageId: string) {
		const index = this.#messages.findIndex((m) => m.id === messageId);
		if (index !== -1) {
			// Create new message object to trigger reactivity
			this.#messages[index] = {
				...this.#messages[index],
				streaming: false
			};
		}
		this.#streaming = false;
	}

	// --- Reasoning Events ---

	onReasoningMessageStart(messageId: string, role: string) {
		const message: Message = {
			id: messageId,
			role: role as MessageRole,
			content: '',
			streaming: true,
			thinking: true,
			timestamp: new Date(),
			toolCalls: [],
			reasoning: ''
		};

		this.#messages.push(message);
		this.#reasoning = true;
	}

	onReasoningDelta(text: string, messageId: string) {
		const index = this.#messages.findIndex((m) => m.id === messageId);
		if (index !== -1) {
			const current = this.#messages[index];
			// Create new message object to trigger reactivity
			this.#messages[index] = {
				...current,
				reasoning: (current.reasoning || '') + text
			};
		}
	}

	onReasoningMessageEnd(messageId: string) {
		const index = this.#messages.findIndex((m) => m.id === messageId);
		if (index !== -1) {
			// Create new message object to trigger reactivity
			this.#messages[index] = {
				...this.#messages[index],
				streaming: false,
				thinking: false
			};
		}
		this.#reasoning = false;
	}

	// --- Tool Call Events ---

	onToolCallStart(callId: string, name: string, messageId: string) {
		const toolCall: ToolCall = {
			callId,
			name,
			messageId,
			status: 'pending',
			startTime: new Date()
		};

		this.#activeTools.push(toolCall);

		// Add to message's tool calls
		const message = this.#messages.find((m) => m.id === messageId);
		if (message) {
			message.toolCalls.push(toolCall);
		}
	}

	onToolCallArgs(callId: string, argsJson: string) {
		const toolCall = this.#activeTools.find((t) => t.callId === callId);
		if (toolCall) {
			try {
				toolCall.args = JSON.parse(argsJson);
				toolCall.status = 'executing';
			} catch (e) {
				console.error('Failed to parse tool args:', e);
				toolCall.error = 'Invalid arguments';
				toolCall.status = 'error';
			}
		}
	}

	onToolCallEnd(callId: string) {
		const toolCall = this.#activeTools.find((t) => t.callId === callId);
		if (toolCall) {
			toolCall.endTime = new Date();
			// Status will be set by onToolCallResult
		}
	}

	onToolCallResult(callId: string, result: string) {
		const toolCall = this.#activeTools.find((t) => t.callId === callId);
		if (toolCall) {
			toolCall.result = result;
			toolCall.status = 'complete';
			toolCall.endTime = new Date();
		}

		// Remove from active tools
		this.#activeTools = this.#activeTools.filter((t) => t.callId !== callId);
	}

	// --- Permission Events ---

	onPermissionRequest(request: {
		permissionId: string;
		sourceName: string;
		functionName: string;
		description?: string;
		callId: string;
		arguments?: Record<string, unknown>;
	}) {
		this.#pendingPermissions.push(request);
	}

	onPermissionApproved(permissionId: string, sourceName: string) {
		this.#pendingPermissions = this.#pendingPermissions.filter(
			(p) => p.permissionId !== permissionId
		);
	}

	onPermissionDenied(permissionId: string, sourceName: string, reason: string) {
		this.#pendingPermissions = this.#pendingPermissions.filter(
			(p) => p.permissionId !== permissionId
		);
	}

	// --- Clarification Events ---

	onClarificationRequest(request: {
		requestId: string;
		sourceName: string;
		question: string;
		agentName?: string;
		options?: string[];
	}) {
		this.#pendingClarifications.push(request);
	}

	// --- Client Tool Events ---

	onClientToolInvokeRequest(request: {
		requestId: string;
		toolName: string;
		callId: string;
		arguments: Record<string, unknown>;
		description?: string;
	}) {
		this.#pendingClientToolRequests.push(request);
		// TODO: Automatically invoke registered client tool handlers
	}

	onClientToolGroupsRegistered(
		registeredToolGroups: string[],
		totalTools: number,
		timestamp: string
	) {
		console.log(
			`[AgentState] Registered ${totalTools} tools in ${registeredToolGroups.length} groups at ${timestamp}`
		);
	}

	// --- Message Turn Events ---

	onMessageTurnStarted(
		messageTurnId: string,
		conversationId: string,
		agentName: string,
		timestamp: string
	) {
		this.#currentTurnId = messageTurnId;
		this.#currentConversationId = conversationId;
		console.log(`[AgentState] Turn started: ${messageTurnId} in ${conversationId}`);
	}

	onMessageTurnFinished(
		messageTurnId: string,
		conversationId: string,
		duration: string,
		timestamp: string
	) {
		this.#currentTurnId = null;
		console.log(`[AgentState] Turn finished: ${messageTurnId} (${duration})`);
	}

	onMessageTurnError(message: string) {
		this.#error = message;
		this.#streaming = false;
		this.#reasoning = false;
		console.error('[AgentState] Turn error:', message);
	}

	// ============================================
	// Public Methods (for user interaction)
	// ============================================

	/**
	 * Clear error state
	 */
	clearError() {
		this.#error = null;
	}

	/**
	 * Add a user message (for local display before sending to backend)
	 */
	addUserMessage(content: string): Message {
		const message: Message = {
			id: `user-${Date.now()}`,
			role: 'user',
			content,
			streaming: false,
			thinking: false,
			timestamp: new Date(),
			toolCalls: []
		};

		this.#messages.push(message);
		return message;
	}

	/**
	 * Clear all messages
	 */
	clearMessages() {
		this.#messages = [];
		this.#activeTools = [];
		this.#pendingPermissions = [];
		this.#pendingClarifications = [];
		this.#error = null;
	}
}
