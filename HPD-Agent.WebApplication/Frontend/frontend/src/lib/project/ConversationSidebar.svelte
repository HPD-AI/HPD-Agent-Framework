<!-- ConversationSidebar.svelte -->
<script lang="ts">
    import { createEventDispatcher, onMount } from 'svelte';
    
    export let projectId: string;
    export let selectedConversationId: string | null = null;
    
    const dispatch = createEventDispatcher();
    const API_BASE = 'http://localhost:5135';
    
    interface Conversation {
        id: string;
        name: string;
        createdAt: string;
        lastActivity: string;
        messageCount: number;
    }
    
    interface Project {
        id: string;
        name: string;
        description: string;
        conversationCount: number;
    }
    
    let conversations: Conversation[] = [];
    let project: Project | null = null;
    let isLoading = false;
    let isCreating = false;
    let showProjectInfo = false;
    
    async function loadProject() {
        try {
            const response = await fetch(`${API_BASE}/projects/${projectId}`);
            if (response.ok) {
                project = await response.json();
            }
        } catch (error) {
            console.error('Error loading project:', error);
        }
    }
    
    async function loadConversations() {
        isLoading = true;
        try {
            const response = await fetch(`${API_BASE}/projects/${projectId}/conversations`);
            if (response.ok) {
                conversations = await response.json();
            } else {
                console.error('Failed to load conversations:', response.status);
            }
        } catch (error) {
            console.error('Error loading conversations:', error);
        } finally {
            isLoading = false;
        }
    }
    
    async function createNewConversation() {
        isCreating = true;
        try {
            const response = await fetch(`${API_BASE}/projects/${projectId}/conversations`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ name: `Chat ${conversations.length + 1}` })
            });
            
            if (response.ok) {
                const newConversation = await response.json();
                await loadConversations();
                selectConversation(newConversation.id);
            } else {
                console.error('Failed to create conversation:', response.status);
            }
        } catch (error) {
            console.error('Error creating conversation:', error);
        } finally {
            isCreating = false;
        }
    }
    
    async function deleteConversation(conversationId: string, event: Event) {
        event.stopPropagation(); // Prevent selecting the conversation
        
        if (!confirm('Are you sure you want to delete this conversation?')) {
            return;
        }
        
        try {
            const response = await fetch(`${API_BASE}/projects/${projectId}/conversations/${conversationId}`, {
                method: 'DELETE'
            });
            
            if (response.ok) {
                // If we're deleting the selected conversation, clear selection
                if (selectedConversationId === conversationId) {
                    selectedConversationId = null;
                    dispatch('conversationSelected', null);
                }
                await loadConversations();
            } else {
                console.error('Failed to delete conversation:', response.status);
            }
        } catch (error) {
            console.error('Error deleting conversation:', error);
        }
    }
    
    function selectConversation(conversationId: string) {
        selectedConversationId = conversationId;
        dispatch('conversationSelected', conversationId);
    }
    
    function goBackToProjects() {
        dispatch('backToProjects');
    }
    
    function formatDate(dateStr: string): string {
        const date = new Date(dateStr);
        const now = new Date();
        const diffMs = now.getTime() - date.getTime();
        const diffHours = diffMs / (1000 * 60 * 60);
        const diffDays = diffMs / (1000 * 60 * 60 * 24);
        
        if (diffHours < 1) {
            return 'Just now';
        } else if (diffHours < 24) {
            return `${Math.floor(diffHours)}h ago`;
        } else if (diffDays < 7) {
            return `${Math.floor(diffDays)}d ago`;
        } else {
            return date.toLocaleDateString();
        }
    }
    
    onMount(() => {
        loadProject();
        loadConversations();
    });
    
    // Reactive statement to reload when projectId changes
    $: if (projectId) {
        loadProject();
        loadConversations();
    }
</script>

<div class="h-screen bg-white border-r border-gray-200 flex flex-col">
    <!-- Header -->
    <div class="p-4 border-b border-gray-200 bg-gray-50">
        <div class="flex items-center justify-between mb-2">
            <button
                on:click={goBackToProjects}
                class="flex items-center text-gray-600 hover:text-gray-800 text-sm transition-colors"
            >
                <svg class="h-4 w-4 mr-1" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M15 19l-7-7 7-7" />
                </svg>
                All Projects
            </button>
            <button
                on:click={() => showProjectInfo = !showProjectInfo}
                class="text-gray-400 hover:text-gray-600 transition-colors"
                title="Project Info"
                aria-label="Toggle project info"
            >
                <svg class="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
            </button>
        </div>
        
        {#if project}
            <h2 class="font-semibold text-gray-800 truncate" title={project.name}>
                {project.name}
            </h2>
            {#if showProjectInfo && project.description}
                <p class="text-xs text-gray-600 mt-1">
                    {project.description}
                </p>
            {/if}
        {/if}
    </div>
    
    <!-- New Conversation Button -->
    <div class="p-4 border-b border-gray-200">
        <button
            on:click={createNewConversation}
            disabled={isCreating}
            class="w-full flex items-center justify-center px-4 py-2 bg-blue-500 text-white rounded-md hover:bg-blue-600 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
        >
            {#if isCreating}
                <div class="animate-spin rounded-full h-4 w-4 border-b-2 border-white mr-2"></div>
                Creating...
            {:else}
                <svg class="h-4 w-4 mr-2" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M12 4v16m8-8H4" />
                </svg>
                New Chat
            {/if}
        </button>
    </div>
    
    <!-- Conversations List -->
    <div class="flex-1 overflow-y-auto">
        {#if isLoading}
            <div class="p-4 text-center">
                <div class="animate-spin rounded-full h-6 w-6 border-b-2 border-gray-400 mx-auto mb-2"></div>
                <span class="text-sm text-gray-500">Loading conversations...</span>
            </div>
        {:else if conversations.length === 0}
            <div class="p-4 text-center text-gray-500">
                <svg class="mx-auto h-12 w-12 text-gray-400 mb-3" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1" d="M8 12h.01M12 12h.01M16 12h.01M21 12c0 4.418-4.03 8-9 8a9.863 9.863 0 01-4.255-.949L3 20l1.395-3.72C3.512 15.042 3 13.574 3 12c0-4.418 4.03-8 9-8s9 3.582 9 8z" />
                </svg>
                <p class="text-sm font-medium text-gray-600 mb-1">No conversations yet</p>
                <p class="text-xs text-gray-500">Create your first chat to get started</p>
            </div>
        {:else}
            <div class="conversation-list divide-y divide-gray-100">
                {#each conversations as conversation}
                    <div
                        class="conversation-item p-3 border-b border-gray-100 cursor-pointer hover:bg-gray-50 transition-colors relative group"
                        class:bg-blue-100={conversation.id === selectedConversationId}
                        class:border-l-4={conversation.id === selectedConversationId}
                        class:border-l-blue-600={conversation.id === selectedConversationId}
                        on:click={() => selectConversation(conversation.id)}
                        on:keydown={(e) => e.key === 'Enter' && selectConversation(conversation.id)}
                        tabindex="0"
                        role="button"
                    >
                        <div class="flex justify-between items-start mb-1">
                            <span 
                                class="conversation-name text-sm font-medium text-gray-800 truncate pr-2"
                                class:text-blue-700={conversation.id === selectedConversationId}
                            >
                                {conversation.name}
                            </span>
                            <button
                                on:click={(e) => deleteConversation(conversation.id, e)}
                                class="opacity-0 group-hover:opacity-100 text-gray-400 hover:text-red-500 transition-all flex-shrink-0"
                                title="Delete conversation"
                                aria-label="Delete conversation"
                            >
                                <svg class="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                                </svg>
                            </button>
                        </div>
                        <div class="flex justify-between items-center text-xs text-gray-500">
                            <span>{conversation.messageCount} messages</span>
                            <span>{formatDate(conversation.lastActivity)}</span>
                        </div>
                    </div>
                {/each}
            </div>
        {/if}
    </div>
    
    <!-- Footer -->
    <div class="p-4 border-t border-gray-200 bg-gray-50">
        <div class="text-xs text-gray-500 text-center">
            {conversations.length} conversation{conversations.length !== 1 ? 's' : ''}
        </div>
    </div>
</div>

