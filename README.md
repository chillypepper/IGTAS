# TAS BepInEx Plugin for IGTAP

Tool-Assisted Speedrun (TAS) plugin for **IGTAP — *An Incremental Game That’s Also a Platformer***.

If you have excellent taste in games, and therefore a love of both incremental and platforming games, you can find it here: https://store.steampowered.com/app/4364730/IGTAP_an_Incremental_Game_Thats_Also_a_Platformer/

This README is human written, but expect dense technical and LLM written content in the `docs` folder. That's where you go if you want to explore the mechanics of this game and build tools for them.

## !! **IMPORTANT NOTE** !!

This plugin is very, VERY early in development and fully experimental. This first release in particular is me pulling out bits of what's working from a mess of personal small experiments, and so you'll quickly come across limitations and things mentioned in the docs that link nowhere.

To set your expectations for what's currently on offer, there are 3 useful [utilities](#utility-controls) for gameplay, on top of a _seemingly_ robust TAS system with input files (eg [to-first-clone](./inputs/to-first-clone.tas)) that you can record and play back easily. This first version is primarily aimed at people wanting to try the tool or see the core determinism handling.

Editing long TAS files is still tedious with missing controls such as frame advance, save states, and live editing, but this is useful for quick tests and may be a good launch point for other developers interested in building tools for this game. I'll be shipping bits and pieces from my tools as I clean them up, but this is something for now.

For devs: a lot of the core work is still in progress (ESPECIALLY the BehaviourTakeover.cs work) and so I haven't cleaned up all the slop yet. I've been depending pretty heavily on tests rather than proper code reviews for most of the work until things get stable, and said tests aren't even being shipped with this version because they're spaghetti. Only specific sections have had proper human review, and heavily in progress items like BehaviourTakeover have had little to none. It's all useful reference, but I'm only shipping this now because there's interest in what's there, and not because it's anywhere near ready.

This work is forked from the original https://github.com/pseudo-psychic/IGTAS/

---

## Requirements

* **BepInEx 5.4.23.5 (x64)**
  https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5

* Plugin download:
  https://github.com/chillypepper/IGTAS/releases/

Tested on **June 25, 2026**, not long after the hard mode release. Things may break across versions!

---

## Building this plugin

If you're up to building this on your own machine, I'd recommend that over downloading a pre-built version. Doubly so if you're intending to look into working on your own mods or an extension to this in the future. Some notes on that below:

You'll need dotnet, with a great guide on installing that here: https://learn.microsoft.com/en-us/dotnet/core/install/windows (includes a dropdown to swap to other operating systems)

With that installed you'll basically just need to run this project in release mode, with a cli example being:

```
dotnet build -c Release
```

And for anyone who's not on Windows, or has their game installed in a non default location, you can also override the game dir like this:

```
dotnet build -c Release -p:GameManagedDir="/home/USER/.steam/debian-installation/steamapps/common/IGTAP an Incremental Game That's Also a Platformer Demo/IGTAP_Data/Managed"
```

That's the default Ubuntu example (just replace USER) but you can replace that with any link to the `Managed` directory of your game wherever it's installed.

---

## Installation

### 1️⃣ Install BepInEx

Download:

```
BepInEx_win_x64_5.4.23.5.zip
```

Open Steam → **IGTAP Demo**

Click:

```
Manage → Browse local files
```

#### Example (Steam menu)

![Browse Local Files](images/steam-browse-local-files.png)

---

Open the BepInEx zip and **copy ALL contents** into the game directory
(the folder containing `IGTAP.exe`).

#### Files inside the BepInEx archive

![BepInEx Zip Contents](images/bepinex-zip-contents.png)

---

### 2️⃣ Generate BepInEx Folders

1. Run the game once using `IGTAP.exe`
2. Close the game

This automatically creates:

```
BepInEx/plugins
```

#### NOTE FOR LINUX USERS

Take a look at https://docs.bepinex.dev/articles/advanced/steam_interop.html and the linked articles to get this working - basically you'll need an extra step to ensure the bepinex wrapper runs around the game, and none of this will work without it. It's a pretty straightforward steam command adjustment though

---

### 3️⃣ Install the TAS Plugin

1. Download the plugin `IGTAS.dll` from Releases.
2. Place the file into:

```
BepInEx/plugins
```

---

### 4️⃣ Set up your TAS inputs

1. Download the `inputs.zip` from Releases.
2. Place the file into:

```
BepInEx/config/TAS
```

NOTE: You may have to create this directory. This is where the plugin will look for all of the TAS inputs (and where your recordings will go too).

#### A note for those working from a cloned setup

In this particular case I recommend you simply symlink your `BepInEx/config/TAS` folder to this project's `./inputs/` directory, because that's what the plugin will be working from.

As a quick intro to what these are (worth a google as I don't have a canonical doc for this): a symlink is basically a pointer to another folder, so that you can create a "fake" `BepInEx/config/TAS` directory, that your operating system will treat as fully real, but all of your files will instead read and write from another directory of your choosing.

For Linux that would be something like:

ln -s "/home/USER/IGTAS/inputs" "/home/USER/.steam/debian-installation/steamapps/common/IGTAP an Incremental Game That's Also a Platformer Demo/BepInEx/config/TAS"

Where the first param is this project's `inputs/` directory, and the second is the `BepInEx/config/TAS` folder inside your game directory.

---

### 5️⃣ Run the Game

Launch the game normally through Steam or the executable.

The plugin loads automatically.

---

## ✅ Final Folder Structure

Your game folder should look similar to this:

```
IGTAP/
│
├── BepInEx/
├── D3D12/
├── IGTAP_Data/
├── MonoBleedingEdge/
│
├── .doorstop_version
├── doorstop_config.ini
├── winhttp.dll
│
├── IGTAP.exe
├── UnityPlayer.dll
└── UnityCrashHandler64.exe
```

#### Example completed install

![Finished Folder Layout](images/final-folder-structure.png)

---

## Usage

**Input:** Keyboard only

*Can be rebinded through settings menu on left-hand side*

### Recording Controls

| Key | Action |
| --- | --- |
| F6 | Start recording |
| F7 | Stop recording / playback |
| F8 | Start playback |

### Utility Controls

| Key | Action |
| --- | --- |
| F2 | Cycle through ability combinations (eg wallclimb + dash) |
| F3 | Activate no-clip/debug movement mode |
| F4 | Toggle hitbox overlay |

---

## Making a TAS

With the limited tooling currently provided, long runs are a real pain to author, so most of the time I expect people will use F6 to record a run from a stable position (like a checkpoint) and then play it back with F8 as needed to test things

**NOTE**: Playback (F8) **always reads `main.tas`** from your TAS folder, NOT the most recently recorded file! Recordings are saved to the `recordings/` subfolder under a timestamped name, so the typical loop is: record (F6 to start, F7 to stop) then just copy and paste the contents into `main.tas` (optionally with a `@frame_rate=-1` at the top for max speed) and play it back (F8). Larger runs typically use `@read_file=tas-file-name` rather than pasting everything into one file, but if you're looking for a quick setup this is easiest at the moment.

For the `.tas` file format and more about the special commands, see [docs/tas-inputs.md](docs/tas-inputs.md). NOTE This is one of the technical docs though, so it's a bit dense - I've included examples of the 2 main commands and then 2 example tas files to copy in the repo [inputs](./inputs/) for reference.

For the more ambitious among you, you can absolutely just start working on those TAS files and letting them run up until you hit issues, then lower the frame_rate and keep tweaking values > restarting runs over and over again to build runs. I personally will not be doing that until I've built up more tools, but if you're hardcore I respect it. It's quite possible there will need to be minor adjustments if I find frame specific issues in bugfixes but I'd feel pretty confident the file format and most of what you record now will remain useful for the life of this demo, so it won't be fully wasted time.

## Known Issues

* Using any inputs, eg keyboard or controller, during a run will interrupt it.
* The deterministic TAS setup is EXPERIMENTAL, and relies on clock overrides, FixedUpdate/Update overrides, and specific takeovers for behaviours that work off uncontrollable wall clocks. If you're looking into these mechanics specifically, the docs in this repo are dense technical docs that explore a lot of the challenges and solutions worked through on this: [README](./docs/README.md)

### Controller Fix

If playback does not work:

1. Unplug the controller
2. Move around in-game using the keyboard for a moment (this resets input to keyboard)
3. Start playback again (without reconnecting the controller)

---

## Troubleshooting

**Plugin not loading?**

Check:

* `BepInEx` folder exists beside `IGTAP.exe`
* `winhttp.dll` is present
* Plugin `.dll` is inside `BepInEx/plugins`
* Game was launched once after installing BepInEx

If BepInEx installed correctly, a console window should appear when the game starts.

There is no official support for this repo, but if you find a genuine bug (meaning you have a well written up way to reproduce the issue) feel free to submit an issue or PR. I make no promises that I'll implement or respond to them, but I am intending to keep working on this in the short term at least.

There is also a `#tas` channel (along with a more general `speedrunning` channel) in the official discord (linked in the main menu of the steam game) which MIGHT have info about issues with this, but please note that it is NOT a support channel! It's for collaboration on TAS work first and foremost, but it's worth a search around for issues there if you have them.

---

## License

See repository license for details.
