/**
 * VoiceActivityIndicatorState - Reactive state for VAD visualization
 *
 * Visual feedback when user is speaking:
 * - Active/inactive states
 * - Speech probability levels
 * - Duration tracking
 */

export class VoiceActivityIndicatorState {
	// Reactive state
	#active = $state(false);
	#speechProbability = $state(0);
	#duration = $state(0);

	// Derived state
	readonly active = $derived(this.#active);
	readonly speechProbability = $derived(this.#speechProbability);

	readonly intensityLevel = $derived.by(() => {
		if (!this.#active) return 'none';
		if (this.#speechProbability >= 0.9) return 'high';
		if (this.#speechProbability >= 0.7) return 'medium';
		return 'low';
	});

	// Props for rendering
	readonly props = $derived({
		'data-vad-indicator': '',
		'data-active': this.#active ? '' : undefined,
		'data-intensity': this.intensityLevel,
		role: 'status',
		'aria-label': 'Voice activity',
		'aria-live': 'polite'
	});

	// Event handlers (called by AgentState)

	onVadStartOfSpeech(timestamp: string, speechProbability: number) {
		this.#active = true;
		this.#speechProbability = speechProbability;

		console.log('[VoiceActivityIndicatorState] Speech started', { timestamp, speechProbability });
	}

	onVadEndOfSpeech(timestamp: string, speechDuration: string, speechProbability: number) {
		this.#active = false;
		this.#speechProbability = speechProbability;
		this.#duration = this.parseDuration(speechDuration);

		console.log('[VoiceActivityIndicatorState] Speech ended', {
			timestamp,
			speechDuration,
			speechProbability
		});
	}

	// Utilities

	private parseDuration(isoDuration: string): number {
		// Parse ISO 8601 duration (e.g., "PT5.2S" = 5.2 seconds)
		const match = isoDuration.match(/PT([\d.]+)S/);
		return match ? parseFloat(match[1]) : 0;
	}
}
