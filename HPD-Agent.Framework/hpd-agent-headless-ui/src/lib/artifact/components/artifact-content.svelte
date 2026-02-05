<script lang="ts">
	import { mergeProps } from 'svelte-toolbelt';
	import { ArtifactProviderState, artifactAttrs } from '../artifact.svelte.js';
	import type { ArtifactContentProps } from '../types.js';

	let { child, children, ref = $bindable(null), ...restProps }: ArtifactContentProps = $props();

	// Get provider state to access content snippet
	const providerState = ArtifactProviderState.get();

	// Build style string - merge user styles with default full-height layout
	const defaultStyle = 'height: 100%; min-height: 0; flex: 1; display: flex; flex-direction: column;';
	const mergedStyle = $derived(
		restProps.style ? `${defaultStyle} ${restProps.style}` : defaultStyle
	);

	// Props for the content container - includes default styles for full height
	const props = $derived(
		mergeProps(restProps, {
			[artifactAttrs.getAttr('content')]: '',
			style: mergedStyle
		})
	);
</script>

{#if child}
	{@render child({ props })}
{:else}
	<div bind:this={ref} {...props}>
		{#if providerState.content}
			{@render providerState.content()}
		{:else}
			{@render children?.()}
		{/if}
	</div>
{/if}
