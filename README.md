![Encora Reprise](https://raw.githubusercontent.com/pekempy/Jellyfin.Plugin.Encora/master/JellyfinPluginBanner.png)

# Encora Jellyfin Agent

This plugin matches your theatrical recording bootlegs against [Encora.it](https://encora.it), pulling in cast, dates, tours, notes, subtitles, and NFT status, plus posters/headshots from [StageMedia.me](https://stagemedia.me).

It supports three kinds of Jellyfin library:
- **Movies** - each video file is matched as a standalone recording.
- **TV Shows** - Series = the show, Season = the tour, Episode = a recording date.
- **Music** - Artist = the show, Album = one recording (tour + date), Track = an Act (or the full recording, if not split).

If no Encora ID is found on a Movie, the plugin falls back to parsing NFO files (if present), allowing metadata to be loaded from locally prepared `.nfo` files using [NFOBuilder](https://github.com/pekempy/NFOBuilder).

<sup>Thanks to [Bubba8291](https://github.com/Bubba8291) for their work on the [StageMedia.me](https://stagemedia.me) database.</sup>

> **Requires Jellyfin `10.11.0` or later** (the plugin is built against .NET 9, matching Jellyfin 10.11+). It will not load on older versions.
> You **must** generate a StageMedia API key from your account to enable headshots/poster fetching.
> The plugin automatically respects Encora's API rate limit (it reads the limit from Encora's own responses) - you don't need to do anything, but very large libraries may take a while to fully scan the first time.
> TV Show and Music library support is newer than Movie support and has had less real-world testing - if you hit something odd, please open an issue.
> Jellyfin does not support playing `.VOB` files directly. Remux/merge them into a single file using [MKVToolNix](https://mkvtoolnix.download/) or [AviDemux](https://avidemux.sourceforge.net/) before adding them to your library. Keep the original VOBs around too - you'll still need them for trading.
---

### Contents
- [Current problems](#current-problems)
- [Installation](#installation)
- [Configuration](#configuration)
- [Library Structure](#library-structure)
- [Title Format Variables](#title-format-variables)
- [Auto-Refresh & Rate Limiting](#auto-refresh--rate-limiting)
- [Troubleshooting](#troubleshooting)
- [Fixing missing posters/headshots](#fixing-missing-posters--headshots)

---
### Current problems

This plugin is under active development and has a couple of known quirks:
1. If a recording is split into Act 1 and Act 2 (or Act-split Music tracks), each file gets the *entire* recording's subtitles rather than just the portion for that act.
2. It is only possible to decide what poster a specific recording/Movie has - StageMedia does not provide posters at the folder/Series/Album level. Whichever recording's poster gets fetched first decides what the parent folder looks like, unless you edit and upload it manually. This behaviour likely can't be changed.
3. TV and Music matching are newer than Movie matching. The mechanics are the same underneath, but they haven't been run against as many real libraries yet.

---

### Installation

#### Option A: Plugin Repository (recommended)

1. Go to **Jellyfin Admin Dashboard** → **Plugins** → **Repositories** → **+ Add Repository**.
2. Set the **Repository Name** to `Encora`, and the **Repository URL** to:
   ```
   https://raw.githubusercontent.com/pekempy/Jellyfin.Plugin.Encora/master/manifest.json
   ```
3. Save, then go to the **Catalog** tab, find **Encora** under **General**, and install it.
4. Restart the Jellyfin server.

This lets Jellyfin notify you of and install updates automatically, without manually downloading zips.

#### Option B: Manual Installation

1. Head to **Releases** and download the latest version of **Jellyfin.Plugin.Encora.zip**:
   [Releases](https://github.com/pekempy/Jellyfin.Plugin.Encora/releases)
2. Extract and place the entire folder containing the `.dll` into your Jellyfin server plugins directory:

   - **Windows:**  
     `C:\ProgramData\Jellyfin\Server\plugins\Encora`
     or
     `%LOCALAPPDATA%\jellyfin\plugins`

   - **Linux (Systemd):**  
     `/var/lib/jellyfin/plugins/Encora`

   - **MacOS:**  
     `/Users/{username}/Library/Application Support/jellyfin/plugins/`

   - **Docker:**  
     Bind mount the folder into the container at `/config/plugins/Encora`

   Your folder structure should look like this:

```
plugins/
└── Jellyfin.Plugin.Encora/
  ├── Jellyfin.Plugin.Encora.dll
  └── other files...
```


3. Restart the Jellyfin server.

---

### Configuration

1. Go to the **Jellyfin Admin Dashboard** → **My Plugins** → **Encora**. Settings are split across five tabs:
   - **Info** - your installed version, links, and a full reference table of every `{variable}` used in title formats.
   - **API Keys** - your Encora and StageMedia API keys, plus the auto-refresh interval (see [below](#auto-refresh--rate-limiting)).
   - **Videos - Movie Library**, **Videos - TV Library**, **Audios - Music Library** - one tab per library type, each with the same layout:
     - **Enable** toggle for that library type.
     - **Which libraries** - by default a setting applies to *every* library of that type on your server; tick specific libraries here to restrict it to just those.
     - **Library Structure** - a live diagram showing how to name your files/folders for that library type, with a switch between the different ID-matching conventions (see [below](#library-structure)).
     - **Title** - the title format(s) for that library type, each with a click-to-insert variable palette and a live preview so you can see exactly what you'll get.
     - **Metadata** - a field-mapping table letting you pick which Encora/StageMedia field feeds each Jellyfin field (Overview, Tagline, Studio, Production Location), plus genre-tag and NFT-tag toggles.
     - **Media** - subtitles, poster/cover art, and thumbnail generation toggles.
2. Enter your API keys:
   - **Encora API key** (you must request this from Encora support)
   - **StageMedia API key** (generate this from your [StageMedia profile](https://stagemedia.me/profile))
3. Enable whichever library type(s) you use, and customise anything else you want.
4. Save settings.

Every time you save settings, the plugin automatically makes sure it isn't excluded from whichever libraries you've enabled/scoped it for - if a library already has a custom metadata downloader/image fetcher selection (from Jellyfin's own **Manage Library** screen), Encora (and StageMedia, for Movies) gets added to it. It never unchecks anything else for you.

If you'd still like to trim a library down to *only* Encora/StageMedia (e.g. to stop a duplicate scraper from overwriting fields), do it manually:
1. Go to **Jellyfin Admin Dashboard** → **Libraries** → **Libraries**
2. Click the **three dots** of the library you want the plugin to work on, and select **Manage library**
3. Uncheck whichever other **Metadata downloaders** and **Image fetchers** you don't want, keeping **Encora** and **StageMedia** (StageMedia only applies to images) checked
4. Check the option **Save artwork into media folders**
5. Click **OK**, click the **three dots** again, select **Scan library** and press **Refresh** (Alternatively select **Replace all metadata** with **Replace existing images** checked if you have previously had metadata in your library)

Note: If you have multiple libraries of the same (or just partly the same) directory, make sure to do this for all of them. However it is best practice to not keep duplicate libraries as this will just cause issues.

---

### Library Structure

To be matched with Encora, a video/audio file needs an Encora ID attached to it somewhere, via one of four conventions (pick whichever suits you - the plugin's settings page has a live diagram for each, per library type):

1. **ID in filename**, e.g. `Frozen {e-2015995}.mkv`
2. **ID in folder name**, e.g. `Frozen {e-2015995}/Frozen.mkv`
3. **`.encora-<id>` marker file** next to the video, e.g. `Frozen/.encora-2015995`
4. **`.encora-id` file** next to the video, containing just the ID, e.g. `Frozen/.encora-id` with contents `2015995`

For **Movies**, any of the four work directly in the movie's folder.

For **TV Shows** (content type "Shows"): the Series folder becomes the Series, any subfolder under it becomes a Season (name it after the tour - Encora renames it for you), and files inside become Episodes. Conventions 3 and 4 (marker/ID files) identify *every* file in their folder, so if a Season folder has more than one dated recording, each recording needs its own subfolder when using those two conventions. Convention 1 or 2 don't have that restriction.

For **Music** (content type "Music"): same idea - Artist folder, then an Album subfolder per recording (name it anything, Encora renames it), then track file(s) inside. Same one-file-per-folder caveat applies to conventions 3 and 4 if an Album has multiple Act files.

---

### Title Format Variables

Every Title Format field in the plugin settings has its own click-to-insert variable palette, but here's the full reference (also shown on the **Info** tab in-app):

| Variable | Maps to | Available in |
|---|---|---|
| `{show}` | Show name. Gets `Act 1`/`Act 2` etc. appended if detected in the filename (Movies only). | Movie, Series, Artist |
| `{date}` | Long-form date, e.g. `December 31, 2024`. Appends `(matinée)` and, for episodes/tracks, `(Act N)` automatically when detected. | Movie, Episode, Album |
| `{date_iso}` | ISO date, e.g. `2024-12-31` | Movie, Episode, Album |
| `{date_usa}` | US-order date, e.g. `12-31-2024` | Movie, Episode, Album |
| `{date_numeric}` | Day-first numeric date, e.g. `31-12-2024` | Movie, Episode, Album |
| `{tour}` | Tour name | Movie, Season, Album |
| `{master}` | Name of the recording's master (whoever recorded/filmed it) | Movie, Episode, Album |
| `{venue}` | Venue name | Movie, Episode, Series, Season, Artist, Album |
| `{city}` | City | Movie, Episode, Series, Season, Artist, Album |
| `{act}` | Act number, only present when "Act N" is detected in the filename | Track |

If the item has a date variant, it's appended, e.g. `December 31, 2024 (3)`.

If a date is missing its month/day, the plugin substitutes a placeholder character you can configure per library type (default `x`, e.g. `2024-11-xx`).

Episode and Track titles don't have `{show}`/`{tour}` - those come from the Series/Season or Artist/Album instead. Series/Season/Artist titles default to `{show}`/`{tour}`/`{show}` respectively but are fully customisable, same as everything else.

---

### Auto-Refresh & Rate Limiting

- **Rate limiting** is automatic - the plugin reads Encora's own `X-RateLimit-*`/`Retry-After` response headers and backs off exactly as long as Encora tells it to. There's nothing to configure.
- **Auto-refresh** (API Keys tab) periodically re-checks items you've already matched, so subtitle/cast/NFT changes on Encora's side get picked up without you having to manually "Replace all metadata". It's off by default (set to 0 hours); once enabled it runs as a normal Jellyfin scheduled task ("Refresh Encora Metadata", under Dashboard → Scheduled Tasks → Encora), where you can also review/adjust its schedule directly.

---

### Troubleshooting
If you're having trouble getting the plugin to work, please confirm that you've done everything below:

1. Make sure Jellyfin is on `10.11.0` or later.
2. Double check that the downloaded folder contains a single .dll file, among three other files.
3. Ensure that you've extracted and placed the entire downloaded folder in the correct directory. If you're on Windows that should be under "ProgramData" (not to be confused with "Program Files"), or in some cases under %appdata%
4. Restart Jellyfin.
5. Under **My plugins**, if the **Encora** plugin shows up and its status is **Status Active**, refer to the **Configuration** step to enable the plugin for your library.
6. Check that the relevant library type is **enabled** in the plugin settings (Videos - Movie/TV Library, or Audios - Music Library), and that you haven't accidentally scoped it to a different library under **Which libraries**.
7. If it still doesn't work, go into the **Jellyfin Admin Dashboard** → **Logs**, and find the most recent log and look for any errors happening around the time when you restarted Jellyfin. Look for any errors regarding plugins being loaded incorrectly, or Jellyfin not having the proper permissions/being read only.
8. If all seems lost, head into the Encora Discord, accept the rules and ask for help in the channel #media-server-agents

---

If your plugin is working, but in your logs you see errors such as `Unsupported codec with id 0 for input stream 2`:
- Identify which recording it is, the line above should say something like `file:/{path}`
- Run [ffmpeg](https://github.com/FFmpeg/FFmpeg) on the offending file to only keep 1 audio and 1 video stream:
    - `ffmpeg -i "{input_file}" -map 0:v -map 0:a -c copy "fixed.mp4"` <- replace {input_file} with the path to the video
    - Rename fixed.mp4 to something you would prefer and re-scan Jellyfin

---

### Fixing Missing Posters / Headshots

If headshots or posters are not showing after matching:

1. Visit [StageMedia.me](https://stagemedia.me)
2. Contribute missing images by uploading them.
3. In Jellyfin, hit **Refresh Metadata** on the affected item.
4. If nothing updates, try **Identify**, re-enter the same title, and save.

Contributing helps _everyone_ using this agent—thank you for supporting the community!  
Make sure you're signed into your StageMedia account when uploading.

---
