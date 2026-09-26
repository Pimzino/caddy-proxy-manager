import logoDark from '@/assets/brand/logo-dark.png';
import logoLight from '@/assets/brand/logo-light.png';
import mark from '@/assets/brand/mark.png';
import { cn } from '@/lib/cn';

// The logo is raster art drawn with code in docs/brand/logo-concepts (regenerate every product image with
// `node docs/brand/logo-concepts/generate.ts assets`). Imported so Vite content-hashes them (static files are cached
// as immutable). The light and dark versions swap with the theme.

/** Full lockup: "caddy" with the teal handle and cut, "Proxy Manager" beneath. Size it by height. */
export function Logo({ className }: { className?: string }) {
  return (
    <>
      <img src={logoLight} alt="Caddy Proxy Manager" draggable={false} className={cn('w-auto select-none dark:hidden', className)} />
      <img src={logoDark} alt="Caddy Proxy Manager" draggable={false} className={cn('hidden w-auto select-none dark:block', className)} />
    </>
  );
}

/** Square mark ("dd" with the handle on an ink tile) for places too small for the lockup. */
export function LogoMark({ className }: { className?: string }) {
  return <img src={mark} alt="Caddy Proxy Manager" draggable={false} className={cn('select-none rounded-md', className)} />;
}
