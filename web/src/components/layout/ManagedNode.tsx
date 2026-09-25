import { Link } from 'react-router';
import { Lock, Network } from 'lucide-react';
import { useCluster, useIsManagedNode } from '@/api/hooks';
import { formatDateTime } from '@/lib/format';

/**
 * Banner shown on every page of a managed cluster node (SPEC round 3 "Node mode"): sites, certificates,
 * access lists, streams and Caddy settings are replicated from the primary and are read-only here.
 */
export function ManagedNodeBanner() {
  const { managed, primaryName } = useIsManagedNode();
  const cluster = useCluster();
  if (!managed) return null;
  const lastContact = cluster.data?.lastPrimaryContactAt;
  return (
    <div role="status" className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-info/25 bg-info-soft px-3 py-1.5 text-sm sm:px-4">
      <span className="flex min-w-0 items-center gap-2 text-fg">
        <Network size={14} className="shrink-0 text-info" aria-hidden />
        <span>
          Managed by <span className="font-medium">{primaryName || 'the cluster primary'}</span> — changes to sites, certificates, access lists,
          streams and Caddy settings are made on the primary.
        </span>
      </span>
      <span className="ml-auto flex items-center gap-3 text-xs text-fg-subtle">
        {lastContact && <span>Last contact {formatDateTime(lastContact)}</span>}
        <Link to="/settings?tab=cluster" className="text-accent-text hover:underline">
          Cluster settings
        </Link>
      </span>
    </div>
  );
}

/** Small marker for page headers whose data is replicated from the primary (read-only on a node). */
export function ReadOnlyOnNode({ className }: { className?: string }) {
  const { managed, primaryName } = useIsManagedNode();
  if (!managed) return null;
  return (
    <span
      className={`inline-flex h-7 items-center gap-1.5 rounded-md border border-border bg-surface-2 px-2 text-xs text-fg-muted ${className ?? ''}`}
      title={`Replicated from ${primaryName || 'the cluster primary'}. Make changes on the primary.`}
    >
      <Lock size={12} aria-hidden />
      Read-only on this node
    </span>
  );
}
