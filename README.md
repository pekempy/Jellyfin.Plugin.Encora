![Encora Reprise](https://i.imgur.com/M3ShJse.png)

# Encora Jellyfin Agent

This plugin scrapes recording data from [Encora.it](https://encora.it), including performer headshots and posters from [StageMedia.me](https://stagemedia.me).

If no Encora ID is found, the plugin will fall back to parsing NFO files (if present), allowing metadata to be loaded from locally prepared `.nfo` files using [NFOBuilder](https://github.com/pekempy/NFOBuilder).

<sup>Thanks to [Bubba8291](https://github.com/Bubba8291) for their work on the [StageMedia.me](https://stagemedia.me) database.</sup>

> ⚠️ The Encora API is rate-limited to **30 requests per minute**. Large libraries may take time to fully scan. 
> 🎭 You **must** generate a StageMedia API key from your account to enable headshots/poster fetching.
> Make sure your Jellyfin is updated. This is tested on `10.10.7`
---

### Contents

- [Installation](#installation)
- [Configuration](#configuration)
- [Troubleshooting](#troubleshooting)
- [Metadata Matching](#metadata-matching)
- [Available Title Variables](#available-title-variables)
- [Fixing missing posters/headshots](#fixing-missing-posters--headshots)

---

### Installation

1. Head to **Releases** and download the latest version of **Jellyfin.Plugin.Encora.zip**:
   [Releases](https://github.com/pekempy/Jellyfin.Plugin.Encora/releases)
2. Extract and place the entire folder containing the `.dll` into your Jellyfin server plugins directory:

   - **Windows:**  
     `C:\ProgramData\Jellyfin\Server\plugins\Encora`
     or
     `C:\Windows\Users\youruser\AppData\Local\jellyfin\plugins`

   - **Linux (Systemd):**  
     `/var/lib/jellyfin/plugins/Encora`

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

1. Go to the **Jellyfin Admin Dashboard** → **My Plugins** → **Encora**.
2. Enter the required API keys:

- **Encora API key** (you must request this from Encora support)
- **StageMedia API key** (generate this from your [StageMedia profile](https://stagemedia.me/profile))

3. Customise any other settings
4. Save settings.


To enable the plugin for the library you want, do the following:
1. Go to **Jellyfin Admin Dashboard** → **Libraries** → **Libraries**
2. Click the **three dots** of the library you want the plugin to work on, and select **Manage library**
3. Uncheck all **Metadata downloaders** and **Image fetchers**, with the exception of **Encora** and **StageMedia**
4. Click **OK**, click the **three dots** again, select **Scan library** and press **Refresh** (Alternatively select **Replace all metadata** with **Replace existing images** checked if you have previously had metadata in your library)

---

### Troubleshooting
If you're having trouble getting the plugin to work, please confirm that you've done everything below:

1. Make sure Jellyfin is updated to version 10.10.7
2. Double check that the downloaded folder contains a single .dll file, among three other files.
3. Ensure that you've extracted and placed the entire downloaded folder in the correct directory. If you're on Windows that should be under "ProgramData" (not to be confused with "Program Files"), or in some cases under %appdata%
4. Restart Jellyfin.
5. Under **My plugins**, if the **Encora** plugin shows up and its status is **Status Active**, refer to the **Configuration** step to enable the plugin for your library.
6. If it still doesn't work, go into the **Jellyfin Admin Dashboard** → **Logs**, and find the most recent log and look for any errors happening around the time when you restarted Jellyfin. Look for any errors regarding plugins being loaded incorrectly, or Jellyfin not having the proper permissions/being read only.
7. If all seems lost, head into the Encora Discord, accept the rules and ask for help in the channel #media-server-agents

---

### Metadata Matching

- To be matched with Encora, your media folder should:

- Contain the Encora ID in the filename or folder name, e.g.  
 `Frozen {e-2015995}`
- Contain a `.encora-{id}` file inside the folder, e.g.
  `Frozen - Broadway - May, 2022/.encora-123`
- Contain a `.encora-id` file inside the folder, e.g.
  `Frozen - Broadway - May, 2022/.encora-id` with file contents `123`

---

### Available Title Variables

**Text:**

- `{show}` → Show name, e.g. `Hadestown`  - This will receive a suffix of `Act 1` / `Act 2` if your files contain those.
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
