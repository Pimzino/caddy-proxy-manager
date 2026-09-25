// Traffic statistics of one server (shared by the Traffic page and the server detail page): filter row
// (range, host), KPI tiles, time series, status classes, top hosts/clients and status codes.
import { useState, type KeyboardEvent, type ReactNode } from 'react';
import { Link } from 'react-router';
import { BarChart3, Info, Settings } from 'lucide-react';
import { errorMessage } from '@/api/client';
import { useTraffic } from '@/api/hooks';
import type { TrafficRange, TrafficReport } from '@/api/types';
import {
  ChartCard,
  ProportionBar,
  seriesTable,
  StackedColumnChart,
  StatTile,
  TimeSeriesChart,
  formatBytesShort,
  formatCount,
  formatMs,
  formatRatio,
  type ChartSeries,
} from '@/components/charts';
import { Button, Callout, Card, CardHeader, EmptyState, Select, Skeleton, Table, TBody, TD, TH, THead, TR } from '@/components/ui';
import { cn } from '@/lib/cn';
import { formatBytes, formatDateTime, formatNumber, formatRelative } from '@/lib/format';
import { useNow } from '@/lib/useNow';
import { formatBucket } from './shared';

export const RANGE_OPTIONS: { value: TrafficRange; label: string; long: string }[] = [
  { value: 'hour', label: '1 h', long: 'the last hour' },
  { value: 'day', label: '24 h', long: 'the last 24 hours' },
  { value: 'week', label: '7 d', long: 'the last 7 days' },
  { value: 'month', label: '30 d', long: 'the last 30 days' },
];

export function isTrafficRange(v: string | null): v is TrafficRange {
  return v === 'hour' || v === 'day' || v === 'week' || v === 'month';
}

/** Segmented control (radio group) for the time range. */
export function RangeSelector({ value, onChange }: { value: TrafficRange; onChange: (r: TrafficRange) => void }) {
  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const i = RANGE_OPTIONS.findIndex((o) => o.value === value);
    let next = -1;
    if (e.key === 'ArrowRight' || e.key === 'ArrowDown') next = (i + 1) % RANGE_OPTIONS.length;
    else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') next = (i - 1 + RANGE_OPTIONS.length) % RANGE_OPTIONS.length;
    if (next < 0) return;
    e.preventDefault();
    onChange(RANGE_OPTIONS[next].value);
    (e.currentTarget.querySelectorAll('button')[next] as HTMLButtonElement | undefined)?.focus();
  };
  return (
    <div role="radiogroup" aria-label="Time range" onKeyDown={onKeyDown} className="inline-flex h-8 items-center rounded-md border border-border-strong bg-surface p-0.5 shadow-xs">
      {RANGE_OPTIONS.map((o) => {
        const selected = o.value === value;
        return (
          <button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={selected}
            tabIndex={selected ? 0 : -1}
            onClick={() => onChange(o.value)}
            className={cn(
              'h-full rounded px-2.5 text-sm font-medium tabular-nums transition-colors focus-visible:outline-2 focus-visible:outline-ring',
              selected ? 'bg-accent-soft text-accent-text' : 'text-fg-muted hover:bg-surface-2 hover:text-fg',
            )}
          >
            {o.label}
          </button>
        );
      })}
    </div>
  );
}

const S_OK = 'var(--viz-1)';
const S_3XX = 'var(--viz-3)';
const S_4XX = 'var(--viz-4)';
const S_5XX = 'var(--viz-5)';
const S_OTHER = 'var(--viz-other)';

const REASONS: Record<number, string> = {
  0: 'Aborted by the client',
  200: 'OK',
  201: 'Created',
  204: 'No Content',
  206: 'Partial Content',
  301: 'Moved Permanently',
  302: 'Found',
  303: 'See Other',
  304: 'Not Modified',
  307: 'Temporary Redirect',
  308: 'Permanent Redirect',
  400: 'Bad Request',
  401: 'Unauthorized',
  403: 'Forbidden',
  404: 'Not Found',
  405: 'Method Not Allowed',
  408: 'Request Timeout',
  410: 'Gone',
  413: 'Content Too Large',
  429: 'Too Many Requests',
  499: 'Client Closed Request',
  500: 'Internal Server Error',
  502: 'Bad Gateway',
  503: 'Service Unavailable',
  504: 'Gateway Timeout',
};

function statusClass(code: number): { label: string; color: string } {
  if (code >= 200 && code < 300) return { label: '2xx', color: S_OK };
  if (code >= 300 && code < 400) return { label: '3xx', color: S_3XX };
  if (code >= 400 && code < 500) return { label: '4xx', color: S_4XX };
  if (code >= 500 && code < 600) return { label: '5xx', color: S_5XX };
  return { label: 'Other', color: S_OTHER };
}

export function TrafficSection({
  serverId,
  range,
  host,
  onRangeChange,
  onHostChange,
  leading,
}: {
  serverId: string;
  range: TrafficRange;
  host: string;
  onRangeChange: (r: TrafficRange) => void;
  onHostChange: (host: string) => void;
  /** Extra filter placed first in the filter row (e.g. the server selector). */
  leading?: ReactNode;
}) {
  const now = useNow(15_000);
  const report = useTraffic(serverId, range, host || undefined);
  // Host options come from the unfiltered report (the same query when no host is selected).
  const all = useTraffic(serverId, range);
  const hostOptions = (all.data?.topHosts ?? []).map((h) => h.host);
  if (host && !hostOptions.includes(host)) hostOptions.unshift(host);
  const d = report.data;

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-2">
        {leading}
        <RangeSelector value={range} onChange={onRangeChange} />
        <div className="w-64 max-w-full">
          <Select aria-label="Host" value={host} onChange={(e) => onHostChange(e.target.value)} mono={!!host}>
            <option value="">All hosts</option>
            {hostOptions.map((h) => (
              <option key={h} value={h}>
                {h}
              </option>
            ))}
          </Select>
        </div>
        {d?.enabled && d.lastIngestAt && (
          <span className="ml-auto text-xs text-fg-subtle" title={formatDateTime(d.lastIngestAt)}>
            Updated {formatRelative(d.lastIngestAt, now)}
          </span>
        )}
      </div>

      {report.isPending ? (
        <TrafficSkeleton />
      ) : report.isError && !d ? (
        <Callout tone="danger" title="Could not load traffic statistics">
          {errorMessage(report.error)}
        </Callout>
      ) : d && !d.enabled ? (
        <Card>
          <EmptyState
            icon={<BarChart3 size={18} />}
            title="Traffic statistics are disabled"
            description="Caddy does not write the statistics log on this server, so no requests are counted. Turn on “Traffic statistics” in the Caddy settings to start collecting."
            action={
              <Link
                to="/settings"
                className="inline-flex h-8 items-center gap-1.5 rounded-md border border-border-strong bg-surface px-3 text-sm font-medium text-fg shadow-xs hover:bg-surface-2 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring"
              >
                <Settings size={14} aria-hidden />
                Open Caddy settings
              </Link>
            }
          />
        </Card>
      ) : d ? (
        <TrafficBody d={d} range={range} dimmed={report.isPlaceholderData} onHostChange={onHostChange} host={host} now={now} />
      ) : null}
      {report.isError && d && (
        <p className="text-xs text-warning" role="status">
          Showing the last loaded statistics — refreshing failed: {errorMessage(report.error)}
        </p>
      )}
    </div>
  );
}

function TrafficSkeleton() {
  return (
    <div className="flex flex-col gap-4" aria-busy="true" aria-label="Loading traffic statistics">
      <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
        {Array.from({ length: 6 }, (_, i) => (
          <Card key={i} className="p-4">
            <Skeleton className="mb-2 h-3 w-20" />
            <Skeleton className="h-6 w-16" />
          </Card>
        ))}
      </div>
      <Card className="p-4">
        <Skeleton className="h-48 w-full" />
      </Card>
    </div>
  );
}

function TrafficBody({
  d,
  range,
  dimmed,
  host,
  onHostChange,
  now,
}: {
  d: TrafficReport;
  range: TrafficRange;
  dimmed: boolean;
  host: string;
  onHostChange: (host: string) => void;
  now: number;
}) {
  const [hover, setHover] = useState<number | null>(null);
  const x = d.series.map((p) => Date.parse(p.at));
  const fmtX = (ms: number) => formatBucket(ms, d.bucketSize);
  const t = d.totals;
  const errors = t.status4xx + t.status5xx;
  const rangeText = RANGE_OPTIONS.find((o) => o.value === range)?.long ?? '';
  const scope = host ? ` for ${host}` : '';
  const perBucket = d.bucketSize === 'minute' ? 'per minute' : d.bucketSize === 'hour' ? 'per hour' : 'per day';
  const xDomain: [number, number] = [Date.parse(d.from), Date.parse(d.to)];

  const requests: ChartSeries = { id: 'requests', label: 'Requests', color: 'var(--viz-1)', values: d.series.map((p) => p.requests) };
  const clients: ChartSeries = { id: 'clients', label: 'Unique clients', color: 'var(--viz-1)', values: d.series.map((p) => p.uniqueClients) };
  const bytes: ChartSeries[] = [
    { id: 'in', label: 'Data in', color: 'var(--viz-1)', values: d.series.map((p) => p.bytesIn) },
    { id: 'out', label: 'Data out', color: 'var(--viz-2)', values: d.series.map((p) => p.bytesOut) },
  ];
  const classes: ChartSeries[] = [
    { id: 'ok', label: '1xx–3xx and other', color: S_OK, values: d.series.map((p) => Math.max(0, p.requests - p.status4xx - p.status5xx)) },
    { id: '4xx', label: '4xx client errors', color: S_4XX, values: d.series.map((p) => p.status4xx) },
    { id: '5xx', label: '5xx server errors', color: S_5XX, values: d.series.map((p) => p.status5xx) },
  ];
  const errorRate = d.series.map((p) => (p.requests ? ((p.status4xx + p.status5xx) / p.requests) * 100 : 0));

  const maxHostRequests = Math.max(1, ...d.topHosts.map((h) => h.requests));
  const maxClientRequests = Math.max(1, ...d.topClients.map((c) => c.requests));
  const codesTotal = d.statusCodes.reduce((a, c) => a + c.count, 0) || 1;

  return (
    <div className={cn('flex flex-col gap-4 transition-opacity', dimmed && 'opacity-60')}>
      <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
        <StatTile
          label="Requests"
          value={<span title={formatNumber(t.requests)}>{formatCount(t.requests)}</span>}
          detail={rangeText}
          trend={{ values: requests.values as number[], x, formatValue: (v) => `${formatCount(v)} requests`, formatX: fmtX }}
        />
        <StatTile
          label="Unique clients"
          value={<span title={formatNumber(t.uniqueClients)}>{formatCount(t.uniqueClients)}</span>}
          detail="distinct client IPs"
          trend={{ values: clients.values as number[], x, formatValue: (v) => `${formatCount(v)} clients`, formatX: fmtX }}
        />
        <StatTile
          label="Data in"
          value={formatBytesShort(t.bytesIn)}
          detail="request bodies"
          trend={{ values: bytes[0].values as number[], x, formatValue: formatBytesShort, formatX: fmtX }}
        />
        <StatTile
          label="Data out"
          value={formatBytesShort(t.bytesOut)}
          detail="response bodies"
          trend={{ values: bytes[1].values as number[], x, formatValue: formatBytesShort, formatX: fmtX }}
        />
        <StatTile
          label="Error rate"
          value={t.requests ? formatRatio(errors / t.requests) : '—'}
          detail={`4xx ${t.requests ? formatRatio(t.status4xx / t.requests) : '—'} · 5xx ${t.requests ? formatRatio(t.status5xx / t.requests) : '—'}`}
          trend={{ values: errorRate, x, formatValue: (v) => `${v.toFixed(v < 1 ? 2 : 1)}% errors`, formatX: fmtX }}
        />
        <StatTile label="Avg. duration" value={t.requests ? formatMs(t.avgDurationMs) : '—'} detail="time to last response byte" />
      </div>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        <ChartCard
          title="Requests"
          description={`${perBucket}${scope}`}
          table={seriesTable(x, [requests], fmtX, formatNumber, 'Period')}
        >
          <TimeSeriesChart
            x={x}
            series={[requests]}
            xDomain={xDomain}
            formatValue={formatCount}
            formatX={fmtX}
            ariaLabel={`Requests ${perBucket} over ${rangeText}${scope}`}
            hoverIndex={hover}
            onHoverIndex={setHover}
            emptyText="No requests in this period"
          />
        </ChartCard>
        <ChartCard
          title="Unique clients"
          description={`distinct client IPs ${perBucket}${scope}`}
          table={seriesTable(x, [clients], fmtX, formatNumber, 'Period')}
        >
          <TimeSeriesChart
            x={x}
            series={[clients]}
            xDomain={xDomain}
            formatValue={formatCount}
            formatX={fmtX}
            ariaLabel={`Unique clients ${perBucket} over ${rangeText}${scope}`}
            hoverIndex={hover}
            onHoverIndex={setHover}
            emptyText="No clients in this period"
          />
        </ChartCard>
        <ChartCard
          title="Data transferred"
          description={`request and response bodies ${perBucket}${scope}`}
          legend={bytes.map((s, i) => ({ label: s.label, color: s.color, value: formatBytesShort(i === 0 ? t.bytesIn : t.bytesOut) }))}
          table={seriesTable(x, bytes, fmtX, formatBytes, 'Period')}
        >
          <TimeSeriesChart
            x={x}
            series={bytes}
            xDomain={xDomain}
            bytes
            formatValue={formatBytesShort}
            formatX={fmtX}
            ariaLabel={`Data in and out ${perBucket} over ${rangeText}${scope}`}
            hoverIndex={hover}
            onHoverIndex={setHover}
            emptyText="No data transferred in this period"
          />
        </ChartCard>
        <ChartCard
          title="Responses by status class"
          description={`${perBucket}${scope}`}
          legend={classes.map((s) => ({ label: s.label, color: s.color, kind: 'swatch' as const }))}
          table={seriesTable(x, classes, fmtX, formatNumber, 'Period')}
        >
          <StackedColumnChart
            x={x}
            series={classes}
            formatValue={formatCount}
            formatX={fmtX}
            ariaLabel={`Responses by status class ${perBucket} over ${rangeText}${scope}`}
            totalLabel="requests"
            emptyText="No requests in this period"
          />
          <div className="mt-3 border-t border-border pt-3">
            <ProportionBar
              ariaLabel={`Share of responses by status class over ${rangeText}`}
              items={[
                { id: '2xx', label: '2xx', value: t.status2xx, color: S_OK },
                { id: '3xx', label: '3xx', value: t.status3xx, color: S_3XX },
                { id: '4xx', label: '4xx', value: t.status4xx, color: S_4XX },
                { id: '5xx', label: '5xx', value: t.status5xx, color: S_5XX },
                { id: 'other', label: 'Other (aborted, 1xx)', value: t.statusOther, color: S_OTHER },
              ]}
            />
          </div>
        </ChartCard>
      </div>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
        <Card className="min-w-0">
          <CardHeader
            title="Top hosts"
            description={host ? 'Filtered to one host — choose “All hosts” to compare.' : 'By requests. Select a host to filter every chart.'}
            actions={
              host ? (
                <Button size="sm" variant="ghost" onClick={() => onHostChange('')}>
                  Show all hosts
                </Button>
              ) : undefined
            }
          />
          {d.topHosts.length === 0 ? (
            <EmptyState title="No requests" description="No site received requests in this period." className="py-8" />
          ) : (
            <Table>
              <THead>
                <tr>
                  <TH>Host</TH>
                  <TH className="text-right">Requests</TH>
                  <TH className="text-right">Clients</TH>
                  <TH className="text-right">In</TH>
                  <TH className="text-right">Out</TH>
                  <TH className="text-right">4xx</TH>
                  <TH className="text-right">5xx</TH>
                </tr>
              </THead>
              <TBody>
                {d.topHosts.map((h) => (
                  <TR key={h.host}>
                    <TD className="max-w-64">
                      <button
                        type="button"
                        onClick={() => onHostChange(h.host === host ? '' : h.host)}
                        className="mono block max-w-full truncate text-left text-accent-text hover:underline focus-visible:outline-2 focus-visible:outline-ring"
                        title={h.host === host ? 'Show all hosts' : `Show only ${h.host}`}
                      >
                        {h.host}
                      </button>
                    </TD>
                    <TD className="text-right tabular-nums">
                      <ShareCell value={h.requests} max={maxHostRequests} text={formatNumber(h.requests)} />
                    </TD>
                    <TD className="text-right tabular-nums">{formatNumber(h.uniqueClients)}</TD>
                    <TD className="text-right whitespace-nowrap tabular-nums">{formatBytesShort(h.bytesIn)}</TD>
                    <TD className="text-right whitespace-nowrap tabular-nums">{formatBytesShort(h.bytesOut)}</TD>
                    <TD className="text-right tabular-nums">
                      <ErrorCount count={h.status4xx} total={h.requests} />
                    </TD>
                    <TD className="text-right tabular-nums">
                      <ErrorCount count={h.status5xx} total={h.requests} severe />
                    </TD>
                  </TR>
                ))}
              </TBody>
            </Table>
          )}
        </Card>

        <Card className="min-w-0">
          <CardHeader title="Top clients" description={`By requests${scope}`} />
          {d.topClients.length === 0 ? (
            <EmptyState title="No clients" description="No client addresses were seen in this period." className="py-8" />
          ) : (
            <Table>
              <THead>
                <tr>
                  <TH>Client IP</TH>
                  <TH className="text-right">Requests</TH>
                  <TH className="text-right">Out</TH>
                  <TH className="text-right">Last seen</TH>
                </tr>
              </THead>
              <TBody>
                {d.topClients.map((c) => (
                  <TR key={c.ip}>
                    <TD className="mono max-w-48 truncate" title={c.ip}>
                      {c.ip}
                    </TD>
                    <TD className="text-right tabular-nums">
                      <ShareCell value={c.requests} max={maxClientRequests} text={formatNumber(c.requests)} />
                    </TD>
                    <TD className="text-right whitespace-nowrap tabular-nums">{formatBytesShort(c.bytesOut)}</TD>
                    <TD className="text-right whitespace-nowrap text-fg-muted" title={formatDateTime(c.lastSeen)}>
                      {formatRelative(c.lastSeen, now)}
                    </TD>
                  </TR>
                ))}
              </TBody>
            </Table>
          )}
        </Card>
      </div>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
        <Card className="min-w-0">
          <CardHeader title="Status codes" description={`All responses${scope} over ${rangeText}`} />
          {d.statusCodes.length === 0 ? (
            <EmptyState title="No responses" className="py-8" />
          ) : (
            <ul className="divide-y divide-border">
              {d.statusCodes.map((c) => {
                const cls = statusClass(c.code);
                const pct = (c.count / codesTotal) * 100;
                return (
                  <li key={c.code} className="grid grid-cols-[3.5rem_minmax(0,1fr)_minmax(80px,30%)_5rem] items-center gap-3 px-4 py-1.5 text-sm">
                    <span className="mono font-medium text-fg">{c.code === 0 ? '—' : c.code}</span>
                    <span className="flex min-w-0 items-center gap-1.5 text-fg-muted">
                      <span aria-hidden className="inline-block h-2.5 w-2.5 shrink-0 rounded-[3px]" style={{ background: cls.color }} />
                      <span className="truncate">
                        {REASONS[c.code] ?? cls.label}
                        <span className="sr-only"> ({cls.label})</span>
                      </span>
                    </span>
                    <span className="h-1.5 overflow-hidden rounded-full bg-surface-2" aria-hidden>
                      <span className="block h-full rounded-full" style={{ width: `${Math.max(pct, 0.5)}%`, background: cls.color }} />
                    </span>
                    <span className="text-right tabular-nums text-fg" title={`${pct.toFixed(2)}%`}>
                      {formatNumber(c.count)}
                    </span>
                  </li>
                );
              })}
            </ul>
          )}
        </Card>
        {d.notes.length > 0 && (
          <Card className="min-w-0 self-start">
            <CardHeader title="About these numbers" icon={<Info size={15} />} />
            <ul className="list-disc space-y-1.5 py-3 pr-4 pl-8 text-sm text-fg-muted">
              {d.notes.map((n, i) => (
                <li key={i}>{n}</li>
              ))}
            </ul>
          </Card>
        )}
      </div>
    </div>
  );
}

/** Number with a thin bar under it showing its share of the largest row (the text carries the value). */
function ShareCell({ value, max, text }: { value: number; max: number; text: string }) {
  return (
    <span className="inline-flex min-w-20 flex-col items-end gap-1">
      <span>{text}</span>
      <span className="h-1 w-full overflow-hidden rounded-full bg-surface-2" aria-hidden>
        <span className="block h-full rounded-full" style={{ width: `${(value / max) * 100}%`, background: 'var(--viz-1)' }} />
      </span>
    </span>
  );
}

function ErrorCount({ count, total, severe }: { count: number; total: number; severe?: boolean }) {
  if (!count) return <span className="text-fg-subtle">0</span>;
  const r = total ? count / total : 0;
  const high = severe ? r >= 0.01 : r >= 0.05;
  return (
    <span className={high ? 'font-medium text-danger' : 'text-fg'} title={`${formatRatio(r)} of requests`}>
      {formatNumber(count)}
    </span>
  );
}
