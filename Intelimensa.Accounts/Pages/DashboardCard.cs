namespace Intelimensa.Accounts.Pages;

/// <summary>One card on a dashboard page (see <c>Shared/_DashboardCards</c>). <paramref name="Icon"/> is the
/// <c>d</c> of a 16x16 SVG path (Bootstrap Icons style); <paramref name="Badge"/> is optional attention text.</summary>
public record DashboardCard(string Title, string Description, string Page, string Button, string Icon, string? Badge = null);
