# Ship Insurance

Server-authoritative Space Engineers grid insurance and paid snapshot repair.

Each policy covers the complete mechanically linked grid group present at enrollment. The largest grid is its anchor; sanitized snapshots and relative transforms preserve every rotor, piston, and attached subgrid as one truth state. The mod tracks later block damage/removal and attacker attribution, then offers a value-based claim once covered loss reaches the configured threshold.

Economy 2 Services Terminals gain a custom **Ship Insurance** control-panel section. Stand at a functional, non-hostile Services Terminal, choose either an existing policy or a nearby uninsured mechanical group from one selector, then insure it, request a quote, submit a claim, expedite remote recovery, inspect history, or cancel a policy.

Snapshot inventories, construction stockpiles, ammunition, fuel, battery charge, and similar consumables are cleared through Space Engineers' projector sanitizer. Claims restore blocks and integrity, not cargo. Blocks added after enrollment remain untouched.

## Chat commands

| Command | Purpose |
| --- | --- |
| `/insurance reload` | Reload server config; Admin rank required. |

All player actions exist only in the Services Terminal UI. Existing policies—including destroyed and unreachable grids—appear directly in the **Insurance target** selector; no policy ID entry is required.

## Pricing and claims

- Block value is the sum of component `MinimalPricePerUnit` values. Configured fallbacks cover modded definitions without economy prices.
- Enrollment costs the larger of `EnrollmentFlatFee` and `EnrollmentValueFraction * snapshot value`.
- Claim loss is value-weighted. Missing blocks contribute their insured integrity value; damaged blocks contribute lost integrity value.
- Claims unlock when loss reaches `MinimumLossRatio` (25% by default).
- Claim price is `ClaimValueFraction * covered loss`, subject to `MinimumClaimFee`.
- A conflicting replacement block at an insured position blocks the claim. Remove that block first; the mod never deletes player changes.
- Loss above 80%, total group loss, or a completely missing insured subgrid becomes full recovery instead of repair. Recovery costs `ClaimValueFraction * full snapshot value` and replaces the insured group from clean, fully built snapshots.
- A destroyed or service-unreachable group can be ordered to the current Services Terminal. Ordering immediately charges a distance-based transport fee and starts a persistent, terminal-bound cooldown based on the terminal's distance from the policy's last known grid position.
- **Status** shows the live recovery ETA and expedite quote. **Expedite remote recovery** can be purchased once per order; like vanilla grid storage, its price is based on remaining seconds and it multiplies the remaining cooldown by the configured factor.
- Once the recovery arrives, use **Claim / recover** again to pay the normal full-recovery claim price and deploy the group into free space near the same Services Terminal. Its remote insured remnants are removed; grids attached after enrollment are preserved.
- Uninsured grids in the recovery area block replacement. This prevents overlap and protects grids attached after enrollment.

## Server configuration

`ShipInsuranceConfig.xml` is generated in world storage on first server start. Defaults:

```xml
<InsuranceConfig>
  <EnrollmentFlatFee>0</EnrollmentFlatFee>
  <EnrollmentValueFraction>0.5</EnrollmentValueFraction>
  <ClaimValueFraction>1</ClaimValueFraction>
  <MinimumLossRatio>0.25</MinimumLossRatio>
  <MinimumClaimFee>0</MinimumClaimFee>
  <UnknownComponentValue>100</UnknownComponentValue>
  <UnknownBlockValue>1000</UnknownBlockValue>
  <MaxPoliciesPerPlayer>5</MaxPoliciesPerPlayer>
  <MaxIncidentLogEntries>100</MaxIncidentLogEntries>
  <TotalLossExtraClearance>10</TotalLossExtraClearance>
  <AllowTotalLossRespawn>true</AllowTotalLossRespawn>
  <RequireGridOwner>true</RequireGridOwner>
  <RequireStationaryGridForClaim>true</RequireStationaryGridForClaim>
  <MaximumClaimLinearSpeed>1</MaximumClaimLinearSpeed>
  <RemoteRecoveryFeePerKilometer>1000</RemoteRecoveryFeePerKilometer>
  <RemoteRecoverySecondsPerKilometer>1</RemoteRecoverySecondsPerKilometer>
  <RemoteRecoveryMinimumSeconds>60</RemoteRecoveryMinimumSeconds>
  <RemoteRecoveryMaximumSeconds>3600</RemoteRecoveryMaximumSeconds>
  <RemoteRecoveryExpediteCostPerSecond>1000</RemoteRecoveryExpediteCostPerSecond>
  <RemoteRecoveryExpediteFactor>0.5</RemoteRecoveryExpediteFactor>
</InsuranceConfig>
```

Edit generated file, then run `/insurance reload` or restart world. Policy snapshots and incident history persist in `ShipInsuranceState.bin64` in same world-storage namespace.
`AllowTotalLossRespawn` is the backward-compatible setting name for all full, total-loss, and remote recovery.

Remote transport costs at least one kilometer of `RemoteRecoveryFeePerKilometer`, then scales with actual distance. Cooldown is `distance in km * RemoteRecoverySecondsPerKilometer`, clamped between the configured minimum and maximum. Expedite costs `remaining seconds * RemoteRecoveryExpediteCostPerSecond`; a factor of `0.5` halves the remaining time.

## Scope

One policy follows the mechanically linked group captured at enrollment. Later attached grids are not added to its truth state. Active, destroyed, and unreachable policies can all be selected directly in the Services Terminal UI.

## Layout

- `ShipInsurance.sln` - Visual Studio solution containing one mod project.
- `ShipInsurance/ShipInsurance.csproj` - .NET Framework 4.8, C# 6, x64 MDK2 project.
- `ShipInsurance/src` - Space Engineers mod payload uploaded to Steam Workshop.
- `ShipInsurance/src/Data/Scripts/ShipInsurance` - separate session lifecycle, command transport, runtime mechanics, terminal controls, persistence, and pricing classes.
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
