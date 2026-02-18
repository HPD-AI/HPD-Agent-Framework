<script lang="ts">
	import { mergeProps, boxWith } from 'svelte-toolbelt';
	import type { BranchSwitcherPrevProps } from '../types.js';
	import { BranchSwitcherPrevState } from '../branch-switcher.svelte.js';

	let {
		'aria-label': ariaLabel = 'Previous branch',
		child,
		children,
		...restProps
	}: BranchSwitcherPrevProps = $props();

	const prevState = BranchSwitcherPrevState.create(boxWith(() => ariaLabel));

	const mergedProps = $derived(mergeProps(restProps, prevState.props));
</script>

{#if child}
	{@render child({ props: mergedProps })}
{:else}
	<button {...mergedProps}>
		{@render children?.()}
	</button>
{/if}
