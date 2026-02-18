<script lang="ts">
	import { mergeProps } from 'svelte-toolbelt';
	import type { SessionListCreateButtonProps } from '../types.js';
	import { SessionListCreateButtonState } from '../session-list.svelte.js';

	let { child, children, ...restProps }: SessionListCreateButtonProps = $props();

	const createBtnState = SessionListCreateButtonState.create();

	const mergedProps = $derived(mergeProps(restProps, createBtnState.props));
</script>

{#if child}
	{@render child({ props: mergedProps })}
{:else}
	<button {...mergedProps}>
		{@render children?.()}
	</button>
{/if}
