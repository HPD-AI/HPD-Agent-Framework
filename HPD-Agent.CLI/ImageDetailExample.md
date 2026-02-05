# Image Detail Parameter - OpenAI Compatibility Only

## Important Note

The `detail` parameter is an **OpenAI-specific feature** that is:
- ✅ **Supported**: When using OpenAI models through OpenRouter (e.g., `openai/gpt-4o`)
- ❌ **NOT Supported**: For native OpenRouter models like `google/gemini-2.5-flash`, `anthropic/claude-3.5-sonnet`, etc.
- ❓ **Undocumented**: Not mentioned in OpenRouter's official API documentation

## Official OpenRouter Format

According to [OpenRouter's documentation](https://openrouter.ai/docs/guides/overview/multimodal/images):

```json
{
  "type": "image_url",
  "image_url": {
    "url": "data:image/jpeg;base64,..."
  }
}
```

**Supported formats**: `image/png`, `image/jpeg`, `image/webp`, `image/gif`

## Your Current Setup

You're using **`google/gemini-2.5-flash`** which does NOT support the `detail` parameter.

The 330,993 tokens you saw for a 324KB image is **normal** for vision models - they process images at high resolution by default.

## Solutions for Reducing Token Usage

### Option 1: Switch to an OpenAI Model (if you need `detail` parameter)

```csharp
// In Program.cs
ProviderKey = "openrouter",
ModelName = "openai/gpt-4o"  // Supports detail parameter
```

Then use:
```csharp
var image = await ImageContent.FromFileAsync("photo.jpg");
image.AdditionalProperties = new() { ["detail"] = "low" };  // ~85 tokens
```

### Option 2: Pre-resize Images (works for ALL models)

This is the universal solution that works with Gemini, Claude, OpenAI, etc.:

```csharp
// Add to ImageContent.cs or create a helper
public static async Task<ImageContent> FromFileWithResizeAsync(
    string filePath,
    int maxWidth = 512,
    int maxHeight = 512)
{
    // Use System.Drawing, ImageSharp, or SkiaSharp to resize
    // Then create ImageContent from resized bytes
    // This will work with ALL vision models
}
```

### Option 3: Accept the Cost

Gemini Flash is already one of the cheaper vision models. If accuracy is important, the token usage might be worth it.

## Current Code Status

The `detail` parameter support is implemented in your OpenRouter provider:
- ✅ **Correctly documented** as OpenAI-specific
- ✅ **Safe to keep** - doesn't break anything
- ✅ **Works** when using OpenAI models through OpenRouter
- ⚠️ **Ignored** by Gemini, Claude, and other non-OpenAI models

## Recommendation

Since you're using Gemini:

1. **Remove the `detail` parameter from Program.cs** (it does nothing)
2. **Either**:
   - Accept the token usage (Gemini Flash is already cheap)
   - OR implement image resizing (works universally)
   - OR switch to an OpenAI model if you specifically need the `detail` feature

## Example: Using with OpenAI Model

```csharp
// Switch to OpenAI model
ModelName = "openai/gpt-4o"

// Then you can use detail parameter
var image = await ImageContent.FromFileAsync("photo.jpg");
image.AdditionalProperties = new() { ["detail"] = "low" };  // Actually works!

var message = ImageInputHelper.CreateImageMessage(
    "What's in this?",
    image,
    detail: "low");  // ~85 tokens instead of ~300k
```

## Official References

- [OpenRouter Image Inputs](https://openrouter.ai/docs/guides/overview/multimodal/images)
- [OpenAI Vision API detail parameter](https://platform.openai.com/docs/guides/vision) (OpenAI docs, not OpenRouter)
