<script lang="ts">
	import { boxWith, mergeProps } from 'svelte-toolbelt';
	import { onDestroy } from 'svelte';
	import { createId } from '$lib/internal/create-id.js';
	import { InputState } from '../input.svelte.js';
	import type { InputProps } from '../types.js';

	const uid = $props.id();

	let {
		id = createId(uid),
		ref = $bindable(null),
		value = $bindable(),
		defaultValue = '',
		onChange,
		onSubmit,
		disabled = false,
		maxRows = 5,
		placeholder = 'Type a message...',
		autoFocus = false,
		name,
		required = false,
		'aria-label': ariaLabel = 'Message input',
		child,
		class: className,
		...restProps
	}: InputProps = $props();

	// Determine if controlled mode
	const isControlled = value !== undefined;
	// Capture defaultValue once (it shouldn't change after mount per React/Svelte conventions)
	let internalValue = $state(defaultValue ?? '');

	// Sync internal value with controlled value
	$effect(() => {
		if (isControlled && value !== undefined) {
			internalValue = value;
		}
	});

	// Resolved value (controlled takes precedence)
	const resolvedValue = $derived(isControlled ? (value ?? '') : internalValue);

	// Sync rows when value changes (for controlled mode or programmatic updates)
	// Note: handleInput also calls updateRows, resulting in double measurement on user input
	// This is acceptable because: (1) ensures controlled mode works, (2) performance impact is minimal
	$effect(() => {
		resolvedValue; // Track changes
		inputState.syncRows();
	});

	// Create state manager
	const inputState = new InputState({
		id: boxWith(() => id),
		ref: boxWith(
			() => ref,
			(v) => (ref = v)
		),
		value: boxWith(
			() => resolvedValue,
			(v) => {
				if (isControlled) {
					value = v;
				} else {
					internalValue = v;
				}
			}
		),
		disabled: boxWith(() => disabled),
		maxRows: boxWith(() => maxRows),
		placeholder: boxWith(() => placeholder),
		autoFocus: boxWith(() => autoFocus),
		name: boxWith(() => name),
		required: boxWith(() => required),
		ariaLabel: boxWith(() => ariaLabel),
	});

	// Event handlers with change event details
	function handleInput(event: Event & { currentTarget: HTMLTextAreaElement }) {
		const textarea = event.currentTarget;
		const newValue = textarea.value;

		inputState.handleInput(event);

		// Trigger onChange callback with the actual textarea value
		// (not inputState.value which may be stale in controlled mode)
		onChange?.({
			reason: 'input-change',
			event,
			value: newValue,
		});
	}

	function handleKeyDown(event: KeyboardEvent) {
		const shouldSubmit = inputState.handleKeyDown(event);

		if (shouldSubmit && onSubmit) {
			event.preventDefault();

			// Only submit if value is not empty (trimmed)
			const trimmedValue = inputState.value.trim();
			if (trimmedValue) {
				onSubmit({
					value: trimmedValue,
					event,
				});

				// NOTE: We don't auto-clear here - let consumers control clearing via bind:value
				// This keeps the component headless and prevents resize bugs
			}
		}
	}

	// Merge props with state props
	const mergedProps = $derived(
		mergeProps(restProps, inputState.props, {
			class: className,
			value: inputState.value,
			oninput: handleInput,
			onfocus: inputState.handleFocus,
			onblur: inputState.handleBlur,
			onkeydown: handleKeyDown,
		})
	);

	// Cleanup when component unmounts
	onDestroy(() => {
		inputState.destroy();
	});
</script>

{#if child}
	{@render child({ props: mergedProps })}
{:else}
	<textarea bind:this={ref} {...mergedProps}></textarea>
{/if}
