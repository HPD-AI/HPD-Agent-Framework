import { writable, get } from 'svelte/store';

export type ArtifactType = 'code' | 'markdown' | 'html' | 'svg' | 'mermaid';

export interface ArtifactState {
  isOpen: boolean;
  title: string;
  type: ArtifactType;
  language?: string;
  content: string;
}

const initialState: ArtifactState = {
  isOpen: false,
  title: '',
  type: 'code',
  language: undefined,
  content: ''
};

function createArtifactStore() {
  const { subscribe, set, update } = writable<ArtifactState>(initialState);

  return {
    subscribe,

    /** Get current state (non-reactive) */
    get: () => get({ subscribe }),

    /** Open a new artifact */
    open: (title: string, type: ArtifactType, language?: string) => {
      set({
        isOpen: true,
        title,
        type,
        language,
        content: ''
      });
    },

    /** Set the artifact content (replace) */
    setContent: (content: string) => {
      update(state => ({ ...state, content }));
    },

    /** Append to the artifact content */
    append: (content: string) => {
      update(state => ({ ...state, content: state.content + content }));
    },

    /** Close the artifact */
    close: () => {
      set(initialState);
    },

    /** Update title */
    setTitle: (title: string) => {
      update(state => ({ ...state, title }));
    }
  };
}

export const artifactStore = createArtifactStore();
