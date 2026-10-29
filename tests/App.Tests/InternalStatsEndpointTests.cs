using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using App.Api.Endpoints;
using App.Core.Entities;
using App.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace App.Tests;

/// <summary>
/// feat(admin) Slice 6: token-authenticated application stats for auth-svc aggregation.
/// Missing/wrong token → 401, unconfigured token → 503, counts grouped by status,
/// newThisMonth scoped to the UTC month.
/// </summary>
public sealed class InternalStatsEndpointTests : IDisposable
{
    private const string Token = "test-internal-token";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _db;

    public InternalStatsEndpointTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetStats_NoToken_Returns401()
    {
        var result = await Invoke(new DefaultHttpContext());

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(401, status.StatusCode);
    }

    [Fact]
    public async Task GetStats_WrongToken_Returns401()
    {
        var result = await Invoke(Context("Bearer wrong-token"));

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(401, status.StatusCode);
    }

    [Fact]
    public async Task GetStats_MissingBearerPrefix_Returns401()
    {
        var result = await Invoke(Context(Token));

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(401, status.StatusCode);
    }

    [Fact]
    public async Task GetStats_MissingConfiguredToken_Returns503()
    {
        var result = await Invoke(Context("Bearer anything"), configuredToken: null);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public async Task GetStats_ValidToken_ReturnsCountsByStatus()
    {
        _db.Applications.Add(NewApplication(ApplicationStatus.Pending));
        _db.Applications.Add(NewApplication(ApplicationStatus.Reviewed));
        _db.Applications.Add(NewApplication(ApplicationStatus.Shortlisted));
        _db.Applications.Add(NewApplication(ApplicationStatus.Accepted));
        _db.Applications.Add(NewApplication(ApplicationStatus.Rejected));
        _db.Applications.Add(NewApplication(ApplicationStatus.Rejected));
        await _db.SaveChangesAsync();

        var result = await Invoke(Context($"Bearer {Token}"));

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(200, status.StatusCode);
        var json = ResultJson(result);
        Assert.Equal(6, json["total"]!.GetValue<int>());
        Assert.Equal(1, json["pending"]!.GetValue<int>());
        Assert.Equal(1, json["accepted"]!.GetValue<int>());
        Assert.Equal(2, json["rejected"]!.GetValue<int>());
    }

    [Fact]
    public async Task GetStats_NewThisMonth_ExcludesPreviousMonths()
    {
        var current = NewApplication(ApplicationStatus.Pending);
        var previous = NewApplication(ApplicationStatus.Pending);
        typeof(Application).GetProperty(nameof(Application.CreatedAt))!
            .SetValue(previous, DateTime.UtcNow.AddMonths(-1));
        _db.Applications.Add(current);
        _db.Applications.Add(previous);
        await _db.SaveChangesAsync();

        var result = await Invoke(Context($"Bearer {Token}"));

        Assert.Equal(1, ResultJson(result)["newThisMonth"]!.GetValue<int>());
    }

    private Task<IResult> Invoke(HttpContext ctx, string? configuredToken = Token) =>
        InternalStatsEndpointInvoker.GetStats(
            ctx, _db, BuildConfig(configuredToken), NullLoggerFactory.Instance);

    private static IConfiguration BuildConfig(string? token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [InternalStatsEndpoints.TokenConfigKey] = token,
            })
            .Build();

    private static HttpContext Context(string authorization)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = authorization;
        return ctx;
    }

    private static Application NewApplication(ApplicationStatus status)
    {
        var applicantId = Guid.NewGuid();
        var application = new Application(Guid.NewGuid(), applicantId, "cv/test.pdf");
        if (status == ApplicationStatus.Pending)
        {
            return application;
        }

        // Walk the state machine so non-Pending rows are created through the domain.
        ApplicationStatus[] path = status switch
        {
            ApplicationStatus.Reviewed => [ApplicationStatus.Reviewed],
            ApplicationStatus.Shortlisted => [ApplicationStatus.Reviewed, ApplicationStatus.Shortlisted],
            ApplicationStatus.Accepted => [ApplicationStatus.Reviewed, ApplicationStatus.Shortlisted, ApplicationStatus.Accepted],
            _ => [ApplicationStatus.Rejected],
        };
        foreach (var next in path)
        {
            application.UpdateStatus(next, applicantId);
        }

        return application;
    }

    private static JsonNode ResultJson(IResult result) =>
        JsonSerializer.SerializeToNode(
            Assert.IsAssignableFrom<IValueHttpResult>(result).Value, WebJson)!;
}

internal static class InternalStatsEndpointInvoker
{
    private static readonly MethodInfo GetStatsMethod =
        typeof(InternalStatsEndpoints).GetMethod(
            "GetStats", BindingFlags.NonPublic | BindingFlags.Static)!;

    public static Task<IResult> GetStats(
        HttpContext ctx,
        AppDbContext db,
        IConfiguration config,
        Microsoft.Extensions.Logging.ILoggerFactory loggerFactory) =>
        (Task<IResult>)GetStatsMethod.Invoke(
            null, [ctx, db, config, loggerFactory, CancellationToken.None])!;
}
