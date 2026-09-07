using System.Text;
using App.Api.Endpoints;
using App.Api.Middleware;
using App.Core.Interfaces;
using App.Infrastructure.Data;
using App.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SharedKernel;

var builder = WebApplication.CreateBuilder(args);

// Structured JSON logging
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ssZ";
});

// Database connection string from environment (PORT-05 / SEC-08)
var conn = builder.Configuration.GetConnectionString("AppDb")
           ?? builder.Configuration["DATABASE_URL_APP"]
           ?? "Host=localhost;Port=5432;Database=job_platform_app;Username=postgres;Password=postgres";

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(conn));

// Register Local CV File Storage
builder.Services.AddSingleton<IFileStorageService, LocalCvStorageService>();

// Auth config
if (!builder.Environment.IsDevelopment())
{
    var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
    var jwt = jwtSection.Get<JwtOptions>() ?? new JwtOptions();

    if (string.IsNullOrEmpty(jwt.Secret) || jwt.Secret.Length < 32)
    {
        throw new InvalidOperationException(
            "JWT secret not configured or too short. Set JWT_SECRET env var (min 32 chars).");
    }

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwt.Issuer,
                ValidAudience = jwt.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
                ClockSkew = TimeSpan.Zero
            };
        });
    builder.Services.AddAuthorization();
}

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "Application Service", Version = "v0.1.0" });
    o.AddSecurityDefinition("X-User-Id", new()
    {
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Name = "X-User-Id",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Description = "Dev-only: Candidate or Recruiter UUID"
    });
    o.AddSecurityDefinition("X-User-Role", new()
    {
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Name = "X-User-Role",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Description = "Dev-only: User | Recruiter | Admin"
    });
});

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseMiddleware<DevAuthMiddleware>();
}
else
{
    app.UseAuthentication();
    app.UseAuthorization();
}

// Health check endpoint (6-nfr.md:REL-06, 8-system-architecture.md)
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "application" }))
   .WithTags("Health")
   .ExcludeFromDescription();

app.MapGet("/", () => Results.Ok(new { service = "application", version = "0.1.0" }))
   .ExcludeFromDescription();

// Register application endpoints
app.MapApplicationEndpoints();

// Auto-migrate on startup if database is available
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    try
    {
        if (db.Database.IsRelational())
        {
            await db.Database.MigrateAsync();
            logger.LogInformation("Database migrated successfully.");
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Could not run DB migrations at startup. If running without Postgres, verify connection.");
        if (!app.Environment.IsDevelopment())
        {
            throw;
        }
    }
}

app.Run();

// For WebApplicationFactory in tests
public partial class Program { }
