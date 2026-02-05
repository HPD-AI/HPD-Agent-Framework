<script lang="ts">
	import { mergeProps } from 'svelte-toolbelt';
	import { ArtifactTriggerState } from '../artifact.svelte.js';
	import type { ArtifactTriggerProps } from '../types.js';

	let {
		disabled = false,
		child,
		children,
		ref = $bindable(null),
		...restProps
	}: ArtifactTriggerProps = $props();

	// Create trigger state
	const triggerState = ArtifactTriggerState.create();

	// Handle click
	function handleClick(e: MouseEvent) {
		if (disabled) {
			e.preventDefault();
			return;
		}
		triggerState.toggle();
	}

	// Props for the trigger button
	const props = $derived(
		mergeProps(restProps, {
			[triggerState.getHPDAttr('trigger')]: '',
			type: 'button',
			disabled: disabled || undefined,
			onclick: handleClick,
			...triggerState.sharedProps
		})
	);

	// Snippet props exposed to children
	const snippetProps = $derived({
		open: triggerState.open
	});
</script>

{#if child}
	{@render child({ props, ...snippetProps })}
{:else}
	<button bind:this={ref} {...props}>
		{@render children?.(snippetProps)}
	</button>
{/if}
