# SE Service Terminal Mods

Monorepo for Space Engineers mods that extend the Economy 2 Services Terminal.

## Projects

- **Service Terminal Framework** owns the live `services` detector proxy and displays a responsive side navigation containing vanilla Services plus every registered mod service.
- **Ship Insurance** provides mechanically linked grid-group insurance and registers its Rich HUD screen with the framework.

The framework keeps the original vanilla use object and delegates its metadata, secondary action, and `Use()` call. It does not replace the Services Terminal definition or model. Other mods register a stable ID, display name, and client-side open delegate over the mod-message API; no compile-time project or assembly reference is required.

## Ship Insurance

Server-authoritative Space Engineers grid insurance and paid snapshot repair.

Each policy covers the complete mechanically linked grid group present at enrollment. The largest grid is its anchor; sanitized snapshots and relative transforms preserve every rotor, piston, and attached subgrid as one truth state. The mod tracks later block damage/removal and attacker attribution, then offers a value-based claim once covered loss reaches the configured threshold.

Each insured subgrid also carries a persistent policy token in Space Engineers' serialized mod storage. If an administrative repair or cut/paste recreates the grid with a different entity ID, the server reattaches the new entity to its existing policy. Tokens absent from the active policy state, and duplicates created while the registered grid still exists, are removed.

Using an Economy 2 Services Terminal opens the framework's centered provider menu with wide service buttons before any service screen. After a custom service is selected, the provider list moves to the free margin on wide layouts and folds into a compact navigation rail when the aspect ratio leaves too little room; the rail expands inward on demand. Selecting **Ship Insurance** opens one unified Rich HUD insurance window without opening vanilla Services underneath. It contains the player's current account balance, a visible single-select list of existing policies and nearby uninsured mechanical groups, and compact policy actions. **Repair cost ledger** opens an overlay for the selected policy that groups repair value, effective rate, and charge by damage cause and responsible-party relationship, then reconciles the subtotal to the final quote with minimum-fee, faction-discount, or locked-quote adjustments. **Close** and Escape are owned by the framework picker, which closes the active service and restores the previous gameplay HUD mode. Selecting **Vanilla services** dismisses the picker and opens the native screen.

## Client dependency

[Service Terminal Framework](ServiceTerminalFramework/README.md) and [Rich HUD Master](https://steamcommunity.com/sharedfiles/filedetails/?id=1965654081) must be enabled to expose Ship Insurance from the physical Services detector. If Rich HUD is unavailable, the retained vanilla Services interaction still opens normally, but the Ship Insurance interface is unavailable.

Snapshot inventories, construction stockpiles, ammunition, fuel, battery charge, and similar consumables are cleared through Space Engineers' projector sanitizer. Claims restore blocks and integrity, not cargo. Blocks added after enrollment remain untouched.

## Chat commands

| Command | Purpose |
| --- | --- |
| `/insurance reload` | Reload server config; Admin rank required. |

All player actions exist only in the Services Terminal UI. Existing policies—including destroyed and unreachable grids—appear directly in the target list; no policy ID entry is required. Each uninsured group shows its current enrollment quote, and every policy row shows its current repair or full-recovery price plus any remote transport charge.

## Pricing and claims

- All grid valuation is component-based: each block contributes only the sum of `configured component price * required component count`. There is no block-level price or fallback. The server generates entries for every loaded vanilla and modded component, initially seeded from its definition price, but runtime insurance pricing uses only the generated `ComponentPrices` table.
- Enrollment costs the larger of `EnrollmentFlatFee` and `EnrollmentValueFraction * snapshot value`.
- Claim loss is value-weighted. Missing blocks contribute their insured integrity value; damaged blocks contribute lost integrity value.
- Claims unlock when loss reaches `MinimumLossRatio` (25% by default).
- Claim price is the sum of each damaged or missing block's component-value loss multiplied by `ClaimValueFraction`, its attacker-relationship fraction, and its damage-cause multiplier, subject to `MinimumClaimFee`.
- A conflicting replacement block at an insured position blocks the claim. Remove that block first; the mod never deletes player changes.
- Loss above 80%, total group loss, or a completely missing insured subgrid becomes full recovery instead of repair. Recovery prices the full snapshot through the same persisted attribution buckets; any unattributed remainder uses the unknown rate.
- Optional economy-faction pricing discounts enrollment and full-recovery value charges according to the player's reputation with the NPC faction operating the Services Terminal. Repair, transport, and expedite fees are unchanged.
- Optional dynamic recovery pricing uses active component offer prices at that economy station, with `ComponentPrices` as the fallback for components the station does not sell.
- Every successful repair or recovery consumes its policy. A remote policy is committed and consumed when transport is ordered, but remains visible until delivery can be completed.
- Insurance service use starts a player-wide cooldown based on service value. Other policies can be purchased during it, but cannot repair, recover, or order transport until it expires.
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
  <CancellationRefundFraction>0.5</CancellationRefundFraction>
  <ClaimValueFraction>1</ClaimValueFraction>
  <OwnerDamageClaimValueFraction>1</OwnerDamageClaimValueFraction>
  <FactionDamageClaimValueFraction>1</FactionDamageClaimValueFraction>
  <OtherDamageClaimValueFraction>0.75</OtherDamageClaimValueFraction>
  <EnvironmentDamageClaimValueFraction>0.75</EnvironmentDamageClaimValueFraction>
  <UnknownDamageClaimValueFraction>0.75</UnknownDamageClaimValueFraction>
  <GrindingDamageClaimMultiplier>1</GrindingDamageClaimMultiplier>
  <RammingDamageClaimMultiplier>1</RammingDamageClaimMultiplier>
  <WeaponDamageClaimMultiplier>1</WeaponDamageClaimMultiplier>
  <EnvironmentDamageClaimMultiplier>1</EnvironmentDamageClaimMultiplier>
  <OtherDamageClaimMultiplier>1</OtherDamageClaimMultiplier>
  <UnknownDamageClaimMultiplier>1</UnknownDamageClaimMultiplier>
  <MinimumLossRatio>0.25</MinimumLossRatio>
  <MinimumClaimFee>0</MinimumClaimFee>
  <DefaultComponentPrice>100</DefaultComponentPrice>
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
  <UseEconomyFactionPricing>false</UseEconomyFactionPricing>
  <UseDynamicRecoveryPricing>false</UseDynamicRecoveryPricing>
  <EconomyFriendlyReputationMin>500</EconomyFriendlyReputationMin>
  <EconomyFriendlyReputationMax>1500</EconomyFriendlyReputationMax>
  <EconomyMaximumFactionDiscount>0.1</EconomyMaximumFactionDiscount>
  <InsuranceCooldownCreditsPerSecond>1000</InsuranceCooldownCreditsPerSecond>
  <InsuranceCooldownMinimumSeconds>60</InsuranceCooldownMinimumSeconds>
  <InsuranceCooldownMaximumSeconds>86400</InsuranceCooldownMaximumSeconds>
  <ComponentPrices>
    <!-- Representative structure; actual entries and seed prices are generated. -->
    <Component>
      <SubtypeId>SteelPlate</SubtypeId>
      <Price>100</Price>
    </Component>
    <Component>
      <SubtypeId>Computer</SubtypeId>
      <Price>1000</Price>
    </Component>
    <!-- Every other loaded component is generated here too. -->
  </ComponentPrices>
</InsuranceConfig>
```

Damage pricing keeps cause and attacker relationship independent. Effective rate is `ClaimValueFraction * relationship fraction * cause multiplier`. `Grinding` includes hand and ship grinders. `Ramming` is deformation damage attributed to another grid. Owner and faction relationships are captured when damage occurs, so later faction changes do not rewrite claim history. Damage with no responsible identity uses the environment rate; unresolved nonzero attackers and untracked loss use the unknown rate. Existing configs receive these defaults when fields are absent.

Edit any generated component price, then run `/insurance reload` or restart the world. Missing entries—including components added by newly enabled mods—are automatically appended. `DefaultComponentPrice` is used only when a newly discovered component has no positive definition price from which to seed its generated entry. A price of `0` deliberately makes that component contribute no value. Updated prices affect new enrollment quotes and live repair/full-recovery quotes for existing policies. Policy snapshots and incident history persist in `ShipInsuranceState.bin64` in same world-storage namespace.
`AllowTotalLossRespawn` is the backward-compatible setting name for all full, total-loss, and remote recovery.

Remote transport costs at least one kilometer of `RemoteRecoveryFeePerKilometer`, then scales with actual distance. Cooldown is `distance in km * RemoteRecoverySecondsPerKilometer`, clamped between the configured minimum and maximum. Expedite costs `remaining seconds * RemoteRecoveryExpediteCostPerSecond`; a factor of `0.5` halves the remaining time.

When `UseEconomyFactionPricing` is enabled at an NPC economy station, reputation at or below `EconomyFriendlyReputationMin` gives no discount. Reputation scales linearly to `EconomyMaximumFactionDiscount` at `EconomyFriendlyReputationMax`, then stays capped. The enrollment flat fee and minimum claim fee remain price floors. When `UseDynamicRecoveryPricing` is enabled, full recovery values each component from the station's cheapest active offer and falls back to the configured component price when no offer exists. Remote recovery locks its full-recovery claim price when transport is ordered, so later reputation or market changes cannot alter the displayed arrival price.

Canceling an unused policy refunds `CancellationRefundFraction` of the enrollment price actually paid; the default is `0.5` (50%). The value is clamped from `0` to `1`, and fractional credits round down. A policy already used for repair, recovery, or transport receives no cancellation refund. Policies created by older versions did not record their paid enrollment price and therefore receive no refund.

Insurance cooldown is `ceil(service cost / InsuranceCooldownCreditsPerSecond)` seconds, clamped between the configured minimum and maximum. Local service uses the final charged repair or recovery cost. Remote transport commits the policy immediately and uses the locked recovery price plus transport fee. Set `InsuranceCooldownCreditsPerSecond` to `0` to disable only the cooldown; policies remain single-use. A pending remote delivery may still be completed during cooldown.

## Scope

One policy follows the mechanically linked group captured at enrollment. Later attached grids are not added to its truth state. Active, destroyed, and unreachable policies can all be selected directly in the Services Terminal UI.

## Layout

- `ServiceTerminalMods.sln` - Visual Studio solution containing every mod project in the monorepo.
- `ServiceTerminalFramework` - standalone framework mod, provider API, detector proxy, and responsive Rich HUD side navigation.
- `ShipInsurance/ShipInsurance.csproj` - .NET Framework 4.8, C# 6, x64 MDK2 project.
- `ShipInsurance/src` - Space Engineers mod payload uploaded to Steam Workshop.
- `ShipInsurance/src/Data/Scripts/ShipInsurance` - separate session lifecycle, framework client, command transport, runtime mechanics, Rich HUD service state and window, persistence, and pricing classes.
- `ShipInsurance/src/Data/Scripts/ShipInsurance/RichHudFramework` - MIT-licensed Rich HUD Framework client sources used by the custom interactive window; its license is included in that directory.
- `ServiceTerminalFrameworkClient.cs` registers Ship Insurance through the framework's load-order-safe mod-message API.
- `ShipInsurance/src/Models` and `tools/ServiceTerminalModelPatcher` contain the previous split-model experiment for comparison; runtime code no longer loads those assets.
- `.github/workflows/steam-workshop-upload.yml` - Ship Insurance upload pipeline.
- `.github/workflows/service-terminal-framework-workshop-upload.yml` - independent framework upload pipeline.

## Local setup

Set the Space Engineers install path with `SE_DIR` or `SE_GAME_ROOT`:

```bash
export SE_DIR="/path/to/steamapps/common/SpaceEngineers"
```

Alternatively, copy `Directory.Build.user.props.example` to `Directory.Build.user.props` and set `SpaceEngineersDir`.

Copy the MDK2 local settings template:

```bash
cp ShipInsurance/ShipInsurance.mdk.local.ini.example ShipInsurance/ShipInsurance.mdk.local.ini
cp ServiceTerminalFramework/ServiceTerminalFramework.mdk.local.ini.example ServiceTerminalFramework/ServiceTerminalFramework.mdk.local.ini
```

Build:

```bash
dotnet restore ServiceTerminalMods.sln
dotnet build ServiceTerminalMods.sln -c Debug -p:Platform=x64
```

## Steam Workshop pipeline

Before uploading:

1. Publish the initial workshop item manually.
2. Replace both `0` values in each mod's `src/modinfo.sbmi` with that mod's Workshop ID.
3. Add GitHub Actions secrets `STEAM_USERNAME` and `STEAM_CONFIG_VDF`.

Each mod uploads only when its own payload changes on `production`, or through its own manual workflow dispatch.
