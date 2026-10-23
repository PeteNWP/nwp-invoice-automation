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
builder.Services.Configure<BenchmarkGraphOptions>(builder.Configuration.GetSection("BenchmarkGraph"));
builder.Services.AddSingleton<UserAccessService>();
builder.Services.AddHttpClient<IGraphDocumentClient, GraphDocumentClient>();
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();
var mvc = builder.Services.AddControllersWithViews();

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

    mvc.AddMicrosoftIdentityUI();
}

builder.Services.AddRazorPages(options =>
{
    if (authEnabled)
    {
        options.Conventions.AuthorizeFolder("/Suppliers", "AdminOnly");
    }
});

// SharePoint is the capture/system-of-record. GraphInboxSyncWorker is the only thing that
// calls Graph for inbox capture, on its own timer; it writes results to SQL. The UI reads
// SQL only through SqlCapturedDocumentStore and never waits on Graph. Mock records remain
// available as an explicit development source or configured fallback.
builder.Services.AddSingleton<MockDocumentStore>();
builder.Services.AddSingleton<IDocumentStore, SqlCapturedDocumentStore>();
builder.Services.AddSingleton<ICapturedDocumentRepository, CapturedDocumentRepository>();
builder.Services.AddSingleton<GraphInboxReader>();
builder.Services.AddHostedService<GraphInboxSyncWorker>();
builder.Services.AddSingleton<IInvoiceExtractionRepository, InvoiceExtractionRepository>();
builder.Services.AddScoped<InvoiceExtractionApiKeyFilter>();

// Extraction worker: claims captured documents from SQL, sends each PDF to the Copilot Studio
// workflow, and writes results to dbo.InvoiceExtraction. Inert unless CopilotExtraction:Enabled.
builder.Services.Configure<CopilotExtractionOptions>(builder.Configuration.GetSection("CopilotExtraction"));
builder.Services.AddSingleton<ExtractionWorkerState>();
builder.Services.AddHttpClient<ICopilotExtractionClient, CopilotExtractionClient>(client =>
{
    var seconds = builder.Configuration.GetValue("CopilotExtraction:HttpTimeoutSeconds", 150);
    client.Timeout = TimeSpan.FromSeconds(Math.Max(30, seconds));
});
builder.Services.AddHostedService<InvoiceProcessingWorker>();
builder.Services.AddSingleton<SupplierProfileStore>();
builder.Services.AddSingleton<INwpSupplierDirectory, NwpSupplierDirectory>();
builder.Services.AddSingleton<GraphBenchmarkClient>();
builder.Services.AddSingleton<BenchmarkStore>();

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
}

app.MapControllers();
app.MapRazorPages();
app.Run();
