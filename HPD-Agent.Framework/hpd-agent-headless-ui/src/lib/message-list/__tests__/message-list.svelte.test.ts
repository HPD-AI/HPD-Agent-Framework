/**
 * MessageList Component Tests
 * Comprehensive testing with comprehensive accessibility checks
 */

import { describe, it, expect } from 'vitest';
import { page } from 'vitest/browser';
import { render } from 'vitest-browser-svelte';
import MessageListTest from './message-list-test.svelte';
import type { Message, MessageRole } from '../../agent/types.ts';

describe('MessageList', () => {
	describe('ARIA Attributes & Accessibility', () => {
		it('should have correct ARIA role and attributes', async () => {
			render(MessageListTest, {
				props: { messages: [] },
			});

			const list = page.getByTestId('message-list');

			// Using expect.element() for consistent testing
			await expect.element(list).toHaveAttribute('role', 'log');
			await expect.element(list).toHaveAttribute('aria-label', 'Message history');
			await expect.element(list).toHaveAttribute('aria-live', 'polite');
			await expect.element(list).toHaveAttribute('aria-atomic', 'false');
		});

		it('should support custom aria-label', async () => {
			render(MessageListTest, {
				props: { messages: [], 'aria-label': 'Chat conversation' },
			});

			const list = page.getByTestId('message-list');
			await expect.element(list).toHaveAttribute('aria-label', 'Chat conversation');
		});

		it('should have tabindex=0 when keyboard navigation enabled', async () => {
			render(MessageListTest, {
				props: { messages: [], keyboardNav: true },
			});

			const list = page.getByTestId('message-list');
			await expect.element(list).toHaveAttribute('tabindex', '0');
		});

		it('should have tabindex=-1 when keyboard navigation disabled', async () => {
			render(MessageListTest, {
				props: { messages: [], keyboardNav: false },
			});

			const list = page.getByTestId('message-list');
			await expect.element(list).toHaveAttribute('tabindex', '-1');
		});
	});

	describe('Data Attributes', () => {
		it('should have bits data attribute', async () => {
			render(MessageListTest, {
				props: { messages: [] },
			});

			const list = page.getByTestId('message-list');
			await expect.element(list).toHaveAttribute('data-message-list');
		});

		it('should display message count', async () => {
			const messages: Message[] = [
				{
					id: '1',
					role: 'user',
					content: 'Hello',
					streaming: false,
					thinking: false,
					reasoning: '',
					toolCalls: [],
					timestamp: new Date(),
				},
				{
					id: '2',
					role: 'assistant',
					content: 'Hi there!',
					streaming: false,
					thinking: false,
					reasoning: '',
					toolCalls: [],
					timestamp: new Date(),
				},
			];

			render(MessageListTest, { props: { messages } });
			const list = page.getByTestId('message-list');
			await expect.element(list).toHaveAttribute('data-message-count', '2');
		});

		it('should update message count reactively', async () => {
			const messages: Message[] = [
				{
					id: '1',
					role: 'user',
					content: 'Hello',
					streaming: false,
					thinking: false,
					reasoning: '',
					toolCalls: [],
					timestamp: new Date(),
				},
			];

			const { container, rerender } = render(MessageListTest, { props: { messages } });

			let list = page.getByTestId('message-list');
			await expect.element(list).toHaveAttribute('data-message-count', '1');

			// Add another message
			const updatedMessages: Message[] = [
				...messages,
				{
					id: '2',
					role: 'assistant' as MessageRole,
					content: 'Hi!',
					streaming: false,
					thinking: false,
					reasoning: '',
					toolCalls: [],
					timestamp: new Date(),
				},
			];

			await rerender({ messages: updatedMessages });
			list = page.getByTestId('message-list');
			await expect.element(list).toHaveAttribute('data-message-count', '2');
		});
	});

	describe('Message Rendering', () => {
		it('should render empty state with zero messages', async () => {
			render(MessageListTest, {
				props: { messages: [] },
			});

			const list = page.getByTestId('message-list');
			await expect.element(list).toHaveAttribute('data-message-count', '0');
		});

		it('should render multiple messages', async () => {
			const messages: Message[] = Array.from({ length: 5 }, (_, i) => ({
				id: `msg-${i}`,
				role: (i % 2 === 0 ? 'user' : 'assistant') as MessageRole,
				content: `Message ${i}`,
				streaming: false,
				thinking: false,
				reasoning: '',
				toolCalls: [],
				timestamp: new Date(),
			}));

			render(MessageListTest, { props: { messages } });
			const list = page.getByTestId('message-list');
			await expect.element(list).toHaveAttribute('data-message-count', '5');
		});
	});

	describe('Component Props', () => {
		it('should accept and apply custom id', async () => {
			render(MessageListTest, {
				props: { messages: [], id: 'custom-message-list' },
			});

			const list = page.getByTestId('message-list');
			await expect.element(list).toHaveAttribute('id', 'custom-message-list');
		});

		it('should merge custom class names', async () => {
			render(MessageListTest, {
				props: { messages: [], class: 'custom-class' },
			});

			const list = page.getByTestId('message-list');
			await expect.element(list).toHaveClass('custom-class');
		});
	});
});
