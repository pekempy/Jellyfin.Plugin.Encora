![Encora Reprise](https://raw.githubusercontent.com/pekempy/Jellyfin.Plugin.Encora/main/JellyfinPluginBanner.png)

# Encora Jellyfin Agent

Matches your theatrical recording bootlegs against [Encora.it](https://encora.it), pulling in cast, dates, tours, notes, subtitles, and NFT status, plus posters/headshots from [StageMedia.me](https://stagemedia.me).

- **Movies** - each video file is a standalone recording.
- **TV Shows** - Series = the show, Season = the tour, Episode = a recording date.
- **Music** - Artist = the show, Album = one recording (tour + date), Track = an Act.

If no Encora ID is found on a Movie, it falls back to parsing `.nfo` files (e.g. from [NFOBuilder](https://github.com/pekempy/NFOBuilder)).

<sup>Thanks to [Bubba8291](https://github.com/Bubba8291) for their work on the [StageMedia.me](https://stagemedia.me) database.</sup>

> Requires Jellyfin `10.11.0+`. Needs a StageMedia API key for headshots/posters. Rate limiting is automatic. Jellyfin can't play `.VOB` directly - remux with [MKVToolNix](https://mkvtoolnix.download/) first (keep the originals for trading). TV/Music support is newer than Movies and less battle-tested - report anything odd.

---

### Contents
- [Known limitations](#known-limitations)
- [Installation](#installation)
- [Configuration](#configuration)
- [Library Structure](#library-structure)
- [Title Format Variables](#title-format-variables)
- [Troubleshooting](#troubleshooting)
- [Fixing missing posters/headshots](#fixing-missing-posters--headshots)

---
### Known limitations

1. Act-split files (Act 1/Act 2, or split Music tracks) each get the *entire* recording's subtitles, not just their portion.
2. Posters are per-recording, not per-folder - StageMedia has no Series/Album-level poster, so whichever recording matches first decides the parent folder's poster (edit manually to override).
3. Flat TV folders (no per-tour subfolder) get bucketed into one "Season Unknown" - see [Library Structure](#library-structure).

---

### Installation

**Option A: Plugin Repository (recommended)**

1. **Dashboard → Plugins → Repositories → + Add Repository**
2. Name: `Encora`, URL: `https://raw.githubusercontent.com/pekempy/Jellyfin.Plugin.Encora/main/manifest.json`
3. Save, open **Catalog**, install **Encora** under General.
4. Restart Jellyfin.

**Option B: Manual**

1. Download the latest `Jellyfin.Plugin.Encora.zip` from [Releases](https://github.com/pekempy/Jellyfin.Plugin.Encora/releases).
2. Extract into your plugins directory so you end up with `plugins/Jellyfin.Plugin.Encora/Jellyfin.Plugin.Encora.dll` (+ other files):
   - Windows: `C:\ProgramData\Jellyfin\Server\plugins\Encora` (or `%LOCALAPPDATA%\jellyfin\plugins`)
   - Linux (systemd): `/var/lib/jellyfin/plugins/Encora`
   - macOS: `/Users/{username}/Library/Application Support/jellyfin/plugins/`
   - Docker: bind mount into `/config/plugins/Encora`
3. Restart Jellyfin.

---

### Configuration

1. **Dashboard → My Plugins → Encora**. Tabs: **Info** (variable reference), **API Keys**, and one tab per library type (**Videos - Movie/TV**, **Audios - Music**) with Enable toggle, library scoping, a live file/folder naming diagram, title formats, field mapping, and media toggles.
2. Enter your **Encora API key** (request from Encora support) and **StageMedia API key** (from your [StageMedia profile](https://stagemedia.me/profile)) on the API Keys tab of the plugin settings.
3. Enable the library type(s) you use, customise as needed, and save.

Saving automatically adds Encora (and StageMedia, for Movies) to any library you've enabled/scoped, without unchecking anything else. To restrict a library to *only* Encora/StageMedia, edit it manually under **Manage library** → uncheck other downloaders/fetchers → check **Save artwork into media folders** → **Scan library** (or **Replace all metadata** if it already has metadata from elsewhere).

Subtitles aren't downloaded automatically during a metadata refresh - "Encora" shows up as a normal subtitle provider (like OpenSubtitles) under each library's **Manage Library → Subtitles** tab, so it's used by Jellyfin's own subtitle search and the "Download missing subtitles" task.

---

### Library Structure

A file needs an Encora ID attached via one of four conventions:

1. **ID in filename**: `Frozen {e-2015995}.mkv`
2. **ID in folder name**: `Frozen {e-2015995}/Frozen.mkv`
3. **`.encora-<id>` marker file** next to the video: `Frozen/.encora-2015995`
4. **`.encora-id` file** next to the video, containing just the ID

For **Movies**, any convention works directly in the movie's folder.    
`../Frozen - 2013-11-27 {e-2015995}`

For **TV Shows**: Series folder = the show, a subfolder under it = a Season (named after the tour automatically), files inside = Episodes. Conventions 3/4 identify *every* file in their folder, so give each dated recording its own subfolder when using them; 1/2 don't have that restriction.    
`../Hadestown/Broadway/Hadestown - 2019-03-28 (New York, NY) {e-1234}`

For **Music**: same idea - Artist folder, Album subfolder per recording, track file(s) inside. Same one-file-per-folder caveat for conventions 3/4.    
`../Wicked/Broadway/Wicked - 2023-01-01 {e-5678}`

---

### Title Format Variables

Every Title Format field has a click-to-insert palette in-app; full reference can be found in the `Info` tab of the plugin settings page.

Date variants get a suffix, e.g. `December 31, 2024 (3)`. Missing day/month uses a configurable placeholder (default `x`, e.g. `2024-11-xx`). Episode/Track titles don't have `{show}`/`{tour}` (those come from the parent Series/Season or Artist/Album), but all title formats are fully customisable.

**Auto-refresh** (API Keys tab, off by default) periodically re-checks already-matched items for subtitle/cast/NFT changes, as a normal scheduled task ("Refresh Encora Metadata").

**Duplicate season cleanup** runs automatically as a second pass right after any library scan finishes. Jellyfin's own scanner can occasionally leave two Season rows behind for the same tour (e.g. a race when several recordings are added to a season folder at once) - one real, one a stale partial duplicate. This pass keeps whichever Season has the most episodes and removes the other's database record; files on disk are never touched.

---

### Troubleshooting

1. Jellyfin is `10.11.0`+.
2. The plugin folder is extracted correctly (one `.dll` + a few other files) in the right plugins directory.
3. Restart Jellyfin after installing.
4. **My Plugins → Encora** shows **Status: Active**.
5. The relevant library type is enabled in plugin settings, and not accidentally scoped away from your library under **Which libraries**.
6. Check **Dashboard → Logs** for errors around the time you restarted (bad plugin load, read-only permissions, etc).
7. Still stuck? Ask in the Encora Prelude, `#media-servers`.

If logs show `Unsupported codec with id 0 for input stream 2`, that file has multiple video/audio streams Jellyfin can't handle - fix with:
```
ffmpeg -i "{input_file}" -map 0:v -map 0:a -c copy "fixed.mp4"
```
then rename and re-scan.

---

### Fixing Missing Posters / Headshots

1. Visit [StageMedia.me](https://stagemedia.me) and upload the missing image (while signed in).
2. In Jellyfin, hit **Refresh Metadata** on the item (or **Identify** → re-enter the same title → save, if that doesn't pick it up).

Contributions help everyone using this agent - thank you!

---
