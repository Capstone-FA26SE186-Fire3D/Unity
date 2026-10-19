# Runtime catalog: releasing a Unity runtime version

The **runtime** is the Unity program plus the shared assets built into the app (Unity as a Library inside the Mobile APK).
A **package** is the per-building/per-scenario content the worker builds. The backend only lets the editor, playtests and
training sessions use a runtime version that is an **active row** of its runtime catalog; it also scores results and checks
permissions. Registering is a PlatformAdmin API call, deliberately not part of any deploy.

## Versions

- `RuntimeCatalog.RuntimeVersion` (Unity) = `APP_RUNTIME_VERSION` (Mobile `src/features/training/runtime-version.ts`) =
  the catalog `runtimeVersion`. Unity repeats it in `Ready`; Mobile reports a mismatch.
- Bump the **patch** for fixes that keep protocol `"1"`, manifest schema `"1"` and the capability set (2.0.1 = launch
  lifecycle fixes). New capabilities or contract changes need a new minor/major and a new payload.
- `RuntimeCatalog.MinPackageRuntimeVersion` is the oldest runtime that can play packages this build produces. Raise it only
  when packages depend on something older runtimes lack. Compatibility rule: same major, app not older than the package's
  `minRuntimeVersion`, same protocol/schema, all required capabilities offered. App and packages need not match exactly.
- A registered row is immutable except `isActive`; never edit a released payload, register a new version instead.

## Release steps

1. Bump the version in Unity and Mobile together; build Unity as a Library, update the Mobile artifact pin, build the APK.
2. Check the payload against this checkout:
   `python -I Tools/RuntimeCatalog/check_payload.py Tools/RuntimeCatalog/runtime-<version>.json`
3. Read the catalog first (PlatformAdmin bearer token):
   `GET https://api.fet3d.io.vn/api/admin/runtime-catalog`
4. Register **inactive**: `POST /api/admin/runtime-catalog` with the payload (`isActive: false`).
   - `201`: registered. Keep the returned `id`.
   - `400 VALIDATION_ERROR`: fix the fields named by JSONPath in `errors`.
   - `409 RUNTIME_VERSION_EXISTS`: read the catalog again and compare the stored row with the payload field by field
     (protocol, schema, capabilities, every contract). Identical: nothing to do. Different: stop; a released version is
     never changed, bump the version instead.
5. Accept the build on a real Android phone (QR → choose training → download → play → exit → play again; Back while
   loading; offline after online start, then sync).
6. Activate: `PATCH /api/admin/runtime-catalog/{id}` with `{"isActive": true}`.

Do not deactivate an older version that released packages, sessions or apps still use; deactivation blocks new publish,
playtest and session starts that target it but never re-pins existing packages or sessions.

## Payloads

| File | Version | Notes |
| --- | --- | --- |
| BE `contracts/editor/v1/fixtures/runtime-catalog.batch1.json` | 2.0.0 | Batch 1 interaction catalog (drop `_comment` before POST). |
| `runtime-2.0.1.json` | 2.0.1 | Same protocol, schema, capabilities and contracts as 2.0.0; registered inactive. |
