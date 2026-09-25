import { ExternalLink } from 'lucide-react';
import type { BinaryOverview } from '@/api/types';
import { Callout } from '@/components/ui';

/** "Caddy Proxy Manager vX is available" (manager self-update notice; the update itself is done with the installer). */
export function ManagerUpdateCallout({ o, className }: { o: BinaryOverview; className?: string }) {
  if (!o.managerUpdateAvailable || !o.managerLatestVersion) return null;
  const v = (x: string) => `v${x.replace(/^v/, '')}`;
  return (
    <Callout
      tone="info"
      className={className}
      title={`Caddy Proxy Manager ${v(o.managerLatestVersion)} is available`}
      actions={
        o.managerLatestUrl && (
          <a href={o.managerLatestUrl} target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1 text-sm font-medium text-accent-text hover:underline">
            Release notes <ExternalLink size={12} aria-hidden />
          </a>
        )
      }
    >
      Installed: <span className="mono">{o.managerVersion ? v(o.managerVersion) : 'unknown'}</span>. Download the new installer from the release page and run it on
      this server; settings and data are kept.
    </Callout>
  );
}
