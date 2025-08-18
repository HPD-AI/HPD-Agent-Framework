<!-- ✨ CLEAN FRONTEND: Updated +page.svelte -->
<script lang="ts">
    import { onMount } from 'svelte';
    import ProjectSelector from '../lib/project/ProjectSelector.svelte';
    import ConversationSidebar from '../lib/project/ConversationSidebar.svelte';
    import * as aguiClient from '@ag-ui/client';
    const { EventType } = aguiClient;
    
    // ✨ CLEAN API CLIENT
    class AgentAPI {
        baseUrl: string;
        constructor(baseUrl = 'http://localhost:5135') {
            this.baseUrl = baseUrl;
        }
        
        async createProject(name: string, description = '') {
            const response = await fetch(`${this.baseUrl}/projects`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name, description })
            });
            if (!response.ok) throw new Error(`Failed to create project: ${response.statusText}`);
            return response.json();
        }
        
        async getProjects() {
            const response = await fetch(`${this.baseUrl}/projects`);
            if (!response.ok) throw new Error(`Failed to get projects: ${response.statusText}`);
            return response.json();
        }
        
        async createConversation(projectId: string, name = '') {
            const response = await fetch(`${this.baseUrl}/projects/${projectId}/conversations`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name })
            });
            if (!response.ok) throw new Error(`Failed to create conversation: ${response.statusText}`);
            return response.json();
        }
        
        async getConversation(projectId: string, conversationId: string) {
            const response = await fetch(`${this.baseUrl}/projects/${projectId}/conversations/${conversationId}`);
            if (!response.ok) throw new Error(`Failed to get conversation: ${response.statusText}`);
            return response.json();
        }
        
        async sendMessage(projectId: string, conversationId: string, message: string) {
            const response = await fetch(`${this.baseUrl}/agent/projects/${projectId}/conversations/${conversationId}/chat`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ message })
            });
            if (!response.ok) throw new Error(`Failed to send message: ${response.statusText}`);
            return response.json();
        }
        
        streamChat(projectId: string, conversationId: string, message: string) {
            return fetch(`${this.baseUrl}/agent/projects/${projectId}/conversations/${conversationId}/stream`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ 
                    threadId: conversationId,
                    messages: [{ content: message }]
                })
            });
        }
        
        async transcribeAudio(projectId: string, conversationId: string, audioBlob: Blob) {
            const response = await fetch(`${this.baseUrl}/agent/projects/${projectId}/conversations/${conversationId}/stt`, {
                method: 'POST',
                headers: { 'Content-Type': 'audio/webm' },
                body: audioBlob
            });
            if (!response.ok) throw new Error(`Failed to transcribe audio: ${response.statusText}`);
            return response.json();
        }
    }
    
    // ✨ CLEAN AUDIO RECORDER
    class AudioRecorder {
        private mediaRecorder?: MediaRecorder;
        private chunks: Blob[] = [];
        public isRecording = false;
        
        async startRecording(): Promise<void> {
            try {
                const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
                this.chunks = [];
                this.mediaRecorder = new MediaRecorder(stream);
                
                this.mediaRecorder.ondataavailable = (e) => {
                    if (e.data.size > 0) this.chunks.push(e.data);
                };
                
                this.mediaRecorder.start();
                this.isRecording = true;
            } catch (error) {
                console.error('Failed to start recording:', error);
                throw new Error('Microphone access denied');
            }
        }
        
        async stopRecording(): Promise<Blob> {
            return new Promise((resolve, reject) => {
                if (!this.mediaRecorder) {
                    reject(new Error('No recording in progress'));
                    return;
                }
                
                this.mediaRecorder.onstop = () => {
                    const blob = new Blob(this.chunks, { type: 'audio/webm' });
                    this.isRecording = false;
                    resolve(blob);
                };
                
                this.mediaRecorder.stop();
                this.mediaRecorder.stream.getTracks().forEach(track => track.stop());
            });
        }
        
        async recordAndTranscribe(api: AgentAPI, projectId: string, conversationId: string): Promise<string> {
            const audioBlob = await this.stopRecording();
            const result = await api.transcribeAudio(projectId, conversationId, audioBlob);
            return result.transcript || '';
        }
    }
    
    // ✨ CLEAN CHAT MANAGER
    interface ChatMessage {
        role: 'user' | 'assistant';
        content: string;
        thinking?: string;
        timestamp: Date;
    }
    
    // ✨ REACTIVE STATE VARIABLES (outside class for Svelte reactivity)
    let messages: ChatMessage[] = [];
    let isLoading = false;
    let streamingContent = '';
    let currentThinking = '';
    
    class ChatManager {
        addMessage(message: Omit<ChatMessage, 'timestamp'>) {
            messages = [...messages, {
                ...message,
                timestamp: new Date()
            }];
        }
        
        updateLastMessage(content?: string, thinking?: string) {
            if (messages.length === 0) {
                console.warn('⚠️ No messages to update');
                return;
            }
            
            const lastMessage = { ...messages[messages.length - 1] };
            console.log('📝 Updating last message. Current:', lastMessage);
            
            if (content !== undefined) {
                lastMessage.content = content;
                console.log('📝 Updated content to:', content);
            }
            if (thinking !== undefined) {
                lastMessage.thinking = thinking;
                console.log('💭 Updated thinking to:', thinking);
            }
            
            messages = [...messages.slice(0, -1), lastMessage];
            console.log('📝 Messages array updated, length:', messages.length);
        }
        
        loadMessages(apiMessages: any[]) {
            messages = apiMessages.map(msg => ({
                role: msg.role,
                content: msg.content,
                timestamp: new Date(msg.timestamp || Date.now())
            }));
        }
        
        setLoading(loading: boolean) {
            isLoading = loading;
        }
        
        setStreamingContent(content: string) {
            streamingContent = content;
        }
        
        setCurrentThinking(thinking: string) {
            currentThinking = thinking;
        }
        
        async sendMessage(content: string, api: AgentAPI, projectId: string, conversationId: string) {
            console.log('📤 Sending message:', content);
            this.addMessage({ role: 'user', content });
            this.setLoading(true);
            this.setStreamingContent('');
            this.setCurrentThinking('');
            
            // Reset streaming content variable
            streamingContent = '';
            currentThinking = '';
            
            // Add placeholder for assistant response
            this.addMessage({ role: 'assistant', content: '', thinking: '' });
            console.log('📝 Added placeholder assistant message');
            
            try {
                await this.streamResponse(content, api, projectId, conversationId);
            } catch (error) {
                console.error('❌ Error in sendMessage:', error);
                this.updateLastMessage(`Error: ${error instanceof Error ? error.message : 'Unknown error'}`);
            } finally {
                this.setLoading(false);
                console.log('✅ Message sending completed');
            }
        }
        
        private async streamResponse(userMessage: string, api: AgentAPI, projectId: string, conversationId: string) {
            console.log('🚀 Starting stream response for message:', userMessage);
            const response = await api.streamChat(projectId, conversationId, userMessage);
            
            if (!response.ok) {
                console.error('❌ Stream response not ok:', response.status, response.statusText);
                throw new Error(`Stream failed: ${response.statusText}`);
            }
            
            console.log('✅ Stream response ok, starting to read...');
            const reader = response.body?.getReader();
            if (!reader) throw new Error('No response body reader');
            
            const decoder = new TextDecoder();
            
            while (true) {
                const { done, value } = await reader.read();
                if (done) {
                    console.log('✅ Stream finished');
                    break;
                }
                
                const chunk = decoder.decode(value, { stream: true });
                console.log('📦 Raw chunk received:', chunk);
                const lines = chunk.split('\n');
                
                for (const line of lines) {
                    if (line.startsWith('data: ')) {
                        const dataContent = line.substring(6);
                        console.log('📋 Processing data line:', dataContent);
                        try {
                            const data = JSON.parse(dataContent);
                            this.handleStreamEvent(data);
                        } catch (e) {
                            console.error('❌ Error parsing event data:', e, 'Raw data:', dataContent);
                        }
                    } else if (line.trim()) {
                        console.log('📋 Non-data line:', line);
                    }
                }
            }
        }
        
        private handleStreamEvent(event: any) {
            console.log('🎯 Stream event received:', event);
            
            switch (event.type) {
                case 'text_message_content':
                case 'TEXT_MESSAGE_CONTENT':
                case EventType.TEXT_MESSAGE_CONTENT:
                    console.log('📝 Text content event:', event);
                    if (event.delta) {
                        console.log('📝 Text content delta:', event.delta);
                        streamingContent += event.delta;
                        // Clear thinking status when we receive actual content
                        currentThinking = '';
                        this.updateLastMessage(streamingContent);
                        console.log('📝 Updated streaming content:', streamingContent);
                    } else if (event.content) {
                        // Handle alternative content property
                        console.log('📝 Text content (alternative):', event.content);
                        streamingContent += event.content;
                        // Clear thinking status when we receive actual content
                        currentThinking = '';
                        this.updateLastMessage(streamingContent);
                    } else {
                        console.warn('⚠️ Text content event missing delta/content:', event);
                    }
                    break;
                    
                case 'thinking_text_message_content':
                case 'THINKING_TEXT_MESSAGE_CONTENT':
                case EventType.THINKING_TEXT_MESSAGE_CONTENT:
                    console.log('💭 Thinking event:', event);
                    if (event.delta) {
                        console.log('💭 Thinking delta:', event.delta);
                        currentThinking = event.delta;
                        this.updateLastMessage(undefined, currentThinking);
                    } else if (event.content) {
                        console.log('💭 Thinking content (alternative):', event.content);
                        currentThinking = event.content;
                        this.updateLastMessage(undefined, currentThinking);
                    }
                    break;
                    
                case 'text_message_start':
                case 'TEXT_MESSAGE_START':
                case EventType.TEXT_MESSAGE_START:
                    console.log('🚀 Text message start');
                    this.setStreamingContent('');
                    streamingContent = '';
                    break;
                    
                case 'text_message_end':
                case 'TEXT_MESSAGE_END':
                case EventType.TEXT_MESSAGE_END:
                    console.log('🏁 Text message end');
                    // Finalize the message
                    this.updateLastMessage(streamingContent);
                    break;
                    
                case 'run_error':
                case 'RUN_ERROR':
                case EventType.RUN_ERROR:
                    console.log('❌ Run error:', event);
                    this.updateLastMessage(`Error: ${event.error || event.message || 'Unknown error'}`);
                    this.setLoading(false);
                    break;
                    
                case 'run_finished':
                case 'RUN_FINISHED':
                case EventType.RUN_FINISHED:
                    console.log('✅ Run finished');
                    this.setLoading(false);
                    break;
                    
                // ✨ TOOL CALL EVENTS
                case 'tool_call_start':
                case 'TOOL_CALL_START':
                case EventType.TOOL_CALL_START:
                    console.log('🔧 Tool call started:', event.toolCallName);
                    if (event.toolCallName) {
                        currentThinking = `Using ${event.toolCallName}...`;
                        this.updateLastMessage(streamingContent, currentThinking);
                    }
                    break;
                    
                case 'tool_call_args':
                case 'TOOL_CALL_ARGS':
                case EventType.TOOL_CALL_ARGS:
                    console.log('⚙️ Tool call args:', event.delta);
                    // Don't add tool arguments to the final content
                    // Just update thinking status
                    if (event.delta) {
                        try {
                            const args = JSON.parse(event.delta);
                            const argDisplay = Object.entries(args)
                                .map(([key, value]) => `${key}=${value}`)
                                .join(', ');
                            currentThinking = `Calculating with ${argDisplay}...`;
                            this.updateLastMessage(streamingContent, currentThinking);
                        } catch {
                            currentThinking = `Processing tool arguments...`;
                            this.updateLastMessage(streamingContent, currentThinking);
                        }
                    }
                    break;
                    
                case 'tool_call_end':
                case 'TOOL_CALL_END':
                case EventType.TOOL_CALL_END:
                    console.log('✅ Tool call completed');
                    currentThinking = `Tool completed, getting result...`;
                    this.updateLastMessage(streamingContent, currentThinking);
                    break;
                    
                default:
                    console.log('❓ Unknown event type:', event.type, event);
                    // Only fall back to delta extraction for truly unknown events
                    // Don't extract deltas from tool events we should ignore
                    if (!event.type?.includes('tool_call') && event.delta) {
                        console.log('📝 Fallback: Found delta in unknown event:', event.delta);
                        streamingContent += event.delta;
                        this.updateLastMessage(streamingContent);
                    } else if (!event.type?.includes('tool_call') && event.content) {
                        console.log('📝 Fallback: Found content in unknown event:', event.content);
                        streamingContent += event.content;
                        this.updateLastMessage(streamingContent);
                    }
                    break;
            }
        }
    }
    
    // ✨ APPLICATION STATE
    let currentProjectId: string | null = null;
    let currentConversationId: string | null = null;
    let currentMessage = '';
    let audioURL = '';
    
    // ✨ CLEAN INSTANCES
    const api = new AgentAPI();
    const chatManager = new ChatManager();
    const audioRecorder = new AudioRecorder();
    
    // ✨ LIFECYCLE
    onMount(() => {
        loadSavedState();
    });
    
    // ✨ REACTIVE UPDATES
    $: if (typeof localStorage !== 'undefined') {
        if (currentProjectId) {
            localStorage.setItem('currentProjectId', currentProjectId);
        } else {
            localStorage.removeItem('currentProjectId');
        }
    }
    
    $: if (typeof localStorage !== 'undefined') {
        if (currentConversationId) {
            localStorage.setItem('currentConversationId', currentConversationId);
        } else {
            localStorage.removeItem('currentConversationId');
        }
    }
    
    $: if (currentProjectId && currentConversationId) {
        loadConversationMessages();
    }
    
    // ✨ HELPER FUNCTIONS
    function loadSavedState() {
        if (typeof localStorage !== 'undefined') {
            currentProjectId = localStorage.getItem('currentProjectId');
            currentConversationId = localStorage.getItem('currentConversationId');
        }
    }
    
    async function loadConversationMessages() {
        if (!currentProjectId || !currentConversationId) return;
        
        try {
            console.log('Loading conversation messages for:', currentProjectId, currentConversationId);
            const conversation = await api.getConversation(currentProjectId, currentConversationId);
            console.log('Received conversation:', conversation);
            console.log('Messages from API:', conversation.messages);
            chatManager.loadMessages(conversation.messages || []);
            console.log('ChatManager messages after load:', messages);
        } catch (error) {
            console.error('Error loading conversation messages:', error);
        }
    }
    
    // ✨ EVENT HANDLERS
    function handleProjectSelected(event: CustomEvent<string>) {
        currentProjectId = event.detail;
        currentConversationId = null;
        messages = [];
    }
    
    function handleConversationSelected(event: CustomEvent<string | null>) {
        currentConversationId = event.detail;
        if (event.detail === null) {
            messages = [];
        }
    }
    
    function handleBackToProjects() {
        currentProjectId = null;
        currentConversationId = null;
        messages = [];
    }
    
    async function sendMessage() {
        if (!currentMessage.trim() || isLoading || !currentProjectId || !currentConversationId) return;
        
        const message = currentMessage.trim();
        currentMessage = '';
        
        await chatManager.sendMessage(message, api, currentProjectId, currentConversationId);
    }
    
    function handleKeypress(event: KeyboardEvent) {
        if (event.key === 'Enter' && !event.shiftKey) {
            event.preventDefault();
            sendMessage();
        }
    }
    
    // ✨ AUDIO HANDLERS
    async function toggleRecording() {
        try {
            if (audioRecorder.isRecording) {
                const transcript = await audioRecorder.recordAndTranscribe(api, currentProjectId!, currentConversationId!);
                currentMessage = transcript;
                await sendMessage();
            } else {
                await audioRecorder.startRecording();
            }
        } catch (error) {
            console.error('Audio recording error:', error);
        }
    }
    
    async function recordDirectToAgent() {
        if (!currentProjectId || !currentConversationId) return;
        
        try {
            await audioRecorder.startRecording();
            const transcript = await audioRecorder.recordAndTranscribe(api, currentProjectId, currentConversationId);
            await chatManager.sendMessage(transcript, api, currentProjectId, currentConversationId);
        } catch (error) {
            console.error('Direct recording error:', error);
        }
    }
</script>

<!-- ✨ CLEAN LAYOUT -->
<div class="h-screen flex">
    {#if !currentProjectId}
        <!-- Project Selection Screen -->
        <div class="w-full">
            <ProjectSelector on:projectSelected={handleProjectSelected} />
        </div>
    {:else}
        <!-- Project View with Sidebar -->
        <div class="w-80 flex-shrink-0">
            <ConversationSidebar 
                projectId={currentProjectId}
                selectedConversationId={currentConversationId}
                on:conversationSelected={handleConversationSelected}
                on:backToProjects={handleBackToProjects}
            />
        </div>
        
        <!-- Chat Interface -->
        <div class="flex-1 flex flex-col">
            {#if !currentConversationId}
                <!-- No Conversation Selected -->
                <div class="flex-1 flex items-center justify-center bg-gray-50">
                    <div class="text-center">
                        <div class="text-gray-400 mb-4">
                            <svg class="mx-auto h-16 w-16" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                                <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1" d="M8 12h.01M12 12h.01M16 12h.01M21 12c0 4.418-4.03 8-9 8a9.863 9.863 0 01-4.255-.949L3 20l1.395-3.72C3.512 15.042 3 13.574 3 12c0-4.418 4.03-8 9-8s9 3.582 9 8z" />
                            </svg>
                        </div>
                        <h3 class="text-lg font-medium text-gray-700 mb-2">Select a Conversation</h3>
                        <p class="text-gray-500 mb-4">Choose a conversation from the sidebar or create a new one to start chatting</p>
                    </div>
                </div>
            {:else}
                <!-- Chat Interface -->
                <div class="flex-1 flex flex-col bg-gray-50">
                    <!-- Chat Header -->
                    <div class="bg-white border-b border-gray-200 px-6 py-4">
                        <h1 class="text-xl font-semibold text-gray-800">
                            HPD-Agent Chat
                        </h1>
                        <p class="text-sm text-gray-500">
                            Conversation in project • {messages.length} messages
                        </p>
                    </div>
                    
                    <!-- Chat Messages -->
                    <div class="flex-1 overflow-y-auto p-6 space-y-4">
                        {#each messages as message}
                            <div class="flex {message.role === 'user' ? 'justify-end' : 'justify-start'}">
                                <div class="max-w-xs lg:max-w-md px-4 py-2 rounded-lg {
                                    message.role === 'user' 
                                        ? 'bg-blue-500 text-white' 
                                        : 'bg-white text-gray-800 shadow-sm border border-gray-200'
                                }">
                                    <div class="text-sm font-medium mb-1">
                                        {message.role === 'user' ? 'You' : 'Agent'}
                                    </div>
                                    
                                    <!-- Show thinking process if available -->
                                    {#if message.thinking && message.thinking.trim()}
                                        <div class="text-xs italic text-gray-600 mb-2 border-l-2 border-gray-300 pl-2">
                                            💭 {message.thinking}
                                        </div>
                                    {/if}
                                    
                                    <div class="whitespace-pre-wrap">{message.content}</div>
                                    
                                    <!-- Show loading state for current message -->
                                    {#if message.role === 'assistant' && isLoading && message === messages[messages.length - 1]}
                                        <div class="mt-2 flex items-center text-xs text-gray-500">
                                            <div class="animate-spin rounded-full h-3 w-3 border-b-2 border-gray-500 mr-2"></div>
                                            {currentThinking || 'Processing...'}
                                        </div>
                                    {/if}
                                </div>
                            </div>
                        {/each}
                        
                        {#if isLoading && messages.length === 0}
                            <div class="flex justify-start">
                                <div class="bg-white text-gray-800 px-4 py-2 rounded-lg shadow-sm border border-gray-200">
                                    <div class="text-sm font-medium mb-1">Agent</div>
                                    <div class="animate-pulse">Initializing...</div>
                                </div>
                            </div>
                        {/if}
                    </div>
                    
                    <!-- Message Input -->
                    <div class="bg-white border-t border-gray-200 p-6">
                        <div class="flex space-x-2">
                            <input
                                bind:value={currentMessage}
                                on:keypress={handleKeypress}
                                placeholder="Type your message here..."
                                disabled={isLoading}
                                class="flex-1 px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-transparent disabled:opacity-50"
                            />
                            
                            <!-- Voice Recording Button -->
                            <button
                                on:click={toggleRecording}
                                aria-label={audioRecorder.isRecording ? 'Stop recording' : 'Record for chat'}
                                class="p-2 rounded-full focus:outline-none focus:ring-2 focus:ring-blue-500"
                                class:bg-red-100={audioRecorder.isRecording}
                                class:hover:bg-red-200={audioRecorder.isRecording}
                                class:bg-blue-100={!audioRecorder.isRecording}
                                class:hover:bg-blue-200={!audioRecorder.isRecording}
                                disabled={isLoading}
                            >
                                {#if audioRecorder.isRecording}
                                    <svg xmlns="http://www.w3.org/2000/svg" class="h-5 w-5 text-red-600" fill="currentColor" viewBox="0 0 24 24">
                                        <rect x="6" y="6" width="12" height="12" />
                                    </svg>
                                {:else}
                                    <svg xmlns="http://www.w3.org/2000/svg" class="h-5 w-5 text-blue-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                                        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 18v3m0 0h3m-3 0H9m6-3a6 6 0 01-12 0V9a6 6 0 0112 0v6z" />
                                    </svg>
                                {/if}
                            </button>
                            
                            <!-- Direct to Agent Button -->
                            <button
                                on:click={recordDirectToAgent}
                                aria-label="Record direct to agent"
                                class="p-2 rounded-full focus:outline-none focus:ring-2 focus:ring-green-500 bg-green-100 hover:bg-green-200"
                                disabled={isLoading || audioRecorder.isRecording}
                            >
                                <svg xmlns="http://www.w3.org/2000/svg" class="h-5 w-5 text-green-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                                </svg>
                            </button>
                            
                            <!-- Send Button -->
                            <button
                                on:click={sendMessage}
                                disabled={isLoading || !currentMessage.trim()}
                                class="px-6 py-2 bg-blue-500 text-white rounded-md hover:bg-blue-600 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2 disabled:opacity-50 disabled:cursor-not-allowed"
                            >
                                {isLoading ? 'Sending...' : 'Send'}
                            </button>
                        </div>
                    </div>
                    
                    {#if audioURL}
                        <div class="px-6 pb-4">
                            <audio controls src={audioURL} class="w-full"></audio>
                        </div>
                    {/if}
                </div>
            {/if}
        </div>
    {/if}
</div>

<!-- Connection Status -->
{#if currentProjectId && currentConversationId}
    <div class="fixed bottom-4 right-4 bg-white rounded-lg shadow-md px-4 py-2 border border-gray-200">
        <div class="text-sm text-gray-600">
            Status: 
            <span class="font-medium text-green-600">
                Ready (Clean Architecture)
            </span>
        </div>
        {#if isLoading}
            <div class="text-xs text-blue-600 mt-1">
                💡 {currentThinking || 'Processing...'}
            </div>
        {/if}
        <div class="text-xs text-gray-500 mt-1">
            🎯 Powered by clean APIs
        </div>
    </div>
{/if}