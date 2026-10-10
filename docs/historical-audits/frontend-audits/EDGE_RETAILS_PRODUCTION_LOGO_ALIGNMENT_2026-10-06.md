# Production logo alignment — October 6, 2026

## Finding

The active sidebar, Login, FirstSetup and MainWindow use `Resources/Brand.xaml` → `Brand.Mark`, including in Release. The previous application/shortcut icon came from `Assets/EdgeRetails.ico`, which was still the old September 20 wordmark. `Assets/EdgeAppIcon.png` likewise displayed the old EDGE wordmark on a large white background. This is an actual branding mismatch between WPF UI and the Windows executable/shortcut, not a database-controlled logo.

No running Desktop process was found in the inspected process snapshot. A separate in-app logo mismatch on the user's screen was not captured in this pass; the verified finding is the obsolete application icon. `Assets/EdgeLogo.png` is a legacy embedded wordmark with no active XAML/code consumers found; it is not the sidebar logo.

## Correction

- Generated `EdgeAppIcon.png` and `EdgeRetails.ico` directly from the existing canonical `Brand.Mark`, without introducing another visual design.
- PNG retains transparent rounded corners. ICO contains 16/24/32/48/64/128/256px PNG frames for Windows display sizes.
- Reusable exporter: `scratch/BrandAssetExport/Program.cs`; source of authority remains `Resources/Brand.xaml`.
- Fresh full Desktop Release build succeeded, zero warnings/errors. ApplicationIcon already points to the corrected ICO.
- An isolated WPF resource loader read the actual compiled Release Desktop Brand.Mark and rendered it; its PNG bytes matched the application icon pixel-for-pixel. Normal App startup/login/database registration was not run.
- Copied the rebuilt Release executable/dependencies into `artifacts/production/desktop` and verified executable/assembly delivery hashes against build output.
- Existing desktop shortcut still launches the real Release application with empty arguments. Its icon now points to `EdgeRetails.Brand.2026-10-06.ico` in the same production folder, providing a new icon-cache identity rather than continuing to request an obsolete EXE icon entry.

Database/configuration/service binaries were not changed by this logo correction. The operational application still uses its existing server-backed database authority. Already pinned Windows shortcuts or launchers outside the inspected desktop shortcut may retain their own old target/icon; those were not changed.
