import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'HPD-Agent',
  description: 'Production-ready multi-agent framework for .NET',

  // Base path for GitHub Pages deployment
  base: '/HPD-Agent-Framework/',

  // Use documentation as the root
  srcDir: '.',

  // Clean URLs (removes .html)
  cleanUrls: true,

  // Theme configuration
  themeConfig: {
    logo: '/logo.svg',

    nav: [
      { text: 'Home', link: '/' },
      { text: 'Getting Started', link: '/Getting Started/00 Agents Overview' },
      { text: 'Tools', link: '/Tools/02.1 CSharp Tools Overview' },
      { text: 'Middleware', link: '/Middleware/04.1 Middleware Lifecycle' },
      { text: 'Events', link: '/Events/05.1 Events Overview' },
    ],

    sidebar: [
      {
        text: 'Getting Started',
        collapsed: false,
        items: [
          { text: 'Agents Overview', link: '/Getting Started/00 Agents Overview' },
          { text: 'Customizing an Agent', link: '/Getting Started/01 Customizing an Agent' },
          { text: 'Multi-Turn Conversations', link: '/Getting Started/02 Multi-Turn Conversations' },
          { text: 'Tool Calling', link: '/Getting Started/03 Tool Calling' },
          { text: 'Middleware', link: '/Getting Started/04 Middleware' },
          { text: 'Event Handling', link: '/Getting Started/05 Event Handling' },
          { text: 'Memory', link: '/Getting Started/06 Memory' },
          { text: 'Building Console Apps', link: '/Getting Started/07 Building Console Apps' },
          { text: 'Building Web Apps', link: '/Getting Started/08 Building Web Apps' },
        ]
      },
      {
        text: 'Tools',
        collapsed: false,
        items: [
          { text: 'C# Tools Overview', link: '/Tools/02.1 CSharp Tools Overview' },
          { text: 'AIFunctions', link: '/Tools/02.1.1 AIFunctions' },
          { text: 'Skills', link: '/Tools/02.1.2 Skills' },
          { text: 'SubAgents', link: '/Tools/02.1.3 SubAgents' },
          { text: 'Tool Dynamic Metadata', link: '/Tools/02.1.4 Tool Dynamic Metadata' },
          { text: 'Context Engineering', link: '/Tools/02.1.5 Context Engineering' },
          { text: 'Writing Instructions', link: '/Tools/02.1.6 Writing Instructions' },
          { text: 'Conditional Expression DSL', link: '/Tools/02.1.7 Conditional Expression DSL' },
          { text: 'MCP Servers', link: '/Tools/02.2 MCP Servers' },
          { text: 'Client Tools', link: '/Tools/02.3 Client Tools' },
        ]
      },
      {
        text: 'Middleware',
        collapsed: false,
        items: [
          { text: 'Middleware Lifecycle', link: '/Middleware/04.1 Middleware Lifecycle' },
          { text: 'Middleware State', link: '/Middleware/04.2 Middleware State' },
          { text: 'Middleware Events', link: '/Middleware/04.3 Middleware Events' },
          { text: 'Built-in Middleware', link: '/Middleware/04.4 Built-in Middleware' },
          { text: 'Custom Middleware', link: '/Middleware/04.5 Custom Middleware' },
        ]
      },
      {
        text: 'Events',
        collapsed: false,
        items: [
          { text: 'Events Overview', link: '/Events/05.1 Events Overview' },
          { text: 'Event Types Reference', link: '/Events/05.2 Event Types Reference' },
          { text: 'Consuming Events', link: '/Events/05.3 Consuming Events' },
          { text: 'SubAgent Events', link: '/Events/05.4 SubAgent Events' },
          { text: 'Streaming & Cancellation', link: '/Events/05.5 Streaming & Cancellation' },
          { text: 'Bidirectional Events', link: '/Events/05.6 Bidirectional Events' },
          { text: 'Custom Events', link: '/Events/05.7 Custom Events' },
        ]
      },
      {
        text: 'Providers',
        collapsed: false,
        items: [
          { text: 'Anthropic', link: '/Agent Builder & Config/Providers/Anthropic' },
          { text: 'OpenAI', link: '/Agent Builder & Config/Providers/OpenAI' },
          { text: 'Azure AI', link: '/Agent Builder & Config/Providers/AzureAI' },
          { text: 'Google AI', link: '/Agent Builder & Config/Providers/GoogleAI' },
          { text: 'Bedrock', link: '/Agent Builder & Config/Providers/Bedrock' },
          { text: 'Mistral', link: '/Agent Builder & Config/Providers/Mistral' },
          { text: 'HuggingFace', link: '/Agent Builder & Config/Providers/Huggingface' },
          { text: 'Ollama', link: '/Agent Builder & Config/Providers/Ollama' },
          { text: 'ONNX Runtime', link: '/Agent Builder & Config/Providers/OnnxRuntime' },
        ]
      }
    ],

    socialLinks: [
      { icon: 'github', link: 'https://github.com/HPD-AI/HPD-Agent' }
    ],

    footer: {
      message: 'Released under the MIT License.',
      copyright: 'Copyright © 2026 HPD-Agent Framework'
    },

    // Search
    search: {
      provider: 'local'
    },

    // Edit link
    editLink: {
      pattern: 'https://github.com/HPD-AI/HPD-Agent/edit/main/documentation/:path',
      text: 'Edit this page on GitHub'
    }
  },

  // Markdown configuration
  markdown: {
    theme: {
      light: 'github-light',
      dark: 'github-dark'
    },
    lineNumbers: true
  },

  // Head tags
  head: [
    ['link', { rel: 'icon', href: '/favicon.ico' }],
    ['meta', { name: 'theme-color', content: '#14B8A6' }],
    ['meta', { property: 'og:title', content: 'HPD-Agent Framework' }],
    ['meta', { property: 'og:description', content: 'Production-ready multi-agent framework for .NET' }],
  ]
})
