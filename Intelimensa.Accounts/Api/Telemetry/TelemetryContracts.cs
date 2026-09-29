namespace Intelimensa.Accounts.Api.Telemetry;

public record TelemetryBatchRequest(Guid AccountDeviceId, List<TelemetrySessionEvent> Sessions);

public record TelemetrySessionEvent(
    DateTimeOffset SessionStartedAt,
    DateTimeOffset SessionEndedAt,
    int DifficultyLevel,
    double Accuracy,
    int SuccessfulCommands);
