# Phase 3 Backup and Restore Server Operations

The Desktop uses the loopback Server API for manual backup, verified history, staged restore preparation, recovery, cutover, and discard. It never receives database credentials, backup keys, or filesystem paths.

## Server-owned configuration

| Setting | Required | Purpose |
|---|---:|---|
| `EDGE_RETAILS_BACKUP_DIR` | No | Server-owned backup directory. Defaults to `%LOCALAPPDATA%\EdgeRetails\Production\backups` for the Server process identity. |
| `EDGE_RETAILS_BACKUP_KEY` | For backup/restore | Exactly 64 hexadecimal characters (32 bytes) used for AES-256-GCM payload protection and derived manifest authentication. Back up this key separately from the encrypted artifacts. |
| `EDGE_RETAILS_PG_BIN` | No | PostgreSQL 18 client tools directory. Defaults to `%ProgramFiles%\PostgreSQL\18\bin`. `pg_dump`, `pg_restore`, `psql`, and `createdb` must be present. |
| `EDGE_RETAILS_PG_MAINTENANCE_HOST` | For restore | PostgreSQL host for the recovery-only administrator connection. It must match the configured runtime endpoint. |
| `EDGE_RETAILS_PG_MAINTENANCE_PORT` | For restore | PostgreSQL TCP port for the recovery-only administrator connection. |
| `EDGE_RETAILS_PG_MAINTENANCE_USERNAME` | For restore | Dedicated PostgreSQL restore administrator identity. |
| `EDGE_RETAILS_PG_MAINTENANCE_PASSWORD` | For restore | Secret for the restore administrator identity. Supply through the Server process secret store/environment, never through Desktop settings or HTTP. |
| `EDGE_RETAILS_PG_MAINTENANCE_DATABASE` | No | Maintenance database; defaults to `postgres`. |
| `EDGE_RETAILS_PRODUCTION_STATE_DIR` | No | Server state root. The HMAC restore journal and its separate local integrity key are stored under this root. |

The restore journal integrity key is generated locally and stored separately from the backup encryption key and maintenance-barrier key. Keep the Server state root protected and backed up under the deployment's recovery procedure. If that key is lost or the journal fails authentication, restore fails closed and requires operator recovery.

## Operator flow

1. Refresh backup history and select a verified backup by its opaque ID.
2. Prepare restore. The Server authenticates the companion manifest, verifies size and SHA-256, decrypts to protected temporary storage, restores into an isolated staging database, and runs schema/business validation.
3. Review the prepared status. Choose **Cut Over** or **Discard**. The Desktop requires the operator to type the Server-issued confirmation phrase, which binds the action to the configured database and restore ID.
4. If a response is lost, select the same backup and retry. The Desktop reuses the persisted client operation ID; the Server returns or reconciles the existing journal instead of starting a second preparation.

The Server requires `SettingsManage` for every backup and restore endpoint. Backup history and restore status DTOs omit connection data, credentials, filesystem paths, full artifact names, staging database names, and raw checksums.
