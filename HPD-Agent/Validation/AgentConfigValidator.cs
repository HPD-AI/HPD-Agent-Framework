using FluentValidation;

public class AgentConfigValidator : AbstractValidator<AgentConfig>
{
    public AgentConfigValidator()
    {
        RuleFor(config => config.Name)
            .NotEmpty()
            .WithMessage("Agent name must not be empty.");

        RuleFor(config => config.MaxFunctionCalls)
            .GreaterThan(0)
            .WithMessage("MaxFunctionCalls must be a positive integer.");

        // Rule to ensure a provider is configured
        RuleFor(config => config.Provider)
            .NotNull()
            .WithMessage("A provider must be configured for the agent.");
    }
}