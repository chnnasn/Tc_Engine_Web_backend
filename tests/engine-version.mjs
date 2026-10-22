import { readFileSync } from 'node:fs'

// Exercise the same commit that the production container actually builds.
const dockerfile = readFileSync(new URL('../Dockerfile', import.meta.url), 'utf8')
export const currentEngineCommit = dockerfile.match(/^ARG ENGINE_COMMIT=([a-f0-9]{40})\r?$/m)?.[1]
if (!currentEngineCommit) throw new Error('Dockerfile must pin a complete engine commit')
export const previousEngineCommits = [
  'b04d81d80a92fdf28ce7c27d37e24005e8a8f984',
  '65f38cfb7b1345dae411882f76b3fa7204e17722',
  '0b8829a864ff53ad0ad2c7ded56ef433325a836a',
  '5734c56ae05241b01a684afb8e62f2b164c69ba2',
  '9a0ccad79bb4ec33225bd8210fe9b47438bb06bc',
  'c28487215bd5df89b35c1d68252ba240231d9c4f',
]
