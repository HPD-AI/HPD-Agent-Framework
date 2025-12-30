/**
 * TurnIndicatorState - Reactive state for turn-taking detection
 *
 * Shows whose turn it is to speak:
 * - User turn vs agent turn
 * - Turn completion probability
 * - Detection method tracking
 */

export class TurnIndicatorState {
	// Reactive state
	#currentTurn = $state<'user' | 'agent' | 'unknown'>('unknown');
	#completionProbability = $state(0);
	#detectionMethod = $state<'heuristic' | 'ml' | 'manual' | 'timeout' | null>(null);

	// Derived state
	readonly currentTurn = $derived(this.#currentTurn);
	readonly completionProbability = $derived(this.#completionProbability);
	readonly isUserTurn = $derived(this.#currentTurn === 'user');
	readonly isAgentTurn = $derived(this.#currentTurn === 'agent');

	// Props for rendering
	readonly props = $derived({
		'data-turn-indicator': '',
		'data-turn': this.#currentTurn,
		'data-user-turn': this.isUserTurn ? '' : undefined,
		'data-agent-turn': this.isAgentTurn ? '' : undefined,
		role: 'status',
		'aria-label': `Current turn: ${this.#currentTurn}`,
		'aria-live': 'polite'
	});

	// Event handlers (called by AgentState)

	onTurnDetected(
		transcribedText: string,
		completionProbability: number,
		silenceDuration: string,
		detectionMethod: 'heuristic' | 'ml' | 'manual' | 'timeout'
	) {
		this.#completionProbability = completionProbability;
		this.#detectionMethod = detectionMethod;

		// High probability means user finished speaking → agent's turn
		this.#currentTurn = completionProbability >= 0.8 ? 'agent' : 'user';

		console.log('[TurnIndicatorState] Turn detected', {
			transcribedText,
			completionProbability,
			silenceDuration,
			detectionMethod,
			currentTurn: this.#currentTurn
		});
	}

	onVadStartOfSpeech(timestamp: string, speechProbability: number) {
		this.#currentTurn = 'user';
		console.log('[TurnIndicatorState] User turn (VAD)', { timestamp, speechProbability });
	}

	onSynthesisStarted(synthesisId: string, modelId?: string, voice?: string, streamId?: string) {
		this.#currentTurn = 'agent';
		console.log('[TurnIndicatorState] Agent turn (TTS)', { synthesisId, modelId, voice });
	}
}
