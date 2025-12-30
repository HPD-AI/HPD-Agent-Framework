/**
 * TranscriptionState - Reactive state for STT transcription display
 *
 * Displays live speech-to-text with:
 * - Interim vs final transcription states
 * - Confidence level indicators
 * - Auto-scroll support
 */

export class TranscriptionState {
	// Reactive state
	#text = $state('');
	#isFinal = $state(false);
	#confidence = $state<number | null>(null);
	#transcriptionId = $state<string | null>(null);

	// Derived state
	readonly text = $derived(this.#text);
	readonly isFinal = $derived(this.#isFinal);
	readonly confidence = $derived(this.#confidence);
	readonly isEmpty = $derived(this.#text.length === 0);

	readonly confidenceLevel = $derived.by(() => {
		if (!this.#confidence) return null;
		if (this.#confidence >= 0.9) return 'high';
		if (this.#confidence >= 0.7) return 'medium';
		return 'low';
	});

	// Props for rendering
	readonly props = $derived({
		'data-transcription': '',
		'data-final': this.#isFinal ? '' : undefined,
		'data-confidence': this.confidenceLevel,
		role: 'status',
		'aria-label': 'Voice transcription',
		'aria-live': 'polite',
		'aria-busy': !this.#isFinal
	});

	// Event handlers (called by AgentState)

	onTranscriptionDelta(
		transcriptionId: string,
		text: string,
		isFinal: boolean,
		confidence?: number
	) {
		this.#transcriptionId = transcriptionId;
		this.#text = text;
		this.#isFinal = isFinal;
		this.#confidence = confidence ?? null;

		console.log('[TranscriptionState] Delta', { text, isFinal, confidence });
	}

	onTranscriptionCompleted(
		transcriptionId: string,
		finalText: string,
		processingDuration: string
	) {
		if (this.#transcriptionId !== transcriptionId) return;

		this.#text = finalText;
		this.#isFinal = true;

		console.log('[TranscriptionState] Completed', { finalText, processingDuration });
	}

	// Public methods

	clear() {
		this.#text = '';
		this.#isFinal = false;
		this.#confidence = null;
		this.#transcriptionId = null;
	}
}
