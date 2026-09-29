namespace Intelimensa.Accounts.Models;

/// <summary>
/// One AxoSync play session's end-of-session summary for a study participant. Written only for
/// accounts with an active (non-withdrawn) <see cref="StudyParticipation"/>; see
/// <c>/api/telemetry</c> for the ingestion endpoint that enforces that gate. Attributed to the
/// <see cref="AccountDevice"/> pairing (i.e. the physical BCI unit the account was using), not the
/// client-computer <see cref="Device"/> -- the learning-curve analysis this exists for cares about
/// which hardware/config combination produced these numbers, not which OS ran the desktop app.
/// </summary>
public class TelemetryEvent
{
    public long Id { get; set; }

    public int AccountId { get; set; }

    public Account? Account { get; set; }

    public Guid AccountDeviceId { get; set; }

    public AccountDevice? AccountDevice { get; set; }

    public DateTimeOffset SessionStartedAt { get; set; }

    public DateTimeOffset SessionEndedAt { get; set; }

    public int DifficultyLevel { get; set; }

    public double Accuracy { get; set; }

    public int SuccessfulCommands { get; set; }

    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
}
