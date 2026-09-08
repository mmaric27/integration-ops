using System.Diagnostics;

namespace IntegrationOps.Api.Processing;

public sealed class IntegrationRunWorker(
    IServiceScopeFactory scopes,
    IIntegrationRunProcessor processor,
    ILogger<IntegrationRunWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using (var scope = scopes.CreateAsyncScope())
                    {
                        if (await scope.ServiceProvider.GetRequiredService<IntegrationRunProcessing>().RecoverOneAsync(stoppingToken))
                        {
                            logger.LogInformation("ProcessingLeaseRecovered");
                        }
                    }

                    stoppingToken.ThrowIfCancellationRequested();
                    ProcessingAttempt? attempt;
                    await using (var scope = scopes.CreateAsyncScope())
                    {
                        attempt = await scope.ServiceProvider.GetRequiredService<IntegrationRunProcessing>().ClaimAsync(stoppingToken);
                    }
                    if (attempt is null)
                    {
                        await Task.Delay(ProcessingPolicy.IdleDelay, stoppingToken);
                        continue;
                    }

                    logger.LogInformation("ProcessingClaimed RunId={RunId} Attempt={Attempt}", attempt.RunId, attempt.Number);
                    var started = Stopwatch.GetTimestamp();
                    var result = await ExecuteProcessorAsync(attempt, stoppingToken);
                    stoppingToken.ThrowIfCancellationRequested();
                    await using (var scope = scopes.CreateAsyncScope())
                    {
                        var completed = await scope.ServiceProvider.GetRequiredService<IntegrationRunProcessing>()
                            .CompleteAsync(attempt, result, stoppingToken);
                        var eventName = !completed ? "ProcessingOwnershipLost" : result.Outcome switch
                        {
                            ProcessingOutcome.Success => "ProcessingSucceeded",
                            ProcessingOutcome.Retryable when attempt.Number < ProcessingPolicy.MaximumAttempts => "ProcessingRetryScheduled",
                            _ => "ProcessingFailed"
                        };
                        logger.LogInformation("{ProcessingEvent} RunId={RunId} Attempt={Attempt} Outcome={Outcome} ErrorCode={ErrorCode} DurationMs={DurationMs}",
                            eventName, attempt.RunId, attempt.Number, result.Outcome,
                            result.Error.HasValue ? ProcessingResult.ErrorCode(result.Error.Value) : null,
                            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    }
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested && IntegrationRunProcessing.IsDatabaseUnavailable(exception))
                {
                    // No exception object/message: dependency diagnostics may contain connection details.
                    logger.LogWarning("ProcessingDatabaseUnavailable");
                    await Task.Delay(ProcessingPolicy.DatabaseDelay, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Never release an allocation while execution could still be active.
        }
        catch (Exception)
        {
            logger.LogCritical("ProcessingCoordinationFailure");
            throw;
        }
        finally
        {
            logger.LogInformation("ProcessingWorkerStopping");
        }
    }

    private async Task<ProcessingResult> ExecuteProcessorAsync(ProcessingAttempt attempt, CancellationToken stoppingToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(ProcessingPolicy.ExecutionDeadline);
        try
        {
            // Only execution/result validation belongs inside this boundary. No coordination calls.
            var result = await processor.ExecuteAsync(attempt, deadline.Token);
            stoppingToken.ThrowIfCancellationRequested();
            deadline.Token.ThrowIfCancellationRequested();
            if (result is null || !result.IsValidProcessorResult())
            {
                return new ProcessingResult(ProcessingOutcome.Permanent, Error: ProcessingError.InternalProcessingFailure);
            }
            return result;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return new ProcessingResult(ProcessingOutcome.Retryable, Error: ProcessingError.ProcessingDeadlineExceeded);
        }
        catch (Exception)
        {
            logger.LogError("ProcessingExecutionFailure RunId={RunId} Attempt={Attempt}", attempt.RunId, attempt.Number);
            return new ProcessingResult(ProcessingOutcome.Permanent, Error: ProcessingError.InternalProcessingFailure);
        }
    }
}
