// HPD-Agent/Agent/FunctionCallProcessor.cs

using Microsoft.Extensions.AI;

/// <summary>
/// Handles all function calling logic, including multi-turn execution and filter pipelines.
/// </summary>
public class FunctionCallProcessor
{
    private readonly ScopedFilterManager? _scopedFilterManager;
    private readonly IAiFunctionFilter? _permissionFilter;
    private readonly IReadOnlyList<IAiFunctionFilter> _aiFunctionFilters;
    private readonly ContinuationPermissionManager? _continuationPermissionManager;
    private readonly int _maxFunctionCalls;

    public FunctionCallProcessor(ScopedFilterManager? scopedFilterManager, IAiFunctionFilter? permissionFilter, IReadOnlyList<IAiFunctionFilter>? aiFunctionFilters, ContinuationPermissionManager? continuationPermissionManager, int maxFunctionCalls)
    {
        _scopedFilterManager = scopedFilterManager;
        _permissionFilter = permissionFilter;
        _aiFunctionFilters = aiFunctionFilters ?? new List<IAiFunctionFilter>();
        _continuationPermissionManager = continuationPermissionManager;
        _maxFunctionCalls = maxFunctionCalls;
    }

    /// <summary>
    /// Processes a response that may contain function calls, handling multi-turn execution.
    /// Based on Microsoft.Extensions.AI.FunctionInvokingChatClient pattern
    /// </summary>
    public async Task<ChatResponse> ProcessResponseWithFiltersAsync(
        ChatResponse response,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IChatClient baseClient,
        CancellationToken cancellationToken)
    {
        // Copy the original messages to avoid multiple enumeration
        List<ChatMessage> originalMessages = [.. messages];
        var currentMessages = originalMessages.AsEnumerable();
        
        List<ChatMessage>? augmentedHistory = null; // the actual history of messages sent on turns other than the first
        ChatResponse? currentResponse = response; // the response from the inner client
        List<ChatMessage>? responseMessages = null; // tracked list of messages, across multiple turns, to be used for the final response
        List<FunctionCallContent>? functionCallContents = null; // function call contents that need responding to in the current turn
        
        // Use OperationTracker
        var operationTracker = new OperationTracker();

        for (int iteration = 0; iteration < _maxFunctionCalls; iteration++)
        {
            functionCallContents?.Clear();

            // Any function call work to do? If yes, ensure we're tracking that work in functionCallContents.
            bool requiresFunctionInvocation =
                (options?.Tools is { Count: > 0 }) &&
                CopyFunctionCalls(currentResponse.Messages, ref functionCallContents);

            // In a common case where we make a request and there's no function calling work required,
            // fast path out by just returning the original response.
            if (iteration == 0 && !requiresFunctionInvocation)
            {
                return currentResponse;
            }

            // Track aggregate details from the response, including all of the response messages
            (responseMessages ??= []).AddRange(currentResponse.Messages);

            // If there are no tools to call, or for any other reason we should stop, we're done.
            // Break out of the loop and allow the handling at the end to configure the response
            // with aggregated data from previous requests.
            if (!requiresFunctionInvocation)
            {
                break;
            }

            // Use OperationTracker
            if (requiresFunctionInvocation)
            {
                operationTracker.TrackFunctionCall(functionCallContents!.Select(fc => fc.Name), iteration + 1);
                
                // Check continuation permission if configured
                if (_continuationPermissionManager != null && iteration > 0)
                {
                    var completedFunctions = GetCompletedFunctionNames(responseMessages);
                    var plannedFunctions = GetPlannedFunctionNames(functionCallContents);
                    var conversationId = ExtractConversationId(originalMessages);
                    var projectId = ExtractProjectId(options);
                    
                    var decision = await _continuationPermissionManager.ShouldContinueAsync(
                        iteration + 1, _maxFunctionCalls, completedFunctions, plannedFunctions, 
                        conversationId, projectId);
                    
                    if (!decision.ShouldContinue)
                    {
                        // Add a message explaining why we stopped
                        var stopMessage = new ChatMessage(ChatRole.Assistant, 
                            decision.Reason ?? "Function call continuation was denied.");
                        responseMessages.Add(stopMessage);
                        break;
                    }
                }
            }

            // Prepare the history for the next iteration.
            PrepareHistoryForNextIteration(originalMessages, ref currentMessages, ref augmentedHistory, currentResponse, responseMessages);

            // Add the responses from the function calls into the augmented history and also into the tracked
            // list of response messages.
            var addedMessages = await ProcessFunctionCallsAsync(augmentedHistory ?? currentMessages.ToList(), options, functionCallContents!, cancellationToken);
            // Add the tool results into the history for the NEXT turn so the model sees them.
            (augmentedHistory ?? (List<ChatMessage>)currentMessages).AddRange(addedMessages);
            responseMessages.AddRange(addedMessages);

            // Call the LLM again with the updated history
            // For the next turn, create a new set of options to avoid forcing the model to call a tool again.
            // This prevents an infinite loop where the model keeps invoking functions.
            var nextTurnOptions = options == null
                ? new ChatOptions()
                : new ChatOptions
                {
                    Tools = options.Tools,
                    // Explicitly reset ToolMode to null (defaults to 'Auto') so the model is free to answer.
                    ToolMode = AutoChatToolMode.Auto,
                    AllowMultipleToolCalls = options.AllowMultipleToolCalls,
                    MaxOutputTokens = options.MaxOutputTokens,
                    Temperature = options.Temperature,
                    TopP = options.TopP,
                    FrequencyPenalty = options.FrequencyPenalty,
                    PresencePenalty = options.PresencePenalty,
                    ResponseFormat = options.ResponseFormat,
                    Seed = options.Seed,
                    StopSequences = options.StopSequences,
                    ModelId = options.ModelId,
                    AdditionalProperties = options.AdditionalProperties
                };

            currentResponse = await baseClient.GetResponseAsync(currentMessages, nextTurnOptions, cancellationToken);
        }

        // Configure the final response with aggregated data
        if (responseMessages != null)
        {
            currentResponse.Messages = responseMessages;
        }

        // Use OperationTracker to get metadata
        var finalMetadata = operationTracker.GetMetadata();
        currentResponse.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        currentResponse.AdditionalProperties[Agent.OperationHadFunctionCallsKey] = finalMetadata.HadFunctionCalls;
        currentResponse.AdditionalProperties[Agent.OperationFunctionCallsKey] = finalMetadata.FunctionCalls.ToArray();
        currentResponse.AdditionalProperties[Agent.OperationFunctionCallCountKey] = finalMetadata.FunctionCallCount;

        return currentResponse;
    }

    /// <summary>
    /// Processes the function calls and returns the messages to add to the conversation
    /// </summary>
    public async Task<IList<ChatMessage>> ProcessFunctionCallsAsync(
        List<ChatMessage> messages,
        ChatOptions? options,
        List<FunctionCallContent> functionCallContents,
        CancellationToken cancellationToken)
    {
        var resultMessages = new List<ChatMessage>();

        // Process each function call through the filter pipeline
        foreach (var functionCall in functionCallContents)
        {
            var toolCallRequest = new ToolCallRequest
            {
                FunctionName = functionCall.Name,
                Arguments = functionCall.Arguments ?? new Dictionary<string, object?>()
            };

            var tempConversation = new Conversation();
            foreach (var msg in messages) { tempConversation.AddMessage(msg); }

            var context = new AiFunctionContext(tempConversation, toolCallRequest)
            {
                Function = FindFunction(toolCallRequest.FunctionName, options?.Tools)
            };

            // The final step in the pipeline is the actual function invocation.
            Func<AiFunctionContext, Task> finalInvoke = async (ctx) =>
            {
                if (ctx.Function is null)
                {
                    ctx.Result = $"Function '{ctx.ToolCallRequest.FunctionName}' not found.";
                    return;
                }
                try
                {
                    var args = new AIFunctionArguments(ctx.ToolCallRequest.Arguments);
                    ctx.Result = await ctx.Function.InvokeAsync(args, cancellationToken);
                }
                catch (Exception ex)
                {
                    ctx.Result = $"Error invoking function: {ex.Message}";
                }
            };

            var pipeline = finalInvoke;

            // Get scoped filters for this function
            var scopedFilters = _scopedFilterManager?.GetApplicableFilters(functionCall.Name)
                                ?? Enumerable.Empty<IAiFunctionFilter>();

            // Combine scoped filters with general AI function filters
            var allFilters = _aiFunctionFilters.Concat(scopedFilters);

            // Wrap all standard filters first.
            foreach (var filter in allFilters.Reverse())
            {
                var previous = pipeline;
                pipeline = ctx => filter.InvokeAsync(ctx, previous);
            }

            // *** CRITICAL: Wrap the permission filter last, so it runs FIRST. ***
            if (_permissionFilter != null)
            {
                var previous = pipeline;
                pipeline = ctx => _permissionFilter.InvokeAsync(ctx, previous);
            }

            // Execute the full pipeline.
            await pipeline(context);

            var functionResult = new FunctionResultContent(functionCall.CallId, context.Result);
            var functionMessage = new ChatMessage(ChatRole.Tool, new AIContent[] { functionResult });
            resultMessages.Add(functionMessage);
        }

        return resultMessages;
    }

    /// <summary>
    /// Helper method to find a function by name in the tools collection
    /// </summary>
    private AIFunction? FindFunction(string functionName, IList<AITool>? tools)
    {
        if (tools == null) return null;
        return tools.OfType<AIFunction>().FirstOrDefault(f => f.Name == functionName);
    }

    /// <summary>
    /// Prepares the various chat message lists after a response from the inner client and before invoking functions
    /// </summary>
    private static void PrepareHistoryForNextIteration(
        IEnumerable<ChatMessage> originalMessages,
        ref IEnumerable<ChatMessage> currentMessages,
        ref List<ChatMessage>? augmentedHistory,
        ChatResponse response,
        List<ChatMessage> allTurnsResponseMessages)
    {
        // We're going to need to augment the history with function result contents.
        // That means we need a separate list to store the augmented history.
        augmentedHistory ??= originalMessages.ToList();

        // Now add the most recent response messages.
        augmentedHistory.AddRange(response.Messages);

        // Use the augmented history as the new set of messages to send.
        currentMessages = augmentedHistory;
    }

    /// <summary>
    /// Copies any FunctionCallContent from messages to functionCalls
    /// </summary>
    private static bool CopyFunctionCalls(
        IList<ChatMessage> messages, ref List<FunctionCallContent>? functionCalls)
    {
        bool any = false;
        int count = messages.Count;
        for (int i = 0; i < count; i++)
        {
            any |= CopyFunctionCalls(messages[i].Contents, ref functionCalls);
        }

        return any;
    }

    /// <summary>
    /// Copies any FunctionCallContent from content to functionCalls
    /// </summary>
    private static bool CopyFunctionCalls(
        IList<AIContent> content, ref List<FunctionCallContent>? functionCalls)
    {
        bool any = false;
        int count = content.Count;
        for (int i = 0; i < count; i++)
        {
            if (content[i] is FunctionCallContent functionCall)
            {
                (functionCalls ??= []).Add(functionCall);
                any = true;
            }
        }

        return any;
    }

    /// <summary>
    /// Helper methods for ContinuationPermissionManager
    /// </summary>
    private static string ExtractConversationId(IEnumerable<ChatMessage> messages)
    {
        if (messages.Any())
            return messages.First().MessageId ?? "temp_conv_id";
        return "unknown_conv_id";
    }

    private static string? ExtractProjectId(ChatOptions? options)
    {
        if (options?.AdditionalProperties?.TryGetValue("Project", out var projectObj) == true && projectObj is Project project)
            return project.Id;
        return null;
    }

    private static string[] GetCompletedFunctionNames(List<ChatMessage> responseMessages)
    {
        var completedCallIds = responseMessages
            .Where(m => m.Role == ChatRole.Tool)
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(fr => fr.CallId)
            .ToHashSet();

        return responseMessages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Where(fc => completedCallIds.Contains(fc.CallId))
            .Select(fc => fc.Name)
            .Distinct()
            .ToArray();
    }

    private static string[] GetPlannedFunctionNames(List<FunctionCallContent>? functionCallContents)
    {
        return functionCallContents?.Select(fc => fc.Name).ToArray() ?? Array.Empty<string>();
    }
}