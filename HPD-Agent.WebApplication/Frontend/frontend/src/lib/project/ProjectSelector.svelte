<!-- ProjectSelector.svelte -->
<script lang="ts">
    import { createEventDispatcher, onMount } from 'svelte';
    
    const dispatch = createEventDispatcher();
    const API_BASE = 'http://localhost:5135';
    
    interface Project {
        id: string;
        name: string;
        description: string;
        createdAt: string;
        lastActivity: string;
        conversationCount: number;
    }
    
    let projects: Project[] = [];
    let showCreateForm = false;
    let newProjectName = '';
    let newProjectDescription = '';
    let isLoading = false;
    let isCreating = false;
    
    async function loadProjects() {
        isLoading = true;
        try {
            const response = await fetch(`${API_BASE}/projects`);
            if (response.ok) {
                projects = await response.json();
            } else {
                console.error('Failed to load projects:', response.status);
            }
        } catch (error) {
            console.error('Error loading projects:', error);
        } finally {
            isLoading = false;
        }
    }
    
    async function createProject() {
        if (!newProjectName.trim()) return;
        
        isCreating = true;
        try {
            const response = await fetch(`${API_BASE}/projects`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    name: newProjectName.trim(),
                    description: newProjectDescription.trim()
                })
            });
            
            if (response.ok) {
                newProjectName = '';
                newProjectDescription = '';
                showCreateForm = false;
                await loadProjects();
            } else {
                const error = await response.text();
                console.error('Failed to create project:', error);
            }
        } catch (error) {
            console.error('Error creating project:', error);
        } finally {
            isCreating = false;
        }
    }
    
    function selectProject(projectId: string) {
        dispatch('projectSelected', projectId);
    }
    
    function formatDate(dateStr: string): string {
        return new Date(dateStr).toLocaleDateString();
    }
    
    function handleKeypress(event: KeyboardEvent) {
        if (event.key === 'Enter' && !event.shiftKey) {
            event.preventDefault();
            createProject();
        }
    }
    
    onMount(loadProjects);
</script>

<div class="min-h-screen bg-gray-50 py-8">
    <div class="max-w-6xl mx-auto px-4">
        <div class="text-center mb-8">
            <h1 class="text-4xl font-bold text-gray-800 mb-2">
                HPD-Agent Projects
            </h1>
            <p class="text-gray-600">
                Select a project to start chatting, or create a new one
            </p>
        </div>
        
        <!-- Create Project Button -->
        <div class="text-center mb-8">
            <button
                on:click={() => showCreateForm = !showCreateForm}
                class="px-6 py-3 bg-blue-500 text-white rounded-lg hover:bg-blue-600 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2 transition-colors"
            >
                {showCreateForm ? 'Cancel' : '+ New Project'}
            </button>
        </div>
        
        <!-- Create Project Form -->
        {#if showCreateForm}
            <div class="bg-white rounded-lg shadow-md p-6 mb-8 max-w-md mx-auto">
                <h3 class="text-lg font-semibold mb-4">Create New Project</h3>
                <div class="space-y-4">
                    <div>
                        <label for="projectName" class="block text-sm font-medium text-gray-700 mb-1">
                            Project Name *
                        </label>
                        <input
                            id="projectName"
                            bind:value={newProjectName}
                            on:keypress={handleKeypress}
                            placeholder="Enter project name"
                            class="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-transparent"
                            disabled={isCreating}
                        />
                    </div>
                    <div>
                        <label for="projectDescription" class="block text-sm font-medium text-gray-700 mb-1">
                            Description
                        </label>
                        <textarea
                            id="projectDescription"
                            bind:value={newProjectDescription}
                            placeholder="Optional description"
                            rows="3"
                            class="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-transparent"
                            disabled={isCreating}
                        ></textarea>
                    </div>
                    <div class="flex space-x-3">
                        <button
                            on:click={createProject}
                            disabled={!newProjectName.trim() || isCreating}
                            class="flex-1 px-4 py-2 bg-green-500 text-white rounded-md hover:bg-green-600 focus:outline-none focus:ring-2 focus:ring-green-500 focus:ring-offset-2 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                        >
                            {isCreating ? 'Creating...' : 'Create Project'}
                        </button>
                        <button
                            on:click={() => showCreateForm = false}
                            disabled={isCreating}
                            class="flex-1 px-4 py-2 bg-gray-500 text-white rounded-md hover:bg-gray-600 focus:outline-none focus:ring-2 focus:ring-gray-500 focus:ring-offset-2 disabled:opacity-50 transition-colors"
                        >
                            Cancel
                        </button>
                    </div>
                </div>
            </div>
        {/if}
        
        <!-- Projects Grid -->
        {#if isLoading}
            <div class="flex justify-center items-center py-12">
                <div class="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-500"></div>
                <span class="ml-3 text-gray-600">Loading projects...</span>
            </div>
        {:else if projects.length === 0}
            <div class="text-center py-12">
                <div class="text-gray-400 mb-4">
                    <svg class="mx-auto h-16 w-16" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1" d="M19 11H5m14 0a2 2 0 012 2v6a2 2 0 01-2 2H5a2 2 0 01-2-2v-6a2 2 0 012-2m14 0V9a2 2 0 00-2-2M5 11V9a2 2 0 012-2m0 0V5a2 2 0 012-2h6a2 2 0 012 2v2M7 7h10" />
                    </svg>
                </div>
                <h3 class="text-lg font-medium text-gray-700 mb-2">No Projects Yet</h3>
                <p class="text-gray-500 mb-4">Create your first project to get started with HPD-Agent</p>
                <button
                    on:click={() => showCreateForm = true}
                    class="px-4 py-2 bg-blue-500 text-white rounded-md hover:bg-blue-600 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2 transition-colors"
                >
                    Create Your First Project
                </button>
            </div>
        {:else}
            <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
                {#each projects as project}
                    <div
                        class="bg-white rounded-lg shadow-md hover:shadow-lg transition-shadow cursor-pointer border border-gray-200 hover:border-blue-300"
                        on:click={() => selectProject(project.id)}
                        on:keydown={(e) => e.key === 'Enter' && selectProject(project.id)}
                        tabindex="0"
                        role="button"
                    >
                        <div class="p-6">
                            <div class="flex items-start justify-between mb-3">
                                <h3 class="text-lg font-semibold text-gray-800 truncate">
                                    {project.name}
                                </h3>
                                <div class="flex items-center text-sm text-gray-500 ml-2">
                                    <svg class="h-4 w-4 mr-1" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                                        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M8 12h.01M12 12h.01M16 12h.01M21 12c0 4.418-4.03 8-9 8a9.863 9.863 0 01-4.255-.949L3 20l1.395-3.72C3.512 15.042 3 13.574 3 12c0-4.418 4.03-8 9-8s9 3.582 9 8z" />
                                    </svg>
                                    {project.conversationCount}
                                </div>
                            </div>
                            
                            {#if project.description}
                                <p class="text-gray-600 text-sm mb-4 line-clamp-2">
                                    {project.description}
                                </p>
                            {/if}
                            
                            <div class="flex justify-between items-center text-xs text-gray-500">
                                <span>Created: {formatDate(project.createdAt)}</span>
                                <span>Last active: {formatDate(project.lastActivity)}</span>
                            </div>
                        </div>
                        
                        <div class="px-6 py-3 bg-gray-50 border-t border-gray-200 rounded-b-lg">
                            <div class="flex items-center text-sm text-blue-600">
                                <span>Open Project</span>
                                <svg class="ml-2 h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M9 5l7 7-7 7" />
                                </svg>
                            </div>
                        </div>
                    </div>
                {/each}
            </div>
        {/if}
    </div>
</div>

<style>
    .line-clamp-2 {
        display: -webkit-box;
        -webkit-line-clamp: 2;
        -webkit-box-orient: vertical;
        overflow: hidden;
    }
</style>