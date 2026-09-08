namespace IntegrationOps.Api.Processing;

public interface IIntegrationRunProcessor
{
    Task<ProcessingResult> ExecuteAsync(ProcessingAttempt attempt, CancellationToken cancellationToken);
}
