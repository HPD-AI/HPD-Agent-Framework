/**
 * Mock Agent - For Testing Without HPD Backend
 *
 * Simulates HPD Agent responses for development and testing.
 * Calls AgentState methods directly to simulate HPD events.
 */

import { AgentState } from '../bits/agent/agent.svelte.js';

function sleep(ms: number): Promise<void> {
	return new Promise((resolve) => setTimeout(resolve, ms));
}

function generateId(): string {
	return `msg-${Date.now()}-${Math.random().toString(36).substring(7)}`;
}

export interface MockAgentOptions {
	/** Delay between text chunks (ms) */
	typingDelay?: number;

	/** Response templates */
	responses?: string[];

	/** Simulate thinking/reasoning */
	enableReasoning?: boolean;
}

export class MockAgent {
	readonly state: AgentState;
	private options: Required<MockAgentOptions>;
	private responseIndex = 0;

	constructor(options: MockAgentOptions = {}) {
		this.state = new AgentState();

		this.options = {
			typingDelay: options.typingDelay ?? 30,
			responses: options.responses ?? [
				'Hello! I am a mock assistant. How can I help you today?',
				'That sounds interesting! Tell me more.',
				'I understand. Let me think about that for a moment...',
				'Great question! Here is what I think...',
				'I am a mock agent, so I cannot actually process that, but in a real scenario...'
			],
			enableReasoning: options.enableReasoning ?? false
		};
	}

	/**
	 * Send a message and simulate assistant response
	 */
	async send(content: string): Promise<void> {
		// Add user message to state
		this.state.addUserMessage(content);

		// Small delay to simulate network
		await sleep(100);

		// Simulate assistant response
		await this.simulateAssistantResponse();
	}

	private async simulateAssistantResponse(): Promise<void> {
		const messageId = generateId();

		// Get next response (cycle through responses)
		const response = this.options.responses[this.responseIndex];
		this.responseIndex = (this.responseIndex + 1) % this.options.responses.length;

		// Optional: Simulate reasoning
		if (this.options.enableReasoning) {
			await this.simulateReasoning(messageId);
		}

		// Start text message
		this.state.onTextMessageStart(messageId, 'assistant');

		// Stream text character by character
		for (const char of response) {
			this.state.onTextDelta(char, messageId);
			await sleep(this.options.typingDelay);
		}

		// End text message
		this.state.onTextMessageEnd(messageId);
	}

	private async simulateReasoning(messageId: string): Promise<void> {
		const reasoning = 'Analyzing the user question...';

		// Simulate reasoning by sending deltas
		for (const char of reasoning) {
			this.state.onReasoningDelta(char, messageId);
			await sleep(this.options.typingDelay);
		}

		await sleep(300); // Pause after reasoning
	}

	/**
	 * Simulate a tool call (for future testing)
	 */
	async simulateToolCall(toolName: string, args: Record<string, unknown>): Promise<void> {
		const callId = generateId();
		const messageId = this.state.lastMessage?.id ?? generateId();

		// Start tool call
		this.state.onToolCallStart(callId, toolName, messageId);

		await sleep(500);

		// Send tool args
		this.state.onToolCallArgs(callId, JSON.stringify(args));

		await sleep(1000);

		// Send tool result
		this.state.onToolCallResult(
			callId,
			JSON.stringify({ success: true, mockData: 'Tool executed successfully' })
		);

		// End tool call
		this.state.onToolCallEnd(callId);
	}

	/**
	 * Approve a permission request
	 */
	async approve(permissionId: string, choice?: import('../bits/agent/types.js').PermissionChoice): Promise<void> {
		this.state.onPermissionApproved(permissionId, '');
	}

	/**
	 * Deny a permission request
	 */
	async deny(permissionId: string, reason?: string): Promise<void> {
		this.state.onPermissionDenied(permissionId, '', reason ?? 'User denied');
	}

	/**
	 * Respond to a clarification request
	 */
	async clarify(clarificationId: string, answer: string): Promise<void> {
		// TODO: Add onClarificationResolved to AgentState
		// For now, just remove from pending
	}

	/**
	 * Abort the current operation (no-op for mock)
	 */
	abort(): void {
		// No-op for mock
	}

	/**
	 * Clear all messages
	 */
	clear(): void {
		this.state.clearMessages();
	}
}

/**
 * Create a mock agent for testing
 */
export function createMockAgent(options?: MockAgentOptions): MockAgent {
	return new MockAgent(options);
}
