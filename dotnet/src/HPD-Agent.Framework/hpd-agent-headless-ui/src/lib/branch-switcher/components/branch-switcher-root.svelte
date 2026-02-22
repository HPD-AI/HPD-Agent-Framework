<script lang="ts">
	import { mergeProps, boxWith } from 'svelte-toolbelt';
	import type { BranchSwitcherRootProps } from '../types.js';
	import { BranchSwitcherRootState } from '../branch-switcher.svelte.js';

	let {
		branch,
		child,
		children,
		...restProps
	}: BranchSwitcherRootProps = $props();

	const rootState = BranchSwitcherRootState.create({
		branch: boxWith(() => branch),
	});

	const mergedProps = $derived(mergeProps(restProps, rootState.props));
</script>

{#if child}
	{@render child({ props: mergedProps, ...rootState.snippetProps })}
{:else}
	<div {...mergedProps}>
		{@render children?.(rootState.snippetProps)}
	</div>
{/if}
