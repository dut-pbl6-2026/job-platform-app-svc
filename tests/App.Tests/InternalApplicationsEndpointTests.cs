using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using App.Api.Auth;
using App.Api.Endpoints;
using App.Core.DTOs;
using App.Infrastructure.Data;
using App.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace App.Tests;

/// <summary>
/// PBL6-10: GET /api/internal/applications — service-to-service export used by job-svc to
/// backfill its recommendation read model. Token-authenticated (fail-closed), 0-based paging
/// per api-conventions §5, oldest first, ids + createdAt only.
/// Applications are created through ApplicationEndpoints.ApplyForJob, the real entry point.
/// </summary>
public sealed class InternalApplicationsEndpointTests : IDisposable
{
    private const string Token = "test-internal-token";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _db;
    private readonly LocalCvStorageService _storage;
    private readonly string _storageDir;

    public InternalApplicationsEndpointTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("InternalApps_" + Guid.NewGuid())
            .Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _storageDir = Path.Combine(Path.GetTempPath(), "app_internal_apps_" + Guid.NewGuid());
        var storageConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["APP_CV_STORAGE_PATH"] = _storageDir })
            .Build();
        _storage = new LocalCvStorageService(storageConfig, NullLogger<LocalCvStorageService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_storageDir))
        {
            try { Directory.Delete(_storageDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        var result = await Invoke(new DefaultHttpContext());

        Assert.Equal(401, StatusOf(result));
    }

    [Fact]
    public async Task WrongToken_Returns401()
    {
        var result = await Invoke(Authorized("Bearer wrong-token"));

        Assert.Equal(401, StatusOf(result));
    }

    [Fact]
    public async Task MissingBearerPrefix_Returns401()
    {
        var result = await Invoke(Authorized(Token));

        Assert.Equal(401, StatusOf(result));
    }

    [Fact]
    public async Task TokenNotConfigured_Returns401()
    {
        var result = await Invoke(Authorized("Bearer anything"), configuredToken: null);

        Assert.Equal(401, StatusOf(result));
    }

    [Fact]
    public async Task NegativePage_Returns400WithPageError()
    {
        var result = await Invoke(Authorized($"Bearer {Token}"), page: -1);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("page", ValidationErrors(result).Keys);
    }

    [Fact]
    public async Task SizeZero_Returns400WithSizeError()
    {
        var result = await Invoke(Authorized($"Bearer {Token}"), size: 0);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("size", ValidationErrors(result).Keys);
    }

    [Fact]
    public async Task Size101_Returns400WithSizeError()
    {
        var result = await Invoke(Authorized($"Bearer {Token}"), size: 101);

        Assert.Equal(400, StatusOf(result));
        Assert.Contains("size", ValidationErrors(result).Keys);
    }

    [Fact]
    public async Task FirstPage_ReturnsOldestFirst()
    {
        var created = await ApplyThree();

        var result = await Invoke(Authorized($"Bearer {Token}"), page: 0, size: 2);

        Assert.Equal(200, StatusOf(result));
        var json = ResultJson(result);
        var ids = json["items"]!.AsArray().Select(i => i!["id"]!.GetValue<Guid>()).ToList();
        Assert.Equal(created.Take(2), ids);
        Assert.Equal(3, json["total"]!.GetValue<int>());
        Assert.Equal(0, json["page"]!.GetValue<int>());
        Assert.Equal(2, json["size"]!.GetValue<int>());
        Assert.Equal(2, json["totalPages"]!.GetValue<int>());
    }

    [Fact]
    public async Task SecondPage_ReturnsRemainder()
    {
        var created = await ApplyThree();

        var result = await Invoke(Authorized($"Bearer {Token}"), page: 1, size: 2);

        var items = ResultJson(result)["items"]!.AsArray();
        var item = Assert.Single(items);
        Assert.Equal(created[2], item!["id"]!.GetValue<Guid>());
    }

    [Fact]
    public async Task PageBeyondEnd_ReturnsEmptyItems()
    {
        await ApplyThree();

        var result = await Invoke(Authorized($"Bearer {Token}"), page: 5, size: 2);

        var json = ResultJson(result);
        Assert.Empty(json["items"]!.AsArray());
        Assert.Equal(3, json["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task EmptyTable_TotalPagesIsOne()
    {
        var result = await Invoke(Authorized($"Bearer {Token}"));

        var json = ResultJson(result);
        Assert.Equal(0, json["total"]!.GetValue<int>());
        Assert.Equal(1, json["totalPages"]!.GetValue<int>());
        Assert.Equal(0, json["page"]!.GetValue<int>());
        Assert.Equal(100, json["size"]!.GetValue<int>());
    }

    [Fact]
    public async Task RejectedApplication_IsStillExported()
    {
        var applicant = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await Apply(applicant, jobId);
        var application = await _db.Applications.SingleAsync();
        var rejected = await ApplicationEndpoints.UpdateApplicationStatus(
            application.Id,
            new UpdateStatusRequest("rejected", "Not a fit"),
            _db,
            UserContext(Guid.NewGuid(), "Recruiter"));
        Assert.Equal(200, StatusOf(rejected));

        var result = await Invoke(Authorized($"Bearer {Token}"));

        var item = Assert.Single(ResultJson(result)["items"]!.AsArray());
        Assert.Equal(application.Id, item!["id"]!.GetValue<Guid>());
        Assert.Equal(jobId, item["jobId"]!.GetValue<Guid>());
        Assert.Equal(applicant, item["applicantId"]!.GetValue<Guid>());
    }

    [Fact]
    public async Task Payload_HasOnlyIdsAndCreatedAt()
    {
        await Apply(Guid.NewGuid(), Guid.NewGuid());

        var result = await Invoke(Authorized($"Bearer {Token}"));

        var item = Assert.Single(ResultJson(result)["items"]!.AsArray())!.AsObject();
        Assert.Equal(
            new[] { "applicantId", "createdAt", "id", "jobId" },
            item.Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    // Three applicants applying in sequence; returns application ids in creation order.
    private async Task<List<Guid>> ApplyThree()
    {
        var jobId = Guid.NewGuid();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var applicant = Guid.NewGuid();
            await Apply(applicant, jobId);
            ids.Add(await _db.Applications
                .Where(a => a.ApplicantId == applicant)
                .Select(a => a.Id)
                .SingleAsync());
            // Distinct CreatedAt values so creation order is unambiguous.
            await Task.Delay(5);
        }

        return ids;
    }

    private async Task Apply(Guid applicantId, Guid jobId)
    {
        var request = MultipartRequest(jobId);
        var result = await ApplicationEndpoints.ApplyForJob(
            request, _db, _storage, UserContext(applicantId, "User"));
        Assert.Equal(201, StatusOf(result));
    }

    private Task<IResult> Invoke(
        HttpContext ctx, string? configuredToken = Token, int? page = null, int? size = null) =>
        InternalApplicationsEndpoints.GetApplications(
            ctx, _db, BuildConfig(configuredToken), NullLoggerFactory.Instance, page, size);

    private static IConfiguration BuildConfig(string? token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [InternalServiceAuth.TokenConfigKey] = token,
            })
            .Build();

    private static HttpContext Authorized(string authorization)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = authorization;
        return ctx;
    }

    private static HttpContext UserContext(Guid userId, string role)
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, role)],
            "TestAuth"));
        ctx.Request.Headers["X-User-Id"] = userId.ToString();
        ctx.Request.Headers["X-User-Role"] = role;
        return ctx;
    }

    private static HttpRequest MultipartRequest(Guid jobId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.ContentType = "multipart/form-data; boundary=----TestBoundary";
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 Dummy PDF content");
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "cv_file", "cv.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf",
        };
        ctx.Request.Form = new FormCollection(
            new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["job_id"] = jobId.ToString(),
            },
            new FormFileCollection { file });
        return ctx.Request;
    }

    private static int? StatusOf(IResult result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;

    private static IDictionary<string, string[]> ValidationErrors(IResult result) =>
        Assert.IsType<HttpValidationProblemDetails>(
            Assert.IsAssignableFrom<IValueHttpResult>(result).Value).Errors;

    private static JsonNode ResultJson(IResult result) =>
        JsonSerializer.SerializeToNode(
            Assert.IsAssignableFrom<IValueHttpResult>(result).Value, WebJson)!;
}
