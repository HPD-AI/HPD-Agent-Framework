/**
 * Tests for workspace derived state and helper logic.
 *
 * These are pure-logic tests that don't require a real backend:
 *   - activeSiblings grouping and sort order
 *   - currentSiblingPosition 1-based display
 *   - canGoNext / canGoPrevious navigation flags
 *   - session/branch state isolation (compound cache key)
 *   - AgentState.loadHistory() is used (not fake streaming)
 */

import { describe, it, expect } from 'vitest';
import { createMockWorkspace } from '../../testing/mock-agent.svelte.ts';

// ============================================
// Helpers
// ============================================

async function tick(ms = 200): Promise<void> {
	await new Promise((r) => setTimeout(r, ms));
}

// ============================================
// activeSiblings grouping
// ============================================

describe('workspace — activeSiblings', () => {
	it('contains the active branch itself when no siblings', () => {
		const ws = createMockWorkspace();
		// main has forkedFrom: null — it is its own sibling group
		const main = ws.branches.get('main')!;
		const siblings = ws.activeSiblings;
		expect(siblings.some((s) => s.id === main.id)).toBe(true);
	});

	it('all siblings share the same forkedFrom and forkedAtMessageIndex', async () => {
		const ws = createMockWorkspace({ typingDelay: 0 });
		await ws.send('msg');
		await tick(300);

		// Create two forks from index 0
		await ws.editMessage(0, 'fork-a');
		await tick(300);
		const forkAId = ws.activeBranchId!;

		// Switch back to main, fork again
		await ws.switchBranch('main');
		await ws.editMessage(0, 'fork-b');
		await tick(300);

		const siblings = ws.activeSiblings;
		const forkedFrom = siblings[0]?.forkedFrom;
		const forkedAt = siblings[0]?.forkedAtMessageIndex;
		expect(siblings.every((s) => s.forkedFrom === forkedFrom)).toBe(true);
		expect(siblings.every((s) => s.forkedAtMessageIndex === forkedAt)).toBe(true);
		expect(forkAId).toBeTruthy(); // just confirms the fork was created
	});

	it('is sorted by siblingIndex ascending', async () => {
		const ws = createMockWorkspace({ typingDelay: 0 });
		await ws.send('msg');
		await tick(300);

		await ws.editMessage(0, 'first fork');
		await tick(300);
		await ws.switchBranch('main');
		await ws.editMessage(0, 'second fork');
		await tick(300);

		const siblings = ws.activeSiblings;
		for (let i = 1; i < siblings.length; i++) {
			expect(siblings[i].siblingIndex).toBeGreaterThanOrEqual(siblings[i - 1].siblingIndex);
		}
	});
});

// ============================================
// currentSiblingPosition
// ============================================

describe('workspace — currentSiblingPosition', () => {
	it('is { current: 1, total: 1 } for a lone branch', () => {
		const ws = createMockWorkspace();
		expect(ws.currentSiblingPosition).toEqual({ current: 1, total: 1 });
	});

	it('current is 1-based (siblingIndex + 1)', () => {
		const ws = createMockWorkspace();
		const main = ws.branches.get('main')!;
		expect(ws.currentSiblingPosition.current).toBe(main.siblingIndex + 1);
	});

	it('is { 0, 0 } when no active branch', async () => {
		const ws = createMockWorkspace();
		await ws.deleteSession('mock-session-1');
		await ws.deleteSession('mock-session-2');
		expect(ws.currentSiblingPosition).toEqual({ current: 0, total: 0 });
	});
});

// ============================================
// canGoNext / canGoPrevious
// ============================================

describe('workspace — canGoNext / canGoPrevious', () => {
	it('both false for a branch with no siblings', () => {
		const ws = createMockWorkspace();
		expect(ws.canGoNext).toBe(false);
		expect(ws.canGoPrevious).toBe(false);
	});

	it('both false when no active branch', async () => {
		const ws = createMockWorkspace();
		await ws.deleteSession('mock-session-1');
		await ws.deleteSession('mock-session-2');
		expect(ws.canGoNext).toBe(false);
		expect(ws.canGoPrevious).toBe(false);
	});
});

// ============================================
// Session isolation (compound cache key)
// ============================================

describe('workspace — session isolation', () => {
	it('session-1:main and session-2:main are independent states', async () => {
		const ws = createMockWorkspace({ typingDelay: 0 });

		// Send to session-1
		await ws.send('session-1 hello');
		await tick(300);
		const s1Messages = ws.state?.messages.length ?? 0;
		expect(s1Messages).toBeGreaterThan(0);

		// Switch to session-2 — should be empty
		await ws.selectSession('mock-session-2');
		expect(ws.state?.messages).toHaveLength(0);

		// Session-1 messages still exist when switching back
		await ws.selectSession('mock-session-1');
		expect(ws.state?.messages.length).toBe(s1Messages);
	});

	it('creating branches in session-1 does not appear in session-2', async () => {
		const ws = createMockWorkspace();
		await ws.createBranch({ branchId: 'session-1-feature' });
		await ws.selectSession('mock-session-2');
		expect(ws.branches.has('session-1-feature')).toBe(false);
	});
});

// ============================================
// loadHistory vs fake streaming
// ============================================

describe('workspace — loadHistory is used for branch switches', () => {
	it('switching to a branch does not leave streaming === true', async () => {
		const ws = createMockWorkspace({ typingDelay: 0 });

		// Send a message (streaming completes)
		await ws.send('hello');
		await tick(300);

		// Create another branch and switch to it
		await ws.createBranch({ branchId: 'other' });
		await ws.switchBranch('other');

		// The other branch's state should not have streaming = true
		expect(ws.state?.streaming).toBe(false);
	});

	it('switching back to main shows settled messages (streaming: false on all)', async () => {
		const ws = createMockWorkspace({ typingDelay: 0 });
		await ws.send('hello');
		await tick(300);

		await ws.createBranch({ branchId: 'other' });
		await ws.switchBranch('other');
		await ws.switchBranch('main');

		const messages = ws.state?.messages ?? [];
		expect(messages.every((m) => m.streaming === false)).toBe(true);
	});
});

// ============================================
// branches map reactivity
// ============================================

describe('workspace — branches map', () => {
	it('branches map reflects active session only', async () => {
		const ws = createMockWorkspace();
		await ws.createBranch({ branchId: 'session-1-only' });
		const session1BranchCount = ws.branches.size;

		await ws.selectSession('mock-session-2');
		expect(ws.branches.size).toBeLessThan(session1BranchCount);
		expect(ws.branches.has('session-1-only')).toBe(false);
	});

	it('branches map updates after createBranch', async () => {
		const ws = createMockWorkspace();
		const before = ws.branches.size;
		await ws.createBranch({ branchId: 'extra' });
		expect(ws.branches.size).toBe(before + 1);
	});

	it('branches map updates after deleteBranch', async () => {
		const ws = createMockWorkspace();
		await ws.createBranch({ branchId: 'extra' });
		const before = ws.branches.size;
		await ws.deleteBranch('extra');
		expect(ws.branches.size).toBe(before - 1);
	});
});

// ============================================
// canGoNext / canGoPrevious — with real siblings
// ============================================

describe('workspace — canGoNext / canGoPrevious with siblings', () => {
	it('canGoNext is true when activeBranch.nextSiblingId is set', async () => {
		const ws = createMockWorkspace({ typingDelay: 0 });
		await ws.send('msg');
		await tick(300);

		// editMessage creates a fork which sets nextSiblingId on the original
		await ws.editMessage(0, 'fork');
		await tick(300);

		// Switch back to main — main now has a next sibling (the fork)
		await ws.switchBranch('main');
		// The fork has previousSiblingId pointing to main, main has nextSiblingId
		// In the mock, canGoNext reflects activeBranch.nextSiblingId != null
		// main's nextSiblingId is set after fork
		expect(ws.canGoNext).toBe(ws.activeBranch?.nextSiblingId != null);
	});

	it('canGoPrevious is true when activeBranch.previousSiblingId is set', async () => {
		const ws = createMockWorkspace({ typingDelay: 0 });
		await ws.send('msg');
		await tick(300);

		await ws.editMessage(0, 'fork');
		await tick(300);

		// The fork is currently active — check its previousSiblingId
		expect(ws.canGoPrevious).toBe(ws.activeBranch?.previousSiblingId != null);
	});
});

// ============================================
// mapToUIMessages — loaded messages field correctness
// ============================================

describe('workspace — loadHistory produces settled messages', () => {
	it('all messages have streaming: false after branch switch', async () => {
		const ws = createMockWorkspace({ typingDelay: 0 });
		await ws.send('hello');
		await tick(300);

		// Switch away and back — history reloaded via loadHistory
		await ws.createBranch({ branchId: 'side' });
		await ws.switchBranch('side');
		await ws.switchBranch('main');

		for (const msg of ws.state!.messages) {
			expect(msg.streaming).toBe(false);
		}
	});

	it('all messages have thinking: false after branch switch', async () => {
		const ws = createMockWorkspace({ typingDelay: 0 });
		await ws.send('hello');
		await tick(300);

		await ws.createBranch({ branchId: 'side' });
		await ws.switchBranch('side');
		await ws.switchBranch('main');

		for (const msg of ws.state!.messages) {
			expect(msg.thinking).toBe(false);
		}
	});

	it('all messages have toolCalls: [] after branch switch', async () => {
		const ws = createMockWorkspace({ typingDelay: 0 });
		await ws.send('hello');
		await tick(300);

		await ws.createBranch({ branchId: 'side' });
		await ws.switchBranch('side');
		await ws.switchBranch('main');

		for (const msg of ws.state!.messages) {
			expect(msg.toolCalls).toEqual([]);
		}
	});

	it('all messages have a valid timestamp Date after branch switch', async () => {
		const ws = createMockWorkspace({ typingDelay: 0 });
		await ws.send('hello');
		await tick(300);

		await ws.createBranch({ branchId: 'side' });
		await ws.switchBranch('side');
		await ws.switchBranch('main');

		for (const msg of ws.state!.messages) {
			expect(msg.timestamp).toBeInstanceOf(Date);
		}
	});
});

// ============================================
// state derived value — switching
// ============================================

describe('workspace — state $derived updates on switch', () => {
	it('state points to a different AgentState after selectSession', async () => {
		const ws = createMockWorkspace();
		const before = ws.state;
		await ws.selectSession('mock-session-2');
		expect(ws.state).not.toBe(before);
	});

	it('state is null when activeSessionId is null', async () => {
		const ws = createMockWorkspace();
		await ws.deleteSession('mock-session-1');
		await ws.deleteSession('mock-session-2');
		expect(ws.state).toBeNull();
	});

	it('state points to a different AgentState after switchBranch', async () => {
		const ws = createMockWorkspace();
		const before = ws.state;
		await ws.createBranch({ branchId: 'other' });
		await ws.switchBranch('other');
		expect(ws.state).not.toBe(before);
	});

	it('state returns to the same object when switching back to a cached branch', async () => {
		const ws = createMockWorkspace();
		const mainState = ws.state;
		await ws.createBranch({ branchId: 'other' });
		await ws.switchBranch('other');
		await ws.switchBranch('main');
		// Should be the exact same AgentState instance (cache hit)
		expect(ws.state).toBe(mainState);
	});
});
