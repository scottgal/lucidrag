import { defineConfig } from 'vite'
import { resolve } from 'path'

export default defineConfig({
  root: resolve(__dirname),
  publicDir: false,
  build: {
    outDir: 'wwwroot/dist',
    emptyOutDir: true,
    manifest: true,
    rollupOptions: {
      input: {
        main: resolve(__dirname, 'src/js/main.js'),
        styles: resolve(__dirname, 'src/css/main.css')
      },
      output: {
        entryFileNames: 'js/[name].js',
        chunkFileNames: 'js/[name].js',
        assetFileNames: (assetInfo) => {
          if (assetInfo.name?.endsWith('.css')) {
            return 'css/[name][extname]'
          }
          return 'assets/[name][extname]'
        }
      }
    }
  },
  css: {
    postcss: {
      plugins: [
        (await import('tailwindcss')).default,
        (await import('autoprefixer')).default
      ]
    }
  },
  server: {
    proxy: {
      '/api': 'http://localhost:5019',
      '/hubs': {
        target: 'http://localhost:5019',
        ws: true
      },
      '/graphql': 'http://localhost:5019'
    }
  }
})
