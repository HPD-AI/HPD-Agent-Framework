/**
 * InterruptionIndicatorState - Reactive state for interruption tracking
 *
 * Shows when agent speech is paused/interrupted:
 * - Interruption status
 * - Pause reasons
 * - Pause duration tracking
 */

export class InterruptionIndicatorState {
	// Reactive state
	#interrupted = $state(false);
	#paused = $state(false);
	#pauseReason = $state<'user_speaking' | 'potential_interruption' | null>(null);
	#pauseDuration = $state(0);

	// Derived state
	readonly interrupted = $derived(this.#interrupted);
	readonly paused = $derived(this.#paused);
	readonly pauseReason = $derived(this.#pauseReason);

	readonly status = $derived.by(() => {
		if (this.#interrupted) return 'interrupted';
		if (this.#paused) return 'paused';
		return 'normal';
	});

	// Props for rendering
	readonly props = $derived({
		'data-interruption-indicator': '',
		'data-status': this.status,
		'data-interrupted': this.#interrupted ? '' : undefined,
		'data-paused': this.#paused ? '' : undefined,
		role: 'status',
		'aria-label': 'Interruption status',
		'aria-live': 'assertive'
	});

	// Event handlers (called by AgentState)

	onUserInterrupted(transcribedText?: string) {
		this.#interrupted = true;
		console.log('[InterruptionIndicatorState] User interrupted', { transcribedText });
	}

	onSpeechPaused(synthesisId: string, reason: 'user_speaking' | 'potential_interruption') {
		this.#paused = true;
		this.#pauseReason = reason;
		console.log('[InterruptionIndicatorState] Speech paused', { synthesisId, reason });
	}

	onSpeechResumed(synthesisId: string, pauseDuration: string) {
		this.#paused = false;
		this.#interrupted = false;
		this.#pauseDuration = this.parseDuration(pauseDuration);
		console.log('[InterruptionIndicatorState] Speech resumed', { synthesisId, pauseDuration });
	}

	// Utilities

	private parseDuration(isoDuration: string): number {
		const match = isoDuration.match(/PT([\d.]+)S/);
		return match ? parseFloat(match[1]) : 0;
	}
}
