namespace Knowz.Core.Envelope;

/// <summary>
/// Represents a paged list of items with metadata.
/// IQueryable-dependent factory methods remain in Knowz.Shared.
/// </summary>
public class PagedList<T>
{
    public List<T> Items { get; set; } = new();
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;

    public PagedList()
    {
    }

    public PagedList(List<T> items, int pageNumber, int pageSize, int totalCount)
    {
        Items = items;
        PageNumber = pageNumber;
        PageSize = pageSize;
        TotalCount = totalCount;
    }
}

/// <summary>
/// Metadata for paged results.
/// </summary>
public class PageMetadata
{
    public int CurrentPage { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public bool HasPrevious { get; set; }
    public bool HasNext { get; set; }

    public static PageMetadata FromPagedList<T>(PagedList<T> pagedList)
    {
        return new PageMetadata
        {
            CurrentPage = pagedList.PageNumber,
            PageSize = pagedList.PageSize,
            TotalCount = pagedList.TotalCount,
            TotalPages = pagedList.TotalPages,
            HasPrevious = pagedList.HasPreviousPage,
            HasNext = pagedList.HasNextPage
        };
    }
}
