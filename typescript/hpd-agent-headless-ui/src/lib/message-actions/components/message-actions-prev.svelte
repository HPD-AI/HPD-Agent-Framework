<script lang="ts">
	import { mergeProps, boxWith } from 'svelte-toolbelt';
	import type { MessageActionsPrevProps } from '../types.js';
	import { MessageActionsPrevState } from '../message-actions.svelte.js';

	let {
		'aria-label': ariaLabel = 'Previous version',
		child,
		children,
		...restProps
	}: MessageActionsPrevProps = $props();

	const prevState = MessageActionsPrevState.create(boxWith(() => ariaLabel));

	const mergedProps = $derived(mergeProps(restProps, prevState.props));
</script>

{#if child}
	{@render child({ props: mergedProps })}
{:else}
	<button {...mergedProps}>
		{@render children?.()}
	</button>
{/if}
