import { defineConfig } from 'vite';

// Two builds into dist/:
//   default:        background service worker (ES module) + popup, copies public/ (manifest, _locales, icons)
//   --mode content: content script as a classic IIFE (content scripts can't be ES modules)
export default defineConfig(({ mode }) =>
  mode === 'content'
    ? {
        root: 'src',
        publicDir: false,
        build: {
          outDir: '../dist',
          emptyOutDir: false,
          lib: { entry: 'content.ts', formats: ['iife'], name: 'fluxerContent', fileName: () => 'content.js' },
        },
      }
    : {
        root: 'src',
        publicDir: '../public',
        cacheDir: '../node_modules/.vite',
        base: '/',
        build: {
          outDir: '../dist',
          emptyOutDir: true,
          modulePreload: false,
          rollupOptions: {
            input: { background: 'src/background.ts', popup: 'src/popup/index.html' },
            output: { entryFileNames: '[name].js', chunkFileNames: 'assets/[name]-[hash].js', assetFileNames: 'assets/[name]-[hash][extname]' },
          },
        },
      },
);
