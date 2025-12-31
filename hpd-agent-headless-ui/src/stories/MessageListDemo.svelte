<script lang="ts">
	import { MessageList, Message } from '$lib/index.js';
	import type { Message as MessageType } from '$lib/agent/types.js';

	let {
		messageCount = 3,
		autoScroll = true,
		streaming = false,
		...restProps
	} = $props();

	// Generate sample messages
	const messages = $derived<MessageType[]>(
		Array.from({ length: messageCount }, (_, i) => ({
			id: `msg-${i}`,
			role: i % 2 === 0 ? 'user' : 'assistant',
			content:
				i % 2 === 0
					? `User message ${Math.floor(i / 2) + 1}: Hello, how can you help me?`
					: `Assistant response ${Math.floor(i / 2) + 1}: I'm here to help you with any questions!`,
			streaming: streaming && i === messageCount - 1,
			thinking: false,
			reasoning: '',
			toolCalls: [],
			timestamp: new Date(Date.now() - (messageCount - i) * 60000)
		}))
	);
</script>

<div class="demo-container">
	<div class="chat-window">
		<MessageList.Root {messages} {autoScroll}>
			{#each messages as message (message.id)}
				<Message {message}>
					{#snippet children({ content, role, streaming, status })}
						<div class="message" data-role={role}>
							<div class="message-header">
								<strong class="role">{role === 'user' ? 'You' : 'Assistant'}</strong>
								<span class="timestamp">{message.timestamp.toLocaleTimeString()}</span>
							</div>
							<div class="message-content">
								{content}
								{#if streaming}
									<span class="cursor">▊</span>
								{/if}
							</div>
						</div>
					{/snippet}
				</Message>
			{/each}
		</MessageList.Root>
	</div>
</div>

<style>
	.demo-container {
		padding: 2rem;
		max-width: 800px;
		font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
	}

	.chat-window {
		height: 500px;
		border: 2px solid #ddd;
		border-radius: 12px;
		overflow: hidden;
		display: flex;
		flex-direction: column;
		background: white;
	}

	:global([data-message-list]) {
		flex: 1;
		overflow-y: auto;
		padding: 1.5rem;
		display: flex;
		flex-direction: column;
		gap: 1rem;
	}

	.message {
		padding: 1rem;
		border-radius: 12px;
		max-width: 80%;
		animation: slideIn 0.3s ease-out;
	}

	@keyframes slideIn {
		from {
			opacity: 0;
			transform: translateY(10px);
		}
		to {
			opacity: 1;
			transform: translateY(0);
		}
	}

	.message[data-role='user'] {
		align-self: flex-end;
		background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
		color: white;
	}

	.message[data-role='assistant'] {
		align-self: flex-start;
		background: #f5f5f5;
		color: #333;
	}

	.message-header {
		display: flex;
		justify-content: space-between;
		align-items: center;
		margin-bottom: 0.5rem;
		font-size: 0.85rem;
		opacity: 0.8;
	}

	.role {
		font-weight: 600;
	}

	.timestamp {
		font-size: 0.75rem;
	}

	.message-content {
		line-height: 1.5;
	}

	.cursor {
		display: inline-block;
		margin-left: 2px;
		animation: blink 1s infinite;
	}

	@keyframes blink {
		0%,
		50% {
			opacity: 1;
		}
		51%,
		100% {
			opacity: 0;
		}
	}
</style>
