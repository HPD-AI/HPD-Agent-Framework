<script lang="ts">
	import { mergeProps } from 'svelte-toolbelt';
	import type { MessageActionsPositionProps } from '../types.js';
	import { MessageActionsPositionState } from '../message-actions.svelte.js';

	let { child, children, ...restProps }: MessageActionsPositionProps = $props();

	const positionState = MessageActionsPositionState.create();

	const mergedProps = $derived(mergeProps(restProps, positionState.props));
</script>

{#if child}
	{@render child({ props: mergedProps, ...positionState.snippetProps })}
{:else}
	<span {...mergedProps}>
		{#if children}
			{@render children(positionState.snippetProps)}
		{:else}
			{positionState.snippetProps.label || positionState.snippetProps.position}
		{/if}
	</span>
{/if}
