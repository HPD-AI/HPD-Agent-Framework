<script lang="ts">
	import { MessageState } from '../message.svelte.js';
	import type { MessageProps } from '../types.js';

	let {
		message,
		child,
		children,
		class: className,
		...restProps
	}: MessageProps = $props();

	// Recreate state instance when message identity changes
	// This ensures we track the CURRENT message object, not the initial one
	let state = $derived(new MessageState(message));

	// Merge class names
	const mergedProps = $derived({
		...state.props,
		...restProps,
		class: className ? `${state.props.class || ''} ${className}`.trim() : state.props.class
	});
</script>

{#if child}
	<!-- Full HTML control via child snippet -->
	{@render child({ props: mergedProps })}
{:else if children}
	<!-- Content customization via children snippet -->
	{@render children(state.snippetProps)}
{:else}
	<!-- Default rendering (minimal, users should customize) -->
	<div {...mergedProps}>
		{#if state.thinking}
			<div data-message-part="thinking">Thinking...</div>
		{/if}

		{#if state.hasReasoning}
			<div data-message-part="reasoning">
				{state.reasoning}
			</div>
		{/if}

		<div data-message-part="content">
			{state.content}
			{#if state.streaming}
				<span data-message-part="cursor">▊</span>
			{/if}
		</div>

		{#if state.hasTools}
			<div data-message-part="tools">
				{#each state.toolCalls as tool}
					<div data-tool-id={tool.callId} data-tool-status={tool.status}>
						Tool: {tool.name} ({tool.status})
					</div>
				{/each}
			</div>
		{/if}
	</div>
{/if}
