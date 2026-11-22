namespace Onboarding.Core.Applications;

public enum WorkflowEvent
{
    ApplicationCreated,
    ValidationStarted,
    ValidationPassed,
    ValidationFailed,
    PhotoCheckPassed,
    PhotoCheckFailed,
    AutoApproved,
    ManualReviewRequired,
    ManuallyApproved,
    ManuallyRejected,
    ProcessingFailed,
}
