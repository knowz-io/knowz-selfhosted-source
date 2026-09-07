import type { HTMLAttributes, ReactNode } from 'react'

interface SurfaceCardProps extends HTMLAttributes<HTMLDivElement> {
  children: ReactNode
}

export default function SurfaceCard({
  children,
  className = '',
  ...props
}: SurfaceCardProps) {
  return (
    <div className={`sh-surface ${className}`.trim()} {...props}>
      {children}
    </div>
  )
}
