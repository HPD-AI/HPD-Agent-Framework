// stores/appState.ts
import { writable, derived } from 'svelte/store';

// Types
export interface Project {
    id: string;
    name: string;
    description: string;
    createdAt: string;
    lastActivity: string;
    conversationCount: number;
}

export interface Conversation {
    id: string;
    name: string;
    createdAt: string;
    lastActivity: string;
    messageCount: number;
}

export interface Message {
    id: string;
    role: 'user' | 'assistant';
    content: string;
    thinking?: string;
    timestamp: string;
}

export interface AppState {
    currentProject: Project | null;
    currentConversation: Conversation | null;
    projects: Project[];
    conversations: Conversation[];
    messages: Message[];
    isLoading: boolean;
    error: string | null;
}

// Initial state
const initialState: AppState = {
    currentProject: null,
    currentConversation: null,
    projects: [],
    conversations: [],
    messages: [],
    isLoading: false,
    error: null
};

// Main store
export const appState = writable<AppState>(initialState);

// Derived stores for easier access
export const currentProject = derived(appState, $state => $state.currentProject);
export const currentConversation = derived(appState, $state => $state.currentConversation);
export const projects = derived(appState, $state => $state.projects);
export const conversations = derived(appState, $state => $state.conversations);
export const messages = derived(appState, $state => $state.messages);
export const isLoading = derived(appState, $state => $state.isLoading);
export const error = derived(appState, $state => $state.error);

// Actions
export const appActions = {
    // Project actions
    setCurrentProject: (project: Project | null) => {
        appState.update(state => ({
            ...state,
            currentProject: project,
            currentConversation: null, // Reset conversation when project changes
            conversations: [],
            messages: []
        }));
        
        // Persist to localStorage
        if (project) {
            localStorage.setItem('currentProjectId', project.id);
        } else {
            localStorage.removeItem('currentProjectId');
        }
    },

    setProjects: (projects: Project[]) => {
        appState.update(state => ({ ...state, projects }));
    },

    addProject: (project: Project) => {
        appState.update(state => ({
            ...state,
            projects: [...state.projects, project]
        }));
    },

    removeProject: (projectId: string) => {
        appState.update(state => ({
            ...state,
            projects: state.projects.filter(p => p.id !== projectId),
            currentProject: state.currentProject?.id === projectId ? null : state.currentProject
        }));
    },

    // Conversation actions
    setCurrentConversation: (conversation: Conversation | null) => {
        appState.update(state => ({
            ...state,
            currentConversation: conversation,
            messages: [] // Clear messages when conversation changes
        }));
        
        // Persist to localStorage
        if (conversation) {
            localStorage.setItem('currentConversationId', conversation.id);
        } else {
            localStorage.removeItem('currentConversationId');
        }
    },

    setConversations: (conversations: Conversation[]) => {
        appState.update(state => ({ ...state, conversations }));
    },

    addConversation: (conversation: Conversation) => {
        appState.update(state => ({
            ...state,
            conversations: [...state.conversations, conversation]
        }));
    },

    removeConversation: (conversationId: string) => {
        appState.update(state => ({
            ...state,
            conversations: state.conversations.filter(c => c.id !== conversationId),
            currentConversation: state.currentConversation?.id === conversationId ? null : state.currentConversation
        }));
    },

    // Message actions
    setMessages: (messages: Message[]) => {
        appState.update(state => ({ ...state, messages }));
    },

    addMessage: (message: Message) => {
        appState.update(state => ({
            ...state,
            messages: [...state.messages, message]
        }));
    },

    updateLastMessage: (content: string, thinking?: string) => {
        appState.update(state => {
            const messages = [...state.messages];
            if (messages.length > 0) {
                messages[messages.length - 1] = {
                    ...messages[messages.length - 1],
                    content,
                    thinking
                };
            }
            return { ...state, messages };
        });
    },

    // UI state actions
    setLoading: (isLoading: boolean) => {
        appState.update(state => ({ ...state, isLoading }));
    },

    setError: (error: string | null) => {
        appState.update(state => ({ ...state, error }));
    },

    // Initialization
    initialize: () => {
        // Load from localStorage
        const projectId = localStorage.getItem('currentProjectId');
        const conversationId = localStorage.getItem('currentConversationId');
        
        if (projectId) {
            // You would typically load the project data from API here
            console.log('Initializing with project:', projectId);
        }
        
        if (conversationId) {
            console.log('Initializing with conversation:', conversationId);
        }
    }
};

// API helper functions
const API_BASE = 'http://localhost:5135';

export const api = {
    // Project API calls
    async loadProjects(): Promise<Project[]> {
        const response = await fetch(`${API_BASE}/projects`);
        if (!response.ok) throw new Error('Failed to load projects');
        return response.json();
    },

    async createProject(name: string, description: string = ''): Promise<Project> {
        const response = await fetch(`${API_BASE}/projects`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name, description })
        });
        if (!response.ok) throw new Error('Failed to create project');
        return response.json();
    },

    async deleteProject(projectId: string): Promise<void> {
        const response = await fetch(`${API_BASE}/projects/${projectId}`, {
            method: 'DELETE'
        });
        if (!response.ok) throw new Error('Failed to delete project');
    },

    // Conversation API calls
    async loadConversations(projectId: string): Promise<Conversation[]> {
        const response = await fetch(`${API_BASE}/projects/${projectId}/conversations`);
        if (!response.ok) throw new Error('Failed to load conversations');
        return response.json();
    },

    async createConversation(projectId: string, name: string = ''): Promise<Conversation> {
        const response = await fetch(`${API_BASE}/projects/${projectId}/conversations`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name })
        });
        if (!response.ok) throw new Error('Failed to create conversation');
        return response.json();
    },

    async deleteConversation(projectId: string, conversationId: string): Promise<void> {
        const response = await fetch(`${API_BASE}/projects/${projectId}/conversations/${conversationId}`, {
            method: 'DELETE'
        });
        if (!response.ok) throw new Error('Failed to delete conversation');
    },

    async loadConversationMessages(projectId: string, conversationId: string): Promise<Message[]> {
        const response = await fetch(`${API_BASE}/projects/${projectId}/conversations/${conversationId}`);
        if (!response.ok) throw new Error('Failed to load conversation');
        const conversation = await response.json();
        return conversation.messages;
    }
};

// Higher-level actions that combine API calls with state updates
export const appOperations = {
    async loadProjects() {
        appActions.setLoading(true);
        appActions.setError(null);
        try {
            const projects = await api.loadProjects();
            appActions.setProjects(projects);
        } catch (error) {
            appActions.setError(error instanceof Error ? error.message : 'Unknown error');
        } finally {
            appActions.setLoading(false);
        }
    },

    async createProject(name: string, description: string = '') {
        appActions.setLoading(true);
        appActions.setError(null);
        try {
            const project = await api.createProject(name, description);
            appActions.addProject(project);
            return project;
        } catch (error) {
            appActions.setError(error instanceof Error ? error.message : 'Unknown error');
            throw error;
        } finally {
            appActions.setLoading(false);
        }
    },

    async selectProject(project: Project) {
        appActions.setCurrentProject(project);
        appActions.setLoading(true);
        try {
            const conversations = await api.loadConversations(project.id);
            appActions.setConversations(conversations);
        } catch (error) {
            appActions.setError(error instanceof Error ? error.message : 'Unknown error');
        } finally {
            appActions.setLoading(false);
        }
    },

    async createConversation(projectId: string, name: string = '') {
        appActions.setLoading(true);
        try {
            const conversation = await api.createConversation(projectId, name);
            appActions.addConversation(conversation);
            return conversation;
        } catch (error) {
            appActions.setError(error instanceof Error ? error.message : 'Unknown error');
            throw error;
        } finally {
            appActions.setLoading(false);
        }
    },

    async selectConversation(projectId: string, conversation: Conversation) {
        appActions.setCurrentConversation(conversation);
        appActions.setLoading(true);
        try {
            const messages = await api.loadConversationMessages(projectId, conversation.id);
            appActions.setMessages(messages);
        } catch (error) {
            appActions.setError(error instanceof Error ? error.message : 'Unknown error');
        } finally {
            appActions.setLoading(false);
        }
    }
};