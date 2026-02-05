/**
 * Artifact Type Definitions
 *
 * Type definitions for the Artifact compositional component.
 * Enables AI-generated content to be rendered in a dedicated side panel.
 */

import type { Snippet } from 'svelte';
import type { HPDPrimitiveDivAttributes, HPDPrimitiveButtonAttributes } from '$lib/shared/types.js';
import type { OnChangeFn, WithChild } from '$lib/internal/types.js';
import type { Without } from 'svelte-toolbelt';

// ============================================
// Slot Data (stored in registry)
// ============================================

export interface ArtifactSlotData {
	id: string;
	title: Snippet | null;
	content: Snippet | null;
}

// ============================================
// Provider Component Props
// ============================================

export type ArtifactProviderPropsWithoutHTML = WithChild<{
	/**
	 * Callback when any artifact opens/closes
	 */
	onOpenChange?: (open: boolean, id: string | null) => void;
}>;

export type ArtifactProviderProps = ArtifactProviderPropsWithoutHTML &
	Without<HPDPrimitiveDivAttributes, ArtifactProviderPropsWithoutHTML>;

// ============================================
// Root Component Props
// ============================================

export type ArtifactRootSnippetProps = {
	/**
	 * Whether this artifact is currently open
	 */
	open: boolean;

	/**
	 * Function to open/close this artifact
	 */
	setOpen: (open: boolean) => void;

	/**
	 * Toggle open state
	 */
	toggle: () => void;
};

export type ArtifactRootPropsWithoutHTML = WithChild<
	{
		/**
		 * Unique identifier for this artifact
		 */
		id: string;

		/**
		 * Initial open state
		 * @default false
		 */
		defaultOpen?: boolean;

		/**
		 * Callback when this artifact opens/closes
		 */
		onOpenChange?: OnChangeFn<boolean>;
	},
	ArtifactRootSnippetProps
>;

export type ArtifactRootProps = ArtifactRootPropsWithoutHTML &
	Without<HPDPrimitiveDivAttributes, ArtifactRootPropsWithoutHTML>;

// ============================================
// Slot Component Props
// ============================================

export type ArtifactSlotPropsWithoutHTML = {
	/**
	 * Snippet for the title area
	 */
	title?: Snippet;

	/**
	 * Snippet for the main content area
	 */
	content?: Snippet;
};

export type ArtifactSlotProps = ArtifactSlotPropsWithoutHTML;

// ============================================
// Trigger Component Props
// ============================================

export type ArtifactTriggerSnippetProps = {
	/**
	 * Whether the artifact is open
	 */
	open: boolean;
};

export type ArtifactTriggerPropsWithoutHTML = WithChild<
	{
		/**
		 * Override disabled state
		 */
		disabled?: boolean;
	},
	ArtifactTriggerSnippetProps
>;

export type ArtifactTriggerProps = ArtifactTriggerPropsWithoutHTML &
	Without<HPDPrimitiveButtonAttributes, ArtifactTriggerPropsWithoutHTML>;

// ============================================
// Panel Component Props
// ============================================

export type ArtifactPanelSnippetProps = {
	/**
	 * Whether any artifact is open
	 */
	open: boolean;

	/**
	 * ID of the currently open artifact
	 */
	openId: string | null;

	/**
	 * The title snippet from the open artifact
	 */
	title: Snippet | null;

	/**
	 * The content snippet from the open artifact
	 */
	content: Snippet | null;

	/**
	 * Function to close the current artifact
	 */
	close: () => void;
};

export type ArtifactPanelPropsWithoutHTML = WithChild<{}, ArtifactPanelSnippetProps>;

export type ArtifactPanelProps = ArtifactPanelPropsWithoutHTML &
	Without<HPDPrimitiveDivAttributes, ArtifactPanelPropsWithoutHTML>;

// ============================================
// Title Component Props (Render Target)
// ============================================

export type ArtifactTitlePropsWithoutHTML = WithChild<{}>;

export type ArtifactTitleProps = ArtifactTitlePropsWithoutHTML &
	Without<HPDPrimitiveDivAttributes, ArtifactTitlePropsWithoutHTML>;

// ============================================
// Content Component Props (Render Target)
// ============================================

export type ArtifactContentPropsWithoutHTML = WithChild<{}>;

export type ArtifactContentProps = ArtifactContentPropsWithoutHTML &
	Without<HPDPrimitiveDivAttributes, ArtifactContentPropsWithoutHTML>;

// ============================================
// Close Component Props
// ============================================

export type ArtifactCloseSnippetProps = {
	/**
	 * Whether any artifact is open
	 */
	open: boolean;
};

export type ArtifactClosePropsWithoutHTML = WithChild<
	{
		/**
		 * Override disabled state
		 */
		disabled?: boolean;
	},
	ArtifactCloseSnippetProps
>;

export type ArtifactCloseProps = ArtifactClosePropsWithoutHTML &
	Without<HPDPrimitiveButtonAttributes, ArtifactClosePropsWithoutHTML>;
