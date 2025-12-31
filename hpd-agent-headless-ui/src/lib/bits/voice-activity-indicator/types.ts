import type { Snippet } from 'svelte';
import type {
	IntensityLevel,
	VoiceActivityIndicatorState,
	VoiceActivityIndicatorStateProps
} from './voice-activity-indicator.svelte.js';

/**
 * Props for VoiceActivityIndicator.Root component
 */
export interface VoiceActivityIndicatorRootProps extends VoiceActivityIndicatorStateProps {
	/**
	 * Snippet for rendering with props passed to parent element
	 */
	child?: Snippet<[{ props: Record<string, unknown> } & VoiceActivityIndicatorRootSnippetProps]>;

	/**
	 * Snippet for rendering voice activity content
	 */
	children?: Snippet<[VoiceActivityIndicatorState]>;

	/**
	 * Reference to the root element
	 */
	ref?: HTMLElement | null;

	/**
	 * Test ID for testing
	 */
	'data-testid'?: string;
}

/**
 * Props passed to the children snippet
 */
export interface VoiceActivityIndicatorRootSnippetProps {
	/**
	 * Whether user is currently speaking
	 */
	active: boolean;

	/**
	 * Speech probability (0-1)
	 */
	speechProbability: number;

	/**
	 * Duration of last speech in seconds
	 */
	duration: number;

	/**
	 * Intensity level based on speech probability
	 */
	intensityLevel: IntensityLevel;
}
