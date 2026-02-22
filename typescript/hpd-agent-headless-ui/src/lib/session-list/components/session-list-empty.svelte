<script lang="ts">
	import { mergeProps } from 'svelte-toolbelt';
	import type { SessionListEmptyProps } from '../types.js';
	import { SessionListEmptyState } from '../session-list.svelte.js';

	let { child, children, ...restProps }: SessionListEmptyProps = $props();

	const emptyState = SessionListEmptyState.create();

	const mergedProps = $derived(mergeProps(restProps, emptyState.props));
</script>

{#if child}
	{@render child({ props: mergedProps })}
{:else}
	<div {...mergedProps}>
		{@render children?.()}
	</div>
{/if}
