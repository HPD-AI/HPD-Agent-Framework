# Stored Agent Definitions

Stored agent definitions let a hosted application manage agent configuration through API routes. A route `agentId` can refer to a stored definition, or fall back to the configured hosting build path when no definition exists.

The stored definition is not the running conversation. It tells hosting what agent to build for a route `agentId`. Session and thread routes then select the durable conversation path, and streaming/input routes start or reuse the active runtime for that `agentId + sessionId + threadId` scope.

The hosting registration name and the route `agentId` are different:

- hosting name: selects an ASP.NET Core hosting bundle registered with `AddHPDAgent(name, ...)`
- route `agentId`: selects the requested agent definition or runtime agent inside that bundle

## Definition Routes

| Operation | Route | Body | Success |
| --- | --- | --- | --- |
| Create definition | `POST /agents` | `CreateAgentRequest` | `201 StoredAgentDto` |
| List definitions | `GET /agents` | none | `200 List<AgentSummaryDto>` |
| Get definition | `GET /agents/{agentId}` | none | `200 StoredAgentDto` |
| Update definition | `PUT /agents/{agentId}` | `UpdateAgentRequest` | `200 StoredAgentDto` |
| Delete definition | `DELETE /agents/{agentId}` | none | `204` |

Create and update validate the supplied `AgentConfig`.

## Summary Versus Full Definition

`AgentSummaryDto` is intended for list views and omits the full config. `StoredAgentDto` includes the stored config and metadata for detail/editing views.

Use the list route for catalogs and the detail route when you need to inspect or edit the definition.

## Create And Update Bodies

Create accepts:

```json
{
  "name": "support-assistant",
  "config": {
    "clients": {
      "chat": {
        "providerKey": "openai",
        "modelName": "gpt-5-mini"
      }
    }
  },
  "metadata": {
    "team": "support"
  }
}
```

Update accepts a replacement config body:

```json
{
  "config": {
    "clients": {
      "chat": {
        "providerKey": "openai",
        "modelName": "gpt-5-mini"
      }
    }
  }
}
```

Keep provider setup aligned with the provider docs. Chat model configuration lives under `clients.chat`.

## Cache Eviction

Updating or deleting a stored definition evicts cached unscoped and thread-owned runtime agents for that `agentId`. The next request rebuilds from the current definition or fallback path.

This is important for hosted UIs: a successful update does not mutate an already active thread run in place, but it does affect subsequent runtime builds.

## Factory Override

If an `IAgentFactory` is registered in dependency injection, it is the first build source. Public code should describe the factory input as the requested agent id. Some source names are misleading here, but the manager passes the route `agentId`.

Use a factory when agent construction needs application services or dynamic policy that should not be stored as an `AgentConfig`.
