Character Mod Exporter: an add-on for PlayerSync. Right-click a character PlayerSync is syncing (ZoneSync, syncshell
or pair) and export the files PlayerSync applied to them into a folder you choose, with a manifest, a report and,
when complete, a Penumbra package for manual import.

0.2.0.0: works with the official PlayerSync. The exporter reads from Penumbra which of PlayerSync's cached files
are applied to the character. Only files the game has loaded are listed (animations, VFX and sounds once they have
played; Scan redraws the character). The patched PlayerSync build is no longer needed; if one with the export IPC is
loaded, the exporter still uses it. Not tested in game yet; see docs/MANUAL_TEST_PLAN.md. Installation: docs/INSTALL.md.
