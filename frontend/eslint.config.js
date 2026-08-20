import js from '@eslint/js'
import pluginVue from 'eslint-plugin-vue'
import { defineConfigWithVueTs, vueTsConfigs } from '@vue/eslint-config-typescript'

// Flat config. The two shared configs below are the ones create-vue scaffolds,
// chosen so this file stays a list of decisions rather than a rule set of its
// own that would drift from what the Vue and typescript-eslint projects
// consider correct.
export default defineConfigWithVueTs(
  {
    name: 'lexarbor/ignores',
    // Build output, plus the two declaration files unplugin-auto-import and
    // unplugin-vue-components rewrite on every build. Both already open with an
    // eslint-disable header, but a generated file should not cost a lint run
    // even to be skipped.
    ignores: ['dist/**', 'auto-imports.d.ts', 'components.d.ts']
  },
  {
    name: 'lexarbor/files',
    // Everything hand-written: src, the Playwright suite in e2e, the type tests,
    // and the two config files at the root.
    files: ['**/*.{ts,vue}']
  },
  // The core rules, which the Vue and typescript-eslint configs layer on top
  // of rather than restate.
  js.configs.recommended,
  pluginVue.configs['flat/recommended'],
  // Not the type-checked variant. vue-tsc already runs over the same files in
  // test:types and reports what a type-aware rule would need the type
  // information for; adding a second, slower pass across four tsconfigs would
  // buy the floating-promise rules at the cost of a config that has to track
  // every project reference.
  vueTsConfigs.recommended,
  {
    name: 'lexarbor/layout-is-not-lint',
    rules: {
      // These two decide where line breaks go inside a template, and they
      // accounted for 106 of the 110 findings on the first run against code no
      // reviewer had objected to. There is no formatter in this repository, so
      // nothing else is asking for one layout over another, and reflowing every
      // template to satisfy a default would produce a large diff that changes
      // no behaviour and settles no argument anyone was having. The rest of
      // flat/recommended stays on, including the ordering and naming rules that
      // do encode a convention rather than a line width.
      'vue/max-attributes-per-line': 'off',
      'vue/singleline-html-element-content-newline': 'off'
    }
  }
)
