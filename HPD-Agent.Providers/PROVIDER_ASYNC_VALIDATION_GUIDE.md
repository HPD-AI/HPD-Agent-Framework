# Provider Async Validation Guide

## Overview

The provider architecture now supports **optional async validation** through the `IProviderFeatures.ValidateConfigurationAsync()` method. This enables providers to perform live API testing, credit checks, and other network-based validation during agent setup.

## Changes Made

### 1. Updated `IProviderFeatures` Interface

Added a default-implementation method for async validation:

```csharp
/// <summary>
/// Validate provider-specific configuration asynchronously with live API testing.
/// This method can perform network requests to validate API keys, check credit balances,
/// test model availability, etc. Providers that don't support async validation should
/// return null (default implementation).
/// </summary>
Task<ProviderValidationResult>? ValidateConfigurationAsync(ProviderConfig config, CancellationToken cancellationToken = default)
    => null; // Default implementation - providers can override
```

**Benefits:**
- ✅ **Non-breaking**: Existing providers continue to work without changes
- ✅ **Optional**: Providers can opt-in by overriding the method
- ✅ **Flexible**: Returns `null` if async validation is not supported

### 2. Created `IProviderExtendedFeatures` Interface

Added an extended interface for providers with advanced capabilities:

```csharp
public interface IProviderExtendedFeatures : IProviderFeatures
{
    /// <summary>
    /// Check if the provider supports credit/usage management.
    /// </summary>
    bool SupportsCreditManagement => false;

    /// <summary>
    /// Check if the provider supports attribution headers (e.g., HTTP-Referer, X-Title).
    /// </summary>
    bool SupportsAttribution => false;

    /// <summary>
    /// Check if the provider supports model routing/fallbacks.
    /// </summary>
    bool SupportsModelRouting => false;
}
```

### 3. Updated OpenRouter Provider

The OpenRouter provider now implements `IProviderExtendedFeatures` and provides async validation with credit checking:

```csharp
internal class OpenRouterProvider : IProviderExtendedFeatures
{
    public bool SupportsCreditManagement => true;
    public bool SupportsAttribution => true;
    public bool SupportsModelRouting => true;

    public async Task<ProviderValidationResult>? ValidateConfigurationAsync(
        ProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        // Synchronous validation first
        var basicValidation = ValidateConfiguration(config);
        if (!basicValidation.IsValid)
            return basicValidation;

        try
        {
            // Create temporary client for testing
            var testClient = CreateChatClient(config) as OpenRouterChatClient;

            // Validate API key works
            var isValid = await testClient.ValidateKeyAsync(cancellationToken);
            if (!isValid)
                return ProviderValidationResult.Failure("Invalid API key or insufficient permissions");

            // Check credit balance
            var keyInfo = await testClient.GetKeyInfoAsync(cancellationToken);
            if (keyInfo.Data.LimitRemaining.HasValue && keyInfo.Data.LimitRemaining <= 0)
                return ProviderValidationResult.Failure("API key has no remaining credits");

            return ProviderValidationResult.Success();
        }
        catch (Exception ex)
        {
            return ProviderValidationResult.Failure($"API key validation failed: {ex.Message}");
        }
    }
}
```

## Current Provider Status

| Provider | Async Validation | Credit Management | Special Features |
|----------|------------------|-------------------|------------------|
| **OpenRouter** | ✅ Implemented | ✅ Yes | Attribution headers, model routing, prompt caching |
| OpenAI | ❌ Not needed | ❌ No | Uses standard SDK |
| Azure OpenAI | ❌ Not needed | ❌ No | Uses Azure SDK |
| Anthropic | ❌ Not needed | ❌ No | Uses Anthropic SDK |
| Ollama | ❌ Not needed | ❌ No | Local deployment |
| GoogleAI | ❌ Not needed | ❌ No | Uses Google SDK |
| Mistral | ❌ Not needed | ❌ No | Uses Mistral SDK |
| AzureAIInference | ❌ Not needed | ❌ No | Uses Azure SDK |
| Bedrock | ❌ Not needed | ❌ No | Uses AWS SDK |
| HuggingFace | ❌ Not needed | ❌ No | Uses HF SDK |
| OnnxRuntime | ❌ Not needed | ❌ No | Local model execution |

## When to Implement Async Validation

Implement `ValidateConfigurationAsync` when your provider needs to:

1. **Test API Keys Live**: Validate that the API key works by making a test request
2. **Check Credit Balances**: Verify the account has sufficient credits/quota
3. **Verify Model Access**: Confirm the specified model is available and accessible
4. **Network Connectivity**: Test that the endpoint is reachable
5. **Account Limits**: Check rate limits, permissions, or account status

### Example Implementation

```csharp
public async Task<ProviderValidationResult>? ValidateConfigurationAsync(
    ProviderConfig config,
    CancellationToken cancellationToken = default)
{
    // 1. Always do synchronous validation first
    var basicValidation = ValidateConfiguration(config);
    if (!basicValidation.IsValid)
        return basicValidation;

    try
    {
        // 2. Create a test client
        var client = CreateChatClient(config);

        // 3. Perform lightweight validation request
        //    (e.g., GET /models, GET /me, GET /credits)
        var isValid = await TestApiKeyAsync(client, cancellationToken);

        if (!isValid)
            return ProviderValidationResult.Failure("API key validation failed");

        return ProviderValidationResult.Success();
    }
    catch (Exception ex)
    {
        // Return detailed error for debugging
        return ProviderValidationResult.Failure($"Validation failed: {ex.Message}");
    }
}
```

## When NOT to Implement Async Validation

Don't implement async validation if:

- ✅ Your provider uses official SDKs that handle validation internally (OpenAI, Anthropic, etc.)
- ✅ The provider is local/offline (Ollama, OnnxRuntime)
- ✅ Validation would require expensive operations (full model loading, inference tests)
- ✅ The provider doesn't expose validation endpoints

## OpenRouter-Specific Features

### Credit Management API

The OpenRouter chat client exposes credit management methods:

```csharp
// Cast to OpenRouterChatClient for access to extended features
if (chatClient is OpenRouterChatClient orClient)
{
    // Check credit status
    var status = await orClient.GetCreditStatusAsync();
    Console.WriteLine(status); // "1.50/10.00 credits (15.0% remaining)"

    // Check if credits available
    bool hasCredits = await orClient.HasCreditsRemainingAsync();

    // Get remaining balance
    float? remaining = await orClient.GetRemainingCreditsAsync();

    // Validate API key
    bool isValid = await orClient.ValidateKeyAsync();
}
```

### Attribution System

OpenRouter requires attribution headers for app rankings:

```csharp
// Method 1: Via AdditionalProperties
var config = new ProviderConfig
{
    ProviderKey = "openrouter",
    ModelName = "anthropic/claude-3.5-sonnet",
    ApiKey = apiKey,
    AdditionalProperties = new()
    {
        ["HttpReferer"] = "https://myapp.com",
        ["AppName"] = "My Awesome App"
    }
};

// Method 2: Helper methods
var config = OpenRouterProvider.CreateConfigWithAttribution(
    apiKey,
    "anthropic/claude-3.5-sonnet",
    "https://myapp.com",
    "My Awesome App"
);

// Method 3: Fluent API
OpenRouterProvider.WithAttribution(config, "https://myapp.com", "My Awesome App");
```

**Smart Defaults:**
- Detects localhost/development environments
- Auto-generates meaningful titles from assembly names
- Prevents generic titles
- Validates URL formats

### Model Routing & Fallbacks

```csharp
var options = new ChatOptions
{
    AdditionalProperties = new()
    {
        // Fallback models if primary fails
        ["models"] = new[] { "anthropic/claude-3.5-sonnet", "openai/gpt-4" },

        // Provider routing
        ["provider_order"] = new[] { "Anthropic", "OpenAI" },
        ["allow_fallbacks"] = true,

        // Cost controls
        ["max_price_prompt"] = 0.001f,  // $0.001 per token
        ["max_price_completion"] = 0.002f,

        // Privacy
        ["data_collection"] = "deny",
        ["zdr"] = true,  // Zero data retention

        // Free tier
        ["use_free_model"] = true  // Appends :free to model name
    }
};
```

### Prompt Caching

OpenRouter supports Anthropic-style prompt caching:

```csharp
var message = new ChatMessage
{
    Role = ChatRole.User,
    Contents = new List<AIContent>
    {
        new TextContent("This is cached content")
        {
            AdditionalProperties = new()
            {
                ["cache_control"] = true  // Enable caching for this block
            }
        }
    }
};
```

## Future Enhancements

Potential additions for other providers:

1. **Azure OpenAI**: Deployment health checks, quota validation
2. **Anthropic**: Prompt caching configuration validation
3. **Bedrock**: IAM permissions verification, model access checks
4. **HuggingFace**: Model loading status, endpoint availability

## Testing Async Validation

```csharp
// Example test
[Fact]
public async Task ValidateConfigurationAsync_WithValidKey_ReturnsSuccess()
{
    var config = new ProviderConfig
    {
        ProviderKey = "openrouter",
        ModelName = "anthropic/claude-3.5-sonnet",
        ApiKey = "valid-key-here"
    };

    var provider = new OpenRouterProvider();
    var result = await provider.ValidateConfigurationAsync(config);

    Assert.True(result.IsValid);
    Assert.Empty(result.Errors);
}

[Fact]
public async Task ValidateConfigurationAsync_WithInvalidKey_ReturnsFailure()
{
    var config = new ProviderConfig
    {
        ProviderKey = "openrouter",
        ModelName = "anthropic/claude-3.5-sonnet",
        ApiKey = "invalid-key"
    };

    var provider = new OpenRouterProvider();
    var result = await provider.ValidateConfigurationAsync(config);

    Assert.False(result.IsValid);
    Assert.NotEmpty(result.Errors);
}
```

## Summary

- ✅ **Non-breaking**: All existing providers work without changes
- ✅ **Opt-in**: Providers add async validation only when beneficial
- ✅ **Extensible**: `IProviderExtendedFeatures` for advanced capabilities
- ✅ **Production-ready**: OpenRouter fully implements all features
- ✅ **Well-documented**: Clear guidance for future implementations
