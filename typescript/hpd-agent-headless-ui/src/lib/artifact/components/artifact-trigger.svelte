<script lang="ts">
	import { mergeProps } from 'svelte-toolbelt';
	import { ArtifactTriggerState } from '../artifact.svelte.js';
	import type { ArtifactTriggerComponentProps } from '../types.js';

	let {
		disabled = false,
		child,
		children,
		ref = $bindable(null),
		...restProps
	}: ArtifactTriggerComponentProps = $props();

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
	const mergedProps = $derived(
		mergeProps(restProps as Record<string, unknown>, {
			[triggerState.getHPDAttr('trigger')]: '',
			type: 'button' as const,
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
	{@render child({ props: mergedProps, ...snippetProps })}
{:else}
	<button bind:this={ref} {...mergedProps}>
		{@render children?.(snippetProps)}
	</button>
{/if}
