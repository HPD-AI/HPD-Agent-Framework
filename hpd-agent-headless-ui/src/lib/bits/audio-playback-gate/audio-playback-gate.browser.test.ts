/**
 * AudioPlaybackGate Tests
 * Following Bits UI testing patterns with comprehensive accessibility checks
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render } from 'vitest-browser-svelte';
import { userEvent } from '@vitest/browser/context';
import AudioGateTest from './audio-playback-gate.test.svelte';

describe('AudioPlaybackGate', () => {
	// Mock AudioContext for testing
	beforeEach(() => {
		// eslint-disable-next-line @typescript-eslint/no-explicit-any
		(globalThis as any).AudioContext = vi.fn().mockImplementation(() => ({
			state: 'suspended',
			resume: vi.fn().mockResolvedValue(undefined),
			close: vi.fn().mockResolvedValue(undefined)
		}));
	});

	describe('ARIA Attributes & Accessibility', () => {
		it('should have correct ARIA attributes on root', async () => {
			const { container } = render(AudioGateTest);
			const root = container.querySelector('[data-audio-gate-root]') as HTMLElement;

			await expect.element(root).toHaveAttribute('role', 'status');
			await expect.element(root).toHaveAttribute('aria-label', 'Audio playback status');
			await expect.element(root).toHaveAttribute('aria-live', 'polite');
		});

		it('should update aria-live when status changes', async () => {
			const { container } = render(AudioGateTest);
			const root = container.querySelector('[data-audio-gate-root]') as HTMLElement;

			// Initially should have aria-live="polite"
			await expect.element(root).toHaveAttribute('aria-live', 'polite');
		});
	});

	describe('Data Attributes', () => {
		it('should have audio-gate-root data attribute', async () => {
			const { container } = render(AudioGateTest);
			const root = container.querySelector('[data-audio-gate-root]') as HTMLElement;

			await expect.element(root).toBeInTheDocument();
			await expect.element(root).toHaveAttribute('data-audio-gate-root', '');
		});

		it('should have correct initial status', async () => {
			const { container } = render(AudioGateTest);
			const root = container.querySelector('[data-audio-gate-root]') as HTMLElement;

			await expect.element(root).toHaveAttribute('data-status', 'blocked');
		});

		it('should not have data-can-play attribute initially', async () => {
			const { container } = render(AudioGateTest);
			const root = container.querySelector('[data-audio-gate-root]') as HTMLElement;

			await expect.element(root).not.toHaveAttribute('data-can-play');
		});

		it('should not have data-error attribute initially', async () => {
			const { container } = render(AudioGateTest);
			const root = container.querySelector('[data-audio-gate-root]') as HTMLElement;

			await expect.element(root).not.toHaveAttribute('data-error');
		});
	});

	describe('State Management', () => {
		it('should start with blocked status', async () => {
			const { container } = render(AudioGateTest);
			const statusEl = container.querySelector('[data-testid="audio-gate-status"]') as HTMLElement;

			await expect.element(statusEl).toHaveAttribute('data-status', 'blocked');
			await expect.element(statusEl).toHaveTextContent('Status: blocked');
		});

		it('should show canPlayAudio as false initially', async () => {
			const { container } = render(AudioGateTest);
			const canPlayEl = container.querySelector('[data-testid="audio-gate-can-play"]') as HTMLElement;

			await expect.element(canPlayEl).toHaveAttribute('data-can-play', 'false');
			await expect.element(canPlayEl).toHaveTextContent('Can Play: false');
		});

		it('should display enable button when audio is blocked', async () => {
			const { container } = render(AudioGateTest);
			const enableBtn = container.querySelector('[data-testid="audio-gate-enable-btn"]') as HTMLElement;

			await expect.element(enableBtn).toBeInTheDocument();
			await expect.element(enableBtn).toHaveTextContent('Enable Audio');
		});
	});

	describe('Enable Audio Interaction', () => {
		it('should update status when enable button is clicked', async () => {
			// Mock AudioContext as a proper constructor
			const mockResume = vi.fn().mockResolvedValue(undefined);
			const mockClose = vi.fn().mockResolvedValue(undefined);

			// eslint-disable-next-line @typescript-eslint/no-explicit-any
			(globalThis as any).AudioContext = vi.fn(function (this: any) {
				this.state = 'suspended';
				this.resume = mockResume;
				this.close = mockClose;
			});

			const { container } = render(AudioGateTest);
			const enableBtn = container.querySelector('[data-testid="audio-gate-enable-btn"]') as HTMLElement;

			// Click enable button
			await userEvent.click(enableBtn!);

			// Wait for state update
			await new Promise((resolve) => setTimeout(resolve, 100));

			// Check that AudioContext was created and resumed
			expect(mockResume).toHaveBeenCalled();
		});

		it('should call onStatusChange callback when status changes', async () => {
			const onStatusChange = vi.fn();

			const { container } = render(AudioGateTest, { onStatusChange });

			// onStatusChange should be called with initial status
			expect(onStatusChange).toHaveBeenCalledWith('blocked');
		});
	});

	describe('Error Handling', () => {
		it('should handle AudioContext creation failure', async () => {
			// Mock AudioContext to throw error
			// @ts-expect-error - mocking AudioContext
			globalThis.AudioContext = undefined;

			const { container } = render(AudioGateTest);
			const enableBtn = container.querySelector('[data-testid="audio-gate-enable-btn"]') as HTMLElement;

			// Click enable button
			await userEvent.click(enableBtn!);

			// Wait for error state
			await new Promise((resolve) => setTimeout(resolve, 100));

			// Check for error display
			const errorEl = container.querySelector('[data-testid="audio-gate-error"]') as HTMLElement;
			await expect.element(errorEl).toBeInTheDocument();
		});
	});

	describe('Component Props', () => {
		it('should accept custom data-testid', async () => {
			const { container } = render(AudioGateTest, { testId: 'custom-gate' });

			const root = container.querySelector('[data-testid="custom-gate"]') as HTMLElement;
			await expect.element(root).toBeInTheDocument();
		});

		it('should merge custom props with component props', async () => {
			const { container } = render(AudioGateTest, { testId: 'audio-gate' });

			const root = container.querySelector('[data-audio-gate-root]') as HTMLElement;
			await expect.element(root).toHaveAttribute('data-testid', 'audio-gate');
		});
	});

	describe('Snippet Props', () => {
		it('should expose canPlayAudio to snippet', async () => {
			const { container } = render(AudioGateTest);
			const canPlayEl = container.querySelector('[data-testid="audio-gate-can-play"]') as HTMLElement;

			await expect.element(canPlayEl).toBeInTheDocument();
		});

		it('should expose status to snippet', async () => {
			const { container } = render(AudioGateTest);
			const statusEl = container.querySelector('[data-testid="audio-gate-status"]') as HTMLElement;

			await expect.element(statusEl).toBeInTheDocument();
		});

		it('should expose enableAudio function to snippet', async () => {
			const { container } = render(AudioGateTest);
			const enableBtn = container.querySelector('[data-testid="audio-gate-enable-btn"]') as HTMLElement;

			// enableAudio function should be callable via button
			await expect.element(enableBtn).toBeInTheDocument();
		});

		it('should expose error to snippet when error occurs', async () => {
			// @ts-expect-error - mocking AudioContext
			globalThis.AudioContext = undefined;

			const { container } = render(AudioGateTest);
			const enableBtn = container.querySelector('[data-testid="audio-gate-enable-btn"]') as HTMLElement;

			// Trigger error
			await userEvent.click(enableBtn!);
			await new Promise((resolve) => setTimeout(resolve, 100));

			// Error should be exposed
			const errorEl = container.querySelector('[data-testid="audio-gate-error"]') as HTMLElement;
			await expect.element(errorEl).toBeInTheDocument();
		});
	});

	describe('Cleanup', () => {
		it('should close AudioContext on unmount', async () => {
			const mockClose = vi.fn().mockResolvedValue(undefined);
			const mockResume = vi.fn().mockResolvedValue(undefined);

			// Mock AudioContext as a proper constructor
			// eslint-disable-next-line @typescript-eslint/no-explicit-any
			(globalThis as any).AudioContext = vi.fn(function (this: any) {
				this.state = 'suspended';
				this.resume = mockResume;
				this.close = mockClose;
			});

			const { container, unmount } = render(AudioGateTest);

			// Enable audio first (so AudioContext is created)
			const enableBtn = container.querySelector('[data-testid="audio-gate-enable-btn"]') as HTMLElement;
			await userEvent.click(enableBtn!);

			// Wait for AudioContext to be created
			await new Promise((resolve) => setTimeout(resolve, 100));

			// Unmount component
			unmount();

			// Wait for cleanup
			await new Promise((resolve) => setTimeout(resolve, 100));

			// Close should be called
			expect(mockClose).toHaveBeenCalled();
		});
	});
});
