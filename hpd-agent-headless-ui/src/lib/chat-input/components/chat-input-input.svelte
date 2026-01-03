<script lang="ts">
	import { mergeProps } from 'svelte-toolbelt';
	import { ChatInputRootState } from '../chat-input.svelte.js';
	import InputRoot from '$lib/input/components/input.svelte';
	import type { ChatInputInputProps } from '../types.js';

	let {
		placeholder = 'Type a message...',
		maxRows = 5,
		minRows = 1,
		disabled,
		ref = $bindable(null),
		child,
		children,
		...restProps
	}: ChatInputInputProps = $props();

	// Get shared state from context
	const rootState = ChatInputRootState.get();

	// Use rootState.value directly - no local state needed
	// Input.Root will be in controlled mode, getting value from rootState

	// Handle value changes from Input.Root
	function handleChange(details: { value: string; reason: string; event?: Event }) {
		rootState.updateValue(details.value, 'user');
	}

	// Handle submit
	function handleSubmit() {
		rootState.submit();
	}

	// Handle focus changes
	function handleFocus() {
		rootState.setFocused(true);
	}

	function handleBlur() {
		rootState.setFocused(false);
	}

	// Resolved disabled state (local prop overrides root)
	const resolvedDisabled = $derived(disabled ?? rootState.disabled);

	// Props for snippet
	const snippetProps = $derived({
		value: rootState.value,
		focused: rootState.focused,
		disabled: resolvedDisabled,
		isEmpty: rootState.isEmpty,
		characterCount: rootState.characterCount,
		canSubmit: rootState.canSubmit
	});

	// Controlled value for Input.Root
	const controlledValue = $derived(rootState.value);

	// Merge props
	const props = $derived(
		mergeProps(restProps, {
			[rootState.getHPDAttr('input')]: '',
			...rootState.sharedProps
		})
	);
</script>

{#if child}
	{@render child({ ...snippetProps, props })}
{:else if children}
	<div {...props}>
		{@render children(snippetProps)}
	</div>
{:else}
	<!-- Default: Use existing Input.Root component wrapped in div with data-chat-input-input -->
	<div {...props}>
		<InputRoot
			bind:ref
			value={controlledValue}
			{placeholder}
			{maxRows}
			disabled={resolvedDisabled}
			onSubmit={handleSubmit}
			onChange={handleChange}
			onfocus={handleFocus}
			onblur={handleBlur}
			{...restProps}
		/>
	</div>
{/if}
