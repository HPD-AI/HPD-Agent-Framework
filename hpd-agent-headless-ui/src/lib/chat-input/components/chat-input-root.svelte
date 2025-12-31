<script lang="ts">
	import { boxWith, mergeProps } from 'svelte-toolbelt';
	import { ChatInputRootState } from '../chat-input.svelte.js';
	import type { ChatInputRootProps } from '../types.js';

	let {
		value = $bindable(),
		defaultValue,
		disabled = false,
		onSubmit,
		onChange,
		child,
		children,
		ref = $bindable(null),
		...restProps
	}: ChatInputRootProps = $props();

	// Create root state with boxed values
	const rootState = ChatInputRootState.create({
		value: boxWith(() => value),
		defaultValue: boxWith(() => defaultValue),
		disabled: boxWith(() => disabled),
		onSubmit: boxWith(() => onSubmit),
		onChange: boxWith(() => onChange)
	});

	// Sync bindable value with state
	$effect(() => {
		if (value !== rootState.value) {
			value = rootState.value;
		}
	});

	// Props for the root container
	const props = $derived(
		mergeProps(restProps, {
			[rootState.getHPDAttr('root')]: '',
			...rootState.sharedProps
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
