<script lang="ts">
	import { boxWith, mergeProps } from 'svelte-toolbelt';
	import type { MessageListProps } from '../types.js';
	import { MessageListState } from '../message-list.svelte.js';
	import { createId } from '$lib/internal/create-id.js';

	const uid = $props.id();

	let {
		messages,
		autoScroll = true,
		keyboardNav = true,
		'aria-label': ariaLabel = 'Message history',
		ref = $bindable(null),
		id = createId(uid),
		child,
		children,
		...restProps
	}: MessageListProps = $props();

	const listState = MessageListState.create({
		messages: boxWith(() => messages),
		autoScroll: boxWith(() => autoScroll),
		keyboardNav: boxWith(() => keyboardNav),
		ariaLabel: boxWith(() => ariaLabel),
		id: boxWith(() => id),
		ref: boxWith(
			() => ref,
			(v) => (ref = v)
		),
	});

	const mergedProps = $derived(mergeProps(restProps, listState.props));

	// Snippet props
	const snippetProps = $derived({
		messages,
		messageCount: listState.messageCount,
	});
</script>

{#if child}
	{@render child({ ...snippetProps, props: mergedProps })}
{:else}
	<div {...mergedProps}>
		{@render children?.(snippetProps)}
	</div>
{/if}
