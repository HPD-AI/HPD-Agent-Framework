<script lang="ts">
	import { mergeProps, boxWith } from 'svelte-toolbelt';
	import type { MessageActionsNextProps } from '../types.js';
	import { MessageActionsNextState } from '../message-actions.svelte.js';

	let {
		'aria-label': ariaLabel = 'Next version',
		child,
		children,
		...restProps
	}: MessageActionsNextProps = $props();

	const nextState = MessageActionsNextState.create(boxWith(() => ariaLabel));

	const mergedProps = $derived(mergeProps(restProps, nextState.props));
</script>

{#if child}
	{@render child({ props: mergedProps })}
{:else}
	<button {...mergedProps}>
		{@render children?.()}
	</button>
{/if}
