// Traffic statistics of a server (?server=local&range=day&host=): range tabs, host filter, KPIs, charts,
// top hosts/clients, status codes. The filters live in the URL so a view can be bookmarked or shared.
import { Link, useSearchParams } from 'react-router';
import { useServers } from '@/api/hooks';
import type { TrafficRange } from '@/api/types';
import { PageHeader, Select } from '@/components/ui';
import { isTrafficRange, TrafficSection } from './TrafficSection';
import { serverStatusInfo } from './shared';

export default function TrafficPage() {
  const [params, setParams] = useSearchParams();
  const servers = useServers();
  const serverId = params.get('server') || 'local';
  const rangeParam = params.get('range');
  const range: TrafficRange = isTrafficRange(rangeParam) ? rangeParam : 'day';
  const host = params.get('host') ?? '';
  const list = servers.data ?? [];
  const current = list.find((s) => s.id === serverId);

  const update = (next: { server?: string; range?: TrafficRange; host?: string }) => {
    const p = new URLSearchParams(params);
    // Defaults are left out of the URL.
    const set = (k: string, v: string | undefined, dflt: string) => {
      if (v === undefined) return;
      if (v && v !== dflt) p.set(k, v);
      else p.delete(k);
    };
    set('server', next.server, 'local');
    set('range', next.range, 'day');
    set('host', next.host, '');
    setParams(p, { replace: true });
  };

  return (
    <>
      <PageHeader
        title="Traffic"
        description={
          <>
            Requests served by Caddy{current && !current.isLocal ? ` on ${current.name}` : ''}, from its access log.{' '}
            {current && (
              <Link to={`/servers/${encodeURIComponent(current.id)}`} className="text-accent-text hover:underline">
                Server details
              </Link>
            )}
          </>
        }
      />
      <TrafficSection
        key={serverId}
        serverId={serverId}
        range={range}
        host={host}
        onRangeChange={(r) => update({ range: r })}
        onHostChange={(h) => update({ host: h })}
        leading={
          list.length > 1 ? (
            <div className="w-56 max-w-full">
              <Select aria-label="Server" value={serverId} onChange={(e) => update({ server: e.target.value, host: '' })}>
                {list.map((s) => (
                  <option key={s.id} value={s.id}>
                    {s.name}
                    {s.isLocal ? ' (this server)' : s.status !== 'online' ? ` — ${serverStatusInfo(s.status).label.toLowerCase()}` : ''}
                  </option>
                ))}
              </Select>
            </div>
          ) : undefined
        }
      />
    </>
  );
}
