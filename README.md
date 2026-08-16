# Gambonanza mods

Source and releases for mods I write for
**[Gambonanza](https://store.steampowered.com/app/3509230/)**, built on the
[GambonanzaMods framework](https://github.com/bentrd/GambonanzaMods).

One folder per mod, each holding its own source, its art, and the built
artefact that gets released.

| Mod | What it does | Latest |
| --- | --- | --- |
| [ImpatientGambit](ImpatientGambit/) | Skip every stage straight to its boss, earn 4x gold. | [releases](../../releases?q=ImpatientGambit) |

## Installing a mod

Grab the zip from [Releases](../../releases) and unpack it into your game's
`Mods/` folder, so you end up with `Gambonanza/Mods/<ModName>/` containing a
`mod.json` and a `.dll`. You need the framework installed first - the
[Gambonanza Mod Manager](https://bentrd.github.io/GambonanzaMods/) does that
part with a button.

Each mod's README lists what else it needs; most gambits need the `GambitApi`
mod, which ships with the framework.

## Why the built DLLs are committed

Gambonanza mods compile against the game's own `Assembly-CSharp.dll` and its
Unity assemblies. Those are copyrighted by Blukulele and Unity, so they are in
no repository - the framework's `build.sh` copies them out of your installed
game into a local `refs/` folder that is never committed.

The consequence: **GitHub Actions cannot compile these mods.** There is no
legal way to give a runner the assemblies it would need.

So the build happens on a machine that owns the game, and the result is
committed alongside the source in `<Mod>/release/`. CI's job is packaging and
publishing, not compiling - which is also why the release trigger is a version
bump rather than a tag. The framework repo solves the same problem the same
way, with its committed `prebuilt/` folder.

The tradeoff is honest but worth naming: a committed binary is only as
trustworthy as the person who committed it. The full source sits next to it in
the same folder, at the same commit, so anyone with the game can rebuild and
compare.

## Building

You need the .NET SDK 8+, an installed copy of Gambonanza, and a
GambonanzaMods checkout beside this one:

```bash
git clone https://github.com/bentrd/GambonanzaMods.git
cd GambonanzaMods && ./build.sh        # populates refs/ from your game, once
cd .. && git clone https://github.com/ben-gambo/mods.git
cd mods && ./build.sh
```

If the two are not siblings, point at the checkout explicitly:

```bash
GAMBONANZA_MODS_DIR=/path/to/GambonanzaMods ./build.sh
```

`./build.sh` builds every mod and stages it into `<Mod>/release/`. Add
`--install` to drop the result straight into your game, or name one mod to
build just that: `./build.sh ImpatientGambit --install`.

## Releasing

There are no tags to push and no workflow to trigger by hand:

1. Bump `"version"` in `<Mod>/mod.json`.
2. `./build.sh <Mod>` - this restages `<Mod>/release/`, manifest included.
3. Commit both and push to `main`.

[`release.yml`](.github/workflows/release.yml) walks every `*/release/mod.json`,
and for any `id`/`version` pair with no matching `<id>-v<version>` release, zips
the folder and publishes it. Versions that are already out are left alone, so
pushing unrelated changes is safe and re-running the workflow is harmless.

It refuses to publish if `<Mod>/release/mod.json` and `<Mod>/mod.json` disagree
on the version, which is what a stale `release/` folder looks like - otherwise a
DLL would ship under a version number it was never built at.

Optional: a `<Mod>/CHANGELOG.md` is used verbatim as the release notes.

## Adding a mod

Copy an existing folder, rename the `.csproj` to match the folder, set a fresh
`id`, `name` and `entry` in `mod.json`, and write a README. The shared build
settings and the lookup for the framework and game assemblies come from
[`Directory.Build.props`](Directory.Build.props); a mod's own `.csproj` only
declares its assembly name and which references it needs.

## Licence

MIT. The mods are mine; Gambonanza is Blukulele's, and none of its assets or
assemblies are redistributed here.
