using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages.Manufacturing;

/// <summary>One unit's production checklist: mark steps done, edit them, undo them.</summary>
public class UnitModel(UnitProvisioning provisioning, ProductionChecklist checklist, ApplicationDbContext db)
    : ProductionPageModel(provisioning, db)
{
    [BindProperty(SupportsGet = true)]
    public string? Serial { get; set; }

    public UnitChecklist? Unit { get; private set; }

    public List<BciDeviceEvent> History { get; private set; } = [];

    public Dictionary<string, string> Emails { get; private set; } = [];

    public List<MountingType> MountingOptions { get; private set; } = [];

    public List<ElectrodeSet> ElectrodeOptions { get; private set; } = [];

    public string Now => ProductionChecklistFormat.Input(DateTimeOffset.UtcNow);

    public async Task OnGetAsync() => await LoadAsync();

    public Task<IActionResult> OnPostMarkAsync(ProductionStepType step, StepForm form) =>
        ChangeAsync(step, form, (userId, input) => checklist.MarkDoneAsync(userId, Serial, step, input, form.ConfirmPaired), "recorded");

    public Task<IActionResult> OnPostEditAsync(ProductionStepType step, StepForm form) =>
        ChangeAsync(step, form, (userId, input) => checklist.EditAsync(userId, Serial, step, input, form.ConfirmPaired), "updated");

    public async Task<IActionResult> OnPostUndoAsync(ProductionStepType step, string? reason, bool confirmPaired)
    {
        if (await UserIdAsync() is not { } userId)
            return Forbid();

        var result = await checklist.UndoAsync(userId, Serial, step, reason, confirmPaired);
        if (!result.IsOk)
            return await FailAsync(result.Message);

        StatusMessage = $"{ProductionChecklist.Title(step)} undone.";
        return RedirectToPage(new { serial = Serial });
    }

    private async Task<IActionResult> ChangeAsync(
        ProductionStepType step, StepForm form,
        Func<string, StepInput, Task<ChecklistResult<ProductionStep>>> action, string verb)
    {
        if (await UserIdAsync() is not { } userId)
            return Forbid();

        if (!ProductionChecklistFormat.TryParseInput(form.PerformedAt, out var performedAt))
            return await FailAsync("The date and time isn't valid.");

        var result = await action(userId, new StepInput(performedAt, form.Note, form.MountingTypeId, form.ElectrodeSetId));
        if (!result.IsOk)
            return await FailAsync(result.Message);

        StatusMessage = $"{ProductionChecklist.Title(step)} {verb}.";
        if (result.Warnings is { Count: > 0 })
            WarningMessage = string.Join(" ", result.Warnings);
        return RedirectToPage(new { serial = Serial });
    }

    protected override async Task LoadAsync()
    {
        Unit = await checklist.GetAsync(Serial);
        MountingOptions = await Db.MountingTypes.OrderBy(m => m.DisplayName).ToListAsync();
        ElectrodeOptions = (await Db.ElectrodeSets.ToListAsync())
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Version, StringComparer.OrdinalIgnoreCase).ToList();
        if (Unit is null)
            return;

        History = await checklist.HistoryAsync(Unit.Unit.Id);
        Emails = await EmailsAsync(Unit.Steps.Select(s => s.PerformedByUserId).Concat(History.Select(e => e.UserId)));
    }

    public class StepForm
    {
        public string? PerformedAt { get; set; }

        public string? Note { get; set; }

        public int? MountingTypeId { get; set; }

        public Guid? ElectrodeSetId { get; set; }

        public bool ConfirmPaired { get; set; }
    }
}
