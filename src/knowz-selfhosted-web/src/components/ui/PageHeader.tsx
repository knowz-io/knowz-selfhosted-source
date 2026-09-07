import type { ElementType, ReactNode } from 'react'

interface PageHeaderProps {
  eyebrow?: string
  title: string
  description?: string
  actions?: ReactNode
  meta?: ReactNode
  titleAs?: ElementType
}

export default function PageHeader({
  eyebrow,
  title,
  description,
  actions,
  meta,
  titleAs: TitleTag = 'h1',
}: PageHeaderProps) {
  return (
    <section className="sh-hero">
      <div className="flex flex-col gap-4 lg:flex-row lg:items-end lg:justify-between">
        <div className="min-w-0 space-y-3">
          {eyebrow && <p className="sh-kicker">{eyebrow}</p>}
          <div className="space-y-2">
            <TitleTag className="text-3xl font-semibold tracking-tight text-foreground sm:text-[2.2rem]">
              {title}
            </TitleTag>
            {description && (
              <p className="max-w-3xl text-sm leading-6 text-muted-foreground sm:text-[0.95rem]">
                {description}
              </p>
            )}
          </div>
        </div>
        {actions && (
          <div className="flex flex-wrap items-center gap-3 lg:justify-end">
            {actions}
          </div>
        )}
      </div>
      {meta && <div className="mt-5">{meta}</div>}
    </section>
  )
}
