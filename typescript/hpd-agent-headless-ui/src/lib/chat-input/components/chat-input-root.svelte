<script lang="ts">
	import { boxWith, mergeProps } from 'svelte-toolbelt';
	import { ChatInputRootState } from '../chat-input.svelte.js';
	import type { ChatInputRootProps } from '../types.js';

	interface Props extends ChatInputRootProps {}

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
	}: Props = $props();

	// Determine if controlled mode at creation
	const isControlled = value !== undefined;

	// Create root state with boxed values
	const rootState = ChatInputRootState.create({
		value: boxWith(() => value),
		defaultValue: boxWith(() => defaultValue),
		disabled: boxWith(() => disabled),
		onSubmit: boxWith(() => onSubmit),
		onChange: boxWith(() => onChange)
	});

	// Sync bindable value with state (only in controlled mode)
	$effect(() => {
		if (isControlled && value !== rootState.value) {
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
