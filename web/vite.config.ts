import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// `process` is not in this project's ambient types (no @types/node, and tsconfig typechecks
// this file), so read the env through a narrow cast.
const nodeEnv = (globalThis as unknown as { process?: { env?: Record<string, string | undefined> } }).process?.env
const devApiTarget = nodeEnv?.AINUR_DEV_API ?? 'http://127.0.0.1:5180'

export default defineConfig({
  plugins: [react()],
  build: { outDir: '../src/Ainur.Server/wwwroot', emptyOutDir: true },
  // Dev server proxies the API to the running Ainur server. Override the target when your
  // server is not on the default port: AINUR_DEV_API=http://127.0.0.1:5181 npm run dev
  server: { proxy: { '/api': { target: devApiTarget, changeOrigin: false } } },
})
