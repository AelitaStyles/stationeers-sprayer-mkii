# Assets

The custom Spray Gun MkII 3D model (`sprayermkii.assets` and its Blender source files in `Assets/Source/`) is based on Stationeers' vanilla Spray Gun mesh, so those files are excluded from source control via `.gitignore`.

You do not need them to build or test the mod:

- If `Assets/sprayermkii.assets` is present when you build, it is embedded into the DLL and deployed alongside the mod.
- If it is absent, the project still builds cleanly and the Spray Gun MkII falls back to the vanilla Spray Gun model with twin paint can slots.
