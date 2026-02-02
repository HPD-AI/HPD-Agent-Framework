import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { SseTransport } from '../src/transports/sse.js';

describe('SseTransport', () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('should connect and receive events', async () => {
    const events: any[] = [];
    const transport = new SseTransport('http://localhost:5135');

    // Mock fetch with streaming response
    const mockStream = new ReadableStream({
      start(controller) {
        controller.enqueue(
          new TextEncoder().encode(
            'data: {"version":"1.0","type":"TEXT_DELTA","text":"Hello","messageId":"msg-1"}\n\n'
          )
        );
        controller.close();
      },
    });

    vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: true,
      body: mockStream,
      text: async () => '',
    } as Response);

    transport.onEvent((event) => events.push(event));

    await transport.connect({
      conversationId: 'test-123',
      messages: [{ content: 'Hi' }],
    });

    // Wait for stream processing
    await new Promise((r) => setTimeout(r, 10));

    expect(events).toHaveLength(1);
    expect(events[0].text).toBe('Hello');
  });

  it('should handle HTTP errors', async () => {
    const transport = new SseTransport('http://localhost:5135');

    vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: false,
      status: 404,
      text: async () => 'Not Found',
    } as Response);

    await expect(
      transport.connect({
        conversationId: 'test-123',
        messages: [{ content: 'Hi' }],
      })
    ).rejects.toThrow('HTTP 404: Not Found');
  });

  it('should handle missing response body', async () => {
    const transport = new SseTransport('http://localhost:5135');

    vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: true,
      body: null,
      text: async () => '',
    } as unknown as Response);

    await expect(
      transport.connect({
        conversationId: 'test-123',
        messages: [{ content: 'Hi' }],
      })
    ).rejects.toThrow('No response body');
  });

  it('should call close handler when stream ends', async () => {
    const transport = new SseTransport('http://localhost:5135');
    const closeHandler = vi.fn();

    const mockStream = new ReadableStream({
      start(controller) {
        controller.close();
      },
    });

    vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: true,
      body: mockStream,
      text: async () => '',
    } as Response);

    transport.onClose(closeHandler);

    await transport.connect({
      conversationId: 'test-123',
      messages: [{ content: 'Hi' }],
    });

    expect(closeHandler).toHaveBeenCalled();
  });

  it('should disconnect on abort', async () => {
    const transport = new SseTransport('http://localhost:5135');
    const closeHandler = vi.fn();

    // Create a stream that never closes
    let streamController: ReadableStreamDefaultController<Uint8Array>;
    const mockStream = new ReadableStream({
      start(controller) {
        streamController = controller;
      },
    });

    vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: true,
      body: mockStream,
      text: async () => '',
    } as Response);

    transport.onClose(closeHandler);

    const connectPromise = transport.connect({
      conversationId: 'test-123',
      messages: [{ content: 'Hi' }],
    });

    // Wait for connection to establish
    await new Promise((r) => setTimeout(r, 10));
    expect(transport.connected).toBe(true);

    // Disconnect
    transport.disconnect();

    // Close the stream to let connect promise resolve
    streamController!.close();

    await connectPromise;
    expect(transport.connected).toBe(false);
  });

  it('should send permission response via HTTP', async () => {
    const transport = new SseTransport('http://localhost:5135');
    (transport as any).conversationId = 'test-123';

    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: true,
      text: async () => '',
    } as Response);

    await transport.send({
      type: 'permission_response',
      permissionId: 'perm-1',
      approved: true,
      choice: 'allow_always',
    });

    expect(fetchSpy).toHaveBeenCalledWith(
      'http://localhost:5135/agent/conversations/test-123/permissions/respond',
      expect.objectContaining({
        method: 'POST',
        body: expect.any(String),
      })
    );
  });

  it('should send clarification response to correct endpoint', async () => {
    const transport = new SseTransport('http://localhost:5135');
    (transport as any).conversationId = 'test-123';

    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: true,
      text: async () => '',
    } as Response);

    await transport.send({
      type: 'clarification_response',
      clarificationId: 'clar-1',
      response: 'Yes, proceed',
    });

    expect(fetchSpy).toHaveBeenCalledWith(
      'http://localhost:5135/agent/conversations/test-123/clarifications/respond',
      expect.objectContaining({
        method: 'POST',
      })
    );
  });

  it('should send continuation response to correct endpoint', async () => {
    const transport = new SseTransport('http://localhost:5135');
    (transport as any).conversationId = 'test-123';

    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue({
      ok: true,
      text: async () => '',
    } as Response);

    await transport.send({
      type: 'continuation_response',
      continuationId: 'cont-1',
      shouldContinue: true,
    });

    expect(fetchSpy).toHaveBeenCalledWith(
      'http://localhost:5135/agent/conversations/test-123/continuations/respond',
      expect.objectContaining({
        method: 'POST',
      })
    );
  });

  it('should throw when sending without connection', async () => {
    const transport = new SseTransport('http://localhost:5135');

    await expect(
      transport.send({
        type: 'permission_response',
        permissionId: 'perm-1',
        approved: true,
      })
    ).rejects.toThrow('Not connected');
  });

  it('should remove trailing slash from base URL', () => {
    const transport = new SseTransport('http://localhost:5135/');
    expect((transport as any).baseUrl).toBe('http://localhost:5135');
  });
});
