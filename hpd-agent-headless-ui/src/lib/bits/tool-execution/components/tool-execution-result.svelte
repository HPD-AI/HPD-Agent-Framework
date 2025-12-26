<script lang="ts">
	/**
	 * ToolExecution.Result - Tool result/error display
	 *
	 * Displays the result or error from tool execution
	 */

	import { mergeProps } from 'svelte-toolbelt';
	import type { ToolExecutionResultProps } from '../types.js';
	import { ToolExecutionResultState } from '../tool-execution.svelte.js';

	let { child, children, ...restProps }: ToolExecutionResultProps = $props();

	const state = ToolExecutionResultState.create();
	const mergedProps = $derived(mergeProps(restProps, state.props));
</script>

{#if child}
	{@render child({ props: mergedProps })}
{:else}
	<div {...mergedProps}>
		{@render children?.(state.snippetProps)}
	</div>
{/if}
