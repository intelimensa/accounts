using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Pages.Manufacturing;

/// <summary>One unit's production checklist: mark steps done, edit them, undo them.</summary>
public class UnitModel(UnitProvisioning provisioning, ProductionChecklist checklist, CatalogService catalog, ApplicationDbContext db)
    : ProductionPageModel(provisioning, db)
{
    [BindProperty(SupportsGet = true)]
    public string? Serial { get; set; }

    public UnitChecklist? Unit { get; private set; }

    public List<BciDeviceEvent> History { get; private set; } = [];

    public Dictionary<string, string> Emails { get; private set; } = [];

    /// <summary>
    /// Active assemblies with contents, for the picker's suggestions (typing a part number works too), narrowed to the
    /// tag the step requires (<c>Manufacturing:StepTags</c>).
    /// </summary>
    public List<CatalogItem> AssemblyOptionsFor(ProductionStepType step)
    {
        var required = checklist.RequiredTag(step);
        return Graph.Items.Values
            .Where(i => i.IsAssembly && i.RetiredAt is null && Graph.ChildrenOf(i.Id).Count > 0
                && (required is null || Graph.HasTag(i.Id, required)))
            .OrderBy(i => i.PartNo, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string? RequiredTag(ProductionStepType step) => checklist.RequiredTag(step);

    public CatalogGraph Graph { get; private set; } = new([], [], []);

    /// <summary>The expanded tree of the assembly each of steps 4 and 5 carries.</summary>
    public Dictionary<Guid, TreeNode> Trees { get; private set; } = [];

    public bool IsSuperseded(Guid id) => Graph.IsSuperseded(id);

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
        return RedirectToPage(pageName: null, pageHandler: null, routeValues: new { serial = Serial }, fragment: $"row-{(int)step}");
    }

    private async Task<IActionResult> ChangeAsync(
        ProductionStepType step, StepForm form,
        Func<string, StepInput, Task<ChecklistResult<ProductionStep>>> action, string verb)
    {
        if (await UserIdAsync() is not { } userId)
            return Forbid();

        if (!ProductionChecklistFormat.TryParseInput(form.PerformedAt, out var performedAt))
            return await FailAsync("The date and time isn't valid.");

        Guid? assemblyId = null;
        if (!string.IsNullOrWhiteSpace(form.AssemblyPartNo))
        {
            var item = await catalog.FindAsync(form.AssemblyPartNo);
            if (item is not { Kind: CatalogKind.Assembly })
                return await FailAsync($"{form.AssemblyPartNo.Trim()} isn't an assembly in the catalog.");
            assemblyId = item.Id;
        }

        var result = await action(userId, new StepInput(performedAt, form.Note, assemblyId));
        if (!result.IsOk)
            return await FailAsync(result.Message);

        StatusMessage = $"{ProductionChecklist.Title(step)} {verb}.";
        if (result.Warnings is { Count: > 0 })
            WarningMessage = string.Join(" ", result.Warnings);
        return RedirectToPage(pageName: null, pageHandler: null, routeValues: new { serial = Serial }, fragment: $"row-{(int)step}");
    }

    protected override async Task LoadAsync()
    {
        Unit = await checklist.GetAsync(Serial);
        Graph = await catalog.LoadGraphAsync();
        if (Unit is null)
            return;

        Trees = Unit.Steps.Where(s => s.AssemblyId is not null).Select(s => s.AssemblyId!.Value).Distinct()
            .ToDictionary(id => id, id => Graph.Tree(id));

        History = await checklist.HistoryAsync(Unit.Unit.Id);
        Emails = await EmailsAsync(Unit.Steps.Select(s => s.PerformedByUserId).Concat(History.Select(e => e.UserId)));
    }

    public class StepForm
    {
        public string? PerformedAt { get; set; }

        public string? Note { get; set; }

        /// <summary>Part number of the assembly for steps 4 and 5; blank = not specified.</summary>
        public string? AssemblyPartNo { get; set; }

        public bool ConfirmPaired { get; set; }
    }
}
