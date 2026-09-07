import { describe, it, expect, beforeEach } from 'vitest'
import { screen, fireEvent } from '@testing-library/react'
import { renderWithProviders } from './test-utils'
import { ViewModeToggle } from '../components/ViewModeToggle'

describe('ViewModeToggle', () => {
  beforeEach(() => {
    localStorage.clear()
  })

  it('Should_RenderTriggerButton_WithDefaultGridMode', () => {
    renderWithProviders(<ViewModeToggle pageKey="knowledge" />)
    expect(screen.getByTestId('view-mode-selector')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /view mode: grid/i })).toBeInTheDocument()
  })

  it('Should_OpenDropdown_WhenTriggerClicked', () => {
    const { container } = renderWithProviders(<ViewModeToggle pageKey="knowledge" />)
    fireEvent.click(screen.getByRole('button', { name: /view mode: grid/i }))
    expect(screen.getByRole('listbox')).toBeInTheDocument()
    expect(container.querySelector('[role="listbox"]')).toBeNull()
    expect(document.body.querySelector('[role="listbox"]')).not.toBeNull()
  })

  it('Should_RenderFiveModeOptions_WhenDropdownOpen', () => {
    renderWithProviders(<ViewModeToggle pageKey="knowledge" />)
    fireEvent.click(screen.getByRole('button', { name: /view mode: grid/i }))
    expect(screen.getByTestId('view-mode-grid')).toBeInTheDocument()
    expect(screen.getByTestId('view-mode-compact')).toBeInTheDocument()
    expect(screen.getByTestId('view-mode-list')).toBeInTheDocument()
    expect(screen.getByTestId('view-mode-gallery')).toBeInTheDocument()
    expect(screen.getByTestId('view-mode-code')).toBeInTheDocument()
  })

  it('Should_UpdateModeAndCloseDropdown_WhenOptionClicked', () => {
    renderWithProviders(<ViewModeToggle pageKey="knowledge" />)
    fireEvent.click(screen.getByRole('button', { name: /view mode: grid/i }))
    fireEvent.click(screen.getByTestId('view-mode-list'))
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: /view mode: list/i })).toBeInTheDocument()
  })

  it('Should_CloseDropdown_WhenEscapePressed', () => {
    renderWithProviders(<ViewModeToggle pageKey="knowledge" />)
    fireEvent.click(screen.getByRole('button', { name: /view mode: grid/i }))
    expect(screen.getByRole('listbox')).toBeInTheDocument()
    fireEvent.keyDown(document, { key: 'Escape' })
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
  })

  it('Should_CloseDropdown_WhenClickedOutside', () => {
    renderWithProviders(
      <div>
        <ViewModeToggle pageKey="knowledge" />
        <div data-testid="outside">Outside</div>
      </div>
    )
    fireEvent.click(screen.getByRole('button', { name: /view mode: grid/i }))
    expect(screen.getByRole('listbox')).toBeInTheDocument()
    fireEvent.mouseDown(screen.getByTestId('outside'))
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
  })

  it('Should_MarkActiveMode_AsSelected_InDropdown', () => {
    renderWithProviders(<ViewModeToggle pageKey="knowledge" />)
    fireEvent.click(screen.getByRole('button', { name: /view mode: grid/i }))
    const gridOption = screen.getByTestId('view-mode-grid')
    expect(gridOption).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByTestId('view-mode-list')).toHaveAttribute('aria-selected', 'false')
  })

  it('Should_RespectAllowedModes_WhenProvided', () => {
    renderWithProviders(<ViewModeToggle pageKey="knowledge" allowedModes={['grid', 'list']} />)
    fireEvent.click(screen.getByRole('button', { name: /view mode: grid/i }))
    expect(screen.getByTestId('view-mode-grid')).toBeInTheDocument()
    expect(screen.getByTestId('view-mode-list')).toBeInTheDocument()
    expect(screen.queryByTestId('view-mode-compact')).not.toBeInTheDocument()
    expect(screen.queryByTestId('view-mode-gallery')).not.toBeInTheDocument()
    expect(screen.queryByTestId('view-mode-code')).not.toBeInTheDocument()
  })

  it('Should_AutoCorrectToFirstAllowedMode_WhenStoredModeNotAllowed', () => {
    localStorage.setItem('knowz-sh-view-mode:knowledge', 'gallery')
    renderWithProviders(<ViewModeToggle pageKey="knowledge" allowedModes={['list', 'code']} />)
    // The effect runs on mount; the active mode should reset to the first allowed mode ('list').
    expect(screen.getByRole('button', { name: /view mode: list/i })).toBeInTheDocument()
    expect(localStorage.getItem('knowz-sh-view-mode:knowledge')).toBe('list')
  })
})
