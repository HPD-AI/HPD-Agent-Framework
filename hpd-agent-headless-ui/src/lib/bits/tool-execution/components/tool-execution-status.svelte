<script lang="ts">
	/**
	 * ToolExecution.Status - Status badge component
	 *
	 * Displays the current status of the tool execution
	 * (pending, executing, complete, error)
	 */

	import { mergeProps } from 'svelte-toolbelt';
	import type { ToolExecutionStatusProps } from '../types.js';
	import { ToolExecutionStatusState } from '../tool-execution.svelte.js';

	let { child, children, ...restProps }: ToolExecutionStatusProps = $props();

	const state = ToolExecutionStatusState.create();
	const mergedProps = $derived(mergeProps(restProps, state.props));
</script>

{#if child}
	{@render child({ props: mergedProps })}
{:else}
	<span {...mergedProps}>
		{@render children?.(state.snippetProps)}
	</span>
{/if}
