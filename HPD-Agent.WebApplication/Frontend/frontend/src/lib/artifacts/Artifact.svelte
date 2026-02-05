<script lang="ts">
  import { artifactStore, type ArtifactState } from './artifact-store.js';

  let artifact: ArtifactState;
  artifactStore.subscribe(value => artifact = value);

  // For user editing
  let isEditing = false;
  let editContent = '';

  // Preview mode for HTML
  let showPreview = false;

  // Check if artifact is previewable (HTML or HTML-like code)
  function isPreviewable(): boolean {
    if (artifact.type === 'html') return true;
    if (artifact.type === 'svg') return true;
    if (artifact.type === 'code' && artifact.language?.toLowerCase() === 'html') return true;
    return false;
  }

  function startEditing() {
    editContent = artifact.content;
    isEditing = true;
  }

  function saveEdits() {
    artifactStore.setContent(editContent);
    isEditing = false;
  }

  function cancelEdits() {
    isEditing = false;
    editContent = '';
  }

  function copyToClipboard() {
    navigator.clipboard.writeText(artifact.content);
  }

  function downloadArtifact() {
    const ext = getFileExtension(artifact.type, artifact.language);
    const filename = `${artifact.title.replace(/[^a-z0-9]/gi, '_').toLowerCase()}${ext}`;
    const blob = new Blob([artifact.content], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = filename;
    a.click();
    URL.revokeObjectURL(url);
  }

  function getFileExtension(type: string, language?: string): string {
    if (type === 'code' && language) {
      const extMap: Record<string, string> = {
        python: '.py',
        javascript: '.js',
        typescript: '.ts',
        java: '.java',
        csharp: '.cs',
        go: '.go',
        rust: '.rs',
        html: '.html',
        css: '.css',
        json: '.json',
        yaml: '.yaml',
        sql: '.sql',
        bash: '.sh',
        powershell: '.ps1'
      };
      return extMap[language.toLowerCase()] || '.txt';
    }
    const typeMap: Record<string, string> = {
      markdown: '.md',
      html: '.html',
      svg: '.svg',
      mermaid: '.mmd'
    };
    return typeMap[type] || '.txt';
  }

  function getLanguageLabel(type: string, language?: string): string {
    if (type === 'code' && language) {
      return language.charAt(0).toUpperCase() + language.slice(1);
    }
    const labels: Record<string, string> = {
      markdown: 'Markdown',
      html: 'HTML',
      svg: 'SVG',
      mermaid: 'Mermaid Diagram'
    };
    return labels[type] || type;
  }
</script>

<div class="h-full flex flex-col bg-gray-900 text-gray-100">
  <!-- Header -->
  <div class="flex items-center justify-between px-4 py-3 bg-gray-800 border-b border-gray-700">
    <div class="flex items-center gap-3">
      <div class="text-sm font-medium">{artifact.title}</div>
      <span class="px-2 py-0.5 text-xs bg-gray-700 rounded">
        {getLanguageLabel(artifact.type, artifact.language)}
      </span>
    </div>
    <div class="flex items-center gap-2">
      {#if !isEditing}
        {#if isPreviewable()}
          <button
            onclick={() => showPreview = !showPreview}
            class="px-3 py-1.5 text-xs rounded transition-colors {showPreview ? 'bg-blue-600 hover:bg-blue-500' : 'bg-gray-700 hover:bg-gray-600'}"
          >
            {showPreview ? 'Code' : 'Preview'}
          </button>
        {/if}
        <button
          onclick={startEditing}
          class="px-3 py-1.5 text-xs bg-gray-700 hover:bg-gray-600 rounded transition-colors"
        >
          Edit
        </button>
      {/if}
      <button
        onclick={copyToClipboard}
        class="px-3 py-1.5 text-xs bg-gray-700 hover:bg-gray-600 rounded transition-colors"
      >
        Copy
      </button>
      <button
        onclick={downloadArtifact}
        class="px-3 py-1.5 text-xs bg-gray-700 hover:bg-gray-600 rounded transition-colors"
      >
        Download
      </button>
      <button
        onclick={() => artifactStore.close()}
        class="px-3 py-1.5 text-xs bg-red-600 hover:bg-red-500 rounded transition-colors"
      >
        Close
      </button>
    </div>
  </div>

  <!-- Content Area -->
  <div class="flex-1 overflow-hidden">
    {#if isEditing}
      <!-- Edit Mode -->
      <div class="h-full flex flex-col">
        <textarea
          bind:value={editContent}
          class="flex-1 w-full p-4 bg-gray-900 text-gray-100 font-mono text-sm resize-none focus:outline-none"
          spellcheck="false"
        ></textarea>
        <div class="flex justify-end gap-2 px-4 py-2 bg-gray-800 border-t border-gray-700">
          <button
            onclick={cancelEdits}
            class="px-4 py-1.5 text-sm bg-gray-600 hover:bg-gray-500 rounded transition-colors"
          >
            Cancel
          </button>
          <button
            onclick={saveEdits}
            class="px-4 py-1.5 text-sm bg-blue-600 hover:bg-blue-500 rounded transition-colors"
          >
            Save Changes
          </button>
        </div>
      </div>
    {:else if showPreview && isPreviewable()}
      <!-- Preview Mode (HTML/SVG) -->
      {#if artifact.type === 'svg'}
        <div class="h-full p-4 overflow-auto bg-white flex items-center justify-center">
          {@html artifact.content}
        </div>
      {:else}
        <!-- HTML Preview with iframe sandbox -->
        <iframe
          srcdoc={artifact.content}
          class="w-full h-full border-0 bg-white"
          sandbox="allow-scripts allow-same-origin"
          title="HTML Preview"
        ></iframe>
      {/if}
    {:else}
      <!-- Code View Mode -->
      <pre class="h-full p-4 overflow-auto font-mono text-sm whitespace-pre-wrap">{artifact.content || '(empty - waiting for content...)'}</pre>
    {/if}
  </div>
</div>
