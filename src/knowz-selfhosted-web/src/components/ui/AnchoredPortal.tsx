import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useState,
  type CSSProperties,
  type HTMLAttributes,
  type ReactNode,
  type RefObject,
} from 'react'
import { createPortal } from 'react-dom'

type Placement = 'bottom-start' | 'bottom-end' | 'top-start' | 'top-end'

interface AnchoredPortalProps extends Omit<HTMLAttributes<HTMLDivElement>, 'children'> {
  open: boolean
  anchorRef: RefObject<HTMLElement | null>
  panelRef?: RefObject<HTMLDivElement | null>
  placement?: Placement
  offset?: number
  children: ReactNode
}

const VIEWPORT_PADDING = 8

function clamp(value: number, min: number, max: number) {
  if (max <= min) return min
  return Math.min(Math.max(value, min), max)
}

function getPosition(rect: DOMRect, placement: Placement, offset: number): CSSProperties {
  const isTop = placement.startsWith('top')
  const isEnd = placement.endsWith('end')

  return {
    ...(isTop
      ? { bottom: Math.max(VIEWPORT_PADDING, window.innerHeight - rect.top + offset) }
      : { top: Math.max(VIEWPORT_PADDING, rect.bottom + offset) }),
    ...(isEnd
      ? { right: Math.max(VIEWPORT_PADDING, window.innerWidth - rect.right) }
      : {
          left: clamp(
            rect.left,
            VIEWPORT_PADDING,
            Math.max(VIEWPORT_PADDING, window.innerWidth - VIEWPORT_PADDING),
          ),
        }),
    maxWidth: `calc(100vw - ${VIEWPORT_PADDING * 2}px)`,
  }
}

export function AnchoredPortal({
  open,
  anchorRef,
  panelRef,
  placement = 'bottom-end',
  offset = 8,
  className,
  style,
  children,
  ...rest
}: AnchoredPortalProps) {
  const [position, setPosition] = useState<CSSProperties | null>(null)

  const reposition = useCallback(() => {
    const rect = anchorRef.current?.getBoundingClientRect()
    if (!rect) return
    setPosition(getPosition(rect, placement, offset))
  }, [anchorRef, offset, placement])

  useLayoutEffect(() => {
    if (open) reposition()
    else setPosition(null)
  }, [open, reposition])

  useEffect(() => {
    if (!open) return
    const handleViewportChange = () => reposition()
    window.addEventListener('scroll', handleViewportChange, true)
    window.addEventListener('resize', handleViewportChange)
    return () => {
      window.removeEventListener('scroll', handleViewportChange, true)
      window.removeEventListener('resize', handleViewportChange)
    }
  }, [open, reposition])

  if (!open || !position) return null

  return createPortal(
    <div
      ref={panelRef}
      className={['fixed z-[120]', className].filter(Boolean).join(' ')}
      style={{ ...position, ...style }}
      {...rest}
    >
      {children}
    </div>,
    document.body,
  )
}
