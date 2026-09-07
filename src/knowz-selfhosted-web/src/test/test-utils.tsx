import { render, type RenderOptions } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import type { ReactElement, ReactNode } from 'react'
import { ViewModeProvider } from '../contexts/ViewModeContext'

function createTestQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
        gcTime: 0,
      },
      mutations: {
        retry: false,
      },
    },
  })
}

interface WrapperOptions {
  initialEntries?: string[]
}

function createWrapper(options: WrapperOptions = {}) {
  const queryClient = createTestQueryClient()
  return function TestWrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={options.initialEntries ?? ['/']}>
          <ViewModeProvider>
            {children}
          </ViewModeProvider>
        </MemoryRouter>
      </QueryClientProvider>
    )
  }
}

export function renderWithProviders(
  ui: ReactElement,
  options: WrapperOptions & Omit<RenderOptions, 'wrapper'> = {},
) {
  const { initialEntries, ...renderOptions } = options
  return render(ui, {
    wrapper: createWrapper({ initialEntries }),
    ...renderOptions,
  })
}
