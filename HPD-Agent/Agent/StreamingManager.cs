// HPD-Agent/Agent/StreamingManager.cs

using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

/// <summary>
/// Manages all streaming logic for the agent, including simple and interleaved streaming with function calls.
/// </summary>
public class StreamingManager
{
    private readonly IChatClient _baseClient;
    private readonly FunctionCallProcessor _functionCallProcessor;
    private readonly int _maxFunctionCalls;

    public StreamingManager(IChatClient baseClient, FunctionCallProcessor functionCallProcessor, int maxFunctionCalls)
    {
        _baseClient = baseClient ?? throw new ArgumentNullException(nameof(baseClient));
        _functionCallProcessor = functionCallProcessor ?? throw new ArgumentNullException(nameof(functionCallProcessor));
        _maxFunctionCalls = maxFunctionCalls;
    }

    /// <summary>
    /// Main streaming method that decides between simple and interleaved streaming
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        bool needsFilterProcessing,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (needsFilterProcessing)
        {
            await foreach (var update in GetInterleavedStreamingResponseAsync(messages, options, cancellationToken))
            {
                yield return update;
            }
        }
        else
        {
            await foreach (var update in _baseClient.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                yield return update;
            }
        }
    }

    /// <summary>
    /// Provides true interleaved streaming that emits text immediately and pauses for tool execution
    /// </summary>
    private async IAsyncEnumerable<ChatResponseUpdate> GetInterleavedStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var currentMessages = messages.ToList();
        var operationTracker = new OperationTracker();

        for (int iteration = 0; iteration < _maxFunctionCalls; iteration++)
        {
            var completedFunctionCalls = new List<FunctionCallContent>();
            var hasStreamedContent = false;
            var streamFinished = false;

            // Stream the response and immediately process function calls as they appear
            await foreach (var update in _baseClient.GetStreamingResponseAsync(currentMessages, options, cancellationToken))
            {
                if (update.Contents != null)
                {
                    var textContent = new List<AIContent>();
                    var functionCalls = new List<FunctionCallContent>();

                    // Separate text content from function calls
                    foreach (var content in update.Contents)
                    {
                        if (content is FunctionCallContent funcCall)
                        {
                            functionCalls.Add(funcCall);
                        }
                        else
                        {
                            textContent.Add(content);
                        }
                    }

                    // Emit text content immediately
                    if (textContent.Any())
                    {
                        yield return new ChatResponseUpdate
                        {
                            Contents = textContent,
                            AdditionalProperties = update.AdditionalProperties
                        };
                        hasStreamedContent = true;
                    }

                    // Process function calls immediately when they appear
                    if (functionCalls.Any())
                    {
                        // Use OperationTracker
                        operationTracker.TrackFunctionCall(functionCalls.Select(fc => fc.Name), iteration + 1);

                        // Execute function calls immediately using FunctionCallProcessor
                        var functionCallMessages = await _functionCallProcessor.ProcessFunctionCallsAsync(currentMessages, options, functionCalls, cancellationToken);

                        // Emit function call results immediately
                        foreach (var funcMessage in functionCallMessages)
                        {
                            foreach (var content in funcMessage.Contents)
                            {
                                yield return new ChatResponseUpdate
                                {
                                    Contents = [content]
                                };
                            }
                        }

                        // Add function results to message history for continuation
                        currentMessages.AddRange(functionCallMessages);
                        completedFunctionCalls.AddRange(functionCalls);
                    }
                }

                // Check if stream finished (finish reason present)
                if (update.FinishReason != null)
                {
                    streamFinished = true;

                    // Emit finish reason
                    yield return new ChatResponseUpdate
                    {
                        Contents = [],
                        FinishReason = update.FinishReason,
                        AdditionalProperties = update.AdditionalProperties
                    };
                }
            }

            // If no function calls were made, or stream finished naturally, we're done
            if (!completedFunctionCalls.Any() || streamFinished)
            {
                break;
            }

            // If we had function calls, continue with next iteration
            // Update options for next turn (allow model to not call tools)
            options = options == null
                ? new ChatOptions()
                : new ChatOptions
                {
                    Tools = options.Tools,
                    ToolMode = AutoChatToolMode.Auto, // Allow model to choose
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
        }

        // Use OperationTracker to get metadata
        var finalMetadata = operationTracker.GetMetadata();
        yield return new ChatResponseUpdate
        {
            Contents = [],
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [Agent.OperationHadFunctionCallsKey] = finalMetadata.HadFunctionCalls,
                [Agent.OperationFunctionCallsKey] = finalMetadata.FunctionCalls.ToArray(),
                [Agent.OperationFunctionCallCountKey] = finalMetadata.FunctionCallCount
            }
        };
    }
}