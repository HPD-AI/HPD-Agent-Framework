/**
 * AudioVisualizerState - Reactive state for audio visualization
 *
 * Frequency analysis visualization:
 * - Multiband frequency analysis (5-7 bands)
 * - Bar, waveform, or radial modes
 * - Real-time volume levels
 */

export class AudioVisualizerState {
	// Reactive state
	#volumes = $state<number[]>([]);
	#bands = $state(5);
	#mode = $state<'bar' | 'waveform' | 'radial'>('bar');

	// Derived state
	readonly volumes = $derived(this.#volumes);
	readonly bands = $derived(this.#bands);
	readonly mode = $derived(this.#mode);

	readonly maxVolume = $derived(Math.max(...this.#volumes, 0));
	readonly avgVolume = $derived(
		this.#volumes.length > 0
			? this.#volumes.reduce((a, b) => a + b, 0) / this.#volumes.length
			: 0
	);

	// Props for rendering
	readonly props = $derived({
		'data-audio-visualizer': '',
		'data-mode': this.#mode,
		'data-bands': this.#bands,
		role: 'img',
		'aria-label': 'Audio visualization'
	});

	// Event handlers (called by AgentState)

	onAudioChunk(
		synthesisId: string,
		base64Audio: string,
		mimeType: string,
		chunkIndex: number,
		duration: string,
		isLast: boolean,
		streamId?: string
	) {
		console.log('[AudioVisualizerState] Analyzing audio chunk', {
			chunkIndex,
			audioSize: base64Audio.length
		});

		// TODO: Analyze audio data to extract volume levels per frequency band
		// This requires Web Audio API AnalyserNode
		// const audioData = this.decodeAudioChunk(base64Audio);
		// this.#volumes = this.analyzeFrequencyBands(audioData, this.#bands);
	}

	// Configuration methods

	setBands(bands: number) {
		this.#bands = bands;
	}

	setMode(mode: 'bar' | 'waveform' | 'radial') {
		this.#mode = mode;
	}

	// TODO: Add Web Audio API methods
	// private decodeAudioChunk(base64Audio: string): Float32Array { }
	// private analyzeFrequencyBands(audioData: Float32Array, bands: number): number[] { }
}
