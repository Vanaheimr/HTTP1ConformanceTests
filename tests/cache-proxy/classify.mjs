// Classify a cache-tests results file the way cache-tests.fyi does.
//
//     node classify.mjs <cache-tests checkout> <results.json> [other.json ...]
//
// The CLI writes one raw outcome per test: true, or [error type, message].
// What that outcome MEANS depends on the test, and the suite decides it, not
// this script: determineTestResult() in test-engine/lib/results.mjs reads the
// test's kind (required, optimal, check) and its dependencies. A failed
// required test is a conformance failure, a failed optimal one is a missed
// optimisation, a check is a yes or a no, and a test whose dependency failed
// says nothing about itself. Counting the raw `true`s instead would mix all
// of those together.
//
// Prints one line per test, "<id>\t<result>\t<message>", sorted by id, then a
// count per result on stderr. With more than one results file, the columns
// are one per file and the message is the first file's.

import { pathToFileURL } from 'node:url'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'

const [checkout, ...files] = process.argv.slice(2)
if (!checkout || files.length === 0) {
  console.error('usage: node classify.mjs <cache-tests checkout> <results.json> [other.json ...]')
  process.exit(2)
}

const load = path => import(pathToFileURL(join(checkout, path)).href)
const { default: suites } = await load('tests/index.mjs')
const { determineTestResult, parseResults, resultTypes } = await load('test-engine/lib/results.mjs')

const names = new Map(Object.entries(resultTypes).map(([name, type]) => [type, name]))
const runs = files.map(file => parseResults(JSON.parse(readFileSync(file, 'utf8'))).results)

const counts = runs.map(() => ({}))
const lines = []

for (const suite of suites) {
  for (const test of suite.tests) {
    const columns = runs.map((results, i) => {
      const name = names.get(determineTestResult(suites, test.id, results))
      counts[i][name] = (counts[i][name] || 0) + 1
      return name
    })
    const raw = runs[0][test.id]
    const message = Array.isArray(raw) ? `${raw[0]}: ${raw[1]}` : ''
    lines.push([test.id, ...columns, message].join('\t'))
  }
}

lines.sort()
console.log(lines.join('\n'))
counts.forEach((count, i) => {
  const summary = Object.keys(count).sort().map(name => `${name} ${count[name]}`).join(', ')
  console.error(`${files[i]}: ${summary}`)
})
