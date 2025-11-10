using A2A;
using HPD.Agent;
using Microsoft.Extensions.AI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MicrosoftAgent = HPD.Agent.Microsoft.Agent;


public class A2AHandler
{
    private readonly MicrosoftAgent _agent;
    private readonly ITaskManager _taskManager;

    // This dictionary will map an A2A taskId to an HPD-Agent Thread (agent is stateless and reusable)
    private readonly ConcurrentDictionary<string, ConversationThread> _activeThreads = new();

    public A2AHandler(MicrosoftAgent agent, ITaskManager taskManager)
    {
        _agent = agent;
        _taskManager = taskManager;

        // Wire up the TaskManager events to your handler methods
        _taskManager.OnAgentCardQuery = GetAgentCardAsync;
        _taskManager.OnTaskCreated = OnTaskCreatedAsync;
        _taskManager.OnTaskUpdated = OnTaskUpdatedAsync;
    }

    private Task<AgentCard> GetAgentCardAsync(string agentUrl, CancellationToken cancellationToken)
    {
        var skills = new List<AgentSkill>();

        // Inspect the agent's tools to generate skills
        if (_agent.DefaultOptions?.Tools != null)
        {
            foreach (var tool in _agent.DefaultOptions.Tools.OfType<AIFunction>())
            {
                skills.Add(new AgentSkill
                {
                    Id = tool.Name,
                    Name = tool.Name,
                    Description = tool.Description,
                    // You can add tags or examples here if you extend your plugin system
                    Tags = new List<string> { "plugin-function" } 
                });
            }
        }

        var agentCard = new AgentCard
        {
            Name = _agent.Name,
            Description = _agent.SystemInstructions ?? "An HPD-Agent.",
            Url = agentUrl, // The URL provided by the A2A framework
            Version = "1.0.0",
            ProtocolVersion = "0.2.6", // Match the spec version
            Capabilities = new AgentCapabilities
            {
                Streaming = true, // Your agent supports streaming
                PushNotifications = false // Not yet implemented
            },
            Skills = skills,
            DefaultInputModes = new List<string> { "text/plain" },
            DefaultOutputModes = new List<string> { "text/plain" }
        };

        return Task.FromResult(agentCard);
    }

    private async Task ProcessAndRespondAsync(AgentTask task, ConversationThread thread, Message a2aMessage, CancellationToken cancellationToken)
    {
        try
        {
            // 1. Update task status to "working"
            await _taskManager.UpdateStatusAsync(task.Id, TaskState.Working, cancellationToken: cancellationToken);

            // 2. Convert A2A message to HPD-Agent message using your mapper
            var hpdMessage = A2AMapper.ToHpdChatMessage(a2aMessage);

            // 3. Add the new message to the thread history before calling the agent
            await thread.AddMessageAsync(hpdMessage, cancellationToken);

            // 4. Use agent RunAsync directly with thread and get the response
            var response = await _agent.RunAsync([hpdMessage], thread, cancellationToken: cancellationToken);

            // 5. Agent automatically updates thread with response messages

            // 6. Create an A2A artifact from the response using your mapper
            var artifact = A2AMapper.ToA2AArtifact(response);
            await _taskManager.ReturnArtifactAsync(task.Id, artifact, cancellationToken);

            // 7. Update task to "completed"
            await _taskManager.UpdateStatusAsync(task.Id, TaskState.Completed, final: true, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            var errorMessage = new Message
            {
                Role = MessageRole.Agent,
                MessageId = Guid.NewGuid().ToString(),
                Parts = [new TextPart { Text = ex.Message }]
            };
            await _taskManager.UpdateStatusAsync(task.Id, TaskState.Failed, errorMessage, final: true, cancellationToken: cancellationToken);
        }
    }

    private async Task OnTaskCreatedAsync(AgentTask task, CancellationToken cancellationToken)
    {
        // A new task starts a new thread (agent is stateless and reusable)
        var thread = _agent.CreateThread();
        _activeThreads[task.Id] = thread;

        var lastMessage = task.History?.LastOrDefault();
        if (lastMessage != null)
        {
            await ProcessAndRespondAsync(task, thread, lastMessage, cancellationToken);
        }
    }

    private async Task OnTaskUpdatedAsync(AgentTask task, CancellationToken cancellationToken)
    {
        // An updated task continues an existing thread
        if (_activeThreads.TryGetValue(task.Id, out var thread))
        {
            var lastMessage = task.History?.LastOrDefault();
            if (lastMessage != null)
            {
                await ProcessAndRespondAsync(task, thread, lastMessage, cancellationToken);
            }
        }
        else
        {
            // Handle the case where the task is updated but we don't have a thread for it.
            // This might happen if the server restarts. We can recreate the thread from history.
            var newThread = _agent.CreateThread();
            if(task.History != null)
            {
                foreach(var msg in task.History)
                {
                    await newThread.AddMessageAsync(A2AMapper.ToHpdChatMessage(msg), cancellationToken);
                }
            }
            _activeThreads[task.Id] = newThread;

            var lastMessage = task.History?.LastOrDefault();
            if (lastMessage != null)
            {
                await ProcessAndRespondAsync(task, newThread, lastMessage, cancellationToken);
            }
        }
    }
}
