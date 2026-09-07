import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { renderWithProviders } from './test-utils'
import CommentSection from '../components/CommentSection'
import type { Comment } from '../lib/types'

vi.mock('../components/MarkdownContent', () => ({
  default: ({ content, compact }: { content: string; compact?: boolean }) => (
    <div data-testid="markdown-content" data-compact={compact ? 'true' : 'false'}>
      {content}
    </div>
  ),
}))

vi.mock('../lib/api-client', () => ({
  api: {
    listComments: vi.fn(),
    addComment: vi.fn(),
    updateComment: vi.fn(),
    deleteComment: vi.fn(),
    attachFileToComment: vi.fn(),
    getCommentAttachments: vi.fn(),
    detachFileFromComment: vi.fn(),
    uploadFile: vi.fn(),
  },
  ApiError: class ApiError extends Error {
    status: number
    constructor(status: number, message: string) {
      super(message)
      this.status = status
      this.name = 'ApiError'
    }
  },
}))

import { api } from '../lib/api-client'

const mockListComments = vi.mocked(api.listComments)
const mockAddComment = vi.mocked(api.addComment)
const mockUpdateComment = vi.mocked(api.updateComment)
const mockDeleteComment = vi.mocked(api.deleteComment)
const mockUploadFile = vi.mocked(api.uploadFile)
const mockAttachFileToComment = vi.mocked(api.attachFileToComment)

const sampleComment: Comment = {
  id: 'comment-1',
  knowledgeId: 'knowledge-1',
  authorName: 'Alice',
  body: 'This is a contribution',
  isAnswer: false,
  createdAt: '2026-02-16T10:00:00Z',
  updatedAt: '2026-02-16T10:00:00Z',
  attachmentCount: 0,
  replies: [],
}

const commentWithReply: Comment = {
  ...sampleComment,
  replies: [
    {
      id: 'reply-1',
      knowledgeId: 'knowledge-1',
      parentCommentId: 'comment-1',
      authorName: 'Bob',
      body: 'This is a reply',
      isAnswer: false,
      createdAt: '2026-02-16T11:00:00Z',
      updatedAt: '2026-02-16T11:00:00Z',
      attachmentCount: 0,
    },
  ],
}

const commentWithAttachment: Comment = {
  ...sampleComment,
  attachmentCount: 2,
}

describe('CommentSection', () => {
  beforeEach(() => {
    mockListComments.mockResolvedValue([])
    mockAddComment.mockResolvedValue(sampleComment)
    mockUpdateComment.mockResolvedValue({ ...sampleComment, body: 'Updated' })
    // WorkGroupID: kc-fix-attach-delete-transcript-20260411-080000 —
    // deleteComment now returns a CommentDeleteResult envelope.
    mockDeleteComment.mockResolvedValue({
      filesPreserved: 0,
      filesDeleted: 0,
      preservedFileNames: [],
      deletedFileNames: [],
    })
  })

  afterEach(() => {
    vi.clearAllMocks()
  })

  // --- Rendering ---

  it('Should_RenderContributionsHeader_WhenMounted', async () => {
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)
    await waitFor(() => {
      expect(screen.getByText('Contributions')).toBeInTheDocument()
    })
  })

  it('Should_ShowEmptyMessage_WhenNoComments', async () => {
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)
    await waitFor(() => {
      expect(screen.getByText(/no contributions yet/i)).toBeInTheDocument()
    })
  })

  it('Should_ShowAddContributionTextarea_WhenMounted', async () => {
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)
    await waitFor(() => {
      expect(screen.getByPlaceholderText(/add a contribution/i)).toBeInTheDocument()
    })
  })

  it('Should_ShowAddContributionButton_WhenMounted', async () => {
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)
    await waitFor(() => {
      expect(screen.getByRole('button', { name: /add contribution/i })).toBeInTheDocument()
    })
  })

  // --- Displaying Comments ---

  it('Should_DisplayCommentAuthorAndBody_WhenCommentsExist', async () => {
    mockListComments.mockResolvedValue([sampleComment])
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)
    await waitFor(() => {
      expect(screen.getByText('Alice')).toBeInTheDocument()
      expect(screen.getByText('This is a contribution')).toBeInTheDocument()
    })
  })

  it('Should_DisplayAttachmentBadge_WhenCommentHasAttachments', async () => {
    mockListComments.mockResolvedValue([commentWithAttachment])
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)
    await waitFor(() => {
      expect(screen.getByText('2')).toBeInTheDocument()
    })
  })

  it('Should_DisplayRepliesIndented_WhenCommentHasReplies', async () => {
    mockListComments.mockResolvedValue([commentWithReply])
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)
    await waitFor(() => {
      expect(screen.getByText('Alice')).toBeInTheDocument()
      expect(screen.getByText('This is a contribution')).toBeInTheDocument()
      expect(screen.getByText('Bob')).toBeInTheDocument()
      expect(screen.getByText('This is a reply')).toBeInTheDocument()
    })
  })

  // --- Adding Comments ---

  it('Should_CallAddComment_WhenFormSubmitted', async () => {
    const user = userEvent.setup()
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)

    await waitFor(() => {
      expect(screen.getByPlaceholderText(/add a contribution/i)).toBeInTheDocument()
    })

    const textarea = screen.getByPlaceholderText(/add a contribution/i)
    await user.type(textarea, 'New contribution text')
    await user.click(screen.getByRole('button', { name: /add contribution/i }))

    await waitFor(() => {
      expect(mockAddComment).toHaveBeenCalledWith('knowledge-1', {
        body: 'New contribution text',
      })
    })
  })

  it('Should_DisableSubmit_WhenTextareaEmpty', async () => {
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)
    await waitFor(() => {
      const btn = screen.getByRole('button', { name: /add contribution/i })
      expect(btn).toBeDisabled()
    })
  })

  // --- Reply ---

  it('Should_ShowReplyForm_WhenReplyClicked', async () => {
    const user = userEvent.setup()
    mockListComments.mockResolvedValue([sampleComment])
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)

    await waitFor(() => {
      expect(screen.getByText('Alice')).toBeInTheDocument()
    })

    await user.click(screen.getByRole('button', { name: /reply/i }))

    await waitFor(() => {
      expect(screen.getByPlaceholderText(/write a reply/i)).toBeInTheDocument()
    })
  })

  it('Should_SubmitReply_WithParentCommentId', async () => {
    const user = userEvent.setup()
    mockListComments.mockResolvedValue([sampleComment])
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)

    await waitFor(() => {
      expect(screen.getByText('Alice')).toBeInTheDocument()
    })

    await user.click(screen.getByRole('button', { name: /reply/i }))
    const replyTextarea = screen.getByPlaceholderText(/write a reply/i)
    await user.type(replyTextarea, 'My reply')

    // Find the submit button within the reply form area
    const submitButtons = screen.getAllByRole('button', { name: /submit/i })
    await user.click(submitButtons[submitButtons.length - 1])

    await waitFor(() => {
      expect(mockAddComment).toHaveBeenCalledWith('knowledge-1', {
        body: 'My reply',
        parentCommentId: 'comment-1',
      })
    })
  })

  // --- Edit ---

  it('Should_ShowEditForm_WhenEditClicked', async () => {
    const user = userEvent.setup()
    mockListComments.mockResolvedValue([sampleComment])
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)

    await waitFor(() => {
      expect(screen.getByText('This is a contribution')).toBeInTheDocument()
    })

    await user.click(screen.getByRole('button', { name: /edit/i }))

    await waitFor(() => {
      expect(screen.getByDisplayValue('This is a contribution')).toBeInTheDocument()
    })
  })

  it('Should_CallUpdateComment_WhenEditSaved', async () => {
    const user = userEvent.setup()
    mockListComments.mockResolvedValue([sampleComment])
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)

    await waitFor(() => {
      expect(screen.getByText('This is a contribution')).toBeInTheDocument()
    })

    await user.click(screen.getByRole('button', { name: /edit/i }))
    const editTextarea = screen.getByDisplayValue('This is a contribution')
    await user.clear(editTextarea)
    await user.type(editTextarea, 'Edited text')
    await user.click(screen.getByRole('button', { name: /save/i }))

    await waitFor(() => {
      expect(mockUpdateComment).toHaveBeenCalledWith('comment-1', { body: 'Edited text' })
    })
  })

  // --- Delete ---

  it('Should_ShowDeleteConfirmation_WhenDeleteClicked', async () => {
    const user = userEvent.setup()
    mockListComments.mockResolvedValue([sampleComment])
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)

    await waitFor(() => {
      expect(screen.getByText('This is a contribution')).toBeInTheDocument()
    })

    await user.click(screen.getByRole('button', { name: /delete/i }))

    await waitFor(() => {
      expect(screen.getByText(/are you sure/i)).toBeInTheDocument()
    })
  })

  it('Should_CallDeleteComment_WhenDeleteConfirmed', async () => {
    const user = userEvent.setup()
    mockListComments.mockResolvedValue([sampleComment])
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)

    await waitFor(() => {
      expect(screen.getByText('This is a contribution')).toBeInTheDocument()
    })

    await user.click(screen.getByRole('button', { name: /delete/i }))

    await waitFor(() => {
      expect(screen.getByText(/are you sure/i)).toBeInTheDocument()
    })

    await user.click(screen.getByRole('button', { name: /confirm/i }))

    await waitFor(() => {
      // WorkGroupID: kc-fix-attach-delete-transcript-20260411-080000 —
      // deleteComment signature now accepts a deleteFiles flag (defaults to false).
      expect(mockDeleteComment).toHaveBeenCalledWith('comment-1', false)
    })
  })

  // WorkGroupID: kc-fix-attach-delete-transcript-20260411-080000 — VERIFY-15
  // Checkbox only renders when comment has >=1 attachment.
  it('Should_NotRenderDeleteFilesCheckbox_WhenCommentHasNoAttachments', async () => {
    const user = userEvent.setup()
    mockListComments.mockResolvedValue([sampleComment]) // attachmentCount: 0
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)

    await waitFor(() => {
      expect(screen.getByText('This is a contribution')).toBeInTheDocument()
    })

    await user.click(screen.getByRole('button', { name: /delete/i }))

    await waitFor(() => {
      expect(screen.getByText(/are you sure/i)).toBeInTheDocument()
    })

    expect(screen.queryByTestId('selfhosted-delete-comment-files-checkbox')).not.toBeInTheDocument()
  })

  it('Should_RenderDeleteFilesCheckbox_WhenCommentHasAttachments', async () => {
    const user = userEvent.setup()
    mockListComments.mockResolvedValue([commentWithAttachment]) // attachmentCount: 2
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)

    await waitFor(() => {
      expect(screen.getByText('This is a contribution')).toBeInTheDocument()
    })

    await user.click(screen.getByRole('button', { name: /delete/i }))

    await waitFor(() => {
      expect(screen.getByTestId('selfhosted-delete-comment-files-checkbox')).toBeInTheDocument()
    })
    // Checkbox is unchecked by default
    const checkbox = screen.getByTestId('selfhosted-delete-comment-files-checkbox') as HTMLInputElement
    expect(checkbox.checked).toBe(false)
  })

  it('Should_CallDeleteComment_WithDeleteFilesFalse_WhenCheckboxUnchecked', async () => {
    const user = userEvent.setup()
    mockListComments.mockResolvedValue([commentWithAttachment])
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)

    await waitFor(() => {
      expect(screen.getByText('This is a contribution')).toBeInTheDocument()
    })

    await user.click(screen.getByRole('button', { name: /delete/i }))

    await waitFor(() => {
      expect(screen.getByTestId('selfhosted-delete-comment-files-checkbox')).toBeInTheDocument()
    })

    // Don't click the checkbox — leave unchecked
    await user.click(screen.getByRole('button', { name: /confirm/i }))

    await waitFor(() => {
      expect(mockDeleteComment).toHaveBeenCalledWith('comment-1', false)
    })
  })

  it('Should_CallDeleteComment_WithDeleteFilesTrue_WhenCheckboxChecked', async () => {
    const user = userEvent.setup()
    mockListComments.mockResolvedValue([commentWithAttachment])
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)

    await waitFor(() => {
      expect(screen.getByText('This is a contribution')).toBeInTheDocument()
    })

    await user.click(screen.getByRole('button', { name: /delete/i }))

    await waitFor(() => {
      expect(screen.getByTestId('selfhosted-delete-comment-files-checkbox')).toBeInTheDocument()
    })

    // Check the checkbox then confirm
    await user.click(screen.getByTestId('selfhosted-delete-comment-files-checkbox'))
    await user.click(screen.getByRole('button', { name: /confirm/i }))

    await waitFor(() => {
      expect(mockDeleteComment).toHaveBeenCalledWith('comment-1', true)
    })
  })

  it('Should_DismissDeleteConfirmation_WhenCancelClicked', async () => {
    const user = userEvent.setup()
    mockListComments.mockResolvedValue([sampleComment])
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)

    await waitFor(() => {
      expect(screen.getByText('This is a contribution')).toBeInTheDocument()
    })

    await user.click(screen.getByRole('button', { name: /delete/i }))

    await waitFor(() => {
      expect(screen.getByText(/are you sure/i)).toBeInTheDocument()
    })

    await user.click(screen.getByRole('button', { name: /cancel/i }))

    await waitFor(() => {
      expect(screen.queryByText(/are you sure/i)).not.toBeInTheDocument()
    })
  })

  // --- API calls ---

  it('Should_CallListComments_WithKnowledgeId', async () => {
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)
    await waitFor(() => {
      expect(mockListComments).toHaveBeenCalledWith('knowledge-1')
    })
  })

  // --- VERIFY: Markdown rendering ---

  it('Should_RenderMarkdownContent_WhenCommentDisplayed', async () => {
    const markdownComment: Comment = {
      ...sampleComment,
      body: '**bold** and _italic_ and `code`',
    }
    mockListComments.mockResolvedValue([markdownComment])
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)
    await waitFor(() => {
      const mdEl = screen.getByTestId('markdown-content')
      expect(mdEl).toBeInTheDocument()
      expect(mdEl).toHaveTextContent('**bold** and _italic_ and `code`')
      expect(mdEl).toHaveAttribute('data-compact', 'true')
    })
  })

  // --- VERIFY: Author initials avatar ---

  it('Should_DisplayAuthorInitialsAvatar_WhenSingleName', async () => {
    mockListComments.mockResolvedValue([sampleComment]) // authorName: 'Alice'
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)
    await waitFor(() => {
      expect(screen.getByText('A')).toBeInTheDocument()
    })
  })

  it('Should_DisplayAuthorInitialsAvatar_WhenTwoWordName', async () => {
    const twoWordComment: Comment = {
      ...sampleComment,
      id: 'comment-two',
      authorName: 'John Doe',
    }
    mockListComments.mockResolvedValue([twoWordComment])
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)
    await waitFor(() => {
      expect(screen.getByText('JD')).toBeInTheDocument()
    })
  })

  it('Should_DisplayAuthorInitialsAvatar_WhenThreeWordName', async () => {
    const threeWordComment: Comment = {
      ...sampleComment,
      id: 'comment-three',
      authorName: 'Mary Jane Watson',
    }
    mockListComments.mockResolvedValue([threeWordComment])
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)
    await waitFor(() => {
      expect(screen.getByText('MJ')).toBeInTheDocument()
    })
  })

  // --- VERIFY: Ctrl+Enter submits new comment form ---

  it('Should_SubmitNewComment_WhenCtrlEnterPressed', async () => {
    const user = userEvent.setup()
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)

    await waitFor(() => {
      expect(screen.getByPlaceholderText(/add a contribution/i)).toBeInTheDocument()
    })

    const textarea = screen.getByPlaceholderText(/add a contribution/i)
    await user.type(textarea, 'Ctrl enter test')
    await user.keyboard('{Control>}{Enter}{/Control}')

    await waitFor(() => {
      expect(mockAddComment).toHaveBeenCalledWith('knowledge-1', {
        body: 'Ctrl enter test',
      })
    })
  })

  it('Should_SubmitNewComment_WhenMetaEnterPressed', async () => {
    const user = userEvent.setup()
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)

    await waitFor(() => {
      expect(screen.getByPlaceholderText(/add a contribution/i)).toBeInTheDocument()
    })

    const textarea = screen.getByPlaceholderText(/add a contribution/i)
    await user.type(textarea, 'Meta enter test')
    await user.keyboard('{Meta>}{Enter}{/Meta}')

    await waitFor(() => {
      expect(mockAddComment).toHaveBeenCalledWith('knowledge-1', {
        body: 'Meta enter test',
      })
    })
  })

  // --- VERIFY: Ctrl+Enter submits reply textarea ---

  it('Should_SubmitReply_WhenCtrlEnterPressedInReplyTextarea', async () => {
    const user = userEvent.setup()
    mockListComments.mockResolvedValue([sampleComment])
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)

    await waitFor(() => {
      expect(screen.getByText('Alice')).toBeInTheDocument()
    })

    await user.click(screen.getByRole('button', { name: /reply/i }))
    const replyTextarea = screen.getByPlaceholderText(/write a reply/i)
    await user.type(replyTextarea, 'Reply via shortcut')
    await user.keyboard('{Control>}{Enter}{/Control}')

    await waitFor(() => {
      expect(mockAddComment).toHaveBeenCalledWith('knowledge-1', {
        body: 'Reply via shortcut',
        parentCommentId: 'comment-1',
      })
    })
  })

  // --- VERIFY: File attachment ---

  it('Should_UploadFileAndAttachToComment_WhenFileSelected', async () => {
    const user = userEvent.setup()
    const createdComment: Comment = {
      ...sampleComment,
      id: 'new-comment-1',
      body: 'Comment with file',
    }
    mockAddComment.mockResolvedValue(createdComment)
    mockUploadFile.mockResolvedValue({
      fileRecordId: 'file-rec-1',
      fileName: 'test.pdf',
      contentType: 'application/pdf',
      sizeBytes: 1024,
      blobUri: 'https://blob/test.pdf',
      success: true,
    })
    mockAttachFileToComment.mockResolvedValue({
      id: 'attach-1',
      fileRecordId: 'file-rec-1',
      commentId: 'new-comment-1',
      createdAt: '2026-02-16T12:00:00Z',
    })

    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)

    await waitFor(() => {
      expect(screen.getByPlaceholderText(/add a contribution/i)).toBeInTheDocument()
    })

    // Select a file via the hidden input
    const file = new File(['test content'], 'test.pdf', { type: 'application/pdf' })
    const fileInput = document.querySelector('input[type="file"]') as HTMLInputElement
    expect(fileInput).toBeTruthy()
    await user.upload(fileInput, file)

    // File preview chip should appear
    await waitFor(() => {
      expect(screen.getByText('test.pdf')).toBeInTheDocument()
    })

    // Type a comment and submit
    const textarea = screen.getByPlaceholderText(/add a contribution/i)
    await user.type(textarea, 'Comment with file')
    await user.click(screen.getByRole('button', { name: /add contribution/i }))

    // Verify upload + attach calls
    await waitFor(() => {
      expect(mockUploadFile).toHaveBeenCalledWith(file)
      expect(mockAddComment).toHaveBeenCalledWith('knowledge-1', {
        body: 'Comment with file',
      })
      expect(mockAttachFileToComment).toHaveBeenCalledWith('new-comment-1', 'file-rec-1')
    })
  })

  // --- VERIFY: Removing pending file before submit ---

  it('Should_ExcludeRemovedFile_WhenRemoveClickedBeforeSubmit', async () => {
    const user = userEvent.setup()
    mockUploadFile.mockResolvedValue({
      fileRecordId: 'file-rec-1',
      fileName: 'test.pdf',
      contentType: 'application/pdf',
      sizeBytes: 1024,
      blobUri: 'https://blob/test.pdf',
      success: true,
    })

    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)

    await waitFor(() => {
      expect(screen.getByPlaceholderText(/add a contribution/i)).toBeInTheDocument()
    })

    // Select a file
    const file = new File(['test content'], 'test.pdf', { type: 'application/pdf' })
    const fileInput = document.querySelector('input[type="file"]') as HTMLInputElement
    await user.upload(fileInput, file)

    // File chip appears
    await waitFor(() => {
      expect(screen.getByText('test.pdf')).toBeInTheDocument()
    })

    // Remove the file
    const removeBtn = screen.getByRole('button', { name: /remove file/i })
    await user.click(removeBtn)

    // File chip should be gone
    expect(screen.queryByText('test.pdf')).not.toBeInTheDocument()

    // Submit without file
    const textarea = screen.getByPlaceholderText(/add a contribution/i)
    await user.type(textarea, 'No file comment')
    await user.click(screen.getByRole('button', { name: /add contribution/i }))

    await waitFor(() => {
      expect(mockAddComment).toHaveBeenCalled()
      expect(mockAttachFileToComment).not.toHaveBeenCalled()
    })
  })

  // --- VERIFY: Comment cards have visible borders and hover states ---

  it('Should_HaveBorderAndRoundedStyling_OnCommentCards', async () => {
    mockListComments.mockResolvedValue([sampleComment])
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)
    await waitFor(() => {
      expect(screen.getByText('Alice')).toBeInTheDocument()
    })
    // The comment card wrapper should have border and rounded classes
    const commentCard = screen.getByText('Alice').closest('[data-testid="comment-card"]')
    expect(commentCard).toBeTruthy()
    expect(commentCard).toHaveClass('rounded-lg')
    expect(commentCard).toHaveClass('border')
    expect(commentCard).toHaveClass('border-border')
  })

  // --- VERIFY: Reply, Edit, Delete still work after visual changes ---

  it('Should_StillAllowReplyEditDelete_AfterVisualUpgrade', async () => {
    const user = userEvent.setup()
    mockListComments.mockResolvedValue([sampleComment])
    renderWithProviders(<CommentSection knowledgeId="knowledge-1" />)

    await waitFor(() => {
      expect(screen.getByText('Alice')).toBeInTheDocument()
    })

    // Reply button exists and works
    expect(screen.getByRole('button', { name: /reply/i })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: /reply/i }))
    expect(screen.getByPlaceholderText(/write a reply/i)).toBeInTheDocument()

    // Cancel reply, then check Edit
    await user.click(screen.getByRole('button', { name: /cancel/i }))
    await user.click(screen.getByRole('button', { name: /edit/i }))
    expect(screen.getByDisplayValue('This is a contribution')).toBeInTheDocument()

    // Cancel edit, then check Delete
    await user.click(screen.getByRole('button', { name: /cancel edit/i }))
    await user.click(screen.getByRole('button', { name: /delete/i }))
    expect(screen.getByText(/are you sure/i)).toBeInTheDocument()
  })
})
