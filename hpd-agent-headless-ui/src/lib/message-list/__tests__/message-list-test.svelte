<script lang="ts">
	import MessageList from '../components/message-list.svelte';
	import type { Message } from '../../agent/types.js';

	let {
		messages = [] as Message[],
		autoScroll = true,
		keyboardNav = true,
		'aria-label': ariaLabel = 'Message history',
		...restProps
	} = $props();
</script>

<MessageList data-testid="message-list" {messages} {autoScroll} {keyboardNav} aria-label={ariaLabel} {...restProps}>
	{#each messages as message (message.id)}
		<div data-message-id={message.id}>{message.content}</div>
	{/each}
</MessageList>
