/**
 * Unit tests for MauiTransport
 * Tests WebView bridge communication, event parsing, and CRUD operations
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { MauiTransport } from '../src/transports/maui';
import type { AgentEvent } from '../src/types/events';

// Mock HybridWebView
const createMockHybridWebView = () => {
  const listeners: Array<(event: CustomEvent) => void> = [];

  return {
    InvokeDotNet: vi.fn(),
    SendRawMessage: vi.fn(),
    addEventListener: (type: string, listener: (event: Event) => void) => {
      if (type === 'HybridWebViewMessageReceived') {
        listeners.push(listener as (event: CustomEvent) => void);
      }
    },
    removeEventListener: (type: string, listener: (event: Event) => void) => {
      const index = listeners.indexOf(listener as (event: CustomEvent) => void);
      if (index > -1) listeners.splice(index, 1);
    },
    triggerMessage: (message: string) => {
      const event = new CustomEvent('HybridWebViewMessageReceived', {
        detail: { message }
      });
      listeners.forEach(l => l(event));
    },
    listeners
  };
};

describe('MauiTransport', () => {
  let transport: MauiTransport;
  let mockHybridWebView: ReturnType<typeof createMockHybridWebView>;

  beforeEach(() => {
    mockHybridWebView = createMockHybridWebView();
    (global as any).window = {
      HybridWebView: mockHybridWebView,
      addEventListener: mockHybridWebView.addEventListener,
      removeEventListener: mockHybridWebView.removeEventListener
    };
    transport = new MauiTransport();
  });

  describe('connect()', () => {
    it('throws error when HybridWebView not available', async () => {
      delete (global as any).window.HybridWebView;
      await expect(transport.connect({
        messages: [{ content: 'Test', role: 'user' }],
        sessionId: 's1',
        branchId: 'main'
      })).rejects.toThrow('MAUI HybridWebView not available');
    });

    it('calls InvokeDotNet with correct parameters', async () => {
      mockHybridWebView.InvokeDotNet.mockResolvedValue('stream-123');

      await transport.connect({
        messages: [{ content: 'Hello', role: 'user' }],
        sessionId: 'session-1',
        branchId: 'main'
      });

      expect(mockHybridWebView.InvokeDotNet).toHaveBeenCalledWith(
        'StartStream',
        ['Hello', 'session-1', 'main', undefined]
      );
    });

    it('sets connected to true on success', async () => {
      mockHybridWebView.InvokeDotNet.mockResolvedValue('stream-123');

      await transport.connect({
        messages: [{ content: 'Test', role: 'user' }],
        sessionId: 's1',
        branchId: 'main'
      });

      expect(transport.connected).toBe(true);
    });

    it('registers message listener', async () => {
      mockHybridWebView.InvokeDotNet.mockResolvedValue('stream-123');

      await transport.connect({
        messages: [{ content: 'Test', role: 'user' }],
        sessionId: 's1',
        branchId: 'main'
      });

      expect(mockHybridWebView.listeners.length).toBeGreaterThan(0);
    });

    it('passes first message content', async () => {
      mockHybridWebView.InvokeDotNet.mockResolvedValue('stream-123');

      await transport.connect({
        messages: [
          { content: 'First message', role: 'user' },
          { content: 'Second message', role: 'user' }
        ],
        sessionId: 's1',
        branchId: 'main'
      });

      expect(mockHybridWebView.InvokeDotNet).toHaveBeenCalledWith(
        'StartStream',
        expect.arrayContaining(['First message'])
      );
    });

    it('passes sessionId and branchId', async () => {
      mockHybridWebView.InvokeDotNet.mockResolvedValue('stream-123');

      await transport.connect({
        messages: [{ content: 'Test', role: 'user' }],
        sessionId: 'my-session',
        branchId: 'my-branch'
      });

      expect(mockHybridWebView.InvokeDotNet).toHaveBeenCalledWith(
        'StartStream',
        expect.arrayContaining(['my-session', 'my-branch'])
      );
    });

    it('passes runConfig when provided', async () => {
      mockHybridWebView.InvokeDotNet.mockResolvedValue('stream-123');

      await transport.connect({
        messages: [{ content: 'Test', role: 'user' }],
        sessionId: 's1',
        branchId: 'main',
        runConfig: { chat: { temperature: 0.7 } }
      });

      const lastCall = mockHybridWebView.InvokeDotNet.mock.calls[0];
      expect(lastCall[1][3]).toContain('temperature');
    });

    it('serializes runConfig as JSON', async () => {
      mockHybridWebView.InvokeDotNet.mockResolvedValue('stream-123');

      const config = { chat: { temperature: 0.9, maxOutputTokens: 1000 } };
      await transport.connect({
        messages: [{ content: 'Test', role: 'user' }],
        sessionId: 's1',
        branchId: 'main',
        runConfig: config
      });

      const lastCall = mockHybridWebView.InvokeDotNet.mock.calls[0];
      const parsedConfig = JSON.parse(lastCall[1][3]);
      expect(parsedConfig.chat.temperature).toBe(0.9);
    });

    it('stores streamId', async () => {
      mockHybridWebView.InvokeDotNet.mockResolvedValue('my-stream-id');

      await transport.connect({
        messages: [{ content: 'Test', role: 'user' }],
        sessionId: 's1',
        branchId: 'main'
      });

      // StreamId is internal, but we can verify it's used later
      expect(transport.connected).toBe(true);
    });

    it('registers abort signal when provided', async () => {
      mockHybridWebView.InvokeDotNet.mockResolvedValue('stream-123');
      const controller = new AbortController();

      await transport.connect({
        messages: [{ content: 'Test', role: 'user' }],
        sessionId: 's1',
        branchId: 'main',
        signal: controller.signal
      });

      // Verify connection established
      expect(transport.connected).toBe(true);

      // Trigger abort
      controller.abort();
      expect(transport.connected).toBe(false);
    });

    it('cleans up on error', async () => {
      mockHybridWebView.InvokeDotNet.mockRejectedValue(new Error('Failed'));

      await expect(transport.connect({
        messages: [{ content: 'Test', role: 'user' }],
        sessionId: 's1',
        branchId: 'main'
      })).rejects.toThrow();

      expect(transport.connected).toBe(false);
    });
  });

  describe('Message Parsing', () => {
    beforeEach(async () => {
      mockHybridWebView.InvokeDotNet.mockResolvedValue('stream-123');
      await transport.connect({
        messages: [{ content: 'Test', role: 'user' }],
        sessionId: 's1',
        branchId: 'main'
      });
    });

    it('parses agent_event correctly', () => {
      const eventHandler = vi.fn();
      transport.onEvent(eventHandler);

      const event: AgentEvent = { type: 'TEXT_DELTA', delta: 'Hello' };
      mockHybridWebView.triggerMessage(`agent_event:stream-123:${JSON.stringify(event)}`);

      expect(eventHandler).toHaveBeenCalledWith(event);
    });

    it('calls event handler with parsed event', () => {
      const eventHandler = vi.fn();
      transport.onEvent(eventHandler);

      mockHybridWebView.triggerMessage(
        `agent_event:stream-123:${JSON.stringify({ type: 'TEXT_DELTA', delta: 'Test' })}`
      );

      expect(eventHandler).toHaveBeenCalledOnce();
      expect(eventHandler).toHaveBeenCalledWith(
        expect.objectContaining({ type: 'TEXT_DELTA', delta: 'Test' })
      );
    });

    it('handles colons in JSON content', () => {
      const eventHandler = vi.fn();
      transport.onEvent(eventHandler);

      const event = { type: 'TEXT_DELTA', delta: 'Time: 12:30:45' };
      mockHybridWebView.triggerMessage(`agent_event:stream-123:${JSON.stringify(event)}`);

      expect(eventHandler).toHaveBeenCalledWith(
        expect.objectContaining({ delta: 'Time: 12:30:45' })
      );
    });

    it('calls close handler on complete', () => {
      const closeHandler = vi.fn();
      transport.onClose(closeHandler);

      mockHybridWebView.triggerMessage('agent_complete:stream-123');

      expect(closeHandler).toHaveBeenCalledOnce();
      expect(transport.connected).toBe(false);
    });

    it('calls error handler on error', () => {
      const errorHandler = vi.fn();
      transport.onError(errorHandler);

      mockHybridWebView.triggerMessage('agent_error:stream-123:Something went wrong');

      expect(errorHandler).toHaveBeenCalledOnce();
      expect(errorHandler).toHaveBeenCalledWith(
        expect.objectContaining({ message: 'Something went wrong' })
      );
      expect(transport.connected).toBe(false);
    });

    it('sets connected to false on complete', () => {
      mockHybridWebView.triggerMessage('agent_complete:stream-123');
      expect(transport.connected).toBe(false);
    });

    it('sets connected to false on error', () => {
      mockHybridWebView.triggerMessage('agent_error:stream-123:Error occurred');
      expect(transport.connected).toBe(false);
    });

    it('ignores events for different streamId', () => {
      const eventHandler = vi.fn();
      transport.onEvent(eventHandler);

      mockHybridWebView.triggerMessage(
        `agent_event:different-stream:${JSON.stringify({ type: 'TEXT_DELTA' })}`
      );

      expect(eventHandler).not.toHaveBeenCalled();
    });

    it('calls error handler on parse error', () => {
      const errorHandler = vi.fn();
      transport.onError(errorHandler);

      mockHybridWebView.triggerMessage('agent_event:stream-123:{ invalid json');

      expect(errorHandler).toHaveBeenCalled();
    });
  });

  describe('Session CRUD', () => {
    it('listSessions calls InvokeDotNet with SearchSessions', async () => {
      mockHybridWebView.InvokeDotNet.mockResolvedValue(JSON.stringify([]));

      await transport.listSessions();

      expect(mockHybridWebView.InvokeDotNet).toHaveBeenCalledWith(
        'SearchSessions',
        expect.any(Array)
      );
    });

    it('listSessions parses JSON to session array', async () => {
      const sessions = [{ sessionId: 's1' }, { sessionId: 's2' }];
      mockHybridWebView.InvokeDotNet.mockResolvedValue(JSON.stringify(sessions));

      const result = await transport.listSessions();

      expect(result).toHaveLength(2);
      expect(result[0].sessionId).toBe('s1');
    });

    it('getSession calls InvokeDotNet with sessionId', async () => {
      mockHybridWebView.InvokeDotNet.mockResolvedValue(JSON.stringify({ sessionId: 's1' }));

      await transport.getSession('s1');

      expect(mockHybridWebView.InvokeDotNet).toHaveBeenCalledWith('GetSession', ['s1']);
    });

    it('getSession returns null on error', async () => {
      mockHybridWebView.InvokeDotNet.mockRejectedValue(new Error('Not found'));

      const result = await transport.getSession('nonexistent');

      expect(result).toBeNull();
    });

    it('createSession serializes metadata as JSON', async () => {
      mockHybridWebView.InvokeDotNet.mockResolvedValue(JSON.stringify({ sessionId: 's1' }));

      await transport.createSession({ metadata: { key: 'value' } });

      const lastCall = mockHybridWebView.InvokeDotNet.mock.calls[0];
      expect(lastCall[1][1]).toContain('key');
    });
  });

  describe('disconnect()', () => {
    beforeEach(async () => {
      mockHybridWebView.InvokeDotNet.mockResolvedValue('stream-123');
      await transport.connect({
        messages: [{ content: 'Test', role: 'user' }],
        sessionId: 's1',
        branchId: 'main'
      });
    });

    it('calls StopStream with streamId', () => {
      transport.disconnect();

      expect(mockHybridWebView.InvokeDotNet).toHaveBeenCalledWith('StopStream', ['stream-123']);
    });

    it('removes event listener', () => {
      const initialListeners = mockHybridWebView.listeners.length;
      transport.disconnect();

      expect(mockHybridWebView.listeners.length).toBeLessThan(initialListeners);
    });

    it('sets connected to false', () => {
      transport.disconnect();
      expect(transport.connected).toBe(false);
    });

    it('clears streamId', () => {
      transport.disconnect();
      // Verify by trying to trigger message - should be ignored
      const eventHandler = vi.fn();
      transport.onEvent(eventHandler);
      mockHybridWebView.triggerMessage('agent_event:stream-123:{}');
      expect(eventHandler).not.toHaveBeenCalled();
    });

    it('works when HybridWebView not available', () => {
      delete (global as any).window.HybridWebView;

      expect(() => transport.disconnect()).not.toThrow();
      expect(transport.connected).toBe(false);
    });
  });
});
