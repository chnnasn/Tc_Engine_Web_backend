import { readFileSync } from 'node:fs'

// Exercise the same commit that the production container actually builds.
const dockerfile = readFileSync(new URL('../Dockerfile', import.meta.url), 'utf8')
export const currentEngineCommit = dockerfile.match(/^ARG ENGINE_COMMIT=([a-f0-9]{40})\r?$/m)?.[1]
if (!currentEngineCommit) throw new Error('Dockerfile must pin a complete engine commit')
export const previousEngineCommits = [
  '0b8829a864ff53ad0ad2c7ded56ef433325a836a',
  '5734c56ae05241b01a684afb8e62f2b164c69ba2',
  '9a0ccad79bb4ec33225bd8210fe9b47438bb06bc',
]
