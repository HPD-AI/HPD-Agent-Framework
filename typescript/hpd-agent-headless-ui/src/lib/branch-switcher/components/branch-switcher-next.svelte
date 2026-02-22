<script lang="ts">
	import { mergeProps, boxWith } from 'svelte-toolbelt';
	import type { BranchSwitcherNextProps } from '../types.js';
	import { BranchSwitcherNextState } from '../branch-switcher.svelte.js';

	let {
		'aria-label': ariaLabel = 'Next branch',
		child,
		children,
		...restProps
	}: BranchSwitcherNextProps = $props();

	const nextState = BranchSwitcherNextState.create(boxWith(() => ariaLabel));

	const mergedProps = $derived(mergeProps(restProps, nextState.props));
</script>

{#if child}
	{@render child({ props: mergedProps })}
{:else}
	<button {...mergedProps}>
		{@render children?.()}
	</button>
{/if}
