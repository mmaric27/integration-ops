import { describe, expect, it } from 'vitest'
import { parseIntegrationRuns } from './integrationRuns'

const validRun = {
  id: '00000000-0000-0000-0000-000000000000',
  partner: 'Example Partner',
  operation: 'Example operation',
  status: 'Pending',
  attemptCount: 1,
  recordCount: 0,
  receivedAt: '2026-08-20T08:04:09Z',
  completedAt: null,
  lastError: null,
}

describe('parseIntegrationRuns', () => {
  it('accepts canonical .NET GUID values', () => {
    expect(parseIntegrationRuns([validRun])).toEqual([validRun])
  })

  it('rejects an unknown status', () => {
    expect(() => parseIntegrationRuns([{ ...validRun, status: 'Unknown' }])).toThrow(
      'API returned an invalid integration-run payload',
    )
  })

  it('rejects invalid timestamps', () => {
    expect(() => parseIntegrationRuns([{ ...validRun, receivedAt: 'not-a-date' }])).toThrow(
      'API returned an invalid integration-run payload',
    )
    expect(() =>
      parseIntegrationRuns([{ ...validRun, receivedAt: '2026-02-30T08:04:09Z' }]),
    ).toThrow('API returned an invalid integration-run payload')
  })

  it.each([
    '0001-01-01T00:00:00Z',
    '0001-01-01T14:00:00+14:00',
    '0004-02-29T00:00:00Z',
    '2000-02-29T00:00:00Z',
    '2024-02-29T08:04:09.1234567Z',
    '9999-12-31T23:59:59.9999999Z',
    '9999-12-31T09:59:59.9999999-14:00',
  ])('accepts a supported .NET timestamp: %s', (timestamp) => {
    const run = { ...validRun, receivedAt: timestamp, completedAt: timestamp }
    expect(parseIntegrationRuns([run])).toEqual([run])
  })

  it.each([
    '0000-01-01T00:00:00Z',
    '0000-12-31T23:00:00-01:00',
    '0001-01-01T00:00:00+14:00',
    '9999-12-31T23:59:59-14:00',
    '2026-08-20T08:04:09+14:01',
    '1900-02-29T00:00:00Z',
  ])('rejects an unsupported .NET timestamp in either field: %s', (timestamp) => {
    expect(() => parseIntegrationRuns([{ ...validRun, receivedAt: timestamp }])).toThrow(
      'API returned an invalid integration-run payload',
    )
    expect(() => parseIntegrationRuns([{ ...validRun, completedAt: timestamp }])).toThrow(
      'API returned an invalid integration-run payload',
    )
  })

  it('accepts an unprocessed Pending run with zero attempts', () => {
    const run = { ...validRun, attemptCount: 0 }
    expect(parseIntegrationRuns([run])).toEqual([run])
  })

  it.each([-1, 0.5, 2_147_483_648])('rejects invalid attempt count %s', (attemptCount) => {
    expect(() => parseIntegrationRuns([{ ...validRun, attemptCount }])).toThrow()
  })

  it('rejects invalid counters', () => {
    expect(() => parseIntegrationRuns([{ ...validRun, attemptCount: -1 }])).toThrow(
      'API returned an invalid integration-run payload',
    )
    expect(() => parseIntegrationRuns([{ ...validRun, recordCount: -1 }])).toThrow(
      'API returned an invalid integration-run payload',
    )
    expect(() =>
      parseIntegrationRuns([{ ...validRun, recordCount: 2_147_483_648 }]),
    ).toThrow('API returned an invalid integration-run payload')
  })
})
