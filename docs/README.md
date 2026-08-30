# Lexarbor documentation

Lexarbor is a self-contained vocabulary catalog and quiz service. The backend, the Vue administration UI, the static files, and the Docker image all live in this repository and are served together on HTTP port 5008.

## Contents

- [Secure self-contained service design](./overview/SecureSelfContainedServiceDesign.md)
- [Overview](./overview/README.md)
- [Domain language](./overview/DomainLanguage.md)
- [Administration frontend specification](./frontend/README.md)
- [ADR-001 Pluggable administrator authentication provider](./adr/ADR-001-pluggable-admin-authentication.md)
- [ADR-002 Bundled vocabulary data and user-supplied imports](./adr/ADR-002-bundled-vocabulary-data.md)
- [ADR-003 SQLite as the only supported storage](./adr/ADR-003-sqlite-only-storage.md)
- [ADR-004 Extracting Lexarbor from the monorepo](./adr/ADR-004-standalone-lexarbor-repository.md)
- [Database facts](./database/README.md)
- [Development](./development/README.md)
- [Repository layout and file ownership](./development/RepositoryLayout.md)
- [Repository automation](./development/Automation.md)
- [Pending decisions](./pending-decisions.md)
