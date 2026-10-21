# Release checklist

1. Build and run the documented regression, elevated IPC and WPF checks on Windows. Keep real SQL restore evidence separate from simulated tests.
2. Review dependencies and generated package notices. Review repository files and Git history for secrets and private information before pushing. Ignoring a file does not remove it from existing history.
3. Build the versioned installer and verify its SHA-256 sidecar. Test installation, restart, upgrade, rollback and uninstall/reinstall on a disposable Windows machine.
4. Verify remote SQL/NAS permissions and a complete restore in an authorized test environment. VERIFYONLY alone is not restore acceptance.
5. Enable private vulnerability reporting in repository settings before public release. Review SECURITY.md and supported-platform documentation.
6. Push the reviewed version tag only when ready to run release automation. Inspect the workflow results and generated draft prerelease, installer, notices and checksum before publishing manually.

## Known limits

Installers are unsigned. Hosted workflow success and target-machine lifecycle checks must be established per release. A successful developer build or a reported server installation does not prove all upgrade/rollback/network scenarios.

There is no cloud destination, automatic retention deletion, restore wizard or external notification transport. Donation links are not configured. No payment is required for core functionality.
