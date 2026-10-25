using Intelimensa.Accounts.Models;

namespace Intelimensa.Accounts.Security;

/// <summary>
/// Every API request made with an access token bound to a client install (<see cref="TokenService.ClientInstallClaim"/>)
/// must carry that install's proof (<see cref="InstallProof"/>), and the install must still be usable. Runs after
/// authorization, where the JWT principal is in place; requests on unbound tokens and cookie sessions pass through
/// unchanged. The checked install is left for the endpoint in <see cref="HttpContextInstallExtensions.GetClientInstall"/>.
/// </summary>
public class InstallProofMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ClientInstallRegistry installs, InstallNonces nonces)
    {
        var claim = context.User.FindFirst(TokenService.ClientInstallClaim)?.Value;
        if (claim is null || !context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        var install = Guid.TryParse(claim, out var installId)
            ? await installs.FindUsableAsync(installId, DateTimeOffset.UtcNow)
            : null;
        if (install is null)
        {
            await RefuseAsync(context, "install_revoked");
            return;
        }

        var proof = await InstallProof.CheckAsync(context.Request, install.SigningKey, nonces);
        if (proof != ProofCheck.Valid)
        {
            await RefuseAsync(context, proof.ErrorCode());
            return;
        }

        context.Items[HttpContextInstallExtensions.ItemKey] = install;
        await next(context);
    }

    private static Task RefuseAsync(HttpContext context, string error)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new { error });
    }
}

public static class HttpContextInstallExtensions
{
    internal const string ItemKey = "Intelimensa.ClientInstall";

    /// <summary>The install whose proof this request carried, or null for an unbound token.</summary>
    public static ClientInstall? GetClientInstall(this HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var install) ? install as ClientInstall : null;
}
