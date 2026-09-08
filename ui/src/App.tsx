import { useEffect, useState } from 'react'
import { parseIntegrationRuns, type IntegrationRun } from './integrationRuns'
import './App.css'

type RequestState = 'loading' | 'success' | 'error'

const dateFormatter = new Intl.DateTimeFormat('en-GB', {
  day: '2-digit',
  month: 'short',
  hour: '2-digit',
  minute: '2-digit',
  timeZone: 'UTC',
  timeZoneName: 'short',
})

function App() {
  const [runs, setRuns] = useState<IntegrationRun[]>([])
  const [requestState, setRequestState] = useState<RequestState>('loading')
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()

    function showError(message: string) {
      if (!controller.signal.aborted) {
        setError(message)
        setRequestState('error')
      }
    }

    async function loadRuns() {
      try {
        const response = await fetch('/api/integration-runs', {
          signal: controller.signal,
        })

        if (!response.ok) {
          showError(`API returned ${response.status}`)
          return
        }

        let parsedRuns: IntegrationRun[]
        try {
          parsedRuns = parseIntegrationRuns(await response.json())
        } catch {
          showError('API returned an invalid integration-run payload')
          return
        }

        if (!controller.signal.aborted) {
          setRuns(parsedRuns)
          setRequestState('success')
        }
      } catch {
        showError('Unable to connect to the API')
      }
    }

    loadRuns()

    return () => controller.abort()
  }, [])

  const failedCount = runs.filter((run) => run.status === 'Failed').length
  const pendingCount = runs.filter((run) => run.status === 'Pending').length
  const processedRecords = runs.reduce((total, run) => total + run.recordCount, 0)

  return (
    <main>
      <header className="masthead">
        <div>
          <p className="eyebrow">Integration Ops</p>
          <h1>Integration run overview</h1>
          <p className="intro">
            A snapshot of synthetic integration runs. Reload to see updated status.
          </p>
        </div>
        <div className="environment">
          <span className="environment-dot" aria-hidden="true" />
          Local synthetic data
        </div>
      </header>

      <section className="metrics" aria-label="Run summary">
        <article>
          <span>Runs in view</span>
          <strong>{requestState === 'success' ? runs.length : '-'}</strong>
        </article>
        <article>
          <span>Pending</span>
          <strong>{requestState === 'success' ? pendingCount : '-'}</strong>
        </article>
        <article className={failedCount ? 'metric-alert' : ''}>
          <span>Needs attention</span>
          <strong>{requestState === 'success' ? failedCount : '-'}</strong>
        </article>
        <article>
          <span>Records processed</span>
          <strong>{requestState === 'success' ? processedRecords : '-'}</strong>
        </article>
      </section>

      <section className="run-panel">
        <div className="panel-heading">
          <div>
            <p className="eyebrow">Run snapshot</p>
            <h2 id="integration-runs-heading">Integration runs</h2>
          </div>
          <span className="refresh-note">API snapshot</span>
        </div>

        {requestState === 'loading' && (
          <p className="notice" role="status" aria-live="polite">
            Loading integration runs...
          </p>
        )}
        {requestState === 'error' && error && (
          <p className="notice notice-error" role="alert">
            Integration data is unavailable: {error}
          </p>
        )}
        {requestState === 'success' && runs.length === 0 && (
          <p className="notice" role="status">
            No integration runs are currently available.
          </p>
        )}

        {requestState === 'success' && runs.length > 0 && (
          <div className="table-scroll">
            <table aria-labelledby="integration-runs-heading">
              <thead>
                <tr>
                  <th scope="col">Partner</th>
                  <th scope="col">Operation</th>
                  <th scope="col">Status</th>
                  <th scope="col">Received</th>
                  <th scope="col">Attempts</th>
                  <th scope="col">Records</th>
                </tr>
              </thead>
              <tbody>
                {runs.map((run) => (
                  <tr key={run.id}>
                    <td data-label="Partner" className="partner-cell">
                      {run.partner}
                      {run.lastError && <small>{run.lastError}</small>}
                    </td>
                    <td data-label="Operation">{run.operation}</td>
                    <td data-label="Status">
                      <span className={`status status-${run.status.toLowerCase()}`}>
                        {run.status}
                      </span>
                    </td>
                    <td data-label="Received">{dateFormatter.format(new Date(run.receivedAt))}</td>
                    <td data-label="Attempts">{run.attemptCount}</td>
                    <td data-label="Records">{run.recordCount}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </main>
  )
}

export default App
