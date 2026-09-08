export const integrationStatuses = ['Pending', 'Succeeded', 'Failed'] as const

export type IntegrationStatus = (typeof integrationStatuses)[number]

export type IntegrationRun = {
  id: string
  partner: string
  operation: string
  status: IntegrationStatus
  attemptCount: number
  recordCount: number
  receivedAt: string
  completedAt: string | null
  lastError: string | null
}

const minimumDotNetTimestamp = Date.parse('0001-01-01T00:00:00Z')
const maximumDotNetTimestamp = Date.parse('9999-12-31T23:59:59.999Z')

export function parseIntegrationRuns(value: unknown): IntegrationRun[] {
  if (!Array.isArray(value) || !value.every(isIntegrationRun)) {
    throw new Error('API returned an invalid integration-run payload')
  }

  return value
}

function isIntegrationRun(value: unknown): value is IntegrationRun {
  if (typeof value !== 'object' || value === null) {
    return false
  }

  const run = value as Record<string, unknown>

  return (
    typeof run.id === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(run.id) &&
    typeof run.partner === 'string' &&
    run.partner.length > 0 &&
    typeof run.operation === 'string' &&
    run.operation.length > 0 &&
    typeof run.status === 'string' &&
    integrationStatuses.includes(run.status as IntegrationStatus) &&
    Number.isInteger(run.attemptCount) &&
    Number(run.attemptCount) >= 0 &&
    Number(run.attemptCount) <= 2_147_483_647 &&
    Number.isInteger(run.recordCount) &&
    Number(run.recordCount) >= 0 &&
    Number(run.recordCount) <= 2_147_483_647 &&
    isDateTime(run.receivedAt) &&
    (run.completedAt === null || isDateTime(run.completedAt)) &&
    (run.lastError === null || typeof run.lastError === 'string')
  )
}

function isDateTime(value: unknown): value is string {
  if (typeof value !== 'string') {
    return false
  }

  const timestamp = Date.parse(value)
  if (
    !Number.isFinite(timestamp) ||
    timestamp < minimumDotNetTimestamp ||
    timestamp > maximumDotNetTimestamp
  ) {
    return false
  }

  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$/.exec(
    value,
  )

  if (!match) {
    return false
  }

  const [, yearText, monthText, dayText, hourText, minuteText, secondText, offset] = match
  const year = Number(yearText)
  const month = Number(monthText)
  const day = Number(dayText)
  const hour = Number(hourText)
  const minute = Number(minuteText)
  const second = Number(secondText)
  const maximumDay = new Date(Date.UTC(year, month, 0)).getUTCDate()

  if (
    year < 1 ||
    month < 1 ||
    month > 12 ||
    day < 1 ||
    day > maximumDay ||
    hour > 23 ||
    minute > 59 ||
    second > 59
  ) {
    return false
  }

  if (offset !== 'Z') {
    const offsetHour = Number(offset.slice(1, 3))
    const offsetMinute = Number(offset.slice(4, 6))
    if (offsetHour > 14 || offsetMinute > 59 || (offsetHour === 14 && offsetMinute !== 0)) {
      return false
    }
  }

  return true
}
