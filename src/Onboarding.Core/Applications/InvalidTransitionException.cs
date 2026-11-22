namespace Onboarding.Core.Applications;

public sealed class InvalidTransitionException(ApplicationStatus from, ApplicationStatus to, WorkflowEvent @event)
    : InvalidOperationException($"Transition {from} -> {to} via {@event} is not allowed")
{
    public ApplicationStatus From { get; } = from;
    public ApplicationStatus To { get; } = to;
    public WorkflowEvent Event { get; } = @event;
}
