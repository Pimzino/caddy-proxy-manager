// Mock of the Caddy Proxy Manager self-update check (GET /api/system/manager-update, POST …/check).
// MOCK_MANAGER_UPDATE=available (default: installed 1.0.1, GitHub has 1.1.0 and 1.0.2),
// none (installed 1.1.0, nothing newer) or error (the GitHub request fails; `error` is set, HTTP 200).
import type { BinaryOverview, ManagerUpdateInfo, ReleaseInfo } from '../src/api/types.ts';
import { RECORDED_MANAGER_RELEASES } from './manager-releases.ts';

export const OFFICIAL_MANAGER_REPO = 'Pimzino/caddy-proxy-manager';
export type ManagerUpdateMode = 'available' | 'none' | 'error';

export function managerUpdateMode(): ManagerUpdateMode {
  const m = process.env.MOCK_MANAGER_UPDATE;
  return m === 'none' || m === 'error' ? m : 'available';
}

/** The installed manager version the mock reports everywhere (health, system info, dashboard, binary overview). */
export function installedManagerVersion(): string {
  return managerUpdateMode() === 'none' ? '1.1.0' : '1.0.1';
}

/**
 * Untrusted HTML a compromised or careless release could carry. The UI must render none of it as live markup:
 * the E2E scenario `manager-update` checks that no handler runs and no script/iframe/javascript: link survives.
 */
export const HOSTILE_NOTES_HTML = `
<h3>Security test content</h3>
<p>Image with a handler: <img src="x" alt="broken image" onerror="window.__pwned=1"></p>
<script>window.__pwned=2</script>
<p><a href="javascript:window.__pwned=3">javascript link</a> · <a href="data:text/html,<script>window.__pwned=4</script>">data link</a> · <a href="/relative/path">relative link</a></p>
<div style="position:fixed;inset:0;z-index:9999;background:red" onclick="window.__pwned=5" onmouseover="window.__pwned=6">Overlay attempt</div>
<iframe src="https://example.com/"></iframe>
<form action="https://example.com/steal"><input name="password" value="x"><button>Send</button></form>
<svg onload="window.__pwned=7"><circle r="5"></circle></svg>
<style>body{display:none}</style>
<p><img src="https://github.com/user-attachments/assets/screenshot.png" alt="Screenshot of the dashboard"></p>
<ul class="contains-task-list"><li class="task-list-item"><input type="checkbox" class="task-list-item-checkbox" checked disabled> Done item</li><li class="task-list-item"><input type="checkbox" class="task-list-item-checkbox" disabled> Open item</li></ul>
<custom-widget data-x="1" onclick="window.__pwned=8">Custom element text is kept</custom-widget>
`;

function releases(): ReleaseInfo[] {
  return RECORDED_MANAGER_RELEASES.map((r) => (r.version === '1.0.2' ? { ...r, notesHtml: `${r.notesHtml ?? ''}${HOSTILE_NOTES_HTML}` } : r));
}

const cmp = (a: string, b: string) => {
  const pa = a.split('.').map(Number);
  const pb = b.split('.').map(Number);
  for (let i = 0; i < 3; i++) if ((pa[i] ?? 0) !== (pb[i] ?? 0)) return (pa[i] ?? 0) - (pb[i] ?? 0);
  return 0;
};

export function managerUpdateInfo(settings: { checkManagerUpdates: boolean; managerReleaseRepo?: string | null }, checkedAt: string): ManagerUpdateInfo {
  const repo = settings.managerReleaseRepo?.trim() || OFFICIAL_MANAGER_REPO;
  const base = {
    repo,
    repoIsDefault: !settings.managerReleaseRepo?.trim(),
    releasesUrl: `https://github.com/${repo}/releases`,
    currentVersion: installedManagerVersion(),
  };
  if (!settings.checkManagerUpdates) return { ...base, enabled: false, updateAvailable: false, newerReleases: [] };
  if (managerUpdateMode() === 'error')
    return {
      ...base,
      enabled: true,
      updateAvailable: false,
      newerReleases: [],
      checkedAt,
      error: `GitHub API request to https://api.github.com/repos/${repo}/releases failed: 403 Forbidden (API rate limit exceeded for 203.0.113.10).`,
    };
  const all = releases();
  const newer = all.filter((r) => cmp(r.version, base.currentVersion) > 0);
  return { ...base, enabled: true, updateAvailable: newer.length > 0, latest: all[0], newerReleases: newer, checkedAt };
}

/** Keeps the manager fields of the Caddy binary overview in line with the update check. */
export function syncManagerOverview(binary: BinaryOverview, info: ManagerUpdateInfo) {
  binary.managerVersion = info.currentVersion;
  binary.managerUpdateAvailable = info.updateAvailable;
  if (info.updateAvailable && info.latest) {
    binary.managerLatestVersion = info.latest.version;
    binary.managerLatestUrl = info.latest.url;
  } else {
    delete binary.managerLatestVersion;
    delete binary.managerLatestUrl;
  }
}
