import ReactMarkdown from 'react-markdown'

interface MarkdownContentProps {
  content: string
  className?: string
  compact?: boolean
}

export default function MarkdownContent({ content, className, compact }: MarkdownContentProps) {
  return (
    <div
      className={`prose prose-sm dark:prose-invert max-w-none ${
        compact ? 'prose-p:my-1 prose-headings:my-2' : ''
      } ${className ?? ''}`}
    >
      <ReactMarkdown>{content}</ReactMarkdown>
    </div>
  )
}
