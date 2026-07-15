# Ship Insurance

Space Engineers mod project for Ship Insurance.

## Layout

- `ShipInsurance.sln` - Visual Studio solution containing one mod project.
- `ShipInsurance/ShipInsurance.csproj` - .NET Framework 4.8, C# 6, x64 MDK2 project.
- `ShipInsurance/src` - Space Engineers mod payload uploaded to Steam Workshop.
- `.github/workflows/steam-workshop-upload.yml` - production-branch and manual Steam upload pipeline.

## Local setup

Set the Space Engineers install path with `SE_DIR` or `SE_GAME_ROOT`:

```bash
export SE_DIR="/path/to/steamapps/common/SpaceEngineers"
```

Alternatively, copy `Directory.Build.user.props.example` to `Directory.Build.user.props` and set `SpaceEngineersDir`.

Copy the MDK2 local settings template:

```bash
cp ShipInsurance/ShipInsurance.mdk.local.ini.example ShipInsurance/ShipInsurance.mdk.local.ini
```

Build:

```bash
dotnet restore ShipInsurance.sln
dotnet build ShipInsurance.sln -c Debug -p:Platform=x64
```

## Steam Workshop pipeline

Before uploading:

1. Publish the initial workshop item manually.
2. Replace both `0` values in `ShipInsurance/src/modinfo.sbmi` with its Workshop ID.
3. Add GitHub Actions secrets `STEAM_USERNAME` and `STEAM_CONFIG_VDF`.

Uploads run for mod payload changes pushed to `production`, or through manual workflow dispatch.
