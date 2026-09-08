import { cleanup, render, screen, within } from '@testing-library/react'
import { StrictMode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from './App'

const validRun = {
  id: '2b31a678-fcb8-4f8b-83d0-b22eaafc9fc7',
  partner: 'Orion Booking',
  operation: 'Reservation export',
  status: 'Succeeded',
  attemptCount: 1,
  recordCount: 184,
  receivedAt: '2026-08-20T07:42:18Z',
  completedAt: '2026-08-20T07:42:51Z',
  lastError: null,
}

const fetchMock = vi.fn<typeof fetch>()

describe('App', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
    vi.unstubAllGlobals()
  })

  it('announces the loading state', () => {
    fetchMock.mockReturnValue(new Promise<Response>(() => undefined))

    render(<App />)

    expect(screen.getByRole('status')).toHaveTextContent('Loading integration runs...')
  })

  it('renders a valid integration-run response', async () => {
    fetchMock.mockResolvedValue(apiResponse([validRun]))

    render(<App />)

    const table = await screen.findByRole('table', { name: 'Integration runs' })
    expect(table).toBeVisible()
    expect(screen.getByRole('heading', { level: 1, name: 'Integration run overview' })).toBeVisible()
    expect(screen.getByText('A snapshot of synthetic integration runs. Reload to see updated status.')).toBeVisible()
    expect(screen.getByText('Orion Booking')).toBeVisible()
    expect(screen.getByText('Succeeded')).toBeVisible()
    expect(within(table).getByText('184')).toBeVisible()
  })

  it('shows an accepted zero-attempt run after remount', async () => {
    fetchMock.mockResolvedValueOnce(apiResponse([]))
    const first = render(<App />)
    await screen.findByText('No integration runs are currently available.')
    first.unmount()
    fetchMock.mockResolvedValueOnce(apiResponse([{ ...validRun, status: 'Pending', attemptCount: 0, recordCount: 0, completedAt: null }]))
    render(<App />)
    const table = await screen.findByRole('table', { name: 'Integration runs' })
    const row = within(table).getByText('Orion Booking').closest('tr')!
    expect(within(row).getByText('Pending')).toBeVisible()
    expect(within(row).getAllByText('0')).toHaveLength(2)
  })

  it('shows processing progress only after a new snapshot is mounted', async () => {
    fetchMock.mockResolvedValueOnce(apiResponse([{ ...validRun, status: 'Pending', attemptCount: 0, recordCount: 0, completedAt: null }]))
    const first = render(<App />)
    await screen.findByText('Pending')
    first.unmount()
    fetchMock.mockResolvedValueOnce(apiResponse([{ ...validRun, attemptCount: 2 }]))
    render(<App />)
    const table = await screen.findByRole('table', { name: 'Integration runs' })
    const row = within(table).getByText('Orion Booking').closest('tr')!
    expect(within(row).getByText('Succeeded')).toBeVisible()
    expect(within(row).getByText('2')).toBeVisible()
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('renders zero metrics and a completed empty state', async () => {
    fetchMock.mockResolvedValue(apiResponse([]))

    render(<App />)

    const emptyState = await screen.findByText('No integration runs are currently available.')
    expect(emptyState).toHaveAttribute('role', 'status')
    const runsMetric = screen.getByText('Runs in view').closest('article')
    expect(runsMetric).not.toBeNull()
    expect(within(runsMetric as HTMLElement).getByText('0')).toBeVisible()
  })

  it('renders an alert for an unsuccessful response', async () => {
    fetchMock.mockResolvedValue(apiResponse(null, 500))

    render(<App />)

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Integration data is unavailable: API returned 500',
    )
  })

  it('rejects a malformed successful response', async () => {
    fetchMock.mockResolvedValue(apiResponse([{ ...validRun, status: 'Unknown' }]))

    render(<App />)

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Integration data is unavailable: API returned an invalid integration-run payload',
    )
  })

  it('does not expose invalid JSON body or parser details', async () => {
    fetchMock.mockResolvedValue(
      new Response('INTERNAL_SAMPLE_DIAGNOSTIC: invalid JSON', {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    )

    render(<App />)

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Integration data is unavailable: API returned an invalid integration-run payload',
    )
    expect(alert).not.toHaveTextContent('INTERNAL_SAMPLE_DIAGNOSTIC')
    expect(alert).not.toHaveTextContent('Unexpected token')
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
  })

  it.each([
    new Error('INTERNAL_SAMPLE_DIAGNOSTIC'),
    'INTERNAL_SAMPLE_DIAGNOSTIC',
    null,
    { detail: 'INTERNAL_SAMPLE_DIAGNOSTIC' },
  ])('projects a rejected fetch into a controlled failure: %j', async (reason) => {
    fetchMock.mockRejectedValue(reason)

    render(<App />)

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(
      'Integration data is unavailable: Unable to connect to the API',
    )
    expect(alert).not.toHaveTextContent('INTERNAL_SAMPLE_DIAGNOSTIC')
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
  })

  it('aborts the outstanding request when unmounted', () => {
    fetchMock.mockReturnValue(new Promise<Response>(() => undefined))

    const { unmount } = render(<App />)
    const signal = fetchMock.mock.calls[0][1]?.signal
    expect(signal).toBeInstanceOf(AbortSignal)
    expect(signal?.aborted).toBe(false)

    unmount()

    expect(signal?.aborted).toBe(true)
  })

  it('does not turn StrictMode cleanup cancellation into an operator error', async () => {
    fetchMock.mockImplementationOnce((_input, options) =>
      new Promise<Response>((_resolve, reject) => {
        options?.signal?.addEventListener('abort', () => reject('cancelled'), { once: true })
      }),
    )
    fetchMock.mockResolvedValueOnce(apiResponse([validRun]))

    render(<StrictMode><App /></StrictMode>)

    expect(await screen.findByRole('table', { name: 'Integration runs' })).toBeVisible()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})

function apiResponse(body: unknown, status = 200): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  } as Response
}
