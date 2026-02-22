<script lang="ts">
	import { boxWith, mergeProps } from 'svelte-toolbelt';
	import { ArtifactProviderState } from '../artifact.svelte.js';
	import type { ArtifactProviderProps } from '../types.js';

	let {
		onOpenChange,
		child,
		children,
		ref = $bindable(null),
		...restProps
	}: ArtifactProviderProps = $props();

	// Create provider state with boxed values
	const providerState = ArtifactProviderState.create({
		onOpenChange: boxWith(() => onOpenChange)
	});

	// Props for the provider container
	const props = $derived(
		mergeProps(restProps, {
			[providerState.getHPDAttr('provider')]: '',
			...providerState.sharedProps
		})
	);
</script>

{#if child}
	{@render child({ props })}
{:else}
	<div bind:this={ref} {...props}>
		{@render children?.()}
	</div>
{/if}
