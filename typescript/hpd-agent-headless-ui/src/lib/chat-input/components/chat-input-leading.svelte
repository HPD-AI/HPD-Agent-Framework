<script lang="ts">
	import { mergeProps, attachRef } from 'svelte-toolbelt';
	import { ChatInputRootState } from '../chat-input.svelte.js';
	import type { ChatInputAccessoryComponentProps } from '../types.js';

	let {
		ref = $bindable(null),
		child,
		children,
		...restProps
	}: ChatInputAccessoryComponentProps = $props();

	// Get shared state from context
	const rootState = ChatInputRootState.get();

	// Ref attachment
	const refAttachment = attachRef(() => ref);

	// Props for snippet
	const snippetProps = $derived({
		value: rootState.value,
		focused: rootState.focused,
		disabled: rootState.disabled,
		isEmpty: rootState.isEmpty,
		characterCount: rootState.characterCount,
		canSubmit: rootState.canSubmit,
		submit: () => rootState.submit(),
		clear: () => rootState.clear()
	});

	// Merge props
	const mergedProps = $derived(
		mergeProps(restProps as Record<string, unknown>, {
			[rootState.getHPDAttr('leading')]: '',
			...rootState.sharedProps,
			...refAttachment
		})
	);
</script>

{#if child}
	{@render child({ ...snippetProps, props: mergedProps })}
{:else if children}
	<div {...mergedProps}>
		{@render children(snippetProps)}
	</div>
{:else}
	<div {...mergedProps}></div>
{/if}
