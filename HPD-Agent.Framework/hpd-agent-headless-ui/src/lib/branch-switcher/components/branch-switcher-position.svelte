<script lang="ts">
	import { mergeProps } from 'svelte-toolbelt';
	import type { BranchSwitcherPositionProps } from '../types.js';
	import { BranchSwitcherPositionState } from '../branch-switcher.svelte.js';

	let { child, children, ...restProps }: BranchSwitcherPositionProps = $props();

	const positionState = BranchSwitcherPositionState.create();

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
