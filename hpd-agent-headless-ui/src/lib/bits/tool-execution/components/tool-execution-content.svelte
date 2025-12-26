<script lang="ts">
	/**
	 * ToolExecution.Content - Collapsible content area
	 *
	 * This component renders the collapsible content area that shows
	 * tool arguments and results when expanded.
	 *
	 */

	import { mergeProps } from 'svelte-toolbelt';
	import type { ToolExecutionContentProps } from '../types.js';
	import { ToolExecutionContentState } from '../tool-execution.svelte.js';

	let { child, children, ...restProps }: ToolExecutionContentProps = $props();

	const state = ToolExecutionContentState.create();
	const mergedProps = $derived(mergeProps(restProps, state.props));
</script>

{#if state.shouldRender}
	{#if child}
		{@render child({ props: mergedProps })}
	{:else}
		<div {...mergedProps}>
			{@render children?.(state.snippetProps)}
		</div>
	{/if}
{/if}
