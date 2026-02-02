/**
 * MessageList State Management
 *
 * Container for chat messages with auto-scrolling and keyboard navigation.
 */

import { Context } from 'runed';
import { attachRef, type ReadableBoxedValues, onDestroyEffect } from 'svelte-toolbelt';
import type { Message } from '../agent/types.ts';
import type { RefAttachment, WithRefOpts } from '$lib/internal/types.js';
import { debounce } from '$lib/internal/debounce.js';

const MessageListRootContext = new Context<MessageListState>('MessageList.Root');

interface MessageListStateOpts
	extends WithRefOpts<{}, HTMLDivElement>,
		ReadableBoxedValues<{
			messages: Message[];
			autoScroll: boolean;
			keyboardNav: boolean;
			ariaLabel: string;
		}> {}

export class MessageListState {
	readonly opts: MessageListStateOpts;
	readonly attachment: RefAttachment<HTMLDivElement>;
	#listNode = $state<HTMLDivElement | null>(null);

	/**
	 * Debounced scroll-to-bottom function
	 * Prevents excessive scroll calculations during rapid message updates (streaming)
	 * 16ms = ~1 frame at 60fps, batches multiple updates into single scroll
	 */
	#scrollToBottom = debounce(() => {
		if (this.#listNode) {
			this.#listNode.scrollTop = this.#listNode.scrollHeight;
		}
	}, 16);

	constructor(opts: MessageListStateOpts) {
		this.opts = opts;
		this.attachment = attachRef(opts.ref, (v) => (this.#listNode = v));

		// Auto-scroll effect when messages change
		$effect(() => {
			const messages = this.opts.messages.current;
			const autoScroll = this.opts.autoScroll.current;

			if (autoScroll && this.#listNode && messages.length > 0) {
				// Debounced scroll batches multiple rapid updates
				this.#scrollToBottom();
			}
		});

		// Cleanup debounced function on component destroy (prevent memory leaks)
		onDestroyEffect(() => {
			this.#scrollToBottom.destroy();
		});
	}

	// Derived state as getter
	get messageCount() {
		return this.opts.messages.current.length;
	}

	static create(opts: MessageListStateOpts): MessageListState {
		return MessageListRootContext.set(new MessageListState(opts));
	}

	readonly props = $derived.by(
		() =>
			({
				id: this.opts.id.current,
				'data-message-list': '',
				'data-message-count': this.messageCount,
				role: 'log',
				'aria-label': this.opts.ariaLabel.current,
				'aria-live': 'polite',
				'aria-atomic': 'false',
				tabindex: this.opts.keyboardNav.current ? 0 : -1,
				...this.attachment,
			}) as const
	);
}
