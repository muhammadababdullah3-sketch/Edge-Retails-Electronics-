# Supplier UI correction — 2026-10-06

Fixed shared TabControl layout: wrapping headers retain complete borders; selected white foreground applies only to header. Content retains theme primary foreground. Supplier drawer borders use the existing rounded control radius.

Verification: Desktop Release build zero warnings/errors; 24 existing UI regression checks passed. Owned WPF supplier fixture rendered Light/Dark: 30 assertions passed (seven full rounded borders and allocated header widths per theme, plus amount foreground per theme). Visual inspection confirmed readable amounts and rounded cards. No operational database accessed or modified.

Production delivery: running production desktop locked DLL. Complete Release staged at artifacts/production/pending-supplier-ui; hidden Apply-AfterClose.ps1 waits for that exact production executable to close naturally, then copies build into existing shortcut target. No process is terminated. deployment-status.txt records outcome. Existing shortcut and dated brand icon preserved. Open app continues old loaded assembly until close/reopen.
