namespace FlowEncode.Controls.Shared;

public enum TaskPresentationState
{
    Idle,
    Queued,
    Running,
    Canceling,
    Completed,
    Failed,
    Cancelled
}
