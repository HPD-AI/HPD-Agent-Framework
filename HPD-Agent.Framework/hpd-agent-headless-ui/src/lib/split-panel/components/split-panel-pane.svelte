<!--
	SplitPanel.Pane Component

	Individual resizable pane within a split panel layout.
	Supports collapse, focus management, and size constraints.
-->
<script lang="ts">
	import type { Snippet } from 'svelte';
	import type { HTMLAttributes } from 'svelte/elements';
	import { mergeProps, boxWith } from 'svelte-toolbelt';
	import { SplitPanelPaneState } from './split-panel-pane-state.svelte.js';

	interface Props extends HTMLAttributes<HTMLDivElement> {
		/** Unique pane ID */
		id: string;

		/** Minimum size in pixels */
		minSize?: number;

		/** Maximum size in pixels */
		maxSize?: number;

		/** Resize priority */
		priority?: 'high' | 'normal' | 'low';

		/** Auto-collapse threshold in pixels */
		autoCollapseThreshold?: number;

		/** Snap points for resize */
		snapPoints?: number[];

		/** Snap threshold in pixels */
		snapThreshold?: number;

		/** Panel type for content state serialization */
		panelType?: string;
	/** Initial size of pane (percentage or pixels based on initialSizeUnit) */
	initialSize?: number;

	/** Unit for initial size: 'percent' or 'pixels' */
	initialSizeUnit?: 'percent' | 'pixels';
		/** Collapsed state (bindable) */
		collapsed?: boolean;

		/**
		 * Strategy for animating collapse/expand transitions.
		 * @default 'unmount'
		 */
		collapseStrategy?: 'unmount' | 'force-mount' | 'view-transition';

		/**
		 * Custom view-transition-name for this pane.
		 * Only used when collapseStrategy is 'view-transition'.
		 */
		viewTransitionName?: string;

		/**
		 * Legacy: Force mount content regardless of collapsed state.
		 * @deprecated Use collapseStrategy="force-mount" instead
		 */
		forceMount?: boolean;

		/** Custom snippet for complete control */
		child?: Snippet<
			[
				{
					props: Record<string, any>;
					isFocused: boolean;
					isCollapsed: boolean;
					size: number;
				}
			]
		>;

		/** Default children */
		children?: Snippet<
			[
				{
					isFocused: boolean;
					isCollapsed: boolean;
					size: number;
					toggle: () => void;
					focus: () => void;
					collapse: () => void;
					expand: () => void;
				}
			]
		>;

		/** Bindable ref to DOM element */
		ref?: HTMLDivElement | null;
	}

	let {
		id,
		minSize,
		maxSize,
		priority,
		autoCollapseThreshold,
		snapPoints,
		snapThreshold,
		panelType,
		initialSize,
		initialSizeUnit,
		collapsed = $bindable(false),
		collapseStrategy,
		viewTransitionName,
		forceMount,
		child,
		children,
		ref = $bindable(null),
		...restProps
	}: Props = $props();

	// Create pane state with boxed values
	const paneState = SplitPanelPaneState.create({
		id: boxWith(() => id),
		minSize: minSize !== undefined ? boxWith(() => minSize) : undefined,
		maxSize: maxSize !== undefined ? boxWith(() => maxSize) : undefined,
		priority: priority !== undefined ? boxWith(() => priority) : undefined,
		autoCollapseThreshold:
			autoCollapseThreshold !== undefined ? boxWith(() => autoCollapseThreshold) : undefined,
		snapPoints: snapPoints !== undefined ? boxWith(() => snapPoints) : undefined,
		snapThreshold: snapThreshold !== undefined ? boxWith(() => snapThreshold) : undefined,
		panelType: panelType !== undefined ? boxWith(() => panelType) : undefined,
		initialSize: initialSize !== undefined ? boxWith(() => initialSize) : undefined,
		initialSizeUnit:
			initialSizeUnit !== undefined ? boxWith(() => initialSizeUnit) : undefined,
		collapseStrategy:
			collapseStrategy !== undefined ? boxWith(() => collapseStrategy) : undefined,
		viewTransitionName:
			viewTransitionName !== undefined ? boxWith(() => viewTransitionName) : undefined,
		forceMount: forceMount !== undefined ? boxWith(() => forceMount) : undefined,
		collapsed: boxWith(
			() => collapsed,
			(v) => (collapsed = v)
		),
		ref: boxWith(
			() => ref,
			(v) => (ref = v)
		)
	});

	// Update pane state with element ref when mounted (for DOM ordering)
	$effect(() => {
		paneState._setElement(ref);
	});

	// Merge props
	const mergedProps = $derived(mergeProps(restProps, paneState.props));
</script>

{#if child}
	{@render child({
		props: mergedProps,
		isFocused: paneState.isFocused,
		isCollapsed: paneState.isCollapsed,
		size: paneState.size
	})}
{:else}
	<div {...mergedProps}>
		{@render children?.(paneState.snippetProps)}
	</div>
{/if}
