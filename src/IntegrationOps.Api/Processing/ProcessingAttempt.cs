namespace IntegrationOps.Api.Processing;

// Passed only after acknowledged claim commit. No entity, context or connection escapes the claim scope.
public sealed record ProcessingAttempt(Guid RunId, string Partner, string Operation, int Number, Guid LeaseToken);
