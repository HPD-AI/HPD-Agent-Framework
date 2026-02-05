namespace AgentAPI.CQRS;

/// <summary>
/// Marker interface for commands
/// </summary>
public interface ICommand
{
}

/// <summary>
/// Command with return value
/// </summary>
/// <typeparam name="TResult">The result type</typeparam>
public interface ICommand<out TResult> : ICommand
{
}
