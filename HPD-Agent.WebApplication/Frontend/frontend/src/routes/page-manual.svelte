<script lang="ts">
    import { onMount } from 'svelte';
    import { AgentClient, type PermissionRequestEvent, type PermissionChoice, type ClientToolInvokeRequestEvent } from '@hpd/hpd-agent-client';
    import { Message } from '@hpd/hpd-agent-headless-ui';
    import Artifact from '$lib/artifacts/Artifact.svelte';
    import { artifactStore, type ArtifactState } from '$lib/artifacts/artifact-store.js';
    import { artifactTool, handleArtifactTool } from '$lib/artifacts/artifact-Toolkit.js';

    const API_BASE = 'http://localhost:5135';

    // Message type matching the headless-ui agent types
    interface MessageType {
        id: string;
        role: 'user' | 'assistant' | 'system';
        content: string;
        streaming: boolean;
        thinking: boolean;
        timestamp: Date;
        toolCalls: any[];
        reasoning?: string;
    }

    interface PendingPermission {
        permissionId: string;
        functionName: string;
        description?: string;
        callId: string;
        arguments?: Record<string, unknown>;
        resolve: (response: { approved: boolean; choice?: PermissionChoice; reason?: string }) => void;
    }

    let conversationId: string | null = null;
    let messages: MessageType[] = [];
    let currentMessage = '';
    let isLoading = false;
    let streamingContent = '';
    let currentReasoning = '';
    let pendingPermission: PendingPermission | null = null;
    let showPermissionDialog = false;

    // Artifact state (reactive)
    let artifact: ArtifactState;
    artifactStore.subscribe(value => artifact = value);

    // Create the client with artifact tool
    const client = new AgentClient({
        baseUrl: API_BASE,
        clientToolGroups: [artifactTool]
    });

    onMount(async () => {
        const saved = localStorage.getItem('conversationId');
        if (saved) {
            try {
                const res = await fetch(`${API_BASE}/conversations/${saved}`);
                if (res.ok) {
                    conversationId = saved;
                    return;
                }
            } catch (e) {
                console.error('Failed to verify conversation:', e);
            }
        }
        await createConversation();
    });

    async function createConversation() {
        const res = await fetch(`${API_BASE}/conversations`, { method: 'POST' });
        const data = await res.json();
        conversationId = data.id;
        console.log('[CREATE] Created new conversation:', conversationId);
        localStorage.setItem('conversationId', conversationId!);
        messages = [];
        artifactStore.close();
    }

    async function sendMessage() {
        if (!currentMessage.trim() || isLoading || !conversationId) return;

        const userMsg = currentMessage.trim();
        currentMessage = '';

        console.log('[SEND] Sending message to conversation:', conversationId);

        // Add user message
        messages = [...messages, {
            id: `user-${Date.now()}`,
            role: 'user',
            content: userMsg,
            streaming: false,
            thinking: false,
            toolCalls: [],
            timestamp: new Date()
        }];

        // Add assistant placeholder (start with thinking state until first event)
        messages = [...messages, {
            id: `assistant-${Date.now()}`,
            role: 'assistant',
            content: '',
            streaming: true,  // Start as streaming (will show loading state)
            thinking: false,
            reasoning: '',
            toolCalls: [],
            timestamp: new Date()
        }];

        isLoading = true;
        streamingContent = '';
        currentReasoning = '';

        try {
            await client.stream(
                conversationId,
                [{ content: userMsg }],
                {
                    // Content handlers
                    onTextDelta: (text) => {
                        console.log('Text delta:', text);
                        streamingContent += text;
                        // Keep reasoning visible - it shows what the model thought before responding
                        updateLastMessage({ streaming: true, thinking: false });
                    },

                    // Reasoning handlers
                    onReasoning: (text, messageId) => {
                        console.log('Reasoning:', text?.substring(0, 100));
                        currentReasoning = text;
                        updateLastMessage({ thinking: true });
                    },

                    // Tool handlers
                    onToolCallStart: (_callId, name) => {
                        console.log('Tool call:', name);
                        updateLastMessage({ thinking: true });
                    },

                    onToolCallResult: (callId) => {
                        console.log('Tool result:', callId);
                    },

                    // Permission handler
                    onPermissionRequest: async (request: PermissionRequestEvent) => {
                        console.log('Permission request:', request.functionName);
                        updateLastMessage({ thinking: true });

                        return new Promise((resolve) => {
                            pendingPermission = {
                                permissionId: request.permissionId,
                                functionName: request.functionName,
                                description: request.description,
                                callId: request.callId,
                                arguments: request.arguments,
                                resolve
                            };
                            showPermissionDialog = true;
                        });
                    },

                    // Client tool handler
                    onClientToolInvoke: async (request: ClientToolInvokeRequestEvent) => {
                        console.log('Client tool invoke:', request.toolName, request.arguments);
                        updateLastMessage({ thinking: true });

                        const response = handleArtifactTool(
                            request.toolName,
                            request.arguments,
                            request.requestId
                        );

                        console.log('Client tool response:', response);
                        return response;
                    },

                    onClientToolGroupsRegistered: (event) => {
                        console.log('Client tool groups registered:', event.registeredToolGroups, 'Total tools:', event.totalTools);
                    },

                    onTurnStart: (iteration) => {
                        console.log('Agent turn started:', iteration);
                    },

                    onTurnEnd: (iteration) => {
                        console.log('Agent turn finished:', iteration);
                    },

                    onComplete: () => {
                        console.log('Message complete');
                        updateLastMessage({ streaming: false, thinking: false });
                        isLoading = false;
                    },

                    onError: (message) => {
                        console.error('Error:', message);
                        streamingContent = `Error: ${message}`;
                        updateLastMessage({ streaming: false, thinking: false });
                        isLoading = false;
                    },

                    onEvent: (event) => {
                        console.log(`[v${event.version}] ${event.type}:`, event);
                    }
                }
            );
        } catch (error) {
            console.error('Stream error:', error);
            streamingContent = `Error: ${error}`;
            updateLastMessage({ streaming: false, thinking: false });
        } finally {
            isLoading = false;
        }
    }

    function updateLastMessage(updates: Partial<MessageType> = {}) {
        if (messages.length === 0) return;
        const lastMsg = { ...messages[messages.length - 1] };
        lastMsg.content = streamingContent;
        lastMsg.reasoning = currentReasoning;
        Object.assign(lastMsg, updates);
        messages = [...messages.slice(0, -1), lastMsg];
    }

    function respondToPermission(approved: boolean, choice: PermissionChoice = 'ask') {
        if (!pendingPermission) return;

        pendingPermission.resolve({
            approved,
            choice,
            reason: approved ? undefined : 'User denied permission'
        });

        showPermissionDialog = false;
        currentReasoning = '';
        updateLastMessage({ thinking: false });
        pendingPermission = null;
    }

    function handleKeypress(e: KeyboardEvent) {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            sendMessage();
        }
    }
</script>

<div class="h-screen flex flex-col bg-gray-50">
    <!-- Header -->
    <div class="bg-white border-b px-6 py-4 flex justify-between items-center">
        <div>
            <h1 class="text-xl font-semibold">HPD-Agent Chat (Headless UI)</h1>
            <p class="text-sm text-gray-500">{messages.length} messages</p>
        </div>
        <button
            onclick={createConversation}
            class="px-4 py-2 bg-gray-100 hover:bg-gray-200 rounded-md text-sm"
        >
            New Chat
        </button>
    </div>

    <!-- Main Content -->
    <div class="flex-1 flex overflow-hidden">
        <!-- Chat Panel -->
        <div class="flex-1 flex flex-col {artifact.isOpen ? 'w-1/3 min-w-[300px] border-r' : ''}">
            <!-- Messages -->
            <div class="flex-1 overflow-y-auto p-6 space-y-4">
                {#each messages as message (message.id)}
                    <Message {message}>
                        {#snippet children({ content, role, streaming, thinking, reasoning, hasReasoning, status })}
                            <div class="flex gap-3 {role === 'user' ? 'justify-end' : ''}">
                                <div class="max-w-[80%] {role === 'user' ? 'bg-blue-500 text-white' : 'bg-white border'} rounded-lg p-4">
                                    <!-- Status Badge -->
                                    {#if role === 'assistant'}
                                        <div class="text-xs mb-2 flex items-center gap-2">
                                            <span class="font-semibold opacity-70">Status:</span>
                                            <span class="px-2 py-0.5 rounded"
                                                  class:bg-blue-100={status === 'streaming'}
                                                  class:bg-purple-100={status === 'thinking'}
                                                  class:bg-green-100={status === 'complete'}
                                                  class:text-blue-700={status === 'streaming'}
                                                  class:text-purple-700={status === 'thinking'}
                                                  class:text-green-700={status === 'complete'}>
                                                {status}
                                            </span>
                                        </div>
                                    {/if}

                                    <!-- Reasoning -->
                                    {#if hasReasoning}
                                        <div class="text-sm italic opacity-70 mb-2 bg-purple-50 border-l-2 border-purple-500 pl-2 py-1">
                                            <strong>Thinking:</strong> {reasoning}
                                        </div>
                                    {/if}

                                    <!-- Content -->
                                    <div class="whitespace-pre-wrap">
                                        {content}
                                        {#if streaming}
                                            <span class="inline-block w-1.5 h-4 bg-current ml-1 animate-pulse">▊</span>
                                        {/if}
                                    </div>
                                </div>
                            </div>
                        {/snippet}
                    </Message>
                {/each}
            </div>

            <!-- Input -->
            <div class="bg-white border-t p-4">
                <div class="flex gap-2">
                    <input
                        bind:value={currentMessage}
                        onkeypress={handleKeypress}
                        placeholder="Type a message..."
                        disabled={isLoading}
                        class="flex-1 px-4 py-2 border rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-50"
                    />
                    <button
                        onclick={sendMessage}
                        disabled={isLoading || !currentMessage.trim()}
                        class="px-6 py-2 bg-blue-500 text-white rounded-md hover:bg-blue-600 disabled:opacity-50"
                    >
                        {isLoading ? '...' : 'Send'}
                    </button>
                </div>
            </div>
        </div>

        <!-- Artifact Panel -->
        {#if artifact.isOpen}
            <div class="w-2/3 flex flex-col">
                <Artifact />
            </div>
        {/if}
    </div>
</div>

<!-- Permission Dialog -->
{#if showPermissionDialog && pendingPermission}
    <div class="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
        <div class="bg-white rounded-lg shadow-xl p-6 max-w-md w-full mx-4">
            <h3 class="text-lg font-semibold mb-2">Permission Required</h3>
            <p class="text-gray-700 mb-1">
                Agent wants to call: <span class="font-mono font-semibold">{pendingPermission.functionName}</span>
            </p>
            <p class="text-sm text-gray-600 mb-4">{pendingPermission.description || 'No description available'}</p>

            {#if pendingPermission.arguments && Object.keys(pendingPermission.arguments).length > 0}
                <div class="bg-gray-50 rounded p-3 mb-4">
                    <p class="text-xs font-semibold text-gray-700 mb-1">Arguments:</p>
                    <pre class="text-xs text-gray-600 overflow-x-auto">{JSON.stringify(pendingPermission.arguments, null, 2)}</pre>
                </div>
            {/if}

            <div class="flex flex-col gap-2">
                <div class="flex gap-2">
                    <button
                        onclick={() => respondToPermission(true, 'ask')}
                        class="flex-1 px-4 py-2 bg-green-500 text-white rounded-md hover:bg-green-600"
                    >
                        Allow Once
                    </button>
                    <button
                        onclick={() => respondToPermission(true, 'allow_always')}
                        class="flex-1 px-4 py-2 bg-green-600 text-white rounded-md hover:bg-green-700"
                    >
                        Always Allow
                    </button>
                </div>
                <div class="flex gap-2">
                    <button
                        onclick={() => respondToPermission(false, 'ask')}
                        class="flex-1 px-4 py-2 bg-red-500 text-white rounded-md hover:bg-red-600"
                    >
                        Deny Once
                    </button>
                    <button
                        onclick={() => respondToPermission(false, 'deny_always')}
                        class="flex-1 px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700"
                    >
                        Never Allow
                    </button>
                </div>
            </div>
        </div>
    </div>
{/if}
