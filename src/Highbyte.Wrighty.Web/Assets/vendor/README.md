# Vendored browser dependency

- Package: htmx
- Version: 2.0.9
- Upstream: https://github.com/bigskysoftware/htmx
- File: `htmx-2.0.9.min.js`
- SHA-256: `6eaa5e1530c14966ae4e2add137c8104a0edcd55a9311550e361d097c0e488fe`
- License: Zero-Clause BSD (`htmx-LICENSE.txt`)

The file is embedded directly in the Wrighty assembly. Ordinary build and publish workflows do not
use npm or download browser dependencies.

- Package: highlight.js custom browser build
- Version: 11.11.1
- Upstream: https://highlightjs.org/download
- Selected language: YAML only
- File: `highlight-yaml-11.11.1.min.js`
- SHA-256: `99775fe31908c6aac992fb04b03ba48fdca58c46af066413d80b4c6043a2ba99`
- License: BSD 3-Clause (`highlight.js-LICENSE.txt`)

The custom build contains highlight.js core plus only the YAML grammar. Wrighty supplies its own
theme rules in `wrighty.css`; no highlight.js theme or remote asset is loaded.
