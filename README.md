![Encora Reprise](Contents/Resources/icon-default.png)

# Encora Jellyfin Agent

This plugin scrapes recording data from [Encora.it](https://encora.it), including performer headshots and posters from [StageMedia.me](https://stagemedia.me).

If no Encora ID is found, the plugin will fall back to parsing NFO files (if present), allowing metadata to be loaded from locally prepared `.nfo` files using [NFOBuilder](https://github.com/pekempy/NFOBuilder).

<sup>Thanks to [Bubba8291](https://github.com/Bubba8291) for their work on the [StageMedia.me](https://stagemedia.me) database.</sup>

> ⚠️ The Encora API is rate-limited to **30 requests per minute**. Large libraries may take time to fully scan. The plugin will handle this automatically and retry once the limit resets.  
> 🎭 You **must** generate a StageMedia API key from your account to enable headshots/poster fetching.

---

### Contents

- [Installation](#installation)
- [Configuration](#configuration)
- [Metadata Matching](#metadata-matching)
- [Available Title Variables](#available-title-variables)
- [Fixing missing posters/headshots](#fixing-missing-posters--headshots)

---

### Installation

1. Download the plugin zip:  
   [Encora-JellyfinPlugin.zip](https://github.com/pekempy/Encora-JellyfinPlugin/releases/latest)

2. Extract and place the plugin `.dll` file into your Jellyfin server plugins directory:

   - **Windows:**  
     `C:\ProgramData\Jellyfin\Server\plugins\Encora`

   - **Linux (Systemd):**  
     `/var/lib/jellyfin/plugins/Encora`

   - **Docker:**  
     Bind mount the folder into the container at `/config/plugins/Encora`

   Your folder structure should look like this:

```
plugins/
└── Encora/
  ├── Encora.Plugin.dll
  └── other files...
```


3. Restart the Jellyfin server.

---

### Configuration

1. Go to the **Jellyfin Admin Dashboard** → **Plugins** → **Encora**.
2. Enter the required API keys:

- **Encora API key** (you must request this from Encora support)
- **StageMedia API key** (generate this from your [StageMedia profile](https://stagemedia.me/profile))

3. Customise any other settings
4. Save settings.

---

### Metadata Matching

- To be matched with Encora, your media folder should:

- Contain the Encora ID in the filename or folder name, e.g.  
 `Frozen {e-2015995}`

---

### Available Title Variables

**Text:**

- `{show}` → Show name, e.g. `Hadestown`  
- `{tour}` → Tour name, e.g. `Broadway`, `West End`  
- `{master}` → Name of the master / taper

**Dates:**

- `{date}` → `December 31, 2024`  
- `{date_iso}` → `2024-12-31`  
- `{date_usa}` → `12-31-2024`  
- `{date_numeric}` → `31-12-2024`

If the item has a variant, it will be shown as `(3)` etc., e.g. `December 31, 2024 (3)`

If the date is partial, missing month/day, you can set a **date placeholder character** in the plugin settings (e.g. `xx-xx-2024`, `2024-12-##`, etc.)

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
