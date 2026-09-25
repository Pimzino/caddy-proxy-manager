import type { LucideIcon } from 'lucide-react';
import {
  Activity,
  ArrowLeftRight,
  Braces,
  Cable,
  ClipboardCheck,
  CornerUpRight,
  FileCode2,
  FolderOpen,
  History,
  LayoutDashboard,
  ListChecks,
  Mail,
  Puzzle,
  ScrollText,
  Server,
  Settings,
  ShieldCheck,
  Users,
} from 'lucide-react';
import type { UserRole } from '@/api/types';

export interface NavItem {
  to: string;
  label: string;
  icon: LucideIcon;
  /** Minimum role to see the item. */
  role?: UserRole;
  end?: boolean;
}

export interface NavGroup {
  label: string;
  items: NavItem[];
}

export const navGroups: NavGroup[] = [
  { label: 'Overview', items: [{ to: '/', label: 'Dashboard', icon: LayoutDashboard, end: true }] },
  {
    label: 'Sites',
    items: [
      { to: '/hosts/proxy', label: 'Proxy Hosts', icon: ArrowLeftRight },
      { to: '/hosts/redirect', label: 'Redirects', icon: CornerUpRight },
      { to: '/hosts/static', label: 'Static Sites', icon: FolderOpen },
      { to: '/hosts/response', label: 'Custom Responses', icon: FileCode2 },
      { to: '/streams', label: 'Streams', icon: Cable },
    ],
  },
  {
    label: 'Security',
    items: [
      { to: '/certificates', label: 'Certificates', icon: ShieldCheck },
      { to: '/access-lists', label: 'Access Lists', icon: ListChecks },
    ],
  },
  {
    label: 'Caddy',
    items: [
      { to: '/caddy/service', label: 'Service & Updates', icon: Server },
      { to: '/caddy/plugins', label: 'Plugins', icon: Puzzle },
      { to: '/caddy/config', label: 'Configuration', icon: Braces },
    ],
  },
  {
    label: 'Server',
    items: [
      { to: '/readiness', label: 'Readiness', icon: ClipboardCheck },
      { to: '/logs', label: 'Logs', icon: ScrollText },
      { to: '/events', label: 'Events', icon: Activity },
    ],
  },
  {
    label: 'Administration',
    items: [
      { to: '/notifications', label: 'Notifications', icon: Mail, role: 'admin' },
      { to: '/settings', label: 'Settings', icon: Settings },
      { to: '/users', label: 'Users', icon: Users, role: 'admin' },
      { to: '/audit', label: 'Audit Log', icon: History, role: 'admin' },
    ],
  },
];

/** Finds the group/page for the breadcrumb and document title. */
export function findNav(pathname: string): { group: string; item: NavItem } | null {
  let best: { group: string; item: NavItem } | null = null;
  for (const g of navGroups) {
    for (const item of g.items) {
      const match = item.end ? pathname === item.to : pathname === item.to || pathname.startsWith(item.to + '/');
      if (match && (!best || item.to.length > best.item.to.length)) best = { group: g.label, item };
    }
  }
  return best;
}
