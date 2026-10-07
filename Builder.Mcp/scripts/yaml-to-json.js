#!/usr/bin/env node
// Converts a design YAML file to JSON (the structure the Builder API speaks): node scripts/yaml-to-json.js in.yaml [out.json]
import { readFileSync, writeFileSync } from 'node:fs'
import { parse } from 'yaml'

const [input, output] = process.argv.slice(2)
if (!input) {
  console.error('usage: node scripts/yaml-to-json.js <design.yaml> [design.json]')
  process.exit(2)
}
const json = JSON.stringify(parse(readFileSync(input, 'utf8')), null, 2) + '\n'
if (output) writeFileSync(output, json)
else process.stdout.write(json)
