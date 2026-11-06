# Release version policy

- Every completed update, including minor fixes and documentation-only updates, must increment the release version in package.json before delivery. Use at least a patch increment; use a minor increment for new features.
- package.json is the single source of the displayed release version in Unity and Blender. Do not change BBFormat.Version unless the exchange format requires a compatibility break.
- Keep CHANGELOG.md current and verify both interface labels. Update the generated Blender add-on stub when installing a release.
