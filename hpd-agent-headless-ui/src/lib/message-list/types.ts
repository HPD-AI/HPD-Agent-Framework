/**
 * MessageList Component Types
 */

import type { HTMLAttributes } from 'svelte/elements';
import type { Message } from '../agent/types.js';
import type { WithChild } from '$lib/internal/types.js';

/**
 * Props for the MessageList component
 */
export type MessageListProps = MessageListPropsWithoutHTML &
	Omit<HTMLAttributes<HTMLDivElement>, keyof MessageListPropsWithoutHTML>;

/**
 * MessageList props without HTML attributes
 */
export type MessageListPropsWithoutHTML = WithChild<
	{
		/**
		 * Array of messages to display
		 */
		messages: Message[];

		/**
		 * Whether to auto-scroll to bottom when new messages arrive
		 * @default true
		 */
		autoScroll?: boolean;

		/**
		 * Whether to enable keyboard navigation (arrow keys)
		 * @default true
		 */
		keyboardNav?: boolean;

		/**
		 * ARIA label for the message list
		 * @default "Message history"
		 */
		'aria-label'?: string;

		/**
		 * Element reference (bindable)
		 */
		ref?: HTMLDivElement | null;

		/**
		 * Unique ID for the message list
		 */
		id?: string;
	},
	MessageListSnippetProps
>;

/**
 * Props passed to snippet customization
 */
export type MessageListSnippetProps = {
	/**
	 * Array of messages being displayed
	 */
	messages: Message[];

	/**
	 * Number of messages in the list
	 */
	messageCount: number;
};
