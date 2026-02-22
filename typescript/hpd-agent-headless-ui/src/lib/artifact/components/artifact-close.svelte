<script lang="ts">
	import { mergeProps } from 'svelte-toolbelt';
	import { ArtifactCloseState } from '../artifact.svelte.js';
	import type { ArtifactCloseProps } from '../types.js';

	let {
		disabled = false,
		child,
		children,
		ref = $bindable(null),
		...restProps
	}: ArtifactCloseProps = $props();

	// Create close state
	const closeState = ArtifactCloseState.create();

	// Handle click
	function handleClick(e: MouseEvent) {
		if (disabled) {
			e.preventDefault();
			return;
		}
		closeState.close();
	}

	// Props for the close button
	const props = $derived(
		mergeProps(restProps, {
			[closeState.getHPDAttr('close')]: '',
			type: 'button',
			disabled: disabled || undefined,
			onclick: handleClick,
			...closeState.sharedProps
		})
	);

	// Snippet props exposed to children
	const snippetProps = $derived({
		open: closeState.open
	});
</script>

{#if child}
	{@render child({ props, ...snippetProps })}
{:else}
	<button bind:this={ref} {...props}>
		{@render children?.(snippetProps)}
	</button>
{/if}
