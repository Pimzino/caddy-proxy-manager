import { useState } from 'react';
import { Download, X } from 'lucide-react';
import { useManagerUpdate } from '@/api/hooks';
import { Button, buttonClasses, Callout } from '@/components/ui';
import { formatBytes } from '@/lib/format';
import { readStorage, writeStorage } from '@/lib/storage';
import { managerAssets, useOpenManagerUpdate, vText } from './ManagerUpdateDialog';

const DISMISS_KEY = 'cpm.managerUpdate.dismissed';

/**
 * "Caddy Proxy Manager vX is available" (manager self-update notice; the update itself is done with the installer).
 * `dismissible`: the notice can be hidden for this version (remembered in this browser); a newer version shows it
 * again, and the top-bar pill stays either way.
 */
export function ManagerUpdateCallout({ className, dismissible }: { className?: string; dismissible?: boolean }) {
  const q = useManagerUpdate();
  const open = useOpenManagerUpdate();
  const [dismissed, setDismissed] = useState(() => readStorage(DISMISS_KEY));
  const info = q.data;
  const latest = info?.updateAvailable ? info.latest : undefined;
  if (!info || !latest) return null;
  if (dismissible && dismissed === latest.version) return null;
  const { msi } = managerAssets(latest);
  const behind = info.newerReleases.length;
  return (
    <Callout
      tone="info"
      className={className}
      title={`Caddy Proxy Manager ${vText(latest.version)} is available`}
      actions={
        <>
          <Button size="sm" onClick={open}>
            What’s new
          </Button>
          {msi && (
            <a href={msi.downloadUrl} className={buttonClasses({ variant: 'primary', size: 'sm' })} title={`${msi.name} · ${formatBytes(msi.size)}`}>
              <Download size={13} aria-hidden />
              Download installer
            </a>
          )}
          {dismissible && (
            <Button
              size="sm"
              variant="ghost"
              iconOnly
              aria-label={`Hide this notice for ${vText(latest.version)}`}
              title="Hide until the next version"
              icon={<X size={14} />}
              onClick={() => {
                writeStorage(DISMISS_KEY, latest.version);
                setDismissed(latest.version);
              }}
            />
          )}
        </>
      }
    >
      Installed: <span className="mono">{vText(info.currentVersion)}</span>
      {behind > 1 && <> ({behind} releases behind)</>}. Run the new installer on this server;
      settings and data are kept.
    </Callout>
  );
}
