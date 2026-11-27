# Sprayer MkII

A Stationeers mod that makes painting pipes, cables, and chutes faster and adds a craftable two-can Spray Gun MkII.

## Features

- **Whole-network painting:** Spraying any pipe (including inline tanks), cable, or chute with a Spray Gun paints the entire connected network at once. Standalone tanks and attached devices are left alone. Hold **Shift** (configurable) to paint a single segment instead.
- **Spray Gun MkII:** Craftable in the Tier 2 Tool Manufactory (10 Steel, 10 Silicon, 5 Electrum). Holds a primary and secondary paint can side by side to paint networks in a repeating stripe pattern. Its body can also be painted.
- **Bigger paint cans:** Scales spray paint can capacity (default `5x`, giving 100 sprays by hand).
- **Fixed Spray Gun efficiency:** Fixes the vanilla Spray Gun applying efficiency twice and makes efficiency configurable (default `6x`, giving 600 sprays per `5x` can).
- **Modded network support:** Works with modded networks built on LibConstruct, such as ReVolt cable trays.
- **Save safe:** Safe to add to or remove from existing saves.

## Requirements

- Stationeers
- BepInEx 5
- [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad) (required to load BepInEx plugin mods in Stationeers)

## Configuration

All settings can be changed in-game via StationeersLaunchPad's mod config menu or in the generated BepInEx config file (`stationeers-sprayer-mkii.cfg`):

| Section | Key | Default | Description |
| --- | --- | --- | --- |
| `Paint Cans` | `CapacityMultiplier` | `5` | Multiplier for spray paint can capacity (`1`–`100`). |
| `Spray Gun` | `Efficiency` | `6` | How many times further a paint can goes in a Spray Gun compared to by hand (`1`–`32`). |
| `Spray Gun` | `SinglePaintModifierKey` | `LeftShift` | Hold this key while spraying to paint a single segment instead of the whole network. |
| `Spray Gun` | `StripePattern` | `PPS` | Repeating stripe pattern when Spray Gun MkII has two paint cans loaded. Use `P`/`1` for Primary and `S`/`2` for Secondary (up to 16 steps). |

## Building from Source

### Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/) (capable of targeting `net48`)
- A local Stationeers installation with BepInEx and StationeersLaunchPad installed

### Build

The project automatically looks for Stationeers in standard Windows and Linux Steam directories. If yours is elsewhere, either:

- Set the `STATIONEERS_DIR` environment variable,
- Pass `-p:StationeersDirectory="/path/to/Stationeers"` to `dotnet build`, or
- Set `<StationeersDirectory>` in `SprayerMkII.props`.

Then build:

```bash
dotnet build -c Release
```

The compiled plugin is written to `bin/Release/net48/SprayerMkII.dll`.

### Local Deploy

On Linux (including Proton) or Git Bash on Windows, you can build and copy the mod straight into your local Stationeers `mods` folder:

```bash
./deploy.sh
```

Run `./deploy.sh --help` for options (`--config`, `--mods-dir`, `--no-build`).

### Project Layout

```text
About/            Steam Workshop metadata and preview images
Assets/           Optional local AssetBundle (gitignored; see Assets/README.md)
src/
  SprayerMkIIPlugin.cs   BepInEx entry point and configuration
  MkII/                  ItemSprayGunMkII prefab, model/slots, and recipe/localisation
  Network/               Network discovery and stripe-order traversal
  Patches/               Prefab, SprayGun, and network spraying Harmony patches
Template/         MSBuild props and git-tag version generator
```

### Note on Game-Derived Assets

The custom Spray Gun MkII 3D model (`Assets/sprayermkii.assets` and its Blender source files) is derived from Stationeers' vanilla Spray Gun mesh and is excluded from this repository via `.gitignore`. You do not need it to build or test the mod: without the bundle, Spray Gun MkII uses the vanilla Spray Gun mesh with twin paint can slots.

## Credits

Started from [stationeers-example-mod](https://github.com/aproposmath/stationeers-example-mod) by aproposmath.

## License

MIT License — see [LICENSE](LICENSE).
