# jelly-discer

A two-deck DJ practice app for Linux that plays music from a Jellyfin server.

    dotnet run -c Release --project src/JellyDiscer.App

Needs the .NET 10 SDK, `ffmpeg` and PortAudio. Optional: Rubber Band (key lock) and `secret-tool` (remembers sign-in).

1. Sign in with a Jellyfin account or Quick Connect. The token is kept in the desktop keyring.
2. In **Settings** choose the master and headphone outputs, then set the headphone delay by ear.
3. Load tracks with **A** / **B** in the library. **Analyze shown** prepares the filtered list ahead of time; the beatmatch drill needs tracks at similar tempos that have been analyzed.

Assist presets: *Guided* shows everything, *Partial* hides the phase meter and sync, *Ears only* hides BPM and grids too.

Keys (change them under **Keys**): deck A `Q` play, `W` cue, `A`/`S` nudge, `Z`/`X` tempo, `E` phones, `R` loop, `T` sync, `G` tap, `1`-`4` hot cues.
Deck B `P` play, `O` cue, `K`/`L` nudge, `N`/`M` tempo, `I` phones, `U` loop, `Y` sync, `H` tap, `7`-`0` hot cues. Arrows move the crossfader.

Knobs: drag or scroll, double-click to reset. Right-click a hot cue to clear it.

Development: `dotnet test`. Set `JELLY_URL` and `JELLY_TOKEN` to include the live server test.
`JELLY_SELFTEST=out.png` runs a silent end-to-end check; `--soak MASTER CUE SECONDS` checks two outputs for dropouts.
