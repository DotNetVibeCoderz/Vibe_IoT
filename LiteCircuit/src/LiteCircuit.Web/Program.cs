using LiteCircuit.Infrastructure;
using LiteCircuit.Infrastructure.Data;
using LiteCircuit.Web.Api;
using LiteCircuit.Web.Components;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(args);

// Blazor Server
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();

// Data, storage, engineering & AI services
builder.Services.AddLiteCircuit(builder.Configuration, builder.Environment.ContentRootPath);

// Identity (cookie auth)
builder.Services.AddAuthentication(o =>
{
    o.DefaultScheme = IdentityConstants.ApplicationScheme;
    o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
}).AddIdentityCookies();

builder.Services.AddIdentityCore<AppUser>(o =>
{
    o.SignIn.RequireConfirmedAccount = false;
    o.Password.RequiredLength = 8;
    o.Password.RequireNonAlphanumeric = false;
    o.User.RequireUniqueEmail = true;
})
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddAuthorization();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/account/login";
    o.AccessDeniedPath = "/account/login";
});

// REST API + Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.UseSwagger();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "LiteCircuit v1"));

app.MapLiteCircuitApi();

app.MapPost("/account/logout", async (SignInManager<AppUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.LocalRedirect("/account/login");
}).DisableAntiforgery();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// Create schema + seed catalog and default admin (admin@litecircuit.dev / Admin123$)
await DbSeeder.SeedAsync(app.Services);

app.Run();
