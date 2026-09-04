using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Nwp.InvoiceAutomation.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

var authEnabled = builder.Configuration.GetValue<bool>("Auth:Enabled");
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<AccessOptions>(builder.Configuration.GetSection("Access"));
builder.Services.Configure<GraphDocumentOptions>(builder.Configuration.GetSection("GraphDocuments"));
builder.Services.AddSingleton<UserAccessService>();
builder.Services.AddHttpClient<IGraphDocumentClient, GraphDocumentClient>();

if (authEnabled)
{
    builder.Services
        .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

        var adminEmails = builder.Configuration.GetSection("Access:AdminEmails").Get<string[]>() ?? [];
        options.AddPolicy("AdminOnly", policy => policy.RequireAssertion(context =>
        {
            var email = context.User.FindFirst("preferred_username")?.Value
                ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                ?? context.User.Identity?.Name;

            return !string.IsNullOrWhiteSpace(email)
                && adminEmails.Any(adminEmail => string.Equals(adminEmail, email, StringComparison.OrdinalIgnoreCase));
        }));
    });

    builder.Services.AddControllersWithViews().AddMicrosoftIdentityUI();
}

builder.Services.AddRazorPages(options =>
{
    if (authEnabled)
    {
        options.Conventions.AuthorizeFolder("/Suppliers", "AdminOnly");
    }
});

// Mock data sources for the shell app. These will be replaced by SQL/mailbox-backed
// implementations as real capture and extraction land.
builder.Services.AddSingleton<IDocumentStore, MockDocumentStore>();
builder.Services.AddSingleton<SupplierProfileStore>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
}

app.MapRazorPages();
app.Run();
