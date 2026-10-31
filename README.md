# bifrost-app
Bifrost Studio finds your Brawlhalla install, downloads the latest release, and installs Bifrost or the Official
modified client on your PC. It can also host your own Bifrost server. The game is converted from your own install on
your own PC. This repo has no game code, art or files.

**Using a modified client on the official servers can get your account banned by Ubisoft, either manually by BMG finding out you're using it via media (images, videos, and/or streams), or Brawlhalla detecting the client in the future (as of October 31 2026, there is no detection).**

**I am not responsible if your account gets banned.**

## Requirements

- Windows (Linux and macOS builds are untested)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build
- (OPTIONAL) Python 3.11+ to build a release or host a server. **Not required for just running the release BifrostStudio.exe**

## Run from source

```bat
dotnet run --project studio
```

## Publish

```bat
python tools/build_studio.py --repo OWNER/NAME
python tools/build_studio.py --rid win-x64 --rid linux-x64 --repo OWNER/NAME --version 20261031-01
```

Each build goes to `build/studio-app/<rid>/` as one self-contained file that runs without .NET installed.
`--app-repo`, `--bifrost-repo` and `--official-repo` take each product's releases from its own repository.
Windows builds get Brawlhalla's icon from your own install, recoloured blue (`--game DIR`, else Steam's libraries);
the icon is made at build time into `build/` and is never in the repo.

## Releases

Every push to `main` that changes `studio/` or the release tools runs `.github/workflows/release.yml`, which runs
`python tools/release_app.py --publish` on GitHub: it builds `BifrostStudio.exe`, writes `app.json` and creates the
release `app-YYYYMMDD-NN<commit>`. Players' apps then offer the update. You can also start it from the Actions tab,
or run the same command on your PC.

GitHub has no Brawlhalla install to read the icon from, so the workflow uses the repository secret `APP_ICON` (the
`.ico` gzipped, in base64; GitHub secrets hold at most 48 KB) when it is set, and the default icon otherwise.

## Where it keeps things

- `%LOCALAPPDATA%\Bifrost`: the kit, downloads and the hosted server
- `%APPDATA%\BifrostStudio\settings.json`: the app's settings

## License

MIT, see [LICENSE](LICENSE).
