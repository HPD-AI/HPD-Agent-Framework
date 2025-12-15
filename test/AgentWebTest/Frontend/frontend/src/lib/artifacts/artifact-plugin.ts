import type {
  ClientPluginDefinition,
  ClientToolInvokeResponse,
  ClientToolAugmentation
} from '@hpd/hpd-agent-client';
import {
  createExpandedPlugin,
  createSuccessResponse,
  createErrorResponse
} from '@hpd/hpd-agent-client';
import { artifactStore, type ArtifactType } from './artifact-store.js';

/**
 * Artifact plugin - enables the agent to create and manage visual artifacts
 * like code snippets, markdown documents, diagrams, etc.
 */
export const artifactPlugin: ClientPluginDefinition = createExpandedPlugin(
  'Artifacts',
  [
    {
      name: 'open_artifact',
      description: 'Opens the artifact canvas to display content. Use for code, markdown, diagrams, or other visual content that benefits from a dedicated view.',
      parametersSchema: {
        type: 'object',
        properties: {
          title: {
            type: 'string',
            description: 'Title for the artifact'
          },
          type: {
            type: 'string',
            enum: ['code', 'markdown', 'html', 'svg', 'mermaid'],
            description: 'Type of content'
          },
          language: {
            type: 'string',
            description: 'Programming language (for code type): python, javascript, typescript, etc.'
          }
        },
        required: ['title', 'type']
      }
    },
    {
      name: 'write_to_artifact',
      description: 'Write content to the currently open artifact. Can replace or append to existing content.',
      parametersSchema: {
        type: 'object',
        properties: {
          content: {
            type: 'string',
            description: 'The content to write'
          },
          append: {
            type: 'boolean',
            description: 'If true, append to existing content. If false (default), replace content.',
            default: false
          }
        },
        required: ['content']
      }
    },
    {
      name: 'read_artifact',
      description: 'Read the current content of the artifact. Use this to see any user edits before making modifications.',
      parametersSchema: {
        type: 'object',
        properties: {}
      }
    },
    {
      name: 'close_artifact',
      description: 'Close the artifact canvas and return to chat-only view.',
      parametersSchema: {
        type: 'object',
        properties: {}
      }
    }
  ],
  {
    description: 'Create and manage visual artifacts like code, documents, and diagrams'
  }
);

/**
 * Handle artifact tool invocations
 */
export function handleArtifactTool(
  toolName: string,
  args: Record<string, unknown>,
  requestId: string
): ClientToolInvokeResponse {
  switch (toolName) {
    case 'open_artifact': {
      const title = args.title as string;
      const type = args.type as ArtifactType;
      const language = args.language as string | undefined;

      artifactStore.open(title, type, language);

      return createSuccessResponse(
        requestId,
        `Artifact opened. Title: "${title}", Type: ${type}${language ? `/${language}` : ''}`
      );
    }

    case 'write_to_artifact': {
      const content = args.content as string;
      const append = args.append as boolean ?? false;

      const artifact = artifactStore.get();
      if (!artifact.isOpen) {
        return createErrorResponse(requestId, 'No artifact is currently open. Call open_artifact first.');
      }

      if (append) {
        artifactStore.append(content);
      } else {
        artifactStore.setContent(content);
      }

      const charCount = artifactStore.get().content.length;
      return createSuccessResponse(
        requestId,
        `Content ${append ? 'appended to' : 'written to'} artifact (${charCount} characters total)`
      );
    }

    case 'read_artifact': {
      const artifact = artifactStore.get();
      if (!artifact.isOpen) {
        return createErrorResponse(requestId, 'No artifact is currently open.');
      }

      return createSuccessResponse(requestId, [
        { type: 'text', text: artifact.content || '(empty)' }
      ]);
    }

    case 'close_artifact': {
      const artifact = artifactStore.get();
      if (!artifact.isOpen) {
        return createSuccessResponse(requestId, 'No artifact was open.');
      }

      const title = artifact.title;
      artifactStore.close();

      return createSuccessResponse(requestId, `Artifact "${title}" closed.`);
    }

    default:
      return createErrorResponse(requestId, `Unknown artifact tool: ${toolName}`);
  }
}
