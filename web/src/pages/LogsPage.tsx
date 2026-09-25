import { useEffect, useId, useMemo, useRef, useState, type ReactNode } from 'react';
import { Download, Pause, Play, RefreshCw } from 'lucide-react';
import { errorMessage, saveBlob } from '@/api/client';
import { useAccessLog, useCaddyLog, useManagerLog } from '@/api/hooks';
import { useAuth } from '@/auth';
import { Button, Callout, Card, Checkbox, PageHeader, SearchInput, Segmented, Select, Spinner, TabPanel, Tabs } from '@/components/ui';
import { cn } from '@/lib/cn';
import { readStorage, writeStorage } from '@/lib/storage';
import { useDebounced } from '@/lib/useDebounced';

type LogTab = 'caddy' | 'access' | 'manager';
const LINE_OPTIONS = [100, 500, 1000, 2000, 5000];
const REFRESH_MS = 5000;

export default function LogsPage() {
  const idBase = useId();
  const { isAdmin } = useAuth();
  const [tab, setTab] = useState<LogTab>('caddy');
  return (
    <>
      <PageHeader title="Logs" description="Tail of the Caddy process log, per-host access logs and the manager’s own log." />
      <Tabs
        idBase={idBase}
        aria-label="Log source"
        value={tab}
        onChange={setTab}
        className="mb-4"
        items={[
          { value: 'caddy', label: 'Caddy' },
          { value: 'access', label: 'Access logs' },
          { value: 'manager', label: 'Manager', hidden: !isAdmin },
        ]}
      />
      <TabPanel idBase={idBase} value="caddy" active={tab === 'caddy'}>
        <ServerFilteredLog kind="caddy" />
      </TabPanel>
      <TabPanel idBase={idBase} value="access" active={tab === 'access'}>
        <AccessLogView />
      </TabPanel>
      <TabPanel idBase={idBase} value="manager" active={tab === 'manager' && isAdmin}>
        <ServerFilteredLog kind="manager" />
      </TabPanel>
    </>
  );
}

function useLogPrefs() {
  const [lines, setLines] = useState(() => Number(readStorage('cpm.logs.lines')) || 500);
  const [auto, setAuto] = useState(false);
  const [pretty, setPretty] = useState(() => readStorage('cpm.logs.pretty') !== 'raw');
  const [wrap, setWrap] = useState(() => readStorage('cpm.logs.wrap') === '1');
  return {
    lines,
    setLines: (n: number) => {
      setLines(n);
      writeStorage('cpm.logs.lines', String(n));
    },
    auto,
    setAuto,
    pretty,
    setPretty: (v: boolean) => {
      setPretty(v);
      writeStorage('cpm.logs.pretty', v ? null : 'raw');
    },
    wrap,
    setWrap: (v: boolean) => {
      setWrap(v);
      writeStorage('cpm.logs.wrap', v ? '1' : null);
    },
  };
}

function ServerFilteredLog({ kind }: { kind: 'caddy' | 'manager' }) {
  const prefs = useLogPrefs();
  const [q, setQ] = useState('');
  const debounced = useDebounced(q.trim(), 400);
  const params = { lines: prefs.lines, q: debounced };
  const caddy = useCaddyLog(params, prefs.auto && kind === 'caddy' ? REFRESH_MS : false);
  const manager = useManagerLog(params, prefs.auto && kind === 'manager' ? REFRESH_MS : false, kind === 'manager');
  const query = kind === 'caddy' ? caddy : manager;
  return (
    <LogFrame
      prefs={prefs}
      filter={<SearchInput value={q} onChange={setQ} placeholder="Filter (server-side)…" aria-label="Filter log lines" />}
      query={query}
      lines={query.data?.lines ?? []}
      file={query.data?.file}
      downloadName={`${kind}.log`}
      emptyText={debounced ? `No lines contain “${debounced}”.` : 'The log is empty.'}
    />
  );
}

function AccessLogView() {
  const prefs = useLogPrefs();
  const [host, setHost] = useState('');
  const [q, setQ] = useState('');
  // Host discovery: the response lists every host with an access log. With no host chosen yet,
  // both queries share the same key, so only one request is made.
  const probe = useAccessLog({ host: '', lines: prefs.lines }, false);
  const effectiveHost = host || probe.data?.hosts[0] || '';
  const query = useAccessLog({ host: effectiveHost, lines: prefs.lines }, prefs.auto ? REFRESH_MS : false);
  const hosts = query.data?.hosts ?? probe.data?.hosts ?? [];
  const needle = q.trim().toLowerCase();
  const lines = useMemo(() => {
    const all = query.data?.lines ?? [];
    return needle ? all.filter((l) => l.toLowerCase().includes(needle)) : all;
  }, [query.data, needle]);

  if (probe.isSuccess && hosts.length === 0 && !host)
    return (
      <Callout tone="info" title="No access logs yet">
        Enable “Access log” in a host’s Advanced tab. Requests are then written to <span className="mono">logs\access\&lt;domain&gt;.log</span>.
      </Callout>
    );

  return (
    <LogFrame
      prefs={prefs}
      filter={
        <>
          <Select aria-label="Host" value={effectiveHost} onChange={(e) => setHost(e.target.value)} mono className="w-64">
            {hosts.map((h) => (
              <option key={h} value={h}>
                {h}
              </option>
            ))}
          </Select>
          <SearchInput value={q} onChange={setQ} placeholder="Filter…" aria-label="Filter log lines" />
        </>
      }
      query={query}
      lines={lines}
      file={query.data?.file}
      downloadName={`access-${effectiveHost || 'log'}.log`}
      emptyText={needle ? `No lines contain “${q.trim()}”.` : 'No requests logged yet.'}
    />
  );
}

interface QueryLike {
  isPending: boolean;
  isFetching: boolean;
  isError: boolean;
  error: unknown;
  refetch: () => unknown;
}

function LogFrame({
  prefs,
  filter,
  query,
  lines,
  file,
  downloadName,
  emptyText,
}: {
  prefs: ReturnType<typeof useLogPrefs>;
  filter: ReactNode;
  query: QueryLike;
  lines: string[];
  file?: string;
  downloadName: string;
  emptyText: string;
}) {
  return (
    <Card>
      <div className="flex flex-wrap items-center gap-2 border-b border-border px-4 py-2.5">
        {filter}
        <Select aria-label="Number of lines" value={prefs.lines} onChange={(e) => prefs.setLines(Number(e.target.value))} className="w-32">
          {LINE_OPTIONS.map((n) => (
            <option key={n} value={n}>
              Last {n}
            </option>
          ))}
        </Select>
        <Segmented
          aria-label="Display"
          size="sm"
          value={prefs.pretty ? 'pretty' : 'raw'}
          onChange={(v) => prefs.setPretty(v === 'pretty')}
          options={[
            { value: 'pretty', label: 'Formatted' },
            { value: 'raw', label: 'Raw' },
          ]}
        />
        <Checkbox label="Wrap" checked={prefs.wrap} onChange={prefs.setWrap} />
        <div className="flex-1" />
        {query.isFetching && <Spinner size={14} label="Refreshing" />}
        <Button
          size="sm"
          variant={prefs.auto ? 'primary' : 'secondary'}
          icon={prefs.auto ? <Pause size={13} /> : <Play size={13} />}
          onClick={() => prefs.setAuto(!prefs.auto)}
          aria-pressed={prefs.auto}
        >
          {prefs.auto ? 'Auto-refresh on' : 'Auto-refresh'}
        </Button>
        <Button size="sm" iconOnly aria-label="Refresh now" icon={<RefreshCw size={13} />} onClick={() => void query.refetch()} />
        <Button
          size="sm"
          iconOnly
          aria-label="Download shown lines"
          title="Download shown lines"
          icon={<Download size={13} />}
          disabled={lines.length === 0}
          onClick={() => saveBlob(new Blob([lines.join('\n') + '\n'], { type: 'text/plain;charset=utf-8' }), downloadName)}
        />
      </div>
      {query.isError ? (
        <div className="p-4">
          <Callout tone="danger" title="Could not read the log">
            {errorMessage(query.error)}
          </Callout>
        </div>
      ) : (
        <LogViewer lines={lines} pretty={prefs.pretty} wrap={prefs.wrap} loading={query.isPending} emptyText={emptyText} />
      )}
      <div className="flex items-center justify-between gap-2 border-t border-border px-4 py-2 text-xs text-fg-subtle">
        <span className="mono truncate">{file ?? ''}</span>
        <span className="shrink-0">{lines.length} lines</span>
      </div>
    </Card>
  );
}

const LEVEL_CLASS: Record<string, string> = {
  debug: 'text-fg-subtle',
  info: 'text-info',
  warn: 'text-warning',
  warning: 'text-warning',
  error: 'text-danger',
  fatal: 'text-danger',
  panic: 'text-danger',
  critical: 'text-danger',
};

function levelOf(line: string): string | null {
  const m = /"level"\s*:\s*"(\w+)"/.exec(line) ?? /\b(DEBUG|INFO|WARN|WARNING|ERROR|FATAL|CRITICAL)\b|\[(dbug|info|warn|fail|crit)\]/i.exec(line);
  if (!m) return null;
  const v = (m[1] ?? m[2] ?? '').toLowerCase();
  return { fail: 'error', crit: 'critical', dbug: 'debug' }[v] ?? v;
}

function formatTs(ts: unknown): string {
  if (typeof ts === 'number') {
    const d = new Date(ts > 1e12 ? ts : ts * 1000);
    return d.toISOString().replace('T', ' ').slice(0, 23);
  }
  if (typeof ts === 'string') return ts.replace('T', ' ').replace(/Z$/, '').slice(0, 23);
  return '';
}

function PrettyLine({ line }: { line: string }) {
  if (!line.startsWith('{')) return <RawLine line={line} />;
  let obj: Record<string, unknown>;
  try {
    obj = JSON.parse(line) as Record<string, unknown>;
  } catch {
    return <RawLine line={line} />;
  }
  const { ts, level, logger, msg, ...rest } = obj;
  const lvl = typeof level === 'string' ? level.toLowerCase() : '';
  // Access log entries: show the request compactly.
  const req = rest.request as { method?: string; host?: string; uri?: string; remote_ip?: string } | undefined;
  const extra = { ...rest };
  if (req) delete extra.request;
  return (
    <>
      <span className="text-fg-subtle">{formatTs(ts)} </span>
      {lvl && <span className={cn('font-semibold uppercase', LEVEL_CLASS[lvl])}>{lvl.padEnd(5)} </span>}
      {typeof logger === 'string' && <span className="text-accent-text">{logger} </span>}
      {typeof msg === 'string' && <span className="text-fg">{msg} </span>}
      {req && (
        <span className="text-fg">
          {req.remote_ip} {req.method} {req.host}
          {req.uri}{' '}
        </span>
      )}
      {typeof extra.status === 'number' && (
        <span className={cn(Number(extra.status) >= 500 ? 'text-danger' : Number(extra.status) >= 400 ? 'text-warning' : 'text-success')}>
          {String(extra.status)}{' '}
        </span>
      )}
      <span className="text-fg-subtle">
        {Object.entries(extra)
          .filter(([k]) => k !== 'status' && k !== 'resp_headers')
          .map(([k, v]) => `${k}=${typeof v === 'object' ? JSON.stringify(v) : String(v)}`)
          .join(' ')}
      </span>
    </>
  );
}

function RawLine({ line }: { line: string }) {
  const lvl = levelOf(line);
  return <span className={cn(lvl === 'error' || lvl === 'fatal' || lvl === 'critical' ? 'text-danger' : lvl === 'warn' || lvl === 'warning' ? 'text-warning' : 'text-fg')}>{line}</span>;
}

function LogViewer({ lines, pretty, wrap, loading, emptyText }: { lines: string[]; pretty: boolean; wrap: boolean; loading: boolean; emptyText: string }) {
  const ref = useRef<HTMLDivElement>(null);
  const stick = useRef(true);
  const last = lines[lines.length - 1];

  useEffect(() => {
    const el = ref.current;
    if (el && stick.current) el.scrollTop = el.scrollHeight;
  }, [lines.length, last]);

  return (
    <div
      ref={ref}
      tabIndex={0}
      role="log"
      aria-label="Log lines"
      onScroll={(e) => {
        const el = e.currentTarget;
        stick.current = el.scrollHeight - el.scrollTop - el.clientHeight < 40;
      }}
      className={cn(
        'mono h-[calc(100dvh-330px)] min-h-[320px] overflow-auto bg-code px-4 py-2 text-[12px] leading-[1.65]',
        wrap ? 'break-all whitespace-pre-wrap' : 'whitespace-pre',
      )}
    >
      {loading ? (
        <p className="py-8 text-center font-sans text-sm text-fg-subtle">Loading…</p>
      ) : lines.length === 0 ? (
        <p className="py-8 text-center font-sans text-sm text-fg-subtle">{emptyText}</p>
      ) : (
        lines.map((l, i) => (
          <div key={i} className="hover:bg-surface-2/60">
            {pretty ? <PrettyLine line={l} /> : <RawLine line={l} />}
          </div>
        ))
      )}
    </div>
  );
}
