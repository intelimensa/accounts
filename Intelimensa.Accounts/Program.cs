using Intelimensa.Accounts.Api.Auth;
using Intelimensa.Accounts.Api.Devices;
using Intelimensa.Accounts.Api.Telemetry;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Security;
using Intelimensa.Accounts.Storage;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Account");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/Register");
    options.Conventions.AuthorizeFolder("/Staff", "Staff");
    options.Conventions.AuthorizePage("/Download");
});

builder.Services.Configure<ReleaseStorageOptions>(builder.Configuration.GetSection("Releases"));
builder.Services.AddSingleton<IReleaseStorage, LocalReleaseStorage>();

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

        // No email-sending infra exists yet -- registered accounts are usable immediately.
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager();
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
    options.AddPolicy("Staff", policy => policy.RequireRole("Staff"));
});

var app = builder.Build();

// Idempotent: ensures the "Staff" role exists even on a fresh DB. Granting it to a specific user
// is a manual bootstrap step (see CLAUDE.md) -- there's no UI for it yet.
await using (var scope = app.Services.CreateAsyncScope())
{
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    if (!await roleManager.RoleExistsAsync("Staff"))
        await roleManager.CreateAsync(new IdentityRole("Staff"));
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
app.MapTelemetryEndpoints();

app.Run();
