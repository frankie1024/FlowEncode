namespace FlowEncode.Controls.Shared;

public enum TaskPresentationState
{
    Idle,
    Validating,
    Queued,
    Running,
    Canceling,
    Completed,
    Failed,
    Cancelled
}
