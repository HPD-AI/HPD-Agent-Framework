<script lang="ts">
	/**
	 * ToolExecution.Args - Tool arguments display
	 *
	 * Displays the arguments passed to the tool in JSON format
	 */

	import { mergeProps } from 'svelte-toolbelt';
	import type { ToolExecutionArgsProps } from '../types.js';
	import { ToolExecutionArgsState } from '../tool-execution.svelte.js';

	let { child, children, ...restProps }: ToolExecutionArgsProps = $props();

	const state = ToolExecutionArgsState.create();
	const mergedProps = $derived(mergeProps(restProps, state.props));
</script>

{#if child}
	{@render child({ props: mergedProps })}
{:else}
	<div {...mergedProps}>
		{@render children?.(state.snippetProps)}
	</div>
{/if}
