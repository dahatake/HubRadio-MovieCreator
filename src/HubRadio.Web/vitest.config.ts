import { defineConfig } from 'vitest/config'

export default defineConfig({
  test: {
    environment: 'node',
    globals: false,
    include: ['src/**/*.test.ts', 'src/**/*.test.tsx'],
    // Bound process fan-out: the automatic pool timed out starting workers on
    // a 20-way Windows host; four workers completed the full Vitest suite twice.
    maxWorkers: 4,
  },
})
