import { defineConfig } from 'vitepress'

const link = (text, path) => ({ text, link: path })
const repositoryName = process.env.GITHUB_REPOSITORY?.split('/')[1]
const base = process.env.GITHUB_ACTIONS && repositoryName ? `/${repositoryName}/` : '/'

const sidebar = [
  {
    text: 'Start Here',
    collapsed: false,
    items: [
      link('Overview', '/'),
      link('Getting Started', '/getting-started/'),
      link('What Is An Agent?', '/getting-started/what-is-an-agent'),
      link('Hello Agent', '/getting-started/hello-agent'),
      link('Streaming Events', '/getting-started/streaming-events'),
      link('Add A Tool', '/getting-started/add-a-tool'),
      link('Multi-Turn Sessions', '/getting-started/multi-turn-sessions'),
      link('Tiny Console Chat Loop', '/getting-started/chat-loop'),
      link('Save Sessions And State', '/getting-started/persistence'),
      link('ASP.NET Hosting', '/getting-started/aspnet-hosting')
    ]
  },
  {
    text: 'Core Concepts',
    collapsed: false,
    items: [
      link('Agent Runtime And Capabilities', '/concepts/agent-runtime-and-capabilities'),
      link('Agent Builder And Agent', '/concepts/agent-builder-and-agent'),
      link('Providers, Clients, And Secrets', '/concepts/providers-clients-and-secrets'),
      link('Tools, Functions, And Harnesses', '/concepts/tools-functions-and-harnesses'),
      link('Sessions, Branches, And Events', '/concepts/sessions-branches-and-events'),
      link('Event Streams And Hierarchies', '/concepts/event-streams-and-hierarchies'),
      link('Middleware Lifecycle', '/concepts/middleware-lifecycle')
    ]
  },
  {
    text: 'Tools',
    collapsed: true,
    items: [
      link('Author A Tool Harness', '/guides/tools/author-a-tool-harness'),
      link('Selective Registration And Config', '/guides/tools/selective-registration-and-config'),
      link('Source Generation, AOT, And Trimming', '/guides/tools/source-generation-aot-and-trimming'),
      link('Collapsing And Containers', '/guides/tools/collapsing-and-containers'),
      link('Externally Executed Client Tools', '/guides/tools/externally-executed-client-tools'),
      link('MCP Tools', '/guides/tools/mcp-tools'),
      link('OpenAPI Tools', '/guides/tools/openapi-tools'),
      link('Multi-Agent Capabilities', '/guides/tools/multi-agent-capabilities')
    ]
  },
  {
    text: 'Events',
    collapsed: true,
    items: [
      link('Events Overview', '/guides/events/overview'),
      link('Tool And Function Events', '/guides/events/tool-and-function-events'),
      link('Bidirectional Events', '/guides/events/bidirectional-events'),
      link('Custom Events', '/guides/events/custom-events'),
      link('Lifecycle, Retry, And Error Events', '/guides/events/lifecycle-retry-and-error-events'),
      link('Live Vs Durable Events', '/guides/events/live-vs-durable-events'),
      link('Serialization And Registration', '/guides/events/serialization-and-registration'),
      link('TypeScript Client Events', '/guides/events/typescript-client'),
      link('Testing Event-Driven Code', '/guides/events/testing-event-driven-code'),
      link('Events Reference', '/reference/events')
    ]
  },
  {
    text: 'Middleware',
    collapsed: true,
    items: [
      link('Middleware Overview', '/guides/middleware/overview'),
      link('Custom Middleware', '/guides/middleware/custom-middleware'),
      link('Permissions Middleware', '/guides/middleware/permissions'),
      link('State Persistence', '/guides/middleware/state-persistence'),
      link('Error Handling', '/guides/middleware/error-handling')
    ]
  },
  {
    text: 'Providers',
    collapsed: true,
    items: [
      link('Providers Overview', '/guides/providers/overview'),
      link('OpenAI And Azure OpenAI', '/guides/providers/openai-and-azure-openai'),
      link('Anthropic', '/guides/providers/anthropic'),
      link('Google AI', '/guides/providers/google-ai'),
      link('Bedrock', '/guides/providers/bedrock'),
      link('Mistral', '/guides/providers/mistral'),
      link('Hugging Face', '/guides/providers/huggingface'),
      link('Ollama', '/guides/providers/ollama'),
      link('ONNX Runtime', '/guides/providers/onnx-runtime'),
      link('ONNX Structured Tool Calling', '/guides/providers/onnx-structured-tool-calling'),
      link('OpenAI Audio', '/guides/providers/openai-audio'),
      link('ElevenLabs Audio', '/guides/providers/elevenlabs-audio'),
      link('Provider Families', '/reference/provider-families'),
      link('Provider Keys And Env Vars', '/reference/provider-keys-and-env-vars')
    ]
  },
  {
    text: 'Sessions And Streaming',
    collapsed: true,
    items: [
      link('Render An Event Stream', '/guides/sessions-and-streaming/render-an-event-stream'),
      link('Branch History And Forking', '/guides/sessions-and-streaming/branch-history-and-forking'),
      link('Compaction', '/guides/sessions-and-streaming/compaction')
    ]
  },
  {
    text: 'Multi-Agent',
    collapsed: true,
    items: [
      link('Subagents', '/guides/agents/subagents'),
      link('Multi-Agent Overview', '/guides/multi-agent/overview'),
      link('Choose A Composition Pattern', '/guides/multi-agent/choose-a-pattern'),
      link('Build A Workflow', '/guides/multi-agent/build-a-workflow'),
      link('Execution Model', '/guides/multi-agent/execution-model'),
      link('Workflow Patterns', '/guides/multi-agent/workflow-patterns'),
      link('Conversation Policies', '/guides/multi-agent/conversation-policies'),
      link('Data Flow Between Nodes', '/guides/multi-agent/data-flow-between-nodes'),
      link('Routing And Handoffs', '/guides/multi-agent/routing-and-handoffs'),
      link('Checkpointing', '/guides/multi-agent/checkpointing'),
      link('Workflow Events', '/guides/multi-agent/workflow-events'),
      link('Config And Export', '/guides/multi-agent/config-and-export')
    ]
  },
  {
    text: 'Hosting And Clients',
    collapsed: true,
    items: [
      link('ASP.NET Core Hosting', '/guides/hosting/aspnet-core'),
      link('Hosted Streaming API', '/guides/hosting/hosted-streaming-api'),
      link('Stored Agent Definitions', '/guides/hosting/stored-agent-definitions'),
      link('Hosted Endpoints Reference', '/reference/hosted-endpoints')
    ]
  },
  {
    text: 'Runtime Systems',
    collapsed: true,
    items: [
      link('Harnesses Overview', '/guides/harnesses/overview'),
      link('Coding Harness', '/guides/harnesses/coding'),
      link('Coding TUI Harness', '/guides/harnesses/coding-tui'),
      link('FileSystem Harness', '/guides/harnesses/filesystem'),
      link('Web Search Harness', '/guides/harnesses/web-search'),
      link('Sandboxing Overview', '/guides/sandboxing/overview'),
      link('Local Process Isolation', '/guides/sandboxing/local-process-isolation'),
      link('FFI Overview', '/guides/ffi/overview'),
      link('Content Upload And Resolution', '/guides/content/content-upload-and-resolution'),
      link('Document Handling And Text Extraction', '/guides/content/document-handling-and-text-extraction')
    ]
  },
  {
    text: 'Audio',
    collapsed: true,
    items: [
      link('Audio Overview', '/guides/audio/overview'),
      link('Runtime Attachment', '/guides/audio/runtime-attachment'),
      link('Text To Speech Output', '/guides/audio/text-to-speech-output'),
      link('Speech To Text Input', '/guides/audio/speech-to-text-input'),
      link('Realtime Audio', '/guides/audio/realtime-audio'),
      link('Audio Events And Traces', '/guides/audio/audio-events-and-traces')
    ]
  },
  {
    text: 'Bots',
    collapsed: true,
    items: [
      link('Bots Overview', '/guides/bots/overview'),
      link('Platform Setup', '/guides/bots/platform-setup'),
      link('Slack', '/guides/bots/slack'),
      link('Discord', '/guides/bots/discord'),
      link('Telegram', '/guides/bots/telegram'),
      link('WhatsApp', '/guides/bots/whatsapp'),
      link('Teams', '/guides/bots/teams'),
      link('Custom Adapters And Source Generation', '/guides/bots/custom-adapters-and-source-generation')
    ]
  },
  {
    text: 'Evaluations',
    collapsed: true,
    items: [
      link('Evaluations Overview', '/guides/evaluations/overview'),
      link('Batch Evals', '/guides/evaluations/batch-evals'),
      link('Evaluator Picker', '/guides/evaluations/evaluator-picker'),
      link('Datasets And Reports', '/guides/evaluations/datasets-and-reports'),
      link('Live Evaluation', '/guides/evaluations/live-evaluation'),
      link('LLM Judges And Safety', '/guides/evaluations/llm-judges-and-safety'),
      link('Red Team', '/guides/evaluations/red-team')
    ]
  },
  {
    text: 'Observability And UI',
    collapsed: true,
    items: [
      link('Logging And Telemetry', '/guides/observability/logging-and-telemetry'),
      link('TUI Overview', '/guides/tui/overview'),
      link('Local Runtime', '/guides/tui/local-runtime'),
      link('Hosted Runtime', '/guides/tui/hosted-runtime'),
      link('Composition', '/guides/tui/composition')
    ]
  }
]

export default defineConfig({
  title: 'HPD Agent',
  description: 'Build production-ready agent applications in .NET',
  base,
  srcDir: '.',
  outDir: './.site-dist',
  cacheDir: './.site-cache',
  cleanUrls: true,
  lastUpdated: true,

  themeConfig: {
    logo: '/logo.svg',

    nav: [
      link('Start', '/getting-started/'),
      link('Concepts', '/concepts/agent-runtime-and-capabilities'),
      {
        text: 'Guides',
        items: [
          link('Tools', '/guides/tools/author-a-tool-harness'),
          link('Events', '/guides/events/overview'),
          link('Middleware', '/guides/middleware/overview'),
          link('Providers', '/guides/providers/overview'),
          link('Multi-Agent', '/guides/multi-agent/overview'),
          link('Hosting', '/guides/hosting/aspnet-core'),
          link('Bots', '/guides/bots/overview'),
          link('Audio', '/guides/audio/overview')
        ]
      },
      link('Reference', '/reference/events')
    ],

    sidebar,

    outline: {
      level: [2, 3],
      label: 'On this page'
    },

    search: {
      provider: 'local'
    },

    socialLinks: [
      { icon: 'github', link: 'https://github.com/HPD-AI/hpd-ai-framework' }
    ],

    footer: {
      message: 'Built for production .NET agent applications.',
      copyright: 'Copyright © 2026 HPD AI'
    },

    editLink: {
      pattern: 'https://github.com/HPD-AI/HPD-Agent/edit/main/:path',
      text: 'Edit this page'
    }
  },

  markdown: {
    theme: {
      light: 'github-light',
      dark: 'github-dark'
    },
    lineNumbers: true
  },

  head: [
    ['link', { rel: 'icon', href: '/favicon.ico' }],
    ['meta', { name: 'theme-color', content: '#0f766e' }],
    ['meta', { property: 'og:title', content: 'HPD Agent' }],
    ['meta', { property: 'og:description', content: 'Build production-ready agent applications in .NET' }]
  ]
})
