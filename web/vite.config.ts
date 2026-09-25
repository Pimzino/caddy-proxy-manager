import { fileURLToPath, URL } from 'node:url';
import { defineConfig, type PluginOption } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

// `npm run dev`      → proxies /api to the real backend (dotnet run with CM_UI_PORT=5081).
// `npm run dev:mock` → serves realistic fixture data from web/mock (dev server only; never part of a build).
export default defineConfig(async ({ command, mode }) => {
  const plugins: PluginOption[] = [react(), tailwindcss()];
  const useMock = command === 'serve' && mode === 'mock';
  if (useMock) {
    const { mockApi } = await import('./mock/plugin.ts');
    plugins.push(mockApi());
  }
  return {
    base: '/',
    plugins,
    resolve: {
      alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
    },
    server: {
      port: 5173,
      strictPort: false,
      proxy: useMock
        ? undefined
        : {
            '/api': { target: process.env.CPM_API ?? 'http://localhost:5081', changeOrigin: false },
          },
    },
    build: {
      outDir: 'dist',
      emptyOutDir: true,
      sourcemap: false,
      chunkSizeWarningLimit: 900,
      assetsInlineLimit: 0,
      rollupOptions: {
        output: {
          // Keep icons and vendor code in stable, cacheable chunks instead of many tiny per-icon files.
          manualChunks(id: string) {
            if (id.includes('node_modules/lucide-react')) return 'icons';
            if (id.includes('node_modules/')) return 'vendor';
            return undefined;
          },
        },
      },
    },
  };
});
