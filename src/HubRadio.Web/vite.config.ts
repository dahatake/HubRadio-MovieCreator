import { defineConfig } from 'vite'

export default defineConfig({
  base: './',
  publicDir: false,
  build: {
    assetsDir: 'assets',
    assetsInlineLimit: 0,
    copyPublicDir: false,
    emptyOutDir: true,
    sourcemap: false,
  },
})
