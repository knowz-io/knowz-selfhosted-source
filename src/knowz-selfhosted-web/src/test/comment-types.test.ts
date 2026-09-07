import { describe, it, expect } from 'vitest'
import type {
  Comment,
  CreateCommentData,
  UpdateCommentData,
} from '../lib/types'

describe('Comment Types', () => {
  it('Should_HaveComment_WithRequiredFields', () => {
    const comment: Comment = {
      id: 'comment-1',
      knowledgeId: 'knowledge-1',
      authorName: 'Test User',
      body: 'This is a contribution',
      isAnswer: false,
      createdAt: '2026-02-16T10:00:00Z',
      updatedAt: '2026-02-16T10:00:00Z',
      attachmentCount: 0,
    }
    expect(comment.id).toBe('comment-1')
    expect(comment.knowledgeId).toBe('knowledge-1')
    expect(comment.authorName).toBe('Test User')
    expect(comment.body).toBe('This is a contribution')
    expect(comment.isAnswer).toBe(false)
    expect(comment.createdAt).toBe('2026-02-16T10:00:00Z')
    expect(comment.updatedAt).toBe('2026-02-16T10:00:00Z')
    expect(comment.attachmentCount).toBe(0)
  })

  it('Should_HaveComment_WithOptionalFields', () => {
    const comment: Comment = {
      id: 'comment-1',
      knowledgeId: 'knowledge-1',
      parentCommentId: 'parent-1',
      authorName: 'Test User',
      body: 'A reply',
      isAnswer: true,
      sentiment: 'positive',
      createdAt: '2026-02-16T10:00:00Z',
      updatedAt: '2026-02-16T10:00:00Z',
      replies: [],
      attachmentCount: 2,
    }
    expect(comment.parentCommentId).toBe('parent-1')
    expect(comment.sentiment).toBe('positive')
    expect(comment.replies).toEqual([])
    expect(comment.attachmentCount).toBe(2)
  })

  it('Should_HaveComment_WithNestedReplies', () => {
    const reply: Comment = {
      id: 'reply-1',
      knowledgeId: 'knowledge-1',
      parentCommentId: 'comment-1',
      authorName: 'Replier',
      body: 'A reply to the comment',
      isAnswer: false,
      createdAt: '2026-02-16T11:00:00Z',
      updatedAt: '2026-02-16T11:00:00Z',
      attachmentCount: 0,
    }
    const comment: Comment = {
      id: 'comment-1',
      knowledgeId: 'knowledge-1',
      authorName: 'Author',
      body: 'Top level comment',
      isAnswer: false,
      createdAt: '2026-02-16T10:00:00Z',
      updatedAt: '2026-02-16T10:00:00Z',
      replies: [reply],
      attachmentCount: 0,
    }
    expect(comment.replies).toHaveLength(1)
    expect(comment.replies![0].parentCommentId).toBe('comment-1')
  })

  it('Should_HaveCreateCommentData_WithRequiredFields', () => {
    const data: CreateCommentData = {
      body: 'New contribution',
    }
    expect(data.body).toBe('New contribution')
  })

  it('Should_HaveCreateCommentData_WithOptionalFields', () => {
    const data: CreateCommentData = {
      body: 'New contribution',
      authorName: 'Custom Author',
      parentCommentId: 'parent-1',
      sentiment: 'neutral',
    }
    expect(data.authorName).toBe('Custom Author')
    expect(data.parentCommentId).toBe('parent-1')
    expect(data.sentiment).toBe('neutral')
  })

  it('Should_HaveUpdateCommentData_WithOptionalFields', () => {
    const data: UpdateCommentData = {
      body: 'Updated body',
      sentiment: 'negative',
    }
    expect(data.body).toBe('Updated body')
    expect(data.sentiment).toBe('negative')
  })

  it('Should_HaveUpdateCommentData_WithPartialFields', () => {
    const bodyOnly: UpdateCommentData = { body: 'Just body' }
    expect(bodyOnly.body).toBe('Just body')
    expect(bodyOnly.sentiment).toBeUndefined()

    const sentimentOnly: UpdateCommentData = { sentiment: 'positive' }
    expect(sentimentOnly.body).toBeUndefined()
    expect(sentimentOnly.sentiment).toBe('positive')
  })
})
