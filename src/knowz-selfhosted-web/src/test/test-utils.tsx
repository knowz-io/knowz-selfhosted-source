import { render, type RenderOptions } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { createContext, useContext, type ReactElement, type ReactNode } from 'react'
import { ViewModeProvider } from '../contexts/ViewModeContext'

const TestContent = createContext<ReactNode>(null)
function TestRoute() {
  return <ViewModeProvider>{useContext(TestContent)}</ViewModeProvider>
}

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
  const router = createMemoryRouter([{ path: '*', element: <TestRoute /> }], { initialEntries: options.initialEntries ?? ['/'] })
  return function TestWrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <TestContent.Provider value={children}>
          <RouterProvider router={router} />
        </TestContent.Provider>
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
