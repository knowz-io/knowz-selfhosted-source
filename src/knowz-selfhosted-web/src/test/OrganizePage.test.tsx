import { describe, it, expect, vi } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { renderWithProviders } from './test-utils'
import OrganizePage from '../pages/OrganizePage'

vi.mock('../pages/TagsPage', () => ({
  default: () => <div>Tags Panel</div>,
}))

vi.mock('../pages/TopicsPage', () => ({
  default: () => <div>Topics Panel</div>,
}))

vi.mock('../pages/EntitiesPage', () => ({
  default: () => <div>Entities Panel</div>,
}))

describe('OrganizePage', () => {
  it('Should_RenderRequestedTab_WhenTabSpecifiedInQuery', () => {
    renderWithProviders(<OrganizePage />, {
      initialEntries: ['/organize?tab=topics'],
    })

    expect(screen.getByText('Topics Panel')).toBeInTheDocument()
    expect(screen.queryByText('Tags Panel')).not.toBeInTheDocument()
  })

  it('Should_SwitchPanels_WhenDifferentTabClicked', async () => {
    const user = userEvent.setup()

    renderWithProviders(<OrganizePage />, {
      initialEntries: ['/organize'],
    })

    expect(screen.getByText('Tags Panel')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Entities' }))
    expect(screen.getByText('Entities Panel')).toBeInTheDocument()
    expect(screen.queryByText('Tags Panel')).not.toBeInTheDocument()
  })
})
