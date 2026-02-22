/**
 * MessageActions Browser Tests
 *
 * Rendered DOM tests using vitest-browser-svelte.
 * Covers: data attributes, ARIA, disabled state, snippet props,
 *         async action behaviour, callbacks, reactivity.
 */

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render } from 'vitest-browser-svelte';
import { page } from 'vitest/browser';
import type { Branch } from '@hpd/hpd-agent-client';
import type { Workspace } from '../../workspace/types.ts';
import type { Message } from '../../agent/types.ts';
import MessageActionsTest from './message-actions-test.svelte';

// ============================================
// Helpers
// ============================================

const createBranch = (overrides: Partial<Branch> = {}): Branch => ({
	id: 'fork-1',
	sessionId: 'session-1',
	name: 'Fork',
	createdAt: new Date().toISOString(),
	lastActivity: new Date().toISOString(),
	messageCount: 2,
	siblingIndex: 1,
	totalSiblings: 3,
	isOriginal: false,
	forkedFrom: 'main',
	forkedAtMessageIndex: 2,
	childBranches: [],
	totalForks: 0,
	previousSiblingId: 'main',
	nextSiblingId: 'fork-2',
	...overrides,
});

const createMessage = (overrides: Partial<Message> = {}): Message => ({
	id: `msg-${Math.random()}`,
	role: 'user',
	content: 'Hello',
	streaming: false,
	thinking: false,
	toolCalls: [],
	timestamp: new Date(),
	...overrides,
});

function makeWorkspace(messages: Message[] = [], editImpl?: () => Promise<void>): Workspace {
	return {
		sessions: [],
		activeSessionId: 'session-1',
		loading: false,
		error: null,
		branches: new Map(),
		activeBranchId: 'main',
		activeBranch: null,
		activeSiblings: [],
		canGoNext: false,
		canGoPrevious: false,
		currentSiblingPosition: { current: 1, total: 1 },
		state: { messages, streaming: false, permissions: [], clarifications: [], clientToolRequests: [] } as any,
		selectSession: vi.fn(),
		createSession: vi.fn(),
		deleteSession: vi.fn(),
		switchBranch: vi.fn(),
		goToNextSibling: vi.fn(),
		goToPreviousSibling: vi.fn(),
		goToSiblingByIndex: vi.fn(),
		editMessage: editImpl ? vi.fn().mockImplementation(editImpl) : vi.fn().mockResolvedValue(undefined),
		deleteBranch: vi.fn(),
		createBranch: vi.fn(),
		refreshBranch: vi.fn(),
		invalidateBranch: vi.fn(),
		send: vi.fn(),
		abort: vi.fn(),
		approve: vi.fn(),
		deny: vi.fn(),
		clarify: vi.fn(),
		clear: vi.fn(),
	};
}

function setup(props: {
	workspace?: Workspace;
	messageIndex?: number;
	role?: Message['role'];
	branch?: Branch | null;
	copyContent?: string;
	onPrev?: () => void;
	onNext?: () => void;
	onEditSuccess?: () => void;
	onEditError?: (err: unknown) => void;
	onRetrySuccess?: () => void;
	onRetryError?: (err: unknown) => void;
	onCopySuccess?: () => void;
	onCopyError?: (err: unknown) => void;
	editAriaLabel?: string;
	retryAriaLabel?: string;
	copyAriaLabel?: string;
	prevAriaLabel?: string;
	nextAriaLabel?: string;
	copyResetDelay?: number;
	editContent?: string;
} = {}) {
	const workspace = props.workspace ?? makeWorkspace();
	render(MessageActionsTest, { props: { workspace, ...props } } as any);

	return {
		root: page.getByTestId('root'),
		editBtn: page.getByTestId('edit-btn'),
		retryBtn: page.getByTestId('retry-btn'),
		copyBtn: page.getByTestId('copy-btn'),
		editTrigger: page.getByTestId('edit-trigger'),
		retryTrigger: page.getByTestId('retry-trigger'),
		copyTrigger: page.getByTestId('copy-trigger'),
		prevBtn: page.getByTestId('prev-btn'),
		nextBtn: page.getByTestId('next-btn'),
		positionEl: page.getByTestId('position-el'),
		snippetHasSiblings: page.getByTestId('snippet-has-siblings'),
		snippetPending: page.getByTestId('snippet-pending'),
		snippetPosition: page.getByTestId('snippet-position'),
		editStatus: page.getByTestId('edit-status'),
		editDisabled: page.getByTestId('edit-disabled'),
		retryStatus: page.getByTestId('retry-status'),
		retryDisabled: page.getByTestId('retry-disabled'),
		copyCopied: page.getByTestId('copy-copied'),
		workspace,
	};
}

// ============================================
// Data Attributes — Root
// ============================================

describe('MessageActions — Root data attributes', () => {
	it('has data-message-actions-root', async () => {
		const t = setup();
		await expect.element(t.root).toHaveAttribute('data-message-actions-root');
	});

	it('has data-role matching role prop', async () => {
		const t = setup({ role: 'assistant' });
		await expect.element(t.root).toHaveAttribute('data-role', 'assistant');
	});

	it('has data-message-index matching messageIndex prop', async () => {
		const t = setup({ messageIndex: 5 });
		await expect.element(t.root).toHaveAttribute('data-message-index', '5');
	});

	it('does not have data-has-siblings when no branch', async () => {
		const t = setup({ branch: null });
		await expect.element(t.root).not.toHaveAttribute('data-has-siblings');
	});

	it('does not have data-has-siblings when branch is original', async () => {
		const t = setup({
			messageIndex: 2,
			branch: createBranch({ isOriginal: true, forkedFrom: undefined, forkedAtMessageIndex: undefined }),
		});
		await expect.element(t.root).not.toHaveAttribute('data-has-siblings');
	});

	it('does not have data-has-siblings when forkedAtMessageIndex does not match', async () => {
		const t = setup({ messageIndex: 0, branch: createBranch({ forkedAtMessageIndex: 3 }) });
		await expect.element(t.root).not.toHaveAttribute('data-has-siblings');
	});

	it('has data-has-siblings when forked at this index with siblings', async () => {
		const t = setup({ messageIndex: 2, branch: createBranch({ forkedAtMessageIndex: 2, totalSiblings: 3 }) });
		await expect.element(t.root).toHaveAttribute('data-has-siblings');
	});
});

// ============================================
// Data Attributes — EditButton
// ============================================

describe('MessageActions — EditButton data attributes', () => {
	it('has data-message-actions-edit', async () => {
		const t = setup({ role: 'user' });
		await expect.element(t.editBtn).toHaveAttribute('data-message-actions-edit');
	});

	it('has type=button', async () => {
		const t = setup({ role: 'user' });
		await expect.element(t.editBtn).toHaveAttribute('type', 'button');
	});

	it('has data-status="idle" initially', async () => {
		const t = setup({ role: 'user' });
		await expect.element(t.editBtn).toHaveAttribute('data-status', 'idle');
	});

	it('has data-disabled when role is assistant', async () => {
		const t = setup({ role: 'assistant' });
		await expect.element(t.editBtn).toHaveAttribute('data-disabled');
	});

	it('has data-disabled when role is system', async () => {
		const t = setup({ role: 'system' });
		await expect.element(t.editBtn).toHaveAttribute('data-disabled');
	});

	it('does not have data-disabled when role is user', async () => {
		const t = setup({ role: 'user' });
		await expect.element(t.editBtn).not.toHaveAttribute('data-disabled');
	});

	it('is disabled (HTML) when role is assistant', async () => {
		const t = setup({ role: 'assistant' });
		await expect.element(t.editBtn).toBeDisabled();
	});

	it('is not disabled (HTML) when role is user', async () => {
		const t = setup({ role: 'user' });
		await expect.element(t.editBtn).not.toBeDisabled();
	});
});

// ============================================
// Data Attributes — RetryButton
// ============================================

describe('MessageActions — RetryButton data attributes', () => {
	it('has data-message-actions-retry', async () => {
		const t = setup();
		await expect.element(t.retryBtn).toHaveAttribute('data-message-actions-retry');
	});

	it('has type=button', async () => {
		const t = setup();
		await expect.element(t.retryBtn).toHaveAttribute('type', 'button');
	});

	it('has data-status="idle" initially', async () => {
		const t = setup();
		await expect.element(t.retryBtn).toHaveAttribute('data-status', 'idle');
	});

	it('has data-disabled when role is system', async () => {
		const t = setup({ role: 'system' });
		await expect.element(t.retryBtn).toHaveAttribute('data-disabled');
	});

	it('does not have data-disabled when role is user', async () => {
		const t = setup({ role: 'user' });
		await expect.element(t.retryBtn).not.toHaveAttribute('data-disabled');
	});

	it('does not have data-disabled when role is assistant', async () => {
		const t = setup({ role: 'assistant' });
		await expect.element(t.retryBtn).not.toHaveAttribute('data-disabled');
	});

	it('is disabled (HTML) when role is system', async () => {
		const t = setup({ role: 'system' });
		await expect.element(t.retryBtn).toBeDisabled();
	});
});

// ============================================
// Data Attributes — CopyButton
// ============================================

describe('MessageActions — CopyButton data attributes', () => {
	beforeEach(() => {
		Object.defineProperty(navigator, 'clipboard', {
			value: { writeText: vi.fn().mockResolvedValue(undefined) },
			configurable: true,
		});
	});

	it('has data-message-actions-copy', async () => {
		const t = setup();
		await expect.element(t.copyBtn).toHaveAttribute('data-message-actions-copy');
	});

	it('has type=button', async () => {
		const t = setup();
		await expect.element(t.copyBtn).toHaveAttribute('type', 'button');
	});

	it('does not have data-copied initially', async () => {
		const t = setup();
		await expect.element(t.copyBtn).not.toHaveAttribute('data-copied');
	});

	it('has data-copied after clicking copy trigger', async () => {
		const t = setup();
		await t.copyTrigger.click();
		await expect.element(t.copyBtn).toHaveAttribute('data-copied');
	});
});

// ============================================
// Data Attributes — Prev / Next / Position
// ============================================

describe('MessageActions — Prev/Next/Position data attributes', () => {
	it('prev has data-message-actions-prev', async () => {
		const t = setup();
		await expect.element(t.prevBtn).toHaveAttribute('data-message-actions-prev');
	});

	it('next has data-message-actions-next', async () => {
		const t = setup();
		await expect.element(t.nextBtn).toHaveAttribute('data-message-actions-next');
	});

	it('position has data-message-actions-position', async () => {
		const t = setup();
		await expect.element(t.positionEl).toHaveAttribute('data-message-actions-position');
	});

	it('position has aria-live="polite"', async () => {
		const t = setup();
		await expect.element(t.positionEl).toHaveAttribute('aria-live', 'polite');
	});

	it('position has aria-atomic="true"', async () => {
		const t = setup();
		await expect.element(t.positionEl).toHaveAttribute('aria-atomic', 'true');
	});

	it('prev and next have type=button', async () => {
		const t = setup();
		await expect.element(t.prevBtn).toHaveAttribute('type', 'button');
		await expect.element(t.nextBtn).toHaveAttribute('type', 'button');
	});

	it('prev is disabled when not forked here', async () => {
		const t = setup({ messageIndex: 0, branch: createBranch({ forkedAtMessageIndex: 5 }) });
		await expect.element(t.prevBtn).toBeDisabled();
	});

	it('next is disabled when not forked here', async () => {
		const t = setup({ messageIndex: 0, branch: createBranch({ forkedAtMessageIndex: 5 }) });
		await expect.element(t.nextBtn).toBeDisabled();
	});

	it('prev is disabled when forked here but no previousSiblingId', async () => {
		const t = setup({
			messageIndex: 2,
			branch: createBranch({ forkedAtMessageIndex: 2, previousSiblingId: undefined }),
		});
		await expect.element(t.prevBtn).toBeDisabled();
	});

	it('prev is enabled when forked here and previousSiblingId exists', async () => {
		const t = setup({
			messageIndex: 2,
			branch: createBranch({ forkedAtMessageIndex: 2, previousSiblingId: 'main' }),
		});
		await expect.element(t.prevBtn).not.toBeDisabled();
	});

	it('next is enabled when forked here and nextSiblingId exists', async () => {
		const t = setup({
			messageIndex: 2,
			branch: createBranch({ forkedAtMessageIndex: 2, nextSiblingId: 'fork-2' }),
		});
		await expect.element(t.nextBtn).not.toBeDisabled();
	});

	it('position shows correct text when forked here', async () => {
		const t = setup({
			messageIndex: 2,
			branch: createBranch({
				forkedAtMessageIndex: 2,
				isOriginal: false,
				siblingIndex: 1,
				totalSiblings: 3,
			}),
		});
		await expect.element(t.positionEl).toHaveTextContent('Fork 2 / 3');
	});

	it('position is empty when not forked here', async () => {
		const t = setup({ messageIndex: 0, branch: null });
		await expect.element(t.positionEl).toHaveTextContent('');
	});

	it('position shows "4 / 4" for last-of-four fork', async () => {
		const t = setup({
			messageIndex: 2,
			branch: createBranch({
				forkedAtMessageIndex: 2,
				isOriginal: false,
				siblingIndex: 3,
				totalSiblings: 4,
			}),
		});
		await expect.element(t.positionEl).toHaveTextContent('Fork 4 / 4');
	});

	it('position shows "1 / 3" for first fork of three', async () => {
		const t = setup({
			messageIndex: 2,
			branch: createBranch({
				forkedAtMessageIndex: 2,
				isOriginal: false,
				siblingIndex: 0,
				totalSiblings: 3,
				previousSiblingId: undefined,
				nextSiblingId: 'fork-2',
			}),
		});
		await expect.element(t.positionEl).toHaveTextContent('Fork 1 / 3');
	});
});

// ============================================
// ARIA labels
// ============================================

describe('MessageActions — ARIA labels', () => {
	it('edit button has default aria-label', async () => {
		const t = setup();
		await expect.element(t.editBtn).toHaveAttribute('aria-label', 'Edit message');
	});

	it('retry button has default aria-label', async () => {
		const t = setup();
		await expect.element(t.retryBtn).toHaveAttribute('aria-label', 'Retry message');
	});

	it('copy button has default aria-label', async () => {
		const t = setup();
		await expect.element(t.copyBtn).toHaveAttribute('aria-label', 'Copy message');
	});

	it('prev has default aria-label', async () => {
		const t = setup();
		await expect.element(t.prevBtn).toHaveAttribute('aria-label', 'Previous version');
	});

	it('next has default aria-label', async () => {
		const t = setup();
		await expect.element(t.nextBtn).toHaveAttribute('aria-label', 'Next version');
	});

	it('edit aria-label is overridable', async () => {
		const t = setup({ editAriaLabel: 'Modify this' });
		await expect.element(t.editBtn).toHaveAttribute('aria-label', 'Modify this');
	});

	it('retry aria-label is overridable', async () => {
		const t = setup({ retryAriaLabel: 'Try again' });
		await expect.element(t.retryBtn).toHaveAttribute('aria-label', 'Try again');
	});

	it('prev aria-label is overridable', async () => {
		const t = setup({ prevAriaLabel: 'Older version' });
		await expect.element(t.prevBtn).toHaveAttribute('aria-label', 'Older version');
	});

	it('next aria-label is overridable', async () => {
		const t = setup({ nextAriaLabel: 'Newer version' });
		await expect.element(t.nextBtn).toHaveAttribute('aria-label', 'Newer version');
	});
});

// ============================================
// Snippet props
// ============================================

describe('MessageActions — Root snippet props', () => {
	it('hasSiblings is false when no branch', async () => {
		const t = setup({ branch: null });
		await expect.element(t.snippetHasSiblings).toHaveTextContent('false');
	});

	it('hasSiblings is true when forked at this index', async () => {
		const t = setup({
			messageIndex: 2,
			branch: createBranch({ forkedAtMessageIndex: 2, totalSiblings: 3 }),
		});
		await expect.element(t.snippetHasSiblings).toHaveTextContent('true');
	});

	it('pending is false initially', async () => {
		const t = setup();
		await expect.element(t.snippetPending).toHaveTextContent('false');
	});

	it('position is empty when no branch', async () => {
		const t = setup({ branch: null });
		await expect.element(t.snippetPosition).toHaveTextContent('');
	});

	it('position shows "2 / 3" when forked here at siblingIndex=1, totalSiblings=3', async () => {
		const t = setup({
			messageIndex: 2,
			branch: createBranch({ forkedAtMessageIndex: 2, siblingIndex: 1, totalSiblings: 3 }),
		});
		await expect.element(t.snippetPosition).toHaveTextContent('2 / 3');
	});
});

describe('MessageActions — EditButton snippet props', () => {
	it('edit-disabled is true for assistant role', async () => {
		const t = setup({ role: 'assistant' });
		await expect.element(t.editDisabled).toHaveTextContent('true');
	});

	it('edit-disabled is false for user role', async () => {
		const t = setup({ role: 'user' });
		await expect.element(t.editDisabled).toHaveTextContent('false');
	});

	it('edit-status is "idle" initially', async () => {
		const t = setup({ role: 'user' });
		await expect.element(t.editStatus).toHaveTextContent('idle');
	});
});

describe('MessageActions — RetryButton snippet props', () => {
	it('retry-disabled is true for system role', async () => {
		const t = setup({ role: 'system' });
		await expect.element(t.retryDisabled).toHaveTextContent('true');
	});

	it('retry-disabled is false for user role', async () => {
		const t = setup({ role: 'user' });
		await expect.element(t.retryDisabled).toHaveTextContent('false');
	});

	it('retry-disabled is false for assistant role', async () => {
		const t = setup({ role: 'assistant' });
		await expect.element(t.retryDisabled).toHaveTextContent('false');
	});

	it('retry-status is "idle" initially', async () => {
		const t = setup();
		await expect.element(t.retryStatus).toHaveTextContent('idle');
	});
});

describe('MessageActions — CopyButton snippet props', () => {
	beforeEach(() => {
		Object.defineProperty(navigator, 'clipboard', {
			value: { writeText: vi.fn().mockResolvedValue(undefined) },
			configurable: true,
		});
	});

	it('copy-copied is false initially', async () => {
		const t = setup();
		await expect.element(t.copyCopied).toHaveTextContent('false');
	});

	it('copy-copied is true after clicking copy', async () => {
		const t = setup();
		await t.copyTrigger.click();
		await expect.element(t.copyCopied).toHaveTextContent('true');
	});

	it('copy trigger text changes to "Copied!" after copy', async () => {
		const t = setup();
		await t.copyTrigger.click();
		await expect.element(t.copyTrigger).toHaveTextContent('Copied!');
	});
});

// ============================================
// Async action behaviour — EditButton
// ============================================

describe('MessageActions — EditButton async behaviour', () => {
	it('calls workspace.editMessage with messageIndex and editContent', async () => {
		const workspace = makeWorkspace([createMessage({ role: 'user', content: 'Hello' })]);
		const t = setup({ workspace, messageIndex: 0, role: 'user', editContent: 'New text' });
		await t.editTrigger.click();
		expect(workspace.editMessage).toHaveBeenCalledWith(0, 'New text');
	});

	it('calls onEditSuccess after success', async () => {
		const onEditSuccess = vi.fn();
		const workspace = makeWorkspace();
		const t = setup({ workspace, role: 'user', onEditSuccess });
		await t.editTrigger.click();
		expect(onEditSuccess).toHaveBeenCalledOnce();
	});

	it('calls onEditError when editMessage throws', async () => {
		const onEditError = vi.fn();
		const workspace = makeWorkspace([], async () => { throw new Error('fail'); });
		const t = setup({ workspace, role: 'user', onEditError });
		await t.editTrigger.click();
		expect(onEditError).toHaveBeenCalledOnce();
	});

	it('edit-status becomes "error" when editMessage throws', async () => {
		const workspace = makeWorkspace([], async () => { throw new Error('fail'); });
		const t = setup({ workspace, role: 'user' });
		await t.editTrigger.click();
		await expect.element(t.editStatus).toHaveTextContent('error');
	});

	it('disabled button does not call workspace.editMessage when force-clicked', async () => {
		const workspace = makeWorkspace();
		const t = setup({ workspace, role: 'assistant' });
		await t.editBtn.click({ force: true });
		expect(workspace.editMessage).not.toHaveBeenCalled();
	});
});

// ============================================
// Async action behaviour — RetryButton
// ============================================

describe('MessageActions — RetryButton async behaviour', () => {
	it('calls workspace.editMessage for user message retry', async () => {
		const messages = [createMessage({ role: 'user', content: 'Hello', id: 'msg-0' })];
		const workspace = makeWorkspace(messages);
		const t = setup({ workspace, messageIndex: 0, role: 'user' });
		await t.retryTrigger.click();
		expect(workspace.editMessage).toHaveBeenCalledWith(0, 'Hello');
	});

	it('calls workspace.editMessage with preceding user msg for assistant retry', async () => {
		const messages = [
			createMessage({ role: 'user', content: 'My question', id: 'msg-0' }),
			createMessage({ role: 'assistant', content: 'My answer', id: 'msg-1' }),
		];
		const workspace = makeWorkspace(messages);
		const t = setup({ workspace, messageIndex: 1, role: 'assistant' });
		await t.retryTrigger.click();
		expect(workspace.editMessage).toHaveBeenCalledWith(0, 'My question');
	});

	it('calls onRetrySuccess after success', async () => {
		const onRetrySuccess = vi.fn();
		const messages = [createMessage({ role: 'user', content: 'Hi' })];
		const workspace = makeWorkspace(messages);
		const t = setup({ workspace, messageIndex: 0, role: 'user', onRetrySuccess });
		await t.retryTrigger.click();
		expect(onRetrySuccess).toHaveBeenCalledOnce();
	});

	it('calls onRetryError when editMessage throws', async () => {
		const onRetryError = vi.fn();
		const messages = [createMessage({ role: 'user', content: 'Hi' })];
		const workspace = makeWorkspace(messages, async () => { throw new Error('fail'); });
		const t = setup({ workspace, messageIndex: 0, role: 'user', onRetryError });
		await t.retryTrigger.click();
		expect(onRetryError).toHaveBeenCalledOnce();
	});

	it('retry-status becomes "error" when editMessage throws', async () => {
		const messages = [createMessage({ role: 'user', content: 'Hi' })];
		const workspace = makeWorkspace(messages, async () => { throw new Error('fail'); });
		const t = setup({ workspace, messageIndex: 0, role: 'user' });
		await t.retryTrigger.click();
		await expect.element(t.retryStatus).toHaveTextContent('error');
	});

	it('system role retry button does not call editMessage when force-clicked', async () => {
		const workspace = makeWorkspace();
		const t = setup({ workspace, role: 'system' });
		await t.retryBtn.click({ force: true });
		expect(workspace.editMessage).not.toHaveBeenCalled();
	});
});

// ============================================
// CopyButton interaction
// ============================================

describe('MessageActions — CopyButton interaction', () => {
	beforeEach(() => {
		Object.defineProperty(navigator, 'clipboard', {
			value: { writeText: vi.fn().mockResolvedValue(undefined) },
			configurable: true,
		});
	});

	it('copies the copyContent prop to clipboard', async () => {
		const t = setup({ copyContent: 'The message text' });
		await t.copyTrigger.click();
		expect(navigator.clipboard.writeText).toHaveBeenCalledWith('The message text');
	});

	it('calls onCopySuccess after successful copy', async () => {
		const onCopySuccess = vi.fn();
		const t = setup({ onCopySuccess });
		await t.copyTrigger.click();
		expect(onCopySuccess).toHaveBeenCalledOnce();
	});

	it('calls onCopyError when clipboard throws', async () => {
		const onCopyError = vi.fn();
		(navigator.clipboard.writeText as ReturnType<typeof vi.fn>).mockRejectedValue(new Error('denied'));
		const t = setup({ onCopyError });
		await t.copyTrigger.click();
		expect(onCopyError).toHaveBeenCalledOnce();
	});

	it('copied stays false when clipboard throws', async () => {
		(navigator.clipboard.writeText as ReturnType<typeof vi.fn>).mockRejectedValue(new Error('denied'));
		const t = setup();
		await t.copyTrigger.click();
		await expect.element(t.copyCopied).toHaveTextContent('false');
	});
});

// ============================================
// Branch nav callbacks
// ============================================

describe('MessageActions — Prev/Next callbacks', () => {
	it('calls onPrev when prev button is clicked and enabled', async () => {
		const onPrev = vi.fn();
		const t = setup({
			messageIndex: 2,
			branch: createBranch({ forkedAtMessageIndex: 2, previousSiblingId: 'main' }),
			onPrev,
		});
		await t.prevBtn.click();
		expect(onPrev).toHaveBeenCalledOnce();
	});

	it('calls onNext when next button is clicked and enabled', async () => {
		const onNext = vi.fn();
		const t = setup({
			messageIndex: 2,
			branch: createBranch({ forkedAtMessageIndex: 2, nextSiblingId: 'fork-2' }),
			onNext,
		});
		await t.nextBtn.click();
		expect(onNext).toHaveBeenCalledOnce();
	});

	it('does not call onPrev when prev is disabled', async () => {
		const onPrev = vi.fn();
		const t = setup({
			messageIndex: 2,
			branch: createBranch({ forkedAtMessageIndex: 2, previousSiblingId: undefined }),
			onPrev,
		});
		await t.prevBtn.click({ force: true });
		expect(onPrev).not.toHaveBeenCalled();
	});

	it('does not call onNext when next is disabled', async () => {
		const onNext = vi.fn();
		const t = setup({
			messageIndex: 2,
			branch: createBranch({ forkedAtMessageIndex: 2, nextSiblingId: undefined }),
			onNext,
		});
		await t.nextBtn.click({ force: true });
		expect(onNext).not.toHaveBeenCalled();
	});
});
