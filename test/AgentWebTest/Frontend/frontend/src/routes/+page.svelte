<script lang="ts">
    import { onMount } from 'svelte';
    import { AgentClient, type PermissionRequestEvent, type PermissionChoice, type FrontendToolInvokeRequestEvent } from '@hpd/hpd-agent-client';
    import Artifact from '$lib/artifacts/Artifact.svelte';
    import { artifactStore, type ArtifactState } from '$lib/artifacts/artifact-store.js';
    import { artifactPlugin, handleArtifactTool } from '$lib/artifacts/artifact-plugin.js';

    const API_BASE = 'http://localhost:5135';

    interface Message {
        role: 'user' | 'assistant';
        content: string;
        thinking?: string;
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
    let messages: Message[] = [];
    let currentMessage = '';
    let isLoading = false;
    let streamingContent = '';
    let currentThinking = '';
    let pendingPermission: PendingPermission | null = null;
    let showPermissionDialog = false;

    // Artifact state (reactive)
    let artifact: ArtifactState;
    artifactStore.subscribe(value => artifact = value);

    // Create the client with artifact plugin
    const client = new AgentClient({
        baseUrl: API_BASE,
        frontendPlugins: [artifactPlugin]
    });

    onMount(async () => {
        const saved = localStorage.getItem('conversationId');
        if (saved) {
            // Verify conversation still exists
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
        // Create new conversation if none exists or verification failed
        await createConversation();
    });

    async function createConversation() {
        const res = await fetch(`${API_BASE}/conversations`, { method: 'POST' });
        const data = await res.json();
        conversationId = data.id;
        console.log('[CREATE] Created new conversation:', conversationId);
        localStorage.setItem('conversationId', conversationId!);
        messages = [];
        // Close any open artifact on new conversation
        artifactStore.close();
    }

    async function sendMessage() {
        if (!currentMessage.trim() || isLoading || !conversationId) return;

        const userMsg = currentMessage.trim();
        currentMessage = '';

        console.log('[SEND] Sending message to conversation:', conversationId);
        messages = [...messages, { role: 'user', content: userMsg }];
        messages = [...messages, { role: 'assistant', content: '', thinking: '' }];

        isLoading = true;
        streamingContent = '';
        currentThinking = '';

        try {
            await client.stream(
                conversationId,
                [{ content: userMsg }],
                {
                    // Content handlers
                    onTextDelta: (text) => {
                        console.log('Text delta:', text);
                        streamingContent += text;
                        currentThinking = '';
                        updateLastMessage();
                    },

                    // Reasoning handlers
                    onReasoning: (text, phase) => {
                        if (phase === 'Delta') {
                            console.log('Reasoning:', text?.substring(0, 100));
                            currentThinking = text;
                            updateLastMessage();
                        }
                    },

                    // Tool handlers
                    onToolCallStart: (_callId, name) => {
                        console.log('Tool call:', name);
                        currentThinking = `Using ${name}...`;
                        updateLastMessage();
                    },

                    onToolCallResult: (callId) => {
                        console.log('Tool result:', callId);
                    },

                    // Permission handler - async, waits for user response
                    onPermissionRequest: async (request: PermissionRequestEvent) => {
                        console.log('Permission request:', request.functionName);
                        currentThinking = `Waiting for permission to use ${request.functionName}...`;
                        updateLastMessage();

                        // Return a promise that resolves when user responds
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

                    // Frontend tool handler - handles artifact tools
                    onFrontendToolInvoke: async (request: FrontendToolInvokeRequestEvent) => {
                        console.log('Frontend tool invoke:', request.toolName, request.arguments);
                        currentThinking = `Executing ${request.toolName}...`;
                        updateLastMessage();

                        // Handle artifact tools
                        const response = handleArtifactTool(
                            request.toolName,
                            request.arguments,
                            request.requestId
                        );

                        console.log('Frontend tool response:', response);
                        return response;
                    },

                    // Frontend plugins registered
                    onFrontendPluginsRegistered: (event) => {
                        console.log('Frontend plugins registered:', event.registeredPlugins, 'Total tools:', event.totalTools);
                    },

                    // Lifecycle handlers
                    onTurnStart: (iteration) => {
                        console.log('Agent turn started:', iteration);
                    },

                    onTurnEnd: (iteration) => {
                        console.log('Agent turn finished:', iteration);
                    },

                    onComplete: () => {
                        console.log('Message complete');
                        isLoading = false;
                    },

                    onError: (message) => {
                        console.error('Error:', message);
                        streamingContent = `Error: ${message}`;
                        updateLastMessage();
                        isLoading = false;
                    },

                    // Raw event access for debugging
                    onEvent: (event) => {
                        console.log(`[v${event.version}] ${event.type}:`, event);
                    }
                }
            );
        } catch (error) {
            console.error('Stream error:', error);
            streamingContent = `Error: ${error}`;
            updateLastMessage();
        } finally {
            isLoading = false;
        }
    }

    function updateLastMessage() {
        if (messages.length === 0) return;
        const lastMsg = { ...messages[messages.length - 1] };
        lastMsg.content = streamingContent;
        lastMsg.thinking = currentThinking;
        messages = [...messages.slice(0, -1), lastMsg];
    }

    function respondToPermission(approved: boolean, choice: PermissionChoice = 'ask') {
        if (!pendingPermission) return;

        // Resolve the promise that the SDK is waiting on
        pendingPermission.resolve({
            approved,
            choice,
            reason: approved ? undefined : 'User denied permission'
        });

        showPermissionDialog = false;
        currentThinking = '';
        updateLastMessage();
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
            <h1 class="text-xl font-semibold">HPD-Agent Chat</h1>
            <p class="text-sm text-gray-500">{messages.length} messages</p>
        </div>
        <button
            onclick={createConversation}
            class="px-4 py-2 bg-gray-100 hover:bg-gray-200 rounded-md text-sm"
        >
            New Chat
        </button>
    </div>

    <!-- Main Content - Split View when artifact is open -->
    <div class="flex-1 flex overflow-hidden">
        <!-- Chat Panel -->
        <div class="flex-1 flex flex-col {artifact.isOpen ? 'w-1/3 min-w-[300px] border-r' : ''}">
            <!-- Messages -->
            <div class="flex-1 overflow-y-auto p-6 space-y-4">
                {#each messages as message, index}
                    <div class="flex gap-3 {message.role === 'user' ? 'justify-end' : ''}">
                        <div class="max-w-[80%] {message.role === 'user' ? 'bg-blue-500 text-white' : 'bg-white border'} rounded-lg p-4">
                            {#if message.thinking && index === messages.length - 1 && isLoading}
                                <div class="text-sm italic opacity-70 mb-2">
                                    {message.thinking}
                                </div>
                            {/if}
                            <div class="whitespace-pre-wrap">
                                {message.content}
                            </div>
                        </div>
                    </div>
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

        <!-- Artifact Panel (2/3 width when open) -->
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
