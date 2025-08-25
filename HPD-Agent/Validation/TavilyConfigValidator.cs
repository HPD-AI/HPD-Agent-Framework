using FluentValidation;

public class TavilyConfigValidator : AbstractValidator<TavilyConfig>
{
    public TavilyConfigValidator()
    {
        RuleFor(config => config.ApiKey)
            .NotEmpty()
            .WithMessage("Tavily API key is required.");

        RuleFor(config => config.MaxResults)
            .InclusiveBetween(1, 20)
            .When(config => config.MaxResults.HasValue) // Only validate if the value is set
            .WithMessage("Tavily MaxResults must be between 1 and 20.");
    }
}