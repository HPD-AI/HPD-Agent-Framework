/**
 * MessageActions State Management
 *
 * Compound headless component for message-level actions: Edit, Retry, and
 * branch navigation (Prev / Next / Position).
 *
 * The branch navigator only becomes active when the active branch was forked
 * at this message index — i.e. this is the message bubble where the divergence
 * happened. That means the ◀ ▶ counter is naturally scoped to the right bubble
 * without any manual condition in the consumer template.
 *
 * Parts: Root, EditButton, RetryButton, Prev, Next, Position
 *
 * @example
 * ```svelte
 * <MessageActions.Root {workspace} {messageIndex} role="user" branch={workspace.activeBranch}>
 *   <MessageActions.EditButton>
 *     {#snippet children({ edit, status })}
 *       <button onclick={() => edit(draft)} disabled={status === 'pending'}>Edit</button>
 *     {/snippet}
 *   </MessageActions.EditButton>
 *   <MessageActions.RetryButton>
 *     {#snippet children({ retry, status })}
 *       <button onclick={retry} disabled={status === 'pending'}>Retry</button>
 *     {/snippet}
 *   </MessageActions.RetryButton>
 *   {#snippet children({ hasSiblings })}
 *     {#if hasSiblings}
 *       <MessageActions.Prev />
 *       <MessageActions.Position />
 *       <MessageActions.Next />
 *     {/if}
 *   {/snippet}
 * </MessageActions.Root>
 * ```
 */

import { Context } from 'runed';
import { type ReadableBox } from 'svelte-toolbelt';
import { createHPDAttrs, boolToEmptyStrOrUndef } from '$lib/internal/attrs.js';
import type { Workspace } from '../workspace/types.ts';
import type { MessageRole } from '../agent/types.ts';
import type { Branch } from '@hpd/hpd-agent-client';
import type {
	MessageActionStatus,
	MessageActionsRootHTMLProps,
	MessageActionsRootSnippetProps,
	MessageActionsEditButtonHTMLProps,
	MessageActionsEditButtonSnippetProps,
	MessageActionsRetryButtonHTMLProps,
	MessageActionsRetryButtonSnippetProps,
	MessageActionsCopyButtonHTMLProps,
	MessageActionsCopyButtonSnippetProps,
	MessageActionsPrevHTMLProps,
	MessageActionsNextHTMLProps,
	MessageActionsPositionHTMLProps,
	MessageActionsPositionSnippetProps,
} from './types.js';

// ============================================
// Data Attributes
// ============================================

export const messageActionsAttrs = createHPDAttrs({
	component: 'message-actions',
	parts: ['root', 'edit', 'retry', 'prev', 'next', 'position'] as const,
});

// ============================================
// Root Context
// ============================================

const MessageActionsRootContext = new Context<MessageActionsRootState>('MessageActions.Root');

// ============================================
// Root State
// ============================================

interface MessageActionsRootStateOpts {
	workspace: ReadableBox<Workspace>;
	messageIndex: ReadableBox<number>;
	role: ReadableBox<MessageRole>;
	branch?: ReadableBox<Branch | null | undefined>;
}

export class MessageActionsRootState {
	readonly #opts: MessageActionsRootStateOpts;

	// Tracks how many actions are currently in-flight
	#pendingCount = $state(0);

	constructor(opts: MessageActionsRootStateOpts) {
		this.#opts = opts;
	}

	static create(opts: MessageActionsRootStateOpts): MessageActionsRootState {
		return MessageActionsRootContext.set(new MessageActionsRootState(opts));
	}

	// ============================================
	// Derived State
	// ============================================

	readonly workspace = $derived.by(() => this.#opts.workspace.current);
	readonly messageIndex = $derived.by(() => this.#opts.messageIndex.current);
	readonly role = $derived.by(() => this.#opts.role.current);
	readonly pending = $derived.by(() => this.#pendingCount > 0);

	// The branch navigator is only active when the active branch was forked
	// at exactly this message index.
	readonly #branch = $derived.by(() => this.#opts.branch?.current ?? null);

	readonly #isForkedHere = $derived.by(() => {
		const branch = this.#branch;
		if (!branch || branch.isOriginal) return false;
		return branch.forkedAtMessageIndex === this.messageIndex;
	});

	readonly hasSiblings = $derived.by(
		() => this.#isForkedHere && (this.#branch?.totalSiblings ?? 0) > 1
	);

	readonly canGoPrevious = $derived.by(
		() => this.#isForkedHere && this.#branch?.previousSiblingId != null
	);

	readonly canGoNext = $derived.by(
		() => this.#isForkedHere && this.#branch?.nextSiblingId != null
	);

	readonly position = $derived.by(() => {
		if (!this.#isForkedHere || !this.#branch) return '';
		return `${this.#branch.siblingIndex + 1} / ${this.#branch.totalSiblings}`;
	});

	readonly positionLabel = $derived.by(() => {
		const branch = this.#branch;
		if (!this.#isForkedHere || !branch || branch.totalSiblings <= 1) return '';
		if (branch.isOriginal) return `Original (1 / ${branch.totalSiblings})`;
		return `Fork ${branch.siblingIndex + 1} / ${branch.totalSiblings}`;
	});

	// ============================================
	// Internal helpers for child states
	// ============================================

	_incrementPending() {
		this.#pendingCount++;
	}

	_decrementPending() {
		this.#pendingCount = Math.max(0, this.#pendingCount - 1);
	}

	// ============================================
	// Props
	// ============================================

	get props(): MessageActionsRootHTMLProps {
		return {
			'data-message-actions-root': '',
			'data-role': this.role,
			'data-message-index': this.messageIndex,
			'data-has-siblings': boolToEmptyStrOrUndef(this.hasSiblings),
		};
	}

	get snippetProps(): MessageActionsRootSnippetProps {
		return {
			role: this.role,
			messageIndex: this.messageIndex,
			pending: this.pending,
			hasSiblings: this.hasSiblings,
			position: this.position,
		};
	}
}

// ============================================
// EditButton State
// ============================================

interface MessageActionsEditButtonStateOpts {
	ariaLabel: ReadableBox<string>;
	onSuccess: ReadableBox<(() => void) | undefined>;
	onError: ReadableBox<((err: unknown) => void) | undefined>;
}

export class MessageActionsEditButtonState {
	readonly #root: MessageActionsRootState;
	readonly #opts: MessageActionsEditButtonStateOpts;

	#status = $state<MessageActionStatus>('idle');

	constructor(root: MessageActionsRootState, opts: MessageActionsEditButtonStateOpts) {
		this.#root = root;
		this.#opts = opts;
	}

	static create(opts: MessageActionsEditButtonStateOpts): MessageActionsEditButtonState {
		const root = MessageActionsRootContext.get();
		return new MessageActionsEditButtonState(root, opts);
	}

	// Only user messages can be edited
	readonly disabled = $derived.by(
		() => this.#root.role !== 'user' || this.#root.pending
	);

	readonly edit = async (newContent: string): Promise<void> => {
		if (this.disabled) return;
		this.#status = 'pending';
		this.#root._incrementPending();
		try {
			await this.#root.workspace.editMessage(this.#root.messageIndex, newContent);
			this.#status = 'idle';
			this.#opts.onSuccess.current?.();
		} catch (err) {
			this.#status = 'error';
			this.#opts.onError.current?.(err);
		} finally {
			this.#root._decrementPending();
		}
	};

	get props(): MessageActionsEditButtonHTMLProps {
		return {
			'data-message-actions-edit': '',
			'data-status': this.#status,
			'data-disabled': boolToEmptyStrOrUndef(this.disabled),
			type: 'button',
			disabled: this.disabled,
			'aria-label': this.#opts.ariaLabel.current,
		};
	}

	get snippetProps(): MessageActionsEditButtonSnippetProps {
		return {
			status: this.#status,
			disabled: this.disabled,
			edit: this.edit,
		};
	}
}

// ============================================
// RetryButton State
// ============================================

interface MessageActionsRetryButtonStateOpts {
	ariaLabel: ReadableBox<string>;
	onSuccess: ReadableBox<(() => void) | undefined>;
	onError: ReadableBox<((err: unknown) => void) | undefined>;
}

export class MessageActionsRetryButtonState {
	readonly #root: MessageActionsRootState;
	readonly #opts: MessageActionsRetryButtonStateOpts;

	#status = $state<MessageActionStatus>('idle');

	constructor(root: MessageActionsRootState, opts: MessageActionsRetryButtonStateOpts) {
		this.#root = root;
		this.#opts = opts;
	}

	static create(opts: MessageActionsRetryButtonStateOpts): MessageActionsRetryButtonState {
		const root = MessageActionsRootContext.get();
		return new MessageActionsRetryButtonState(root, opts);
	}

	// Retry is available for user messages (re-send same content)
	// and assistant messages (retry the preceding user message)
	// System messages cannot be retried
	readonly disabled = $derived.by(
		() => this.#root.role === 'system' || this.#root.pending
	);

	readonly retry = async (): Promise<void> => {
		if (this.disabled) return;

		const { workspace, messageIndex, role } = this.#root;
		const state = workspace.state;
		if (!state) return;

		// For a user message: re-send it (fork at same index, same content)
		// For an assistant message: find the preceding user message and re-send that
		let targetIndex = messageIndex;
		if (role === 'assistant') {
			const messages = state.messages;
			let found = -1;
			for (let i = messageIndex - 1; i >= 0; i--) {
				if (messages[i].role === 'user') {
					found = i;
					break;
				}
			}
			if (found === -1) return;
			targetIndex = found;
		}

		const messages = state.messages;
		const targetMessage = messages[targetIndex];
		if (!targetMessage) return;

		this.#status = 'pending';
		this.#root._incrementPending();
		try {
			await workspace.editMessage(targetIndex, targetMessage.content);
			this.#status = 'idle';
			this.#opts.onSuccess.current?.();
		} catch (err) {
			this.#status = 'error';
			this.#opts.onError.current?.(err);
		} finally {
			this.#root._decrementPending();
		}
	};

	get props(): MessageActionsRetryButtonHTMLProps {
		return {
			'data-message-actions-retry': '',
			'data-status': this.#status,
			'data-disabled': boolToEmptyStrOrUndef(this.disabled),
			type: 'button',
			disabled: this.disabled,
			'aria-label': this.#opts.ariaLabel.current,
		};
	}

	get snippetProps(): MessageActionsRetryButtonSnippetProps {
		return {
			status: this.#status,
			disabled: this.disabled,
			retry: this.retry,
		};
	}
}

// ============================================
// CopyButton State
// ============================================

interface MessageActionsCopyButtonStateOpts {
	content: ReadableBox<string>;
	resetDelay: ReadableBox<number>;
	ariaLabel: ReadableBox<string>;
	onSuccess: ReadableBox<(() => void) | undefined>;
	onError: ReadableBox<((err: unknown) => void) | undefined>;
}

export class MessageActionsCopyButtonState {
	readonly #opts: MessageActionsCopyButtonStateOpts;

	#copied = $state(false);
	#resetTimer: ReturnType<typeof setTimeout> | null = null;

	constructor(opts: MessageActionsCopyButtonStateOpts) {
		this.#opts = opts;
	}

	static create(opts: MessageActionsCopyButtonStateOpts): MessageActionsCopyButtonState {
		return new MessageActionsCopyButtonState(opts);
	}

	readonly copy = async (): Promise<void> => {
		try {
			await navigator.clipboard.writeText(this.#opts.content.current);
			if (this.#resetTimer !== null) clearTimeout(this.#resetTimer);
			this.#copied = true;
			this.#resetTimer = setTimeout(() => {
				this.#copied = false;
				this.#resetTimer = null;
			}, this.#opts.resetDelay.current);
			this.#opts.onSuccess.current?.();
		} catch (err) {
			this.#opts.onError.current?.(err);
		}
	};

	get props(): MessageActionsCopyButtonHTMLProps {
		return {
			'data-message-actions-copy': '',
			'data-copied': boolToEmptyStrOrUndef(this.#copied),
			type: 'button',
			'aria-label': this.#opts.ariaLabel.current,
		};
	}

	get snippetProps(): MessageActionsCopyButtonSnippetProps {
		return {
			copied: this.#copied,
			copy: this.copy,
		};
	}
}

// ============================================
// Prev State
// ============================================

export class MessageActionsPrevState {
	readonly #root: MessageActionsRootState;
	readonly #ariaLabel: ReadableBox<string>;

	constructor(root: MessageActionsRootState, ariaLabel: ReadableBox<string>) {
		this.#root = root;
		this.#ariaLabel = ariaLabel;
	}

	static create(ariaLabel: ReadableBox<string>): MessageActionsPrevState {
		const root = MessageActionsRootContext.get();
		return new MessageActionsPrevState(root, ariaLabel);
	}

	get props(): MessageActionsPrevHTMLProps {
		return {
			'data-message-actions-prev': '',
			'data-disabled': boolToEmptyStrOrUndef(!this.#root.canGoPrevious),
			type: 'button',
			disabled: !this.#root.canGoPrevious,
			'aria-label': this.#ariaLabel.current,
		};
	}
}

// ============================================
// Next State
// ============================================

export class MessageActionsNextState {
	readonly #root: MessageActionsRootState;
	readonly #ariaLabel: ReadableBox<string>;

	constructor(root: MessageActionsRootState, ariaLabel: ReadableBox<string>) {
		this.#root = root;
		this.#ariaLabel = ariaLabel;
	}

	static create(ariaLabel: ReadableBox<string>): MessageActionsNextState {
		const root = MessageActionsRootContext.get();
		return new MessageActionsNextState(root, ariaLabel);
	}

	get props(): MessageActionsNextHTMLProps {
		return {
			'data-message-actions-next': '',
			'data-disabled': boolToEmptyStrOrUndef(!this.#root.canGoNext),
			type: 'button',
			disabled: !this.#root.canGoNext,
			'aria-label': this.#ariaLabel.current,
		};
	}
}

// ============================================
// Position State
// ============================================

export class MessageActionsPositionState {
	readonly #root: MessageActionsRootState;

	constructor(root: MessageActionsRootState) {
		this.#root = root;
	}

	static create(): MessageActionsPositionState {
		const root = MessageActionsRootContext.get();
		return new MessageActionsPositionState(root);
	}

	get props(): MessageActionsPositionHTMLProps {
		return {
			'data-message-actions-position': '',
			'aria-live': 'polite',
			'aria-atomic': 'true',
		};
	}

	get snippetProps(): MessageActionsPositionSnippetProps {
		return {
			position: this.#root.position,
			label: this.#root.positionLabel,
		};
	}
}
