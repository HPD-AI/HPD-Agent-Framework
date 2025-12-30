/**
 * AudioPlayerState - Reactive state for TTS audio playback
 *
 * Handles audio chunk playback with support for:
 * - Streaming audio chunks
 * - Pause/resume during interruptions
 * - Web Audio API for visualization
 * - HTML Audio Element for simple playback
 */

export class AudioPlayerState {
	// Reactive state
	#playing = $state(false);
	#buffering = $state(false);
	#currentTime = $state(0);
	#duration = $state(0);
	#synthesisId = $state<string | null>(null);

	// Derived state
	readonly playing = $derived(this.#playing);
	readonly buffering = $derived(this.#buffering);
	readonly currentTime = $derived(this.#currentTime);
	readonly duration = $derived(this.#duration);

	readonly progress = $derived(
		this.#duration > 0 ? this.#currentTime / this.#duration : 0
	);

	readonly status = $derived.by(() => {
		if (this.#buffering) return 'buffering';
		if (this.#playing) return 'playing';
		return 'idle';
	});

	// Props for rendering
	readonly props = $derived({
		'data-audio-player': '',
		'data-status': this.status,
		'data-playing': this.#playing ? '' : undefined,
		'data-buffering': this.#buffering ? '' : undefined,
		role: 'region',
		'aria-label': 'Audio playback',
		'aria-busy': this.#buffering
	});

	// Event handlers (called by AgentState)

	onSynthesisStarted(synthesisId: string, modelId?: string, voice?: string, streamId?: string) {
		this.#synthesisId = synthesisId;
		this.#buffering = true;
		console.log('[AudioPlayerState] Synthesis started', { synthesisId, modelId, voice, streamId });
		// TODO: Initialize audio context/element
	}

	onAudioChunk(
		synthesisId: string,
		base64Audio: string,
		mimeType: string,
		chunkIndex: number,
		duration: string,
		isLast: boolean,
		streamId?: string
	) {
		if (this.#synthesisId !== synthesisId) return;

		console.log('[AudioPlayerState] Audio chunk received', {
			chunkIndex,
			mimeType,
			duration,
			isLast,
			audioSize: base64Audio.length
		});

		// TODO: Decode and play/queue audio chunk
		// Simple approach: const audio = new Audio(`data:${mimeType};base64,${base64Audio}`);
		// Advanced: Use Web Audio API for visualization support
	}

	onSynthesisCompleted(
		synthesisId: string,
		wasInterrupted: boolean,
		totalChunks: number,
		deliveredChunks: number,
		streamId?: string
	) {
		if (this.#synthesisId !== synthesisId) return;

		this.#buffering = false;
		console.log('[AudioPlayerState] Synthesis completed', {
			wasInterrupted,
			totalChunks,
			deliveredChunks
		});
		// TODO: Finalize playback
	}

	onSpeechPaused(synthesisId: string, reason: 'user_speaking' | 'potential_interruption') {
		if (this.#synthesisId !== synthesisId) return;

		console.log('[AudioPlayerState] Speech paused', { reason });
		// TODO: Pause current audio
	}

	onSpeechResumed(synthesisId: string, pauseDuration: string) {
		if (this.#synthesisId !== synthesisId) return;

		console.log('[AudioPlayerState] Speech resumed', { pauseDuration });
		// TODO: Resume audio from pause point
	}

	// Public methods (for manual control)

	play() {
		this.#playing = true;
		// TODO: Implement playback
	}

	pause() {
		this.#playing = false;
		// TODO: Implement pause
	}

	stop() {
		this.#playing = false;
		this.#currentTime = 0;
		// TODO: Implement stop
	}
}
