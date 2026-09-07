import { useEffect, useRef, type ReactNode } from 'react'
import { useLocation } from 'react-router-dom'
import Header from './Header'
import AppVersionFooter from './AppVersionFooter'

interface LayoutProps {
  children: ReactNode
}

export default function Layout({ children }: LayoutProps) {
  const location = useLocation()
  const mainRef = useRef<HTMLElement>(null)

  useEffect(() => {
    window.scrollTo({ top: 0, left: 0 })
    mainRef.current?.focus({ preventScroll: true })
  }, [location.pathname])

  return (
    <div className="flex min-h-screen flex-col bg-background text-foreground">
      <a
        href="#main-content"
        data-testid="sh-skip-link"
        className="sr-only focus:not-sr-only focus:fixed focus:left-2 focus:top-2 focus:z-[100] focus:rounded-md focus:bg-primary focus:px-4 focus:py-2 focus:text-primary-foreground focus:shadow-lg"
      >
        Skip to content
      </a>
      <Header />
      <main
        id="main-content"
        ref={mainRef}
        tabIndex={-1}
        role="main"
        className="flex-1 w-full focus:outline-none"
      >
        <div className="mx-auto max-w-7xl px-4 py-5 sm:px-6 lg:px-8 lg:py-6">
          {children}
        </div>
      </main>
      {/*
        SH_InstanceCopyHygiene R6 / VERIFY-L17: the version is visible on the
        login page AND on every authenticated surface, so a screenshot of any
        page identifies the build it came from.
      */}
      <footer className="w-full border-t border-border">
        <div className="mx-auto max-w-7xl px-4 py-3 sm:px-6 lg:px-8">
          <AppVersionFooter />
        </div>
      </footer>
    </div>
  )
}
