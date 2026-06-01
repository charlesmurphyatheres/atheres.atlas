import type { BulkStatusResult } from '../../services/apiService'

/**
 * Builds a one-line summary of a bulk status update so silent server-side
 * rewrites (stale rows demoted to RouteOmitted, ungeocoded rows dropped
 * from the route batch, no-active-hub) are surfaced to the operator.
 *
 * Returns null when the response is fully successful with nothing notable.
 */
export function summarizeBulkStatusResult(
  result: BulkStatusResult,
  intendedStatus: string,
): string | null {
  const parts: string[] = []

  if (result.routeOmitted && result.routeOmitted > 0) {
    parts.push(
      `${result.routeOmitted} order${result.routeOmitted === 1 ? '' : 's'} marked Route Omitted (more than a day old).`,
    )
  }

  if (intendedStatus === 'Scheduled') {
    // Bullet up the geocode side-effects first — operators are often
    // surprised that "scheduling" also resolves addresses with Google,
    // so making it visible avoids "why was this slower than usual?".
    const geo: string[] = []
    if (result.storesGeocoded && result.storesGeocoded > 0)
      geo.push(`${result.storesGeocoded} store${result.storesGeocoded === 1 ? '' : 's'}`)
    if (result.hubsGeocoded && result.hubsGeocoded > 0)
      geo.push(`${result.hubsGeocoded} hub${result.hubsGeocoded === 1 ? '' : 's'}`)
    if (result.warehousesGeocoded && result.warehousesGeocoded > 0)
      geo.push(`${result.warehousesGeocoded} warehouse${result.warehousesGeocoded === 1 ? '' : 's'}`)
    if (geo.length > 0) parts.push(`Geocoded ${geo.join(', ')} via Google Maps.`)

    if (result.geocodeFailures && result.geocodeFailures > 0) {
      parts.push(
        `${result.geocodeFailures} address${result.geocodeFailures === 1 ? '' : 'es'} couldn't be geocoded — check the underlying address fields.`,
      )
    }

    if (result.routesQueued && result.routesQueued > 0) {
      parts.push(`Queued ${result.routesQueued} optimization run${result.routesQueued === 1 ? '' : 's'}.`)
    }
    if (result.ordersUngeocoded && result.ordersUngeocoded > 0) {
      parts.push(
        `${result.ordersUngeocoded} order${result.ordersUngeocoded === 1 ? '' : 's'} skipped — no geocode could be resolved.`,
      )
    }
    if (result.noActiveHub) {
      parts.push('No active hub configured for this company — nothing was routed.')
    }
    if (
      (!result.routesQueued || result.routesQueued === 0) &&
      result.updated > (result.routeOmitted ?? 0) &&
      !result.ordersUngeocoded &&
      !result.noActiveHub
    ) {
      // Updated rows existed but no route went out and we have no obvious
      // reason. Almost certainly the optimizer pipeline silently dropped
      // them; tell the operator to check the host log.
      parts.push('No optimization run was queued — check the Functions log for details.')
    }
  }

  return parts.length > 0 ? parts.join(' ') : null
}
