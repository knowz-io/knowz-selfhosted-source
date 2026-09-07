using Knowz.Core.Entities;
using Knowz.SelfHosted.Application.DTOs;
using Knowz.Core.Interfaces;
using Knowz.SelfHosted.Application.Specifications;
using Knowz.SelfHosted.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Knowz.SelfHosted.Application.Services;

/// <summary>
/// Service for topic listing and details.
/// Uses ISelfHostedRepository for spec-based queries and DbContext for projection queries.
/// </summary>
public class TopicService
{
    private readonly ISelfHostedRepository<Topic> _topicRepo;
    private readonly SelfHostedDbContext _db;
    private readonly ILogger<TopicService> _logger;

    public TopicService(
        ISelfHostedRepository<Topic> topicRepo,
        SelfHostedDbContext db,
        ILogger<TopicService> logger)
    {
        _topicRepo = topicRepo;
        _db = db;
        _logger = logger;
    }

    public async Task<TopicListResponse> ListTopicsAsync(int limit, CancellationToken ct)
    {
        var topics = await _db.Topics
            .OrderBy(t => t.Name)
            .Take(limit)
            .Select(t => new TopicListItem(
                t.Id, t.Name, t.Description,
                t.KnowledgeItems.Count))
            .ToListAsync(ct);

        return new TopicListResponse(topics, topics.Count);
    }

    public async Task<TopicDetailResponse?> GetTopicDetailsAsync(Guid id, CancellationToken ct)
    {
        var topic = await _topicRepo.FirstOrDefaultAsync(new TopicByIdWithKnowledgeSpec(id), ct);

        if (topic == null)
            return null;

        var knowledgeItems = topic.KnowledgeItems
            .OrderByDescending(k => k.UpdatedAt)
            .Take(50)
            .Select(k => new KnowledgeListItem(
                k.Id, k.Title,
                SearchFacade.Truncate(k.Summary ?? k.Content, 200) ?? string.Empty,
                k.Type.ToString(),
                k.FilePath,
                null, null, k.CreatedByUserId, null,
                k.CreatedAt, k.UpdatedAt,
                k.IsIndexed));

        return new TopicDetailResponse(topic.Id, topic.Name, topic.Description, knowledgeItems);
    }
}
