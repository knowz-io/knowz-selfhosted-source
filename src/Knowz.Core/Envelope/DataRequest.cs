namespace Knowz.Core.Envelope;

/// <summary>
/// Standard data request with filtering, sorting, and pagination.
/// </summary>
public class DataRequest
{
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? SearchTerm { get; set; }
    public string? SortBy { get; set; }
    public bool SortDescending { get; set; }
    public Dictionary<string, string> Filters { get; set; } = new();

    public int Skip => (PageNumber - 1) * PageSize;
    public int Take => PageSize;

    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchTerm);
    public bool HasSort => !string.IsNullOrWhiteSpace(SortBy);
    public bool HasFilters => Filters.Any();

    public void Validate()
    {
        if (PageNumber < 1)
            PageNumber = 1;

        if (PageSize < 1)
            PageSize = 1;

        if (PageSize > 100)
            PageSize = 100;
    }
}

/// <summary>
/// Generic data request with typed filter model.
/// </summary>
public class DataRequest<TFilter> : DataRequest where TFilter : class, new()
{
    public TFilter Filter { get; set; } = new();
}
