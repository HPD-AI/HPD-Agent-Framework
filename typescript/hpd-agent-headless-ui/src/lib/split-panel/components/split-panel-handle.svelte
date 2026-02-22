<!--
	SplitPanel.Handle Component

	Resize handle between panes. Supports mouse/touch drag and keyboard resize.
	Provides visual feedback during drag operations.
-->
<script lang="ts">
	import type { Snippet } from 'svelte';
	import type { HTMLAttributes } from 'svelte/elements';
	import { mergeProps, boxWith } from 'svelte-toolbelt';
	import { SplitPanelHandleState } from './split-panel-handle-state.svelte.js';
	import { SplitPanelSplitContext } from './split-panel-split-state.svelte.js';

	interface Props extends Omit<HTMLAttributes<HTMLDivElement>, 'children'> {
		/** Whether handle is disabled */
		disabled?: boolean;

		/** Hit area size in pixels */
		hitAreaSize?: number;

		/** Keyboard step size */
		keyboardStep?: number;

		/** Large keyboard step size (with Shift) */
		keyboardStepLarge?: number;

		/** Parent path for divider operations */
		parentPath?: number[];

		/** Divider index within parent */
		dividerIndex?: number;

		/** Resize axis */
		axis?: 'row' | 'column';

		/**
		 * Double-click to reset adjacent panes to equal sizes.
		 * Common IDE pattern for quick layout reset.
		 * @default true
		 */
		resetOnDoubleClick?: boolean;

		/**
		 * Single click (not drag) to toggle collapse on nearest collapsible pane.
		 * Useful for quick collapse without dragging.
		 * @default false
		 */
		toggleCollapseOnClick?: boolean;

		/** Callback when drag starts */
		onDragStart?: () => void;

		/** Callback when drag ends */
		onDragEnd?: () => void;

		/** Callback before double-click reset. Return false to prevent. */
		onDoubleClick?: () => boolean | void;

		/** Custom snippet for complete control */
		child?: Snippet<
			[
				{
					props: Record<string, any>;
					isDragging: boolean;
					isDisabled: boolean;
					axis: 'row' | 'column';
				}
			]
		>;

		/** Default children */
		children?: Snippet<
			[
				{
					isDragging: boolean;
					isDisabled: boolean;
					axis: 'row' | 'column';
				}
			]
		>;

		/** Bindable ref to DOM element */
		ref?: HTMLDivElement | null;
	}

	let {
		disabled,
		hitAreaSize,
		keyboardStep,
		keyboardStepLarge,
		parentPath,
		dividerIndex,
		axis,
		resetOnDoubleClick,
		toggleCollapseOnClick,
		onDragStart,
		onDragEnd,
		onDoubleClick,
		child,
		children,
		ref = $bindable(null),
		...restProps
	}: Props = $props();

	// Get parent split from context
	const parentSplit = SplitPanelSplitContext.getOr(null);

	// Create handle state with boxed values
	const handleState = SplitPanelHandleState.create(
		{
			disabled: disabled !== undefined ? boxWith(() => disabled) : undefined,
			hitAreaSize: hitAreaSize !== undefined ? boxWith(() => hitAreaSize) : undefined,
			keyboardStep: keyboardStep !== undefined ? boxWith(() => keyboardStep) : undefined,
			keyboardStepLarge:
				keyboardStepLarge !== undefined ? boxWith(() => keyboardStepLarge) : undefined,
			parentPath: parentPath !== undefined ? boxWith(() => parentPath) : undefined,
			dividerIndex: dividerIndex !== undefined ? boxWith(() => dividerIndex) : undefined,
			axis: axis !== undefined ? boxWith(() => axis) : undefined,
			resetOnDoubleClick:
				resetOnDoubleClick !== undefined ? boxWith(() => resetOnDoubleClick) : undefined,
			toggleCollapseOnClick:
				toggleCollapseOnClick !== undefined ? boxWith(() => toggleCollapseOnClick) : undefined,
			onDragStart,
			onDragEnd,
			onDoubleClick,
			ref: boxWith(
				() => ref,
				(v) => (ref = v)
			)
		},
		parentSplit
	);

	// Merge props
	const mergedProps = $derived(mergeProps(restProps, handleState.props));
</script>

{#if child}
	{@render child({
		props: mergedProps,
		isDragging: handleState.snippetProps.isDragging,
		isDisabled: handleState.snippetProps.isDisabled,
		axis: handleState.snippetProps.axis
	})}
{:else}
	<div {...mergedProps}>
		{@render children?.(handleState.snippetProps)}
	</div>
{/if}
