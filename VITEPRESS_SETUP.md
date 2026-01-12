# VitePress Documentation Setup

VitePress has been successfully set up for your HPD-Agent documentation!

## 📁 Structure

```
/OfficialDocs/
├── .vitepress/
│   ├── config.js           # VitePress configuration
│   └── theme/
│       ├── index.js        # Theme entry point
│       └── custom.css      # Custom dark theme (matching your landing page)
├── index.md                # Docs home page
├── Getting Started/
├── Tools/
├── Middleware/
├── Events/
└── Agent Builder & Config/
```

## 🚀 Commands

### Start Development Server
```bash
npm run docs:dev
```
This will start a local server at `http://localhost:5173` with hot reload.

### Build for Production
```bash
npm run docs:build
```
Generates static HTML files in `OfficialDocs/.vitepress/dist/`

### Preview Production Build
```bash
npm run docs:preview
```
Preview the production build locally before deployment.

## 🎨 Theme

The VitePress theme has been customized to match your landing page:

- **Primary Color**: Teal (#14B8A6)
- **Background**: Dark (#111827)
- **Accent Colors**: Matching your existing palette
- **Code Blocks**: Dark theme with syntax highlighting
- **Typography**: Clean, readable fonts

## 📝 Writing Documentation

### Frontmatter

Each markdown file can have frontmatter for metadata:

```markdown
---
title: My Page Title
description: Page description
---

# Content starts here
```

### Code Blocks

Syntax highlighting is automatic:

````markdown
```csharp
var agent = new AgentBuilder()
    .WithOpenAI("gpt-4")
    .Build();
```
````

### Custom Containers

```markdown
::: tip
This is a tip
:::

::: warning
This is a warning
:::

::: danger
This is dangerous
:::
```

## 🔗 Linking Pages

Use relative paths:

```markdown
[See Tools Overview](/Tools/02.1 C# Tools Overview)
```

## 📱 Features Included

 **Search** - Local search built-in
 **Dark Mode** - Always dark (matching your brand)
 **Sidebar** - Auto-generated from config
 **Mobile Responsive** - Works on all devices
 **Syntax Highlighting** - C# and other languages
 **Edit on GitHub** - Link to edit pages
 **Social Links** - GitHub integration

## 🚢 Deployment

### GitHub Pages

1. Build the docs:
   ```bash
   npm run docs:build
   ```

2. The built files are in `OfficialDocs/.vitepress/dist/`

3. Push to GitHub and enable GitHub Pages pointing to this directory, or use GitHub Actions.

### Netlify/Vercel

Both support VitePress out of the box:

- **Build command**: `npm run docs:build`
- **Output directory**: `OfficialDocs/.vitepress/dist`

## 🔧 Customization

### Config File
Edit `OfficialDocs/.vitepress/config.js` to:
- Add/remove navigation items
- Update sidebar structure
- Change site metadata

### Theme
Edit `OfficialDocs/.vitepress/theme/custom.css` to:
- Modify colors
- Change typography
- Add custom styles

## 📚 Next Steps

1. **Run the dev server**: `npm run docs:dev`
2. **Visit**: http://localhost:5173
3. **Edit your markdown files** - changes will hot reload
4. **Customize the theme** as needed

## 🔗 Integrating with Landing Page

Your custom landing page is at `InternalDocs/HPD.Agent/index.html`.

To integrate:

1. **Option A**: Keep them separate
   - Landing page: `/InternalDocs/HPD.Agent/index.html`
   - Docs: VitePress at `/OfficialDocs/`

2. **Option B**: Make VitePress the main site
   - Use VitePress for everything
   - Customize `OfficialDocs/index.md` to match your landing page

3. **Option C**: Hybrid approach
   - Serve your custom `index.html` at root
   - Serve VitePress docs at `/docs/` subdirectory

## 📖 Resources

- [VitePress Docs](https://vitepress.dev/)
- [Markdown Extensions](https://vitepress.dev/guide/markdown)
- [Theme Customization](https://vitepress.dev/guide/custom-theme)
