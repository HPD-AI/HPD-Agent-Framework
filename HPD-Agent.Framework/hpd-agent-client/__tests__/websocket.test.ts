import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { WebSocketTransport } from '../src/transports/websocket.js';

// Mock WebSocket
class MockWebSocket {
  static CONNECTING = 0;
  static OPEN = 1;
  static CLOSING = 2;
  static CLOSED = 3;

  url: string;
  readyState = MockWebSocket.CONNECTING;
  onopen?: () => void;
  onmessage?: (event: { data: string }) => void;
  onerror?: () => void;
  onclose?: () => void;
  sentMessages: string[] = [];

  constructor(url: string) {
    this.url = url;
    // Simulate async connection
    setTimeout(() => {
      this.readyState = MockWebSocket.OPEN;
      this.onopen?.();
    }, 0);
  }

  send(data: string) {
    this.sentMessages.push(data);
  }

  close() {
    this.readyState = MockWebSocket.CLOSED;
    this.onclose?.();
  }

  // Test helper to simulate receiving a message
  simulateMessage(data: string) {
    this.onmessage?.({ data });
  }

  // Test helper to simulate error
  simulateError() {
    this.onerror?.();
  }
}

describe('WebSocketTransport', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    (globalThis as any).WebSocket = MockWebSocket;
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('should convert http to ws URL', () => {
    const transport = new WebSocketTransport('http://localhost:5135');
    expect((transport as any).baseUrl).toBe('ws://localhost:5135');
  });

  it('should convert https to wss URL', () => {
    const transport = new WebSocketTransport('https://example.com');
    expect((transport as any).baseUrl).toBe('wss://example.com');
  });

  it('should remove trailing slash', () => {
    const transport = new WebSocketTransport('http://localhost:5135/');
    expect((transport as any).baseUrl).toBe('ws://localhost:5135');
  });

  it('should connect and send initial messages', async () => {
    const transport = new WebSocketTransport('http://localhost:5135');

    await transport.connect({
      conversationId: 'test-123',
      messages: [{ content: 'Hello' }],
    });

    const ws = (transport as any).ws as MockWebSocket;
    expect(ws.url).toBe('ws://localhost:5135/agent/conversations/test-123/ws');
    expect(ws.sentMessages).toContainEqual(JSON.stringify({ messages: [{ content: 'Hello' }] }));
  });

  it('should receive and parse events', async () => {
    const events: any[] = [];
    const transport = new WebSocketTransport('http://localhost:5135');

    transport.onEvent((event) => events.push(event));

    await transport.connect({
      conversationId: 'test-123',
      messages: [{ content: 'Hi' }],
    });

    const ws = (transport as any).ws as MockWebSocket;
    ws.simulateMessage('{"version":"1.0","type":"TEXT_DELTA","text":"Hello","messageId":"msg-1"}');

    expect(events).toHaveLength(1);
    expect(events[0].text).toBe('Hello');
  });

  it('should ignore invalid JSON messages', async () => {
    const events: any[] = [];
    const transport = new WebSocketTransport('http://localhost:5135');

    transport.onEvent((event) => events.push(event));

    await transport.connect({
      conversationId: 'test-123',
      messages: [{ content: 'Hi' }],
    });

    const ws = (transport as any).ws as MockWebSocket;
    ws.simulateMessage('not valid json');

    expect(events).toHaveLength(0);
  });

  it('should send messages directly over WebSocket', async () => {
    const transport = new WebSocketTransport('http://localhost:5135');

    await transport.connect({
      conversationId: 'test-123',
      messages: [{ content: 'Hi' }],
    });

    await transport.send({
      type: 'permission_response',
      permissionId: 'perm-1',
      approved: true,
    });

    const ws = (transport as any).ws as MockWebSocket;
    expect(ws.sentMessages).toContainEqual(
      JSON.stringify({
        type: 'permission_response',
        permissionId: 'perm-1',
        approved: true,
      })
    );
  });

  it('should call close handler on disconnect', async () => {
    const transport = new WebSocketTransport('http://localhost:5135');
    const closeHandler = vi.fn();

    transport.onClose(closeHandler);

    await transport.connect({
      conversationId: 'test-123',
      messages: [{ content: 'Hi' }],
    });

    transport.disconnect();

    expect(closeHandler).toHaveBeenCalled();
  });

  it('should throw when sending without connection', async () => {
    const transport = new WebSocketTransport('http://localhost:5135');

    await expect(
      transport.send({
        type: 'permission_response',
        permissionId: 'perm-1',
        approved: true,
      })
    ).rejects.toThrow('WebSocket not connected');
  });

  it('should report connected state correctly', async () => {
    const transport = new WebSocketTransport('http://localhost:5135');

    expect(transport.connected).toBe(false);

    await transport.connect({
      conversationId: 'test-123',
      messages: [{ content: 'Hi' }],
    });

    expect(transport.connected).toBe(true);

    transport.disconnect();

    expect(transport.connected).toBe(false);
  });

  it('should handle abort signal during connection', async () => {
    const transport = new WebSocketTransport('http://localhost:5135');
    const controller = new AbortController();

    // Abort immediately
    controller.abort();

    await expect(
      transport.connect({
        conversationId: 'test-123',
        messages: [{ content: 'Hi' }],
        signal: controller.signal,
      })
    ).rejects.toThrow('Aborted');
  });

  it('should call error handler on WebSocket error', async () => {
    // Create a WebSocket that errors on connect (without calling onopen)
    class ErrorWebSocket {
      static CONNECTING = 0;
      static OPEN = 1;
      static CLOSING = 2;
      static CLOSED = 3;

      url: string;
      readyState = ErrorWebSocket.CONNECTING;
      onopen?: () => void;
      onmessage?: (event: { data: string }) => void;
      onerror?: () => void;
      onclose?: () => void;

      constructor(url: string) {
        this.url = url;
        // Fire error instead of onopen
        setTimeout(() => {
          this.onerror?.();
        }, 0);
      }

      send(_data: string) {}
      close() {
        this.readyState = ErrorWebSocket.CLOSED;
        this.onclose?.();
      }
    }

    (globalThis as any).WebSocket = ErrorWebSocket;

    const transport = new WebSocketTransport('http://localhost:5135');
    const errorHandler = vi.fn();

    transport.onError(errorHandler);

    await expect(
      transport.connect({
        conversationId: 'test-123',
        messages: [{ content: 'Hi' }],
      })
    ).rejects.toThrow('WebSocket error');

    expect(errorHandler).toHaveBeenCalled();
  });
});
