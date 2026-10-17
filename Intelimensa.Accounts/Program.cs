using Intelimensa.Accounts.Api.Auth;
using Intelimensa.Accounts.Api.Devices;
using Intelimensa.Accounts.Api.Manufacturing;
using Intelimensa.Accounts.Api.Updates;
using Intelimensa.Accounts.Api.Telemetry;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Email;
using Intelimensa.Accounts.Manufacturing;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Publishing;
using Intelimensa.Accounts.Security;
using Intelimensa.Accounts.Storage;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Account");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/Register");
    options.Conventions.AllowAnonymousToPage("/Account/ConfirmEmail");
    options.Conventions.AuthorizeFolder("/Admin", "Administrator");
    options.Conventions.AuthorizeFolder("/Staff", "Staff");
    options.Conventions.AuthorizeFolder("/Manufacturing", "ManufacturerPortal");
    options.Conventions.AuthorizePage("/Download");
});

builder.Services.Configure<ReleaseStorageOptions>(builder.Configuration.GetSection("Releases"));
builder.Services.AddSingleton<IReleaseStorage, LocalReleaseStorage>();
builder.Services.Configure<ReleasePublishingOptions>(builder.Configuration.GetSection("Releases"));
builder.Services.AddScoped<ReleasePublisher>();
builder.Services.Configure<FirmwareStorageOptions>(builder.Configuration.GetSection("Firmware"));
builder.Services.AddSingleton<IFirmwareStorage, LocalFirmwareStorage>();

builder.Services.AddScoped<UnitProvisioning>();
builder.Services.AddOptions<ManufacturingOptions>()
    .Bind(builder.Configuration.GetSection("Manufacturing"))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ManufacturingOptions>, ManufacturingOptionsValidator>();
builder.Services.Configure<DeviceRegistrationOptions>(builder.Configuration.GetSection("Devices"));
builder.Services.AddSingleton<RegistrationFailureLimiter>();

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // Length over composition rules (NIST 800-63B) -- Argon2id already defends against
        // offline cracking, so complexity requirements mostly just add user friction.
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredLength = 12;

        options.User.RequireUniqueEmail = true;

        // Not RequireConfirmedAccount: an unconfirmed user must still be able to sign in to the web
        // portal to ask for another confirmation email. They are Pending until staff approve, and
        // staff can only approve a confirmed address, so this never opens API/download access.
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders(); // AddIdentityCore doesn't: the email-confirmation token needs one
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromDays(3));

builder.Services.AddOptions<EmailOptions>()
    .Bind(builder.Configuration.GetSection("Email"))
    .ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<EmailOptions>, EmailOptionsValidator>();
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));
builder.Services.AddSingleton<ConfirmationResendLimiter>();
if (!string.IsNullOrWhiteSpace(builder.Configuration["Email:Host"]))
    builder.Services.AddSingleton<IMailSender, SmtpMailSender>();
else
    builder.Services.AddSingleton<IMailSender, LoggingMailSender>();
builder.Services.AddScoped<EmailConfirmation>();
builder.Services.AddScoped<IPasswordHasher<ApplicationUser>, Argon2idPasswordHasher>();
builder.Services.AddScoped<TokenService>();

var jwtSigningKey = builder.Configuration["Jwt:SigningKey"];
if (string.IsNullOrWhiteSpace(jwtSigningKey))
{
    throw new InvalidOperationException(
        "Jwt:SigningKey is not configured. Set it via " +
        "'dotnet user-secrets set \"Jwt:SigningKey\" \"<base64 value>\"' in Development " +
        "(a real secret store in other environments).");
}

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
    })
    // SignInManager<TUser>.AuthenticationScheme defaults to IdentityConstants.ApplicationScheme
    // ("Identity.Application") and isn't reconfigured by AddIdentityCore -- the cookie scheme
    // must be registered under that exact name, not CookieAuthenticationDefaults.AuthenticationScheme
    // ("Cookies"), or SignInManager.SignInAsync throws "No sign-in handler is registered".
    .AddCookie(IdentityConstants.ApplicationScheme, options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
        // Without this a revoked role would live on in the cookie until it expires.
        options.Events.OnValidatePrincipal = SecurityStampCheck.ValidateAsync;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(jwtSigningKey)),
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Grants and revokes the other roles (/Admin). Deliberately not implied by Staff, and not
    // grantable from the UI: only scripts/setrole.sh adds an Administrator.
    options.AddPolicy("Administrator", policy => policy.RequireRole("Administrator"));

    options.AddPolicy("Staff", policy => policy.RequireRole("Staff"));

    // The /Manufacturing web pages (cookie). Same role as the API policy below, just the cookie
    // scheme instead of the JWT one; still separate from Staff.
    options.AddPolicy("ManufacturerPortal", policy => policy.RequireRole("Manufacturer"));

    // Manufacturing-station API (JWT, not the cookie). Deliberately separate from Staff: a
    // Manufacturer can mint device identities but sees nothing else, and Staff can't mint them.
    options.AddPolicy("Manufacturer", policy => policy
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireRole("Manufacturer"));

    // AxoSync's update check and installer download (JWT): any signed-in account; the endpoints
    // additionally require it to still be usable.
    options.AddPolicy("ReleaseClient", policy => policy
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser());

    // The publish script's API (JWT). Its own role, so publishing builds is neither Staff's nor a
    // Manufacturer's job and can be given to a build machine's account alone.
    options.AddPolicy("Publisher", policy => policy
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireRole("Publisher"));
});

var app = builder.Build();

if (string.IsNullOrWhiteSpace(builder.Configuration["Email:Host"]) && !app.Environment.IsDevelopment())
    app.Logger.LogWarning("Email:Host is not set: confirmation emails are only logged, not sent, so new accounts cannot confirm their address.");

// Idempotent: ensures the roles exist even on a fresh DB. Administrator is granted by hand
// (scripts/setrole.sh); an Administrator grants the others at /Admin/Roles.
await using (var scope = app.Services.CreateAsyncScope())
{
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    foreach (var role in new[] { "Administrator", "Staff", "Manufacturer", "Publisher" })
        if (!await roleManager.RoleExistsAsync(role))
            await roleManager.CreateAsync(new IdentityRole(role));
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();
app.MapAuthEndpoints();
app.MapDeviceEndpoints();
app.MapManufacturingEndpoints();
app.MapReleaseEndpoints();
app.MapTelemetryEndpoints();

app.Run();
