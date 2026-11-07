import { describe, it, expect, vi } from 'vitest'
import { render, screen, fireEvent, waitFor } from '@testing-library/react'
import LocalDecryptPage from '../pages/LocalDecryptPage'
import { runDecrypt } from '../lib/decrypt-flow'
vi.mock('../lib/decrypt-flow', () => ({ runDecrypt: vi.fn(), SafeDecryptError: class SafeDecryptError extends Error {}, GENERIC_DECRYPT_ERROR: 'Could not decrypt. Check that your key and message match, then try again.' }))
describe('local message recovery', () => {
  it('passes protected-key passwords locally, displays empty plaintext and clears key inputs', async () => {
    vi.mocked(runDecrypt).mockResolvedValueOnce({ plaintext: '', fingerprint: 'f', vaultId: 'v', contentAadBound: true })
    render(<LocalDecryptPage />)
    fireEvent.change(screen.getByTestId('decrypt-message'), { target: { value: 'sealed-message' } })
    fireEvent.change(screen.getByTestId('decrypt-key'), { target: { value: 'private-key' } })
    fireEvent.change(screen.getByLabelText('Key file passphrase'), { target: { value: 'key-password' } })
    fireEvent.click(screen.getByTestId('decrypt-submit'))
    await screen.findByTestId('decrypt-plaintext')
    expect(runDecrypt).toHaveBeenCalledWith(expect.objectContaining({ messageText: 'sealed-message', keyText: 'private-key', passphrase: 'key-password' }))
    expect(screen.getByTestId('decrypt-key')).toHaveValue('')
    expect(screen.getByLabelText('Key file passphrase')).toHaveValue('')
    fireEvent.click(screen.getByRole('button', { name: 'Clear recovered message' }))
    expect(screen.queryByTestId('decrypt-plaintext')).toBeNull()
  })
  it('does not display an unexpected exception that could contain input data', async () => {
    vi.mocked(runDecrypt).mockRejectedValueOnce(new Error('PRIVATE-INPUT'))
    render(<LocalDecryptPage />)
    fireEvent.click(screen.getByTestId('decrypt-submit'))
    await waitFor(() => expect(screen.getByTestId('decrypt-error')).not.toHaveTextContent('PRIVATE-INPUT'))
  })
})
