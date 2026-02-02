<script lang="ts">
	/**
	 * ToolExecution.Trigger - Clickable header for expand/collapse
	 *
	 * This component renders the clickable trigger button that
	 * expands/collapses the tool execution details.
	 *
	 */

	import { mergeProps } from 'svelte-toolbelt';
	import type { ToolExecutionTriggerProps } from '../types.js';
	import { ToolExecutionTriggerState } from '../tool-execution.svelte.js';

	let { child, children, ...restProps }: ToolExecutionTriggerProps = $props();

	// Get state from context
	const state = ToolExecutionTriggerState.create();

	// Merge props
	const mergedProps = $derived(mergeProps(restProps, state.props));
</script>

{#if child}
	{@render child({ props: mergedProps })}
{:else}
	<button {...mergedProps}>
		{@render children?.(state.snippetProps)}
	</button>
{/if}
