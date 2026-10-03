import js from '@eslint/js'
import { defineConfig } from 'eslint/config'
import pluginVue from 'eslint-plugin-vue'
import tseslint from 'typescript-eslint'

// Compose the upstream recommended configs directly. The Vue TypeScript
// wrapper discovers files through fast-glob, which pulls in unpatched braces.
export default defineConfig(
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
  // Not the type-checked variant. vue-tsc already runs over the same files in
  // test:types and reports what a type-aware rule would need the type
  // information for; adding a second, slower pass across four tsconfigs would
  // buy the floating-promise rules at the cost of a config that has to track
  // every project reference.
  tseslint.configs.recommended.map(config => config.files
    ? { ...config, files: ['**/*.{ts,vue}'] }
    : config),
  pluginVue.configs['flat/recommended'],
  {
    name: 'lexarbor/vue-typescript',
    files: ['**/*.vue'],
    languageOptions: {
      parserOptions: {
        parser: tseslint.parser
      }
    },
    rules: {
      'vue/block-lang': ['error', { script: { lang: ['ts'], allowNoLang: false } }]
    }
  },
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
