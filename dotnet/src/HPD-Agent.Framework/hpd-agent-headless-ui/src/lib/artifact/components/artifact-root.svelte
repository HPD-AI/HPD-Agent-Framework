<script lang="ts">
	import { boxWith, mergeProps } from 'svelte-toolbelt';
	import { ArtifactRootState } from '../artifact.svelte.js';
	import type { ArtifactRootProps } from '../types.js';

	let {
		id,
		defaultOpen = false,
		onOpenChange,
		child,
		children,
		ref = $bindable(null),
		...restProps
	}: ArtifactRootProps = $props();

	// Create root state with boxed values
	const rootState = ArtifactRootState.create({
		id: boxWith(() => id),
		defaultOpen: boxWith(() => defaultOpen),
		onOpenChange: boxWith(() => onOpenChange)
	});

	// Props for the root container
	const props = $derived(
		mergeProps(restProps, {
			[rootState.getHPDAttr('root')]: '',
			...rootState.sharedProps
		})
	);

	// Snippet props exposed to children
	const snippetProps = $derived({
		open: rootState.open,
		setOpen: (open: boolean) => rootState.setOpen(open),
		toggle: () => rootState.toggle()
	});
</script>

{#if child}
	{@render child({ props, ...snippetProps })}
{:else}
	<div bind:this={ref} {...props}>
		{@render children?.(snippetProps)}
	</div>
{/if}
