<script lang="ts">
	import { mergeProps } from 'svelte-toolbelt';
	import { createId } from '$lib/internal/create-id.js';
	import type { InputProps } from '../types.js';
	import { kbd } from '$lib/internal/kbd.js';

	const uid = $props.id();

	let {
		id = createId(uid),
		ref = $bindable(null),
		value = $bindable(),
		defaultValue = '',
		onChange,
		onSubmit,
		disabled = false,
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
	let internalValue = $state(defaultValue ?? '');

	// Sync internal value with controlled value
	$effect(() => {
		if (isControlled && value !== undefined) {
			internalValue = value;
		}
	});

	// Resolved value (controlled takes precedence)
	const resolvedValue = $derived(isControlled ? (value ?? '') : internalValue);

	// Event handlers
	function handleInput(event: Event & { currentTarget: HTMLInputElement }) {
		const input = event.currentTarget;
		const newValue = input.value;

		// Update value
		if (isControlled) {
			value = newValue;
		} else {
			internalValue = newValue;
		}

		// Trigger onChange callback
		onChange?.({
			reason: 'input-change',
			event,
			value: newValue,
		});
	}

	function handleKeyDown(event: KeyboardEvent) {
		// Submit on Enter (no Shift key needed for single-line input)
		if (event.key === kbd.ENTER && !event.isComposing) {
			event.preventDefault();

			// Only submit if value is not empty (trimmed)
			const trimmedValue = resolvedValue.trim();
			if (trimmedValue && onSubmit) {
				onSubmit({
					value: trimmedValue,
					event,
				});
			}
		}
	}

	let focused = $state(false);

	function handleFocus() {
		focused = true;
	}

	function handleBlur() {
		focused = false;
	}

	// Merge props
	const mergedProps = $derived(
		mergeProps(restProps, {
			id,
			type: 'text',
			role: 'textbox',
			'aria-label': ariaLabel,
			'aria-disabled': disabled,
			'data-input': '',
			'data-disabled': disabled ? '' : undefined,
			'data-filled': resolvedValue.length > 0 ? '' : undefined,
			'data-focused': focused ? '' : undefined,
			class: className,
			disabled,
			placeholder,
			autofocus: autoFocus,
			name,
			required,
			value: resolvedValue,
			oninput: handleInput,
			onfocus: handleFocus,
			onblur: handleBlur,
			onkeydown: handleKeyDown,
		})
	);
</script>

{#if child}
	{@render child({ props: mergedProps })}
{:else}
	<input bind:this={ref} {...mergedProps} />
{/if}
