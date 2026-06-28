# Changelog

All notable changes to Project Amity will be documented in this file.

## [1.2.0] - 2026-06-28
### Added
- **Server-Side Media Duration Extraction**: Automated video duration parsing using `ffprobe` format metadata on the server, ensuring piped transcoded streams are not filtered out of Continue Watching.
- **Icon-Only Details Buttons**: Converted secondary actions in TV & Movie details screens into circular icon-only buttons with hover tooltips (`title` elements).
- **Home View Resizer integration**: Added dynamic sizing support to Home page grids (`Continue Watching` and `Recently Added`) using flex-grid layouts and global CSS property overrides.
- **About Modal**: Integrated an About overlay panel highlighting the Project Amity brand and name nod to *Jaws*.

### Fixed
- **Double Activation Keyboard Bug**: Fixed `remote-navigation.js` Enter keydown programmatic `.click()` triggering duplicates on native buttons.
- **Overlay Keydown Focus Isolation**: Fixed early returns in remote navigation listeners that blocked directional arrow key input in details, metadata editor, and folder picker modes.
- **Details Layout Nesting**: Added missing `</div>` tag in `index.html` details row that caused downstream overlays (like player) to nest inside the hidden details overlay.

## [1.1.0] - 2026-06-27
### Added
- **Playlist Manager**: Created interactive custom playlists with inline card up/down reordering.
- **Autoplay Queue**: Implemented auto-advancing TV season playback queues.
- **Thread-Safety lock**: Integrated semaphore serialization for connection queries in `GlacierDbService`.

## [1.0.0] - 2026-06-25
### Added
- Initial Release of Project Amity Local Media Server.
- Local folder indexing, metadata scraping via TMDB & TVmaze, and HTML5 Web player with custom seeking controls.
