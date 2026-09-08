namespace IntegrationOps.Api.Processing;

public sealed class SyntheticIntegrationRunProcessor : IIntegrationRunProcessor
{
    public Task<ProcessingResult> ExecuteAsync(ProcessingAttempt attempt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = attempt.Operation switch
        {
            "Synthetic:RetryOnce" when attempt.Number == 1 => new ProcessingResult(
                ProcessingOutcome.Retryable, Error: ProcessingError.SyntheticRetryableFailure),
            "Synthetic:AlwaysRetry" => new ProcessingResult(
                ProcessingOutcome.Retryable, Error: ProcessingError.SyntheticRetryableFailure),
            "Synthetic:Fail" => new ProcessingResult(
                ProcessingOutcome.Permanent, Error: ProcessingError.SyntheticPermanentFailure),
            _ => new ProcessingResult(ProcessingOutcome.Success, 1)
        };
        return Task.FromResult(result);
    }
}
