namespace IntegrationOps.Api.Processing;

public enum ProcessingOutcome { Success, Retryable, Permanent }
public enum ProcessingError
{
    SyntheticRetryableFailure,
    SyntheticPermanentFailure,
    ProcessingDeadlineExceeded,
    AttemptAbandoned,
    AttemptExhausted,
    InternalProcessingFailure
}

public sealed record ProcessingResult(ProcessingOutcome Outcome, int RecordCount = 0, ProcessingError? Error = null)
{
    public static string ErrorCode(ProcessingError error) => error switch
    {
        ProcessingError.SyntheticRetryableFailure => "synthetic_retryable_failure",
        ProcessingError.SyntheticPermanentFailure => "synthetic_permanent_failure",
        ProcessingError.ProcessingDeadlineExceeded => "processing_deadline_exceeded",
        ProcessingError.AttemptAbandoned => "attempt_abandoned",
        ProcessingError.AttemptExhausted => "attempt_exhausted",
        ProcessingError.InternalProcessingFailure => "internal_processing_failure",
        _ => throw new ArgumentOutOfRangeException(nameof(error))
    };

    public bool IsValidProcessorResult() => Outcome switch
    {
        ProcessingOutcome.Success => RecordCount >= 0 && Error is null,
        ProcessingOutcome.Retryable => RecordCount == 0 && Error is
            ProcessingError.SyntheticRetryableFailure or ProcessingError.ProcessingDeadlineExceeded,
        ProcessingOutcome.Permanent => RecordCount == 0 && Error is
            ProcessingError.SyntheticPermanentFailure or ProcessingError.InternalProcessingFailure,
        _ => false
    };
}
