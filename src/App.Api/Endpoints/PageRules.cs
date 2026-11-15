namespace App.Api.Endpoints;

/// <summary>
/// Paging rules for the public application list endpoints (/me and /job/{jobId}). Pages are 1-based.
/// Out-of-range values are rejected with 400 rather than clamped, and the row offset is checked in
/// 64-bit arithmetic so that page * size can never overflow the int used by Skip.
/// </summary>
public static class PageRules
{
    public const int DefaultPage = 1;
    public const int DefaultSize = 20;
    public const int MaxSize = 100;

    public static Dictionary<string, string[]> Validate(int page, int size)
    {
        var errors = new Dictionary<string, string[]>();

        if (page < 1)
        {
            errors["page"] = ["page must be 1 or greater."];
        }
        else if ((long)(page - 1) * size > int.MaxValue)
        {
            errors["page"] = ["page is beyond the supported range for this page size."];
        }

        if (size is < 1 or > MaxSize)
        {
            errors["size"] = [$"size must be between 1 and {MaxSize}."];
        }

        return errors;
    }
}
