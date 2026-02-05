<script lang="ts">
	import { mergeProps } from 'svelte-toolbelt';
	import { ArtifactProviderState, artifactAttrs } from '../artifact.svelte.js';
	import type { ArtifactTitleProps } from '../types.js';

	let { child, children, ref = $bindable(null), ...restProps }: ArtifactTitleProps = $props();

	// Get provider state to access title snippet
	const providerState = ArtifactProviderState.get();

	// Props for the title container
	const props = $derived(
		mergeProps(restProps, {
			[artifactAttrs.getAttr('title')]: ''
		})
	);
</script>

{#if child}
	{@render child({ props })}
{:else}
	<div bind:this={ref} {...props}>
		{#if providerState.title}
			{@render providerState.title()}
		{:else}
			{@render children?.()}
		{/if}
	</div>
{/if}
