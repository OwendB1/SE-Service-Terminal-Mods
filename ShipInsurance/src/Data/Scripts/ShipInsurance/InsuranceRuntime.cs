using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace ShipInsurance
{
    internal sealed class InsuranceRuntime
    {
        private const string ConfigFile = "ShipInsuranceConfig.xml";
        private const string StateFile = "ShipInsuranceState.bin64";
        private static readonly Guid GridMarkerStorageKey =
            new Guid("b3f19457-2e24-4d41-9fba-38b428f92b07");
        internal const double ServiceGridDiscoveryRange = 250.0;
        internal const double ServiceTerminalUseDistance = 15.0;

        private readonly Dictionary<long, IMyCubeGrid> _subscribedGrids = new Dictionary<long, IMyCubeGrid>();
        private readonly Dictionary<long, Dictionary<Vector3I, DamageAttribution>> _lastDamage = new Dictionary<long, Dictionary<Vector3I, DamageAttribution>>();
        private readonly Dictionary<IMySlimBlock, float> _integrityBeforeDamage =
            new Dictionary<IMySlimBlock, float>();
        private readonly Dictionary<string, Dictionary<Vector3I, BlockLossAttribution>>
            _lossAttributionByGrid =
                new Dictionary<string, Dictionary<Vector3I, BlockLossAttribution>>(
                    StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<Vector3I, MyObjectBuilder_CubeBlock>>
            _insuredBlocksByGrid =
                new Dictionary<string, Dictionary<Vector3I, MyObjectBuilder_CubeBlock>>(
                    StringComparer.Ordinal);
        private readonly Dictionary<long, string> _identityNames = new Dictionary<long, string>();
        private readonly Dictionary<MyDefinitionId, long> _blockComponentValues = new Dictionary<MyDefinitionId, long>();
        private readonly Dictionary<MyDefinitionId, long> _componentPrices =
            new Dictionary<MyDefinitionId, long>();
        private readonly HashSet<long> _repairingGrids = new HashSet<long>();
        private readonly HashSet<long> _pendingGridReconciliation = new HashSet<long>();

        private InsuranceConfig _config = new InsuranceConfig();
        private InsuranceState _state = new InsuranceState();
        private Action<ulong, string> _sendResponse;
        private bool _active;
        private bool _isServer;
        private bool _dirty;
        private int _frame;

        private sealed class EconomyPricingContext
        {
            public string FactionTag;
            public int Reputation;
            public double Discount;
            public bool DynamicRecoveryPrice;
            public readonly Dictionary<MyDefinitionId, long> ComponentPrices =
                new Dictionary<MyDefinitionId, long>();
        }

        internal void Start(bool isServer, Action<ulong, string> sendResponse)
        {
            _active = true;
            _isServer = isServer;
            _sendResponse = sendResponse;

            if (_isServer)
            {
                LoadConfig();
                LoadState();
                MyAPIGateway.Entities.OnEntityAdd += OnEntityAdded;
                InitializeGridMarkers();
                MyAPIGateway.Session.DamageSystem.RegisterBeforeDamageHandler(1000,
                    OnBeforeDamageApplied);
                MyAPIGateway.Session.DamageSystem.RegisterAfterDamageHandler(1000, OnDamageApplied);
                RefreshGridSubscriptions();
                if (_dirty) SaveState();
            }
        }

        internal void Update()
        {
            if (!_active || !_isServer) return;

            _frame++;
            if (_frame % 100 == 0)
            {
                UpdateLastKnownPoses();
                RefreshGridSubscriptions();
            }

            if (_dirty && _frame % 3600 == 0)
                SaveState();
        }

        internal void Save()
        {
            if (_isServer) SaveState();
        }

        internal void Stop()
        {
            _active = false;

            foreach (IMyCubeGrid grid in _subscribedGrids.Values)
                UnhookGrid(grid);

            _subscribedGrids.Clear();
            _lastDamage.Clear();
            _integrityBeforeDamage.Clear();
            _lossAttributionByGrid.Clear();
            _insuredBlocksByGrid.Clear();
            _identityNames.Clear();
            _blockComponentValues.Clear();
            _componentPrices.Clear();
            _repairingGrids.Clear();
            _pendingGridReconciliation.Clear();
            if (_isServer && MyAPIGateway.Entities != null)
                MyAPIGateway.Entities.OnEntityAdd -= OnEntityAdded;
            _sendResponse = null;
        }

        internal List<PolicySummary> BuildPolicySummaries(IMyPlayer player, long serviceTerminalId)
        {
            ReconcilePendingGridMarkers();
            IMyFunctionalBlock terminal = FindServiceTerminal(serviceTerminalId);
            EconomyPricingContext pricing = BuildEconomyPricingContext(player, terminal);
            long cooldownReadyUtcTicks = GetInsuranceCooldownReadyUtcTicks(
                player.IdentityId, DateTime.UtcNow.Ticks);
            List<PolicySummary> policies = new List<PolicySummary>();

            for (int i = 0; i < _state.Policies.Count; i++)
            {
                InsurancePolicy policy = _state.Policies[i];
                if (policy.OwnerIdentityId != player.IdentityId) continue;
                long selectionGridId = 0;
                for (int gridIndex = 0; gridIndex < policy.Grids.Count; gridIndex++)
                {
                    IMyCubeGrid grid = FindGrid(policy.Grids[gridIndex].GridEntityId);
                    if (grid == null) continue;
                    selectionGridId = grid.EntityId;
                    break;
                }

                long nowUtcTicks = DateTime.UtcNow.Ticks;
                long remainingSeconds = InsuranceMath.RemainingSeconds(policy.RecoveryReadyUtcTicks, nowUtcTicks);
                bool canExpedite = policy.RecoveryTerminalEntityId == serviceTerminalId &&
                                   remainingSeconds > 0 && !policy.RecoveryExpedited;
                bool remote = policy.RecoveryReadyUtcTicks > 0 || selectionGridId == 0 ||
                              !IsPolicyWithinServiceRange(policy, terminal);
                ClaimQuote quote = BuildQuote(policy, pricing);
                if (remote)
                {
                    quote.Recovery = true;
                    ApplyRecoveryPricing(policy, quote, pricing);
                }

                long transportCost = policy.RecoveryReadyUtcTicks > 0
                    ? policy.RecoveryTransportFee
                    : remote && terminal != null
                        ? InsuranceMath.RemoteRecoveryFee(
                            Vector3D.Distance(terminal.GetPosition(), policy.LastKnownPose.Position),
                            _config.RemoteRecoveryFeePerKilometer)
                        : 0;
                policies.Add(new PolicySummary
                {
                    PolicyId = policy.PolicyId,
                    GridName = policy.GridName,
                    SelectionGridId = selectionGridId,
                    TotalLoss = selectionGridId == 0,
                    Remote = remote,
                    RecoveryReadyUtcTicks = policy.RecoveryReadyUtcTicks,
                    RecoveryTerminalEntityId = policy.RecoveryTerminalEntityId,
                    RecoveryDistanceMeters = policy.RecoveryDistanceMeters,
                    ExpeditePrice = canExpedite
                        ? InsuranceMath.ExpediteFee(remainingSeconds,
                            _config.RemoteRecoveryExpediteCostPerSecond)
                        : 0,
                    ExpediteReductionPercent = (int)Math.Round(
                        (1.0 - InsuranceMath.Clamp01(_config.RemoteRecoveryExpediteFactor)) * 100.0),
                    RecoveryExpedited = policy.RecoveryExpedited,
                    ClaimCost = quote.Cost,
                    Recovery = quote.Recovery,
                    TransportCost = transportCost,
                    ShipValue = quote.BaselineValue,
                    LossRatio = quote.LossRatio,
                    FactionTag = quote.FactionTag,
                    FactionReputation = quote.FactionReputation,
                    FactionDiscountPercent = (int)Math.Round(quote.FactionDiscount * 100.0),
                    DynamicRecoveryPrice = quote.DynamicRecoveryPrice,
                    RecoveryPriceLocked = quote.RecoveryPriceLocked,
                    InsuranceCooldownReadyUtcTicks = HasRecoveryOrder(policy)
                        ? 0
                        : cooldownReadyUtcTicks
                });
            }

            AddEnrollmentSummaries(player, terminal, pricing, policies);

            return policies;
        }

        internal ClaimLedger BuildClaimLedger(IMyPlayer player, long policyId,
            long serviceTerminalId)
        {
            InsurancePolicy policy = ResolveOwnedPolicy(player, 0, policyId);
            if (policy == null) return null;

            IMyFunctionalBlock terminal = FindServiceTerminal(serviceTerminalId);
            EconomyPricingContext pricing = BuildEconomyPricingContext(player, terminal);
            ClaimQuote quote = BuildQuote(policy, pricing);
            bool remote = HasRecoveryOrder(policy) || !HasLivePolicyGrid(policy) ||
                          (terminal != null && !IsPolicyWithinServiceRange(policy, terminal));
            if (remote)
            {
                quote.Recovery = true;
                ApplyRecoveryPricing(policy, quote, pricing);
            }

            return CreateClaimLedger(policy, quote,
                pricing.DynamicRecoveryPrice ? pricing.ComponentPrices : null);
        }

        private void AddEnrollmentSummaries(IMyPlayer player, IMyFunctionalBlock terminal,
            EconomyPricingContext pricing, List<PolicySummary> summaries)
        {
            if (player == null || terminal == null) return;

            BoundingSphereD sphere = new BoundingSphereD(terminal.GetPosition(),
                ServiceGridDiscoveryRange);
            List<IMyEntity> entities = MyAPIGateway.Entities.GetEntitiesInSphere(ref sphere);
            HashSet<long> seenGroups = new HashSet<long>();
            for (int i = 0; i < entities.Count; i++)
            {
                IMyCubeGrid grid = entities[i] as IMyCubeGrid;
                if (grid == null || grid.Closed) continue;
                List<IMyCubeGrid> group = GetMechanicalGroup(grid);
                if (group.Count == 0) continue;
                IMyCubeGrid anchor = group[0];
                if (!seenGroups.Add(anchor.EntityId)) continue;

                bool eligible = true;
                long baselineValue = 0;
                List<IMySlimBlock> blocks = new List<IMySlimBlock>();
                for (int groupIndex = 0; groupIndex < group.Count; groupIndex++)
                {
                    IMyCubeGrid member = group[groupIndex];
                    if ((_config.RequireGridOwner && !member.BigOwners.Contains(player.IdentityId)) ||
                        FindPolicyByGrid(member.EntityId) != null)
                    {
                        eligible = false;
                        break;
                    }

                    blocks.Clear();
                    member.GetBlocks(blocks);
                    if (blocks.Count == 0)
                    {
                        eligible = false;
                        break;
                    }
                    for (int blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
                    {
                        IMySlimBlock block = blocks[blockIndex];
                        double integrity = block.MaxIntegrity <= 0f
                            ? 0.0
                            : block.Integrity / block.MaxIntegrity;
                        baselineValue = InsuranceMath.Add(baselineValue,
                            InsuranceMath.Scale(GetBlockComponentValue(block.BlockDefinition.Id),
                                InsuranceMath.Clamp01(integrity)));
                    }
                }
                if (!eligible) continue;

                summaries.Add(new PolicySummary
                {
                    GridName = string.IsNullOrWhiteSpace(anchor.CustomName)
                        ? anchor.DisplayName
                        : anchor.CustomName,
                    SelectionGridId = anchor.EntityId,
                    ShipValue = baselineValue,
                    EnrollmentCost = InsuranceMath.EnrollmentFee(baselineValue,
                        _config.EnrollmentFlatFee, _config.EnrollmentValueFraction,
                        pricing.Discount),
                    DistanceMeters = Vector3D.Distance(terminal.GetPosition(),
                        anchor.WorldAABB.Center),
                    FactionTag = pricing.FactionTag,
                    FactionReputation = pricing.Reputation,
                    FactionDiscountPercent = (int)Math.Round(pricing.Discount * 100.0)
                });
            }
        }

        internal bool TryValidateServiceTerminalRequest(IMyPlayer player, long terminalId, long targetGridId,
            bool requireGrid, out IMyCubeGrid grid, out string error)
        {
            grid = null;
            error = null;

            IMyEntity entity;
            IMyFunctionalBlock terminal;
            if (!MyAPIGateway.Entities.TryGetEntityById(terminalId, out entity) ||
                (terminal = entity as IMyFunctionalBlock) == null || terminal.Closed ||
                !IsServicesTerminal(terminal) ||
                !terminal.IsFunctional || !terminal.Enabled)
            {
                error = "Insurance service terminal is unavailable.";
                return false;
            }

            if (terminal.GetUserRelationToOwner(player.IdentityId) == MyRelationsBetweenPlayerAndBlock.Enemies)
            {
                error = "Insurance service terminal access denied.";
                return false;
            }

            double useDistance = ServiceTerminalUseDistance;
            if (Vector3D.DistanceSquared(player.GetPosition(), terminal.GetPosition()) > useDistance * useDistance)
            {
                error = "Move within " + useDistance.ToString("0", CultureInfo.InvariantCulture) +
                        " m of insurance service terminal.";
                return false;
            }

            if (!requireGrid) return true;

            grid = FindGrid(targetGridId);
            if (grid == null)
            {
                error = "Selected grid is no longer available.";
                return false;
            }

            double gridRange = ServiceGridDiscoveryRange + grid.WorldAABB.HalfExtents.Length();
            if (Vector3D.DistanceSquared(grid.WorldAABB.Center, terminal.GetPosition()) > gridRange * gridRange)
            {
                grid = null;
                error = "Selected grid is outside service range.";
                return false;
            }

            if (_config.RequireGridOwner && !grid.BigOwners.Contains(player.IdentityId))
            {
                grid = null;
                error = "You must be a major owner of selected group anchor.";
                return false;
            }

            return true;
        }

        internal void InsureGrid(IMyPlayer player, ulong steamId, long clientGridId,
            IMyCubeGrid serviceGrid, long serviceTerminalId)
        {
            IMyCubeGrid grid = serviceGrid;
            if (grid == null || grid.EntityId != clientGridId)
            {
                SendResponse(steamId, "Select a nearby mechanical group at service terminal to insure it.");
                return;
            }

            List<IMyCubeGrid> group = GetMechanicalGroup(grid);
            IMyCubeGrid anchor = group[0];
            for (int i = 0; i < group.Count; i++)
            {
                if (_config.RequireGridOwner && !group[i].BigOwners.Contains(player.IdentityId))
                {
                    SendResponse(steamId, "You must be a major owner of every grid in mechanical group.");
                    return;
                }

                if (FindPolicyByGrid(group[i].EntityId) != null)
                {
                    SendResponse(steamId, "Mechanical group already contains an insured grid.");
                    return;
                }
            }

            int policyCount = 0;
            for (int i = 0; i < _state.Policies.Count; i++)
                if (_state.Policies[i].OwnerIdentityId == player.IdentityId) policyCount++;

            if (policyCount >= _config.MaxPoliciesPerPlayer)
            {
                SendResponse(steamId, "Policy limit reached (" + _config.MaxPoliciesPerPlayer + ").");
                return;
            }

            List<InsuredGridSnapshot> snapshots = new List<InsuredGridSnapshot>(group.Count);
            MatrixD anchorInverse = MatrixD.Invert(anchor.WorldMatrix);
            long baselineValue = 0;
            int blockCount = 0;
            double clearanceRadius = 5.0;
            for (int i = 0; i < group.Count; i++)
            {
                IMyCubeGrid member = group[i];
                MyObjectBuilder_CubeGrid blueprint = CreateSanitizedBlueprint(member);
                if (blueprint == null || blueprint.CubeBlocks == null || blueprint.CubeBlocks.Count == 0)
                {
                    SendResponse(steamId, "Could not snapshot every grid in mechanical group.");
                    return;
                }

                snapshots.Add(new InsuredGridSnapshot
                {
                    GridEntityId = member.EntityId,
                    Blueprint = blueprint,
                    RelativePose = new MyPositionAndOrientation(member.WorldMatrix * anchorInverse),
                    PersistentId = NewPersistentGridId()
                });
                baselineValue = InsuranceMath.Add(baselineValue, CalculateBaselineValue(blueprint));
                blockCount += blueprint.CubeBlocks.Count;
                clearanceRadius = Math.Max(clearanceRadius,
                    Vector3D.Distance(anchor.WorldAABB.Center, member.WorldAABB.Center) +
                    member.WorldAABB.HalfExtents.Length() + _config.TotalLossExtraClearance);
            }

            EconomyPricingContext pricing = BuildEconomyPricingContext(player,
                FindServiceTerminal(serviceTerminalId));
            long fee = InsuranceMath.EnrollmentFee(baselineValue, _config.EnrollmentFlatFee,
                _config.EnrollmentValueFraction, pricing.Discount);
            long balance;
            if (!player.TryGetBalanceInfo(out balance) || balance < fee)
            {
                SendResponse(steamId, "Enrollment costs " + Money(fee) + " SC; balance " + Money(balance) + " SC.");
                return;
            }

            if (fee > 0) player.RequestChangeBalance(-fee);

            InsurancePolicy policy = new InsurancePolicy
            {
                PolicyId = _state.NextPolicyId++,
                GridEntityId = anchor.EntityId,
                OwnerIdentityId = player.IdentityId,
                OwnerSteamId = steamId,
                GridName = string.IsNullOrWhiteSpace(anchor.CustomName) ? anchor.DisplayName : anchor.CustomName,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
                BaselineValue = baselineValue,
                EnrollmentCost = fee,
                LastKnownPose = new MyPositionAndOrientation(anchor.WorldMatrix),
                ClearanceRadius = clearanceRadius,
                Grids = snapshots
            };

            _state.Policies.Add(policy);
            for (int i = 0; i < group.Count; i++)
            {
                SetGridMarker(group[i], snapshots[i].PersistentId);
                SubscribeGrid(group[i]);
            }
            AddIncident(policy, new IncidentRecord
            {
                UtcTicks = DateTime.UtcNow.Ticks,
                EventType = "PolicyCreated",
                AttackerIdentityId = player.IdentityId,
                AttackerName = player.DisplayName
            });
            _dirty = true;
            SaveState();

            SendResponse(steamId, "Policy #" + policy.PolicyId + " created for " + policy.GridName +
                ". Snapshot " + group.Count + " mechanically linked grids / " + blockCount +
                " blocks, value " + Money(baselineValue) +
                " SC, enrollment " + Money(fee) + " SC" +
                FormatEconomyPricing(pricing.FactionTag, pricing.Reputation,
                    pricing.Discount, false, false) + ".");
        }

        internal void ShowStatus(IMyPlayer player, ulong steamId, long clientGridId, long policyId,
            long serviceTerminalId)
        {
            InsurancePolicy policy = ResolveOwnedPolicy(player, clientGridId, policyId);
            if (policy == null)
            {
                SendResponse(steamId, "Policy not found. Refresh the Insurance target selector.");
                return;
            }

            IMyFunctionalBlock terminal = FindServiceTerminal(serviceTerminalId);
            EconomyPricingContext pricing = BuildEconomyPricingContext(player, terminal);
            ClaimQuote quote = BuildQuote(policy, pricing);
            bool remote = HasRecoveryOrder(policy) ||
                          (policyId > 0 && terminal != null &&
                           !IsPolicyWithinServiceRange(policy, terminal));
            if (remote)
            {
                quote.Recovery = true;
                ApplyRecoveryPricing(policy, quote, pricing);
            }
            StringBuilder text = new StringBuilder(FormatQuote(policy, quote));
            if (HasRecoveryOrder(policy))
            {
                text.Append(FormatRecoveryOrder(policy, serviceTerminalId, DateTime.UtcNow.Ticks));
            }
            else
            {
                if (remote)
                {
                    double distance = Vector3D.Distance(terminal.GetPosition(), policy.LastKnownPose.Position);
                    long transportFee = InsuranceMath.RemoteRecoveryFee(distance,
                        _config.RemoteRecoveryFeePerKilometer);
                    long cooldown = InsuranceMath.RemoteRecoveryCooldownSeconds(distance,
                        _config.RemoteRecoverySecondsPerKilometer, _config.RemoteRecoveryMinimumSeconds,
                        _config.RemoteRecoveryMaximumSeconds);
                    text.Append("\nRemote recovery: ").Append(Distance(distance)).Append(", transport ")
                        .Append(Money(transportFee)).Append(" SC, cooldown ").Append(Duration(cooldown))
                        .Append(". Use Claim to order.");
                }
            }
            SendResponse(steamId, text.ToString());
        }

        internal void ShowHistory(IMyPlayer player, ulong steamId, long clientGridId, long policyId)
        {
            InsurancePolicy policy = ResolveOwnedPolicy(player, clientGridId, policyId);
            if (policy == null)
            {
                SendResponse(steamId, "Policy not found. Refresh the Insurance target selector.");
                return;
            }

            StringBuilder text = new StringBuilder("Policy #").Append(policy.PolicyId).Append(" recent incidents:");
            int start = Math.Max(0, policy.Incidents.Count - 8);
            if (start == policy.Incidents.Count) text.Append(" none");
            for (int i = start; i < policy.Incidents.Count; i++)
            {
                IncidentRecord incident = policy.Incidents[i];
                text.Append("\n").Append(new DateTime(incident.UtcTicks, DateTimeKind.Utc).ToString("u", CultureInfo.InvariantCulture))
                    .Append(" ").Append(incident.EventType);
                if (!string.IsNullOrEmpty(incident.DamageType)) text.Append("/").Append(incident.DamageType);
                if (incident.DamageCause != InsuranceDamageCause.Unknown ||
                    incident.DamageRelationship != InsuranceDamageRelationship.Unknown)
                    text.Append(" [").Append(incident.DamageCause).Append("/")
                        .Append(incident.DamageRelationship).Append("]");
                if (!string.IsNullOrEmpty(incident.AttackerName)) text.Append(" by ").Append(incident.AttackerName);
                if (incident.EventCount > 1) text.Append(" x").Append(incident.EventCount);
            }
            SendResponse(steamId, text.ToString());
        }

        internal void CancelPolicy(IMyPlayer player, ulong steamId, long clientGridId, long policyId)
        {
            InsurancePolicy policy = ResolveOwnedPolicy(player, clientGridId, policyId);
            if (policy == null)
            {
                SendResponse(steamId, "Policy not found. Refresh the Insurance target selector.");
                return;
            }

            long refund = policy.Consumed
                ? 0
                : InsuranceMath.CancellationRefund(policy.EnrollmentCost,
                    _config.CancellationRefundFraction);
            if (refund > 0) player.RequestChangeBalance(refund);

            bool consumed = policy.Consumed;
            RemovePolicy(policy);
            _dirty = true;
            SaveState();
            string refundText = consumed
                ? " No refund: policy was already used."
                : refund > 0
                    ? " Refunded " + Money(refund) + " SC (" +
                      Percent(_config.CancellationRefundFraction) + " of paid enrollment)."
                    : " No cancellation refund was due.";
            SendResponse(steamId, "Policy #" + policy.PolicyId + " canceled." + refundText);
        }

        internal void ReloadConfig(IMyPlayer player, ulong steamId)
        {
            if (player.PromoteLevel < MyPromoteLevel.Admin)
            {
                SendResponse(steamId, "Admin permission required.");
                return;
            }

            LoadConfig();
            _blockComponentValues.Clear();
            SendResponse(steamId, "ShipInsuranceConfig.xml reloaded.");
        }

        internal void Claim(IMyPlayer player, ulong steamId, long clientGridId, long policyId,
            long serviceTerminalId)
        {
            InsurancePolicy policy = ResolveOwnedPolicy(player, clientGridId, policyId);
            if (policy == null)
            {
                SendResponse(steamId, "Policy not found. Refresh the Services Terminal policy list.");
                return;
            }

            IMyFunctionalBlock serviceTerminal = FindServiceTerminal(serviceTerminalId);
            EconomyPricingContext pricing = BuildEconomyPricingContext(player, serviceTerminal);
            if (HasRecoveryOrder(policy) && FindServiceTerminal(policy.RecoveryTerminalEntityId) == null)
            {
                ClearRecoveryOrder(policy);
                _dirty = true;
                SaveState();
            }

            if (policy.Consumed && !HasRecoveryOrder(policy))
            {
                SendResponse(steamId, "This insurance policy has already been used up.");
                return;
            }
            if (!HasRecoveryOrder(policy))
            {
                long nowUtcTicks = DateTime.UtcNow.Ticks;
                long cooldownReadyUtcTicks = GetInsuranceCooldownReadyUtcTicks(
                    player.IdentityId, nowUtcTicks);
                long cooldownSeconds = InsuranceMath.RemainingSeconds(
                    cooldownReadyUtcTicks, nowUtcTicks);
                if (cooldownSeconds > 0)
                {
                    SendResponse(steamId, "Insurance services are cooling down for " +
                        Duration(cooldownSeconds) + ".");
                    return;
                }
            }

            bool hasLiveGrid = HasLivePolicyGrid(policy);
            bool remoteRecovery = HasRecoveryOrder(policy) ||
                                  (serviceTerminal != null && policyId > 0 &&
                                   !IsPolicyWithinServiceRange(policy, serviceTerminal));
            if (hasLiveGrid)
            {
                if (policyId == 0 && FindPolicyByGrid(clientGridId) != policy)
                {
                    SendResponse(steamId, "Select an insured group or policy at the Services Terminal.");
                    return;
                }

                for (int i = 0; i < policy.Grids.Count; i++)
                {
                    IMyCubeGrid grid = FindGrid(policy.Grids[i].GridEntityId);
                    if (grid == null) continue;
                    if (_config.RequireGridOwner && !grid.BigOwners.Contains(player.IdentityId))
                    {
                        SendResponse(steamId, "You must remain a major owner of every surviving insured grid.");
                        return;
                    }

                    if (!remoteRecovery && _config.RequireStationaryGridForClaim && grid.Physics != null &&
                        grid.Physics.LinearVelocity.LengthSquared() >
                        _config.MaximumClaimLinearSpeed * _config.MaximumClaimLinearSpeed)
                    {
                        SendResponse(steamId, "Stop the mechanical grid group before claiming.");
                        return;
                    }
                }
            }
            else if (!_config.AllowTotalLossRespawn)
            {
                SendResponse(steamId, "Total-loss recovery is disabled by server config.");
                return;
            }

            ClaimQuote quote = BuildQuote(policy, pricing);
            if (remoteRecovery)
            {
                quote.Recovery = true;
                ApplyRecoveryPricing(policy, quote, pricing);
            }
            if (quote.Recovery && !_config.AllowTotalLossRespawn)
            {
                SendResponse(steamId, "Full group recovery is disabled by server config.");
                return;
            }

            if (remoteRecovery)
            {
                if (!HasRecoveryOrder(policy))
                {
                    BeginRemoteRecovery(player, steamId, policy, serviceTerminal, quote);
                    return;
                }

                if (policy.RecoveryTerminalEntityId != serviceTerminalId)
                {
                    SendResponse(steamId, "Recovery is assigned to the Services Terminal that placed the order." +
                        FormatRecoveryOrder(policy, serviceTerminalId, DateTime.UtcNow.Ticks));
                    return;
                }

                long remainingSeconds = InsuranceMath.RemainingSeconds(policy.RecoveryReadyUtcTicks,
                    DateTime.UtcNow.Ticks);
                if (remainingSeconds > 0)
                {
                    SendResponse(steamId, FormatQuote(policy, quote) +
                        FormatRecoveryOrder(policy, serviceTerminalId, DateTime.UtcNow.Ticks));
                    return;
                }
            }

            MyPositionAndOrientation recoveryPose = policy.LastKnownPose;
            if (remoteRecovery && !TryGetServiceRecoveryPose(serviceTerminal, policy, out recoveryPose))
            {
                SendResponse(steamId, "No clear recovery space was found near this Services Terminal.");
                return;
            }

            if (quote.Recovery && !IsRecoveryAreaClear(policy, recoveryPose))
            {
                SendResponse(steamId, "Recovery area contains an uninsured grid. Clear it first.");
                return;
            }

            if (!quote.Recovery && quote.ConflictingBlocks > 0)
            {
                SendResponse(steamId, FormatQuote(policy, quote) +
                    "\nClaim blocked: " + quote.ConflictingBlocks + " insured positions contain different blocks.");
                return;
            }

            if (!remoteRecovery && quote.LossRatio + 0.0000001 < InsuranceMath.Clamp01(_config.MinimumLossRatio))
            {
                SendResponse(steamId, FormatQuote(policy, quote) +
                    "\nMinimum claim threshold is " + Percent(_config.MinimumLossRatio) + ".");
                return;
            }

            if (!quote.Recovery && quote.LossValue <= 0)
            {
                SendResponse(steamId, "Nothing covered needs repair.");
                return;
            }

            long balance = 0;
            if (quote.Cost > 0 && (!player.TryGetBalanceInfo(out balance) || balance < quote.Cost))
            {
                SendResponse(steamId, FormatQuote(policy, quote) + "\nInsufficient balance: " + Money(balance) + " SC.");
                return;
            }

            if (quote.Cost > 0) player.RequestChangeBalance(-quote.Cost);
            long repairedAttributedCost = 0;
            int repairedBlocks = 0;

            if (quote.Recovery)
            {
                if (!RecoverGridGroup(policy, recoveryPose))
                {
                    if (quote.Cost > 0) player.RequestChangeBalance(quote.Cost);
                    SendResponse(steamId, "Group recovery failed. Claim charge was refunded.");
                    return;
                }

                repairedBlocks = CountSnapshotBlocks(policy);
            }
            else
            {
                for (int i = 0; i < policy.Grids.Count; i++)
                {
                    IMyCubeGrid repairGrid = FindGrid(policy.Grids[i].GridEntityId);
                    if (repairGrid != null) _repairingGrids.Add(repairGrid.EntityId);
                }
                try
                {
                    for (int i = 0; i < quote.Items.Count; i++)
                    {
                        RepairItem item = quote.Items[i];
                        bool repaired = item.Missing
                            ? RestoreMissingBlock(item.Grid, item.Snapshot)
                            : RestoreDamagedBlock(item);
                        if (!repaired) continue;
                        repairedBlocks++;
                        repairedAttributedCost = InsuranceMath.Add(repairedAttributedCost,
                            item.ClaimCost);
                    }
                }
                finally
                {
                    for (int i = 0; i < policy.Grids.Count; i++)
                        _repairingGrids.Remove(policy.Grids[i].GridEntityId);
                }
            }

            long actualCost = quote.Recovery
                ? quote.Cost
                : repairedBlocks <= 0 ? 0 :
                    InsuranceMath.AttributedClaimFee(repairedAttributedCost,
                        _config.MinimumClaimFee, 0.0);
            if (actualCost > quote.Cost) actualCost = quote.Cost;
            long refund = quote.Cost - actualCost;
            if (refund > 0) player.RequestChangeBalance(refund);

            if (repairedBlocks <= 0)
            {
                SendResponse(steamId, "No covered blocks were restored. Claim charge was refunded; policy remains active.");
                return;
            }

            long transportFee = policy.RecoveryTransportFee;
            policy.LastClaimUtcTicks = DateTime.UtcNow.Ticks;
            ClearRecoveryOrder(policy);
            AddIncident(policy, new IncidentRecord
            {
                UtcTicks = policy.LastClaimUtcTicks,
                EventType = remoteRecovery ? "RemoteRecovery" :
                    quote.TotalLoss ? "TotalLossRecovery" : quote.Recovery ? "Recovery" : "Claim",
                AttackerIdentityId = player.IdentityId,
                AttackerName = player.DisplayName,
                DamageAmount = repairedBlocks
            });
            long insuranceCooldownSeconds = remoteRecovery
                ? InsuranceMath.RemainingSeconds(GetInsuranceCooldownReadyUtcTicks(
                    player.IdentityId, policy.LastClaimUtcTicks), policy.LastClaimUtcTicks)
                : StartInsuranceCooldown(player.IdentityId, actualCost,
                    policy.LastClaimUtcTicks);
            RemovePolicy(policy);
            _dirty = true;
            SaveState();

            SendResponse(steamId, "Claim complete for policy #" + policy.PolicyId + ": restored " + repairedBlocks +
                " blocks, charged " + Money(actualCost) + " SC" +
                (remoteRecovery ? ", plus " + Money(transportFee) + " SC transport paid when ordered" : string.Empty) +
                (refund > 0 ? ", refunded " + Money(refund) + " SC" : string.Empty) +
                ". Policy used up" +
                (insuranceCooldownSeconds > 0
                    ? "; insurance cooldown " + Duration(insuranceCooldownSeconds)
                    : string.Empty) + ".");
        }

        private void BeginRemoteRecovery(IMyPlayer player, ulong steamId, InsurancePolicy policy,
            IMyFunctionalBlock serviceTerminal, ClaimQuote quote)
        {
            if (serviceTerminal == null)
            {
                SendResponse(steamId, "Services Terminal is unavailable.");
                return;
            }

            double distance = Vector3D.Distance(serviceTerminal.GetPosition(), policy.LastKnownPose.Position);
            long transportFee = InsuranceMath.RemoteRecoveryFee(distance,
                _config.RemoteRecoveryFeePerKilometer);
            long cooldownSeconds = InsuranceMath.RemoteRecoveryCooldownSeconds(distance,
                _config.RemoteRecoverySecondsPerKilometer, _config.RemoteRecoveryMinimumSeconds,
                _config.RemoteRecoveryMaximumSeconds);
            long balance = 0;
            if (transportFee > 0 && (!player.TryGetBalanceInfo(out balance) || balance < transportFee))
            {
                SendResponse(steamId, FormatQuote(policy, quote) + "\nRemote transport costs " +
                    Money(transportFee) + " SC; balance " + Money(balance) + " SC.");
                return;
            }

            if (transportFee > 0) player.RequestChangeBalance(-transportFee);
            long nowUtcTicks = DateTime.UtcNow.Ticks;
            policy.RecoveryTerminalEntityId = serviceTerminal.EntityId;
            policy.RecoveryReadyUtcTicks = InsuranceMath.Add(nowUtcTicks,
                InsuranceMath.Multiply(cooldownSeconds, TimeSpan.TicksPerSecond));
            policy.RecoveryDistanceMeters = distance;
            policy.RecoveryTransportFee = transportFee;
            policy.RecoveryExpedited = false;
            policy.RecoveryClaimCost = quote.Cost;
            policy.RecoveryClaimCostLocked = true;
            policy.RecoveryDynamicPrice = quote.DynamicRecoveryPrice;
            policy.RecoveryPricingFactionTag = quote.FactionTag;
            policy.RecoveryPricingReputation = quote.FactionReputation;
            policy.RecoveryPricingDiscount = quote.FactionDiscount;
            policy.Consumed = true;
            long insuranceCooldownSeconds = StartInsuranceCooldown(player.IdentityId,
                InsuranceMath.Add(quote.Cost, transportFee), nowUtcTicks);
            AddIncident(policy, new IncidentRecord
            {
                UtcTicks = nowUtcTicks,
                EventType = "RemoteRecoveryOrdered",
                AttackerIdentityId = player.IdentityId,
                AttackerName = player.DisplayName
            });
            _dirty = true;
            SaveState();

            SendResponse(steamId, FormatQuote(policy, quote) +
                FormatRecoveryOrder(policy, serviceTerminal.EntityId, nowUtcTicks) +
                "\nPolicy used up by transport order" +
                (insuranceCooldownSeconds > 0
                    ? "; insurance cooldown " + Duration(insuranceCooldownSeconds)
                    : string.Empty) + ".");
        }

        internal void ExpediteRecovery(IMyPlayer player, ulong steamId, long policyId,
            long serviceTerminalId)
        {
            InsurancePolicy policy = ResolveOwnedPolicy(player, 0, policyId);
            if (policy == null)
            {
                SendResponse(steamId, "Select a recovery policy at the Services Terminal.");
                return;
            }
            if (!HasRecoveryOrder(policy))
            {
                SendResponse(steamId, "Start remote recovery with Claim before expediting it.");
                return;
            }
            if (policy.RecoveryTerminalEntityId != serviceTerminalId)
            {
                SendResponse(steamId, "Only the Services Terminal that placed this order can expedite it.");
                return;
            }
            if (policy.RecoveryExpedited)
            {
                SendResponse(steamId, "This recovery order has already been expedited once.");
                return;
            }

            long nowUtcTicks = DateTime.UtcNow.Ticks;
            long remainingSeconds = InsuranceMath.RemainingSeconds(policy.RecoveryReadyUtcTicks,
                nowUtcTicks);
            if (remainingSeconds <= 0)
            {
                SendResponse(steamId, "Recovery has arrived. Use Claim to deploy it.");
                return;
            }
            double factor = InsuranceMath.Clamp01(_config.RemoteRecoveryExpediteFactor);
            if (factor >= 1.0)
            {
                SendResponse(steamId, "Remote recovery expedite is disabled by server config.");
                return;
            }

            long expeditePrice = InsuranceMath.ExpediteFee(remainingSeconds,
                _config.RemoteRecoveryExpediteCostPerSecond);
            long balance = 0;
            if (expeditePrice > 0 && (!player.TryGetBalanceInfo(out balance) || balance < expeditePrice))
            {
                SendResponse(steamId, "Expedite costs " + Money(expeditePrice) +
                    " SC; balance " + Money(balance) + " SC.");
                return;
            }

            if (expeditePrice > 0) player.RequestChangeBalance(-expeditePrice);
            policy.RecoveryReadyUtcTicks = InsuranceMath.ExpediteReadyUtcTicks(nowUtcTicks,
                policy.RecoveryReadyUtcTicks, factor);
            policy.RecoveryExpedited = true;
            AddIncident(policy, new IncidentRecord
            {
                UtcTicks = nowUtcTicks,
                EventType = "RemoteRecoveryExpedited",
                AttackerIdentityId = player.IdentityId,
                AttackerName = player.DisplayName
            });
            _dirty = true;
            SaveState();

            SendResponse(steamId, "Recovery expedited for " + Money(expeditePrice) + " SC." +
                FormatRecoveryOrder(policy, serviceTerminalId, nowUtcTicks));
        }

        private ClaimQuote BuildQuote(InsurancePolicy policy, EconomyPricingContext pricing)
        {
            ClaimQuote quote = new ClaimQuote
            {
                TotalLoss = !HasLivePolicyGrid(policy),
                BaselineValue = CalculatePolicyBaselineValue(policy)
            };

            bool missingGrid = false;
            for (int gridIndex = 0; gridIndex < policy.Grids.Count; gridIndex++)
            {
                InsuredGridSnapshot insuredGrid = policy.Grids[gridIndex];
                MyObjectBuilder_CubeGrid blueprint = insuredGrid.Blueprint;
                if (blueprint == null || blueprint.CubeBlocks == null) continue;
                IMyCubeGrid grid = FindGrid(insuredGrid.GridEntityId);
                if (grid == null) missingGrid = true;

                for (int i = 0; i < blueprint.CubeBlocks.Count; i++)
                {
                    MyObjectBuilder_CubeBlock snapshot = blueprint.CubeBlocks[i];
                    long fullValue = GetBlockComponentValue(snapshot);
                    double targetRatio = InsuranceMath.Clamp01(snapshot.IntegrityPercent);
                    IMySlimBlock current = grid == null ? null : grid.GetCubeBlock(snapshot.Min);

                    if (current == null)
                    {
                        long loss = InsuranceMath.Scale(fullValue, targetRatio);
                        quote.MissingBlocks++;
                        AddRepairItem(quote, insuredGrid, snapshot, null, grid, loss,
                            targetRatio, true);
                        continue;
                    }

                    if (!BlocksMatch(snapshot, current))
                    {
                        quote.ConflictingBlocks++;
                        quote.LossValue = InsuranceMath.Add(quote.LossValue,
                            InsuranceMath.Scale(fullValue, targetRatio));
                        continue;
                    }

                    double currentRatio = current.MaxIntegrity <= 0f ? 0.0 : current.Integrity / current.MaxIntegrity;
                    double delta = Math.Max(0.0, targetRatio - currentRatio);
                    if (delta <= 0.0001) continue;

                    long damageLoss = InsuranceMath.Scale(fullValue, delta);
                    quote.DamagedBlocks++;
                    AddRepairItem(quote, insuredGrid, snapshot, current, grid, damageLoss,
                        delta, false);
                }
            }

            quote.Recovery = InsuranceMath.RequiresRecovery(quote.TotalLoss, missingGrid, quote.LossRatio);
            if (quote.Recovery)
                ApplyRecoveryPricing(policy, quote, pricing);
            else
                quote.Cost = quote.LossValue <= 0 ? 0 :
                    InsuranceMath.AttributedClaimFee(quote.AttributedCost,
                        _config.MinimumClaimFee, 0.0);
            return quote;
        }

        private void ApplyRecoveryPricing(InsurancePolicy policy, ClaimQuote quote,
            EconomyPricingContext pricing)
        {
            quote.RecoveryValue = quote.BaselineValue;
            quote.FactionTag = pricing.FactionTag;
            quote.FactionReputation = pricing.Reputation;
            quote.FactionDiscount = pricing.Discount;
            quote.DynamicRecoveryPrice = pricing.DynamicRecoveryPrice;

            if (HasRecoveryOrder(policy) && policy.RecoveryClaimCostLocked)
            {
                quote.Cost = policy.RecoveryClaimCost;
                quote.FactionTag = policy.RecoveryPricingFactionTag;
                quote.FactionReputation = policy.RecoveryPricingReputation;
                quote.FactionDiscount = policy.RecoveryPricingDiscount;
                quote.DynamicRecoveryPrice = policy.RecoveryDynamicPrice;
                quote.RecoveryPriceLocked = true;
                return;
            }

            if (pricing.DynamicRecoveryPrice)
                quote.RecoveryValue = CalculatePolicyBaselineValue(policy,
                    pricing.ComponentPrices);
            long attributedCost = CalculateRecoveryAttributedCost(policy,
                pricing.DynamicRecoveryPrice ? pricing.ComponentPrices : null);
            quote.Cost = quote.RecoveryValue <= 0 ? 0 :
                InsuranceMath.AttributedClaimFee(attributedCost,
                    _config.MinimumClaimFee, pricing.Discount);
        }

        private void AddRepairItem(ClaimQuote quote, InsuredGridSnapshot insuredGrid,
            MyObjectBuilder_CubeBlock snapshot, IMySlimBlock current, IMyCubeGrid grid,
            long lossValue, double integrityDelta, bool missing)
        {
            quote.LossValue = InsuranceMath.Add(quote.LossValue, lossValue);
            long claimCost = CalculateAttributedCost(insuredGrid, (Vector3I)snapshot.Min,
                lossValue, integrityDelta);
            quote.AttributedCost = InsuranceMath.Add(quote.AttributedCost, claimCost);
            quote.Items.Add(new RepairItem
            {
                InsuredGrid = insuredGrid,
                Snapshot = snapshot,
                Existing = current,
                Grid = grid,
                LossValue = lossValue,
                ClaimCost = claimCost,
                IntegrityDelta = integrityDelta,
                Missing = missing
            });
        }

        private ClaimLedger CreateClaimLedger(InsurancePolicy policy, ClaimQuote quote,
            Dictionary<MyDefinitionId, long> componentPrices)
        {
            Dictionary<int, LedgerAccumulator> groups =
                new Dictionary<int, LedgerAccumulator>();
            long repairValue = 0;
            long attributedSubtotal = 0;

            if (quote.Recovery)
            {
                for (int gridIndex = 0; gridIndex < policy.Grids.Count; gridIndex++)
                {
                    InsuredGridSnapshot insuredGrid = policy.Grids[gridIndex];
                    MyObjectBuilder_CubeGrid blueprint = insuredGrid.Blueprint;
                    if (blueprint == null || blueprint.CubeBlocks == null) continue;
                    for (int blockIndex = 0; blockIndex < blueprint.CubeBlocks.Count; blockIndex++)
                    {
                        MyObjectBuilder_CubeBlock block = blueprint.CubeBlocks[blockIndex];
                        double ratio = InsuranceMath.Clamp01(block.IntegrityPercent);
                        long value = InsuranceMath.Scale(GetBlockComponentValue(block.GetId(),
                            componentPrices), ratio);
                        long cost = CalculateAttributedCost(insuredGrid,
                            (Vector3I)block.Min, value, ratio);
                        repairValue = InsuranceMath.Add(repairValue, value);
                        attributedSubtotal = InsuranceMath.Add(attributedSubtotal, cost);
                        AccumulateLedgerGroups(groups, insuredGrid, (Vector3I)block.Min,
                            value, ratio);
                    }
                }
            }
            else
            {
                for (int i = 0; i < quote.Items.Count; i++)
                {
                    RepairItem item = quote.Items[i];
                    repairValue = InsuranceMath.Add(repairValue, item.LossValue);
                    attributedSubtotal = InsuranceMath.Add(attributedSubtotal, item.ClaimCost);
                    AccumulateLedgerGroups(groups, item.InsuredGrid,
                        (Vector3I)item.Snapshot.Min, item.LossValue, item.IntegrityDelta);
                }
            }

            ClaimLedger ledger = new ClaimLedger
            {
                PolicyId = policy.PolicyId,
                GridName = policy.GridName,
                Recovery = quote.Recovery,
                RepairValue = repairValue,
                UnrepairableValue = Math.Max(0, quote.LossValue - repairValue),
                AttributedSubtotal = attributedSubtotal,
                FinalCost = quote.Cost,
                FactionTag = quote.FactionTag,
                FactionReputation = quote.FactionReputation,
                DynamicRecoveryPrice = quote.DynamicRecoveryPrice,
                RecoveryPriceLocked = quote.RecoveryPriceLocked
            };
            BuildLedgerEntries(ledger, groups);

            long adjusted = attributedSubtotal;
            if (quote.RecoveryPriceLocked)
            {
                AddLedgerAdjustment(ledger, "Locked quote adjustment", quote.Cost - adjusted);
                adjusted = quote.Cost;
            }
            else
            {
                long discounted = quote.Recovery && quote.FactionDiscount > 0.0
                    ? InsuranceMath.Scale(attributedSubtotal, 1.0 - quote.FactionDiscount)
                    : attributedSubtotal;
                AddLedgerAdjustment(ledger, "Faction reputation discount",
                    discounted - adjusted);
                adjusted = discounted;
                AddLedgerAdjustment(ledger, "Minimum claim fee", quote.Cost - adjusted);
                adjusted = quote.Cost;
            }
            return ledger;
        }

        private void AccumulateLedgerGroups(Dictionary<int, LedgerAccumulator> groups,
            InsuredGridSnapshot insuredGrid, Vector3I position, long value, double lossRatio)
        {
            if (value <= 0 || lossRatio <= 0.0) return;
            double recorded = 0.0;
            BlockLossAttribution attribution;
            if (GetLossAttributionMap(insuredGrid).TryGetValue(position, out attribution) &&
                attribution.Buckets != null)
                for (int i = 0; i < attribution.Buckets.Count; i++)
                    if (attribution.Buckets[i] != null &&
                        attribution.Buckets[i].IntegrityLossRatio > 0.0)
                        recorded += attribution.Buckets[i].IntegrityLossRatio;

            double scale = recorded > lossRatio ? lossRatio / recorded : 1.0;
            double used = 0.0;
            if (attribution != null && attribution.Buckets != null)
            {
                for (int i = 0; i < attribution.Buckets.Count; i++)
                {
                    LossAttributionBucket bucket = attribution.Buckets[i];
                    if (bucket == null || bucket.IntegrityLossRatio <= 0.0) continue;
                    double share = bucket.IntegrityLossRatio * scale / lossRatio;
                    used += share;
                    AddLedgerGroup(groups, bucket.Cause, bucket.Relationship, value, share);
                }
            }
            if (used < 1.0)
                AddLedgerGroup(groups, InsuranceDamageCause.Unknown,
                    InsuranceDamageRelationship.Unknown, value, 1.0 - used);
        }

        private void AddLedgerGroup(Dictionary<int, LedgerAccumulator> groups,
            InsuranceDamageCause cause, InsuranceDamageRelationship relationship,
            long value, double share)
        {
            if (share <= 0.0) return;
            int key = ((int)cause << 8) | (int)relationship;
            LedgerAccumulator group;
            if (!groups.TryGetValue(key, out group))
            {
                group = new LedgerAccumulator
                {
                    Cause = cause,
                    Relationship = relationship,
                    Rate = DamageClaimFraction(cause, relationship)
                };
                groups[key] = group;
            }
            group.RepairValue += value * share;
            group.RawCost += value * share * group.Rate;
        }

        private static void BuildLedgerEntries(ClaimLedger ledger,
            Dictionary<int, LedgerAccumulator> groups)
        {
            List<LedgerAccumulator> ordered = new List<LedgerAccumulator>(groups.Values);
            ordered.Sort(delegate(LedgerAccumulator left, LedgerAccumulator right)
            {
                int cost = right.RawCost.CompareTo(left.RawCost);
                if (cost != 0) return cost;
                int cause = left.Cause.CompareTo(right.Cause);
                return cause != 0 ? cause : left.Relationship.CompareTo(right.Relationship);
            });
            double rawCost = 0.0;
            double rawValue = 0.0;
            for (int i = 0; i < ordered.Count; i++)
            {
                rawCost += ordered[i].RawCost;
                rawValue += ordered[i].RepairValue;
            }
            long remainingCost = ledger.AttributedSubtotal;
            long remainingValue = ledger.RepairValue;
            for (int i = 0; i < ordered.Count; i++)
            {
                LedgerAccumulator group = ordered[i];
                bool last = i == ordered.Count - 1;
                long cost = last ? remainingCost : rawCost <= 0.0 ? 0 :
                    Math.Min(remainingCost, (long)Math.Floor(
                        ledger.AttributedSubtotal * group.RawCost / rawCost));
                long value = last ? remainingValue : rawValue <= 0.0 ? 0 :
                    Math.Min(remainingValue, (long)Math.Floor(
                        ledger.RepairValue * group.RepairValue / rawValue));
                remainingCost -= cost;
                remainingValue -= value;
                ledger.Entries.Add(new ClaimLedgerEntry
                {
                    Cause = group.Cause,
                    Relationship = group.Relationship,
                    RepairValue = value,
                    Rate = group.Rate,
                    Cost = cost
                });
            }
        }

        private static void AddLedgerAdjustment(ClaimLedger ledger, string label, long amount)
        {
            if (amount == 0) return;
            ledger.Adjustments.Add(new ClaimLedgerAdjustment { Label = label, Amount = amount });
        }

        private sealed class LedgerAccumulator
        {
            internal InsuranceDamageCause Cause;
            internal InsuranceDamageRelationship Relationship;
            internal double RepairValue;
            internal double RawCost;
            internal double Rate;
        }

        private static bool BlocksMatch(MyObjectBuilder_CubeBlock snapshot, IMySlimBlock current)
        {
            return current.Min == (Vector3I)snapshot.Min &&
                   current.BlockDefinition.Id == snapshot.GetId() &&
                   current.Orientation.Forward == snapshot.BlockOrientation.Forward &&
                   current.Orientation.Up == snapshot.BlockOrientation.Up;
        }

        private bool RestoreMissingBlock(IMyCubeGrid grid, MyObjectBuilder_CubeBlock snapshot)
        {
            try
            {
                MyObjectBuilder_CubeBlock clone = Clone(snapshot);
                clone.EntityId = 0;
                clone.Name = null;
                return grid.AddBlock(clone, true) != null;
            }
            catch (Exception exception)
            {
                Log("Could not restore block at " + snapshot.Min, exception);
                return false;
            }
        }

        private bool RestoreDamagedBlock(RepairItem item)
        {
            if (item.Existing == null || item.Existing.IsDestroyed) return false;

            try
            {
                float integrityBefore = item.Existing.Integrity;
                MyCubeBlockDefinition definition = item.Existing.BlockDefinition as MyCubeBlockDefinition;
                float mountAmount = float.MaxValue;
                if (definition != null && definition.IntegrityPointsPerSec > 0f)
                    mountAmount = (float)(item.Existing.MaxIntegrity * item.IntegrityDelta / definition.IntegrityPointsPerSec);

                item.Existing.IncreaseMountLevel(mountAmount, item.Snapshot.Owner, null, float.MaxValue, true, item.Snapshot.ShareMode);
                if (item.Existing.HasDeformation) item.Existing.FixBones(0f, float.MaxValue);
                return item.Existing.Integrity > integrityBefore + 0.001f;
            }
            catch (Exception exception)
            {
                Log("Could not repair block at " + item.Snapshot.Min, exception);
                return false;
            }
        }

        private bool RecoverGridGroup(InsurancePolicy policy, MyPositionAndOrientation recoveryPose)
        {
            List<IMyCubeGrid> created = new List<IMyCubeGrid>(policy.Grids.Count);
            List<long> recoveringIds = new List<long>(policy.Grids.Count);
            bool committed = false;
            try
            {
                MatrixD anchorMatrix = recoveryPose.GetMatrix();
                List<MyObjectBuilder_CubeGrid> clones = new List<MyObjectBuilder_CubeGrid>(policy.Grids.Count);
                for (int i = 0; i < policy.Grids.Count; i++)
                {
                    InsuredGridSnapshot snapshot = policy.Grids[i];
                    MyObjectBuilder_CubeGrid clone = Clone(snapshot.Blueprint);
                    clone.PositionAndOrientation = new MyPositionAndOrientation(snapshot.RelativePose.GetMatrix() * anchorMatrix);
                    clone.LinearVelocity = Vector3.Zero;
                    clone.AngularVelocity = Vector3.Zero;
                    for (int blockIndex = 0; blockIndex < clone.CubeBlocks.Count; blockIndex++)
                    {
                        clone.CubeBlocks[blockIndex].BuildPercent = 1f;
                        clone.CubeBlocks[blockIndex].IntegrityPercent = 1f;
                    }
                    clones.Add(clone);
                }

                MyAPIGateway.Entities.RemapObjectBuilderCollection(clones);
                for (int i = 0; i < clones.Count; i++)
                {
                    IMyCubeGrid grid = MyAPIGateway.Entities.CreateFromObjectBuilder(clones[i]) as IMyCubeGrid;
                    if (grid == null) return false;
                    created.Add(grid);
                }

                for (int i = 0; i < policy.Grids.Count; i++)
                {
                    long oldId = policy.Grids[i].GridEntityId;
                    IMyCubeGrid oldGrid = FindGrid(oldId);
                    UnsubscribeGrid(oldId);
                    if (oldGrid == null) continue;
                    _repairingGrids.Add(oldId);
                    recoveringIds.Add(oldId);
                    oldGrid.Close();
                    MyAPIGateway.Entities.RemoveEntity(oldGrid);
                }

                for (int i = 0; i < created.Count; i++)
                {
                    SetGridMarker(created[i], policy.Grids[i].PersistentId);
                    MyAPIGateway.Entities.AddEntity(created[i], true);
                    policy.Grids[i].GridEntityId = created[i].EntityId;
                    SubscribeGrid(created[i]);
                }
                policy.GridEntityId = policy.Grids[0].GridEntityId;
                policy.LastKnownPose = recoveryPose;
                committed = true;
                try
                {
                    List<MyObjectBuilder_EntityBase> spawnedBuilders = new List<MyObjectBuilder_EntityBase>(clones.Count);
                    for (int i = 0; i < clones.Count; i++) spawnedBuilders.Add(clones[i]);
                    MyAPIGateway.Multiplayer.SendEntitiesCreated(spawnedBuilders);
                }
                catch (Exception exception)
                {
                    Log("Could not announce recovered grids", exception);
                }
                return true;
            }
            catch (Exception exception)
            {
                Log("Grid group recovery failed", exception);
                return false;
            }
            finally
            {
                for (int i = 0; i < recoveringIds.Count; i++) _repairingGrids.Remove(recoveringIds[i]);
                if (!committed)
                    for (int i = 0; i < created.Count; i++)
                        if (!created[i].Closed) created[i].Close();
            }
        }

        private bool IsRecoveryAreaClear(InsurancePolicy policy, MyPositionAndOrientation recoveryPose)
        {
            BoundingSphereD sphere = new BoundingSphereD(recoveryPose.Position, Math.Max(5.0, policy.ClearanceRadius));
            List<IMyEntity> entities = MyAPIGateway.Entities.GetEntitiesInSphere(ref sphere);
            for (int i = 0; i < entities.Count; i++)
            {
                IMyCubeGrid grid = entities[i] as IMyCubeGrid;
                if (grid != null && !grid.Closed && FindPolicyByGrid(grid.EntityId) != policy) return false;
            }
            return true;
        }

        private string FormatQuote(InsurancePolicy policy, ClaimQuote quote)
        {
            return "Policy #" + policy.PolicyId + " " + policy.GridName +
                   "\nLoss: " + Percent(quote.LossRatio) + " (" + Money(quote.LossValue) + " / " + Money(quote.BaselineValue) + " SC)" +
                   "\nMissing: " + quote.MissingBlocks + ", damaged: " + quote.DamagedBlocks + ", conflicts: " + quote.ConflictingBlocks +
                   "\nClaim price: " + Money(quote.Cost) + " SC" +
                   (quote.TotalLoss ? " [total-loss recovery]" : quote.Recovery ? " [full group recovery]" : string.Empty) +
                   FormatEconomyPricing(quote.FactionTag, quote.FactionReputation,
                       quote.FactionDiscount, quote.DynamicRecoveryPrice,
                       quote.RecoveryPriceLocked);
        }

        private static string FormatEconomyPricing(string factionTag, int reputation,
            double discount, bool dynamicRecoveryPrice, bool locked)
        {
            if (string.IsNullOrWhiteSpace(factionTag) && !dynamicRecoveryPrice && !locked)
                return string.Empty;

            bool economy = !string.IsNullOrWhiteSpace(factionTag) || dynamicRecoveryPrice;
            StringBuilder text = new StringBuilder(" [");
            if (economy) text.Append("economy");
            if (!string.IsNullOrWhiteSpace(factionTag))
                text.Append(" ").Append(factionTag).Append(" rep ").Append(reputation);
            if (discount > 0.0)
                text.Append(", ").Append(Percent(discount)).Append(" discount");
            if (dynamicRecoveryPrice) text.Append(", market priced");
            if (locked) text.Append(economy ? ", locked quote" : "locked quote");
            return text.Append("]").ToString();
        }

        private string FormatRecoveryOrder(InsurancePolicy policy, long serviceTerminalId, long nowUtcTicks)
        {
            long remainingSeconds = InsuranceMath.RemainingSeconds(policy.RecoveryReadyUtcTicks,
                nowUtcTicks);
            StringBuilder text = new StringBuilder("\nRemote recovery: ")
                .Append(Distance(policy.RecoveryDistanceMeters)).Append(", transport ")
                .Append(Money(policy.RecoveryTransportFee)).Append(" SC paid, ");
            if (remainingSeconds <= 0)
            {
                text.Append("ready to deploy with Claim");
            }
            else
            {
                text.Append("ETA ").Append(Duration(remainingSeconds));
                if (policy.RecoveryExpedited)
                {
                    text.Append(" [expedited]");
                }
                else if (policy.RecoveryTerminalEntityId == serviceTerminalId &&
                         InsuranceMath.Clamp01(_config.RemoteRecoveryExpediteFactor) < 1.0)
                {
                    long expeditePrice = InsuranceMath.ExpediteFee(remainingSeconds,
                        _config.RemoteRecoveryExpediteCostPerSecond);
                    int reduction = (int)Math.Round(
                        (1.0 - InsuranceMath.Clamp01(_config.RemoteRecoveryExpediteFactor)) * 100.0);
                    text.Append("; Expedite ").Append(Money(expeditePrice)).Append(" SC for ")
                        .Append(reduction).Append("% less remaining time");
                }
            }
            if (policy.RecoveryTerminalEntityId != serviceTerminalId)
                text.Append(" [assigned to another Services Terminal]");
            return text.ToString();
        }

        private static bool HasRecoveryOrder(InsurancePolicy policy)
        {
            return policy != null && policy.RecoveryTerminalEntityId != 0 &&
                   policy.RecoveryReadyUtcTicks > 0;
        }

        private void RemovePolicy(InsurancePolicy policy)
        {
            if (policy == null || !_state.Policies.Remove(policy)) return;
            for (int i = 0; i < policy.Grids.Count; i++)
            {
                long gridId = policy.Grids[i].GridEntityId;
                _lossAttributionByGrid.Remove(policy.Grids[i].PersistentId);
                _insuredBlocksByGrid.Remove(policy.Grids[i].PersistentId);
                RemoveGridMarker(FindGrid(gridId), policy.Grids[i].PersistentId);
                if (FindPolicyByGrid(gridId) == null) UnsubscribeGrid(gridId);
            }
        }

        private long GetInsuranceCooldownReadyUtcTicks(long ownerIdentityId,
            long nowUtcTicks)
        {
            if (_state.Cooldowns == null) return 0;
            if (_config.InsuranceCooldownCreditsPerSecond <= 0)
            {
                if (_state.Cooldowns.Count > 0)
                {
                    _state.Cooldowns.Clear();
                    _dirty = true;
                }
                return 0;
            }
            for (int i = _state.Cooldowns.Count - 1; i >= 0; i--)
            {
                InsuranceCooldown cooldown = _state.Cooldowns[i];
                if (cooldown == null || cooldown.ReadyUtcTicks <= nowUtcTicks)
                {
                    _state.Cooldowns.RemoveAt(i);
                    _dirty = true;
                    continue;
                }
                if (cooldown.OwnerIdentityId == ownerIdentityId)
                    return cooldown.ReadyUtcTicks;
            }
            return 0;
        }

        private long StartInsuranceCooldown(long ownerIdentityId, long serviceCost,
            long nowUtcTicks)
        {
            long seconds = InsuranceMath.InsuranceCooldownSeconds(serviceCost,
                _config.InsuranceCooldownCreditsPerSecond,
                _config.InsuranceCooldownMinimumSeconds,
                _config.InsuranceCooldownMaximumSeconds);
            if (seconds <= 0) return 0;

            if (_state.Cooldowns == null)
                _state.Cooldowns = new List<InsuranceCooldown>();
            InsuranceCooldown cooldown = null;
            for (int i = 0; i < _state.Cooldowns.Count; i++)
            {
                if (_state.Cooldowns[i].OwnerIdentityId != ownerIdentityId) continue;
                cooldown = _state.Cooldowns[i];
                break;
            }
            if (cooldown == null)
            {
                cooldown = new InsuranceCooldown { OwnerIdentityId = ownerIdentityId };
                _state.Cooldowns.Add(cooldown);
            }
            cooldown.ReadyUtcTicks = InsuranceMath.Add(nowUtcTicks,
                InsuranceMath.Multiply(seconds, TimeSpan.TicksPerSecond));
            cooldown.ServiceCost = Math.Max(0, serviceCost);
            _dirty = true;
            return seconds;
        }

        private static void ClearRecoveryOrder(InsurancePolicy policy)
        {
            policy.RecoveryTerminalEntityId = 0;
            policy.RecoveryReadyUtcTicks = 0;
            policy.RecoveryDistanceMeters = 0.0;
            policy.RecoveryTransportFee = 0;
            policy.RecoveryExpedited = false;
            policy.RecoveryClaimCost = 0;
            policy.RecoveryClaimCostLocked = false;
            policy.RecoveryDynamicPrice = false;
            policy.RecoveryPricingFactionTag = null;
            policy.RecoveryPricingReputation = 0;
            policy.RecoveryPricingDiscount = 0.0;
        }

        private static string Distance(double meters)
        {
            return meters >= 1000.0
                ? (meters / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " km"
                : Math.Max(0.0, meters).ToString("0", CultureInfo.InvariantCulture) + " m";
        }

        internal static string Duration(long seconds)
        {
            TimeSpan duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return duration.TotalDays >= 1.0
                ? duration.Days + "d " + duration.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture)
                : duration.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
        }

        private MyObjectBuilder_CubeGrid CreateSanitizedBlueprint(IMyCubeGrid grid)
        {
            MyObjectBuilder_CubeGrid blueprint = grid.GetObjectBuilder(true) as MyObjectBuilder_CubeGrid;
            if (blueprint == null || blueprint.CubeBlocks == null) return null;

            List<long> owners = new List<long>(blueprint.CubeBlocks.Count);
            List<MyOwnershipShareModeEnum> shares = new List<MyOwnershipShareModeEnum>(blueprint.CubeBlocks.Count);
            for (int i = 0; i < blueprint.CubeBlocks.Count; i++)
            {
                owners.Add(blueprint.CubeBlocks[i].Owner);
                shares.Add(blueprint.CubeBlocks[i].ShareMode);
            }

            blueprint.SetupForProjector();
            for (int i = 0; i < blueprint.CubeBlocks.Count; i++)
            {
                blueprint.CubeBlocks[i].Owner = owners[i];
                blueprint.CubeBlocks[i].ShareMode = shares[i];
            }
            return blueprint;
        }

        private long CalculateBaselineValue(MyObjectBuilder_CubeGrid blueprint)
        {
            return CalculateBaselineValue(blueprint, null);
        }

        private long CalculateBaselineValue(MyObjectBuilder_CubeGrid blueprint,
            Dictionary<MyDefinitionId, long> componentPrices)
        {
            long value = 0;
            for (int i = 0; i < blueprint.CubeBlocks.Count; i++)
            {
                MyObjectBuilder_CubeBlock block = blueprint.CubeBlocks[i];
                value = InsuranceMath.Add(value, InsuranceMath.Scale(
                    GetBlockComponentValue(block.GetId(), componentPrices),
                    InsuranceMath.Clamp01(block.IntegrityPercent)));
            }
            return value;
        }

        private long CalculatePolicyBaselineValue(InsurancePolicy policy)
        {
            return CalculatePolicyBaselineValue(policy, null);
        }

        private long CalculatePolicyBaselineValue(InsurancePolicy policy,
            Dictionary<MyDefinitionId, long> componentPrices)
        {
            long value = 0;
            if (policy == null || policy.Grids == null) return value;
            for (int i = 0; i < policy.Grids.Count; i++)
            {
                MyObjectBuilder_CubeGrid blueprint = policy.Grids[i].Blueprint;
                if (blueprint == null || blueprint.CubeBlocks == null) continue;
                value = InsuranceMath.Add(value,
                    CalculateBaselineValue(blueprint, componentPrices));
            }
            return value;
        }

        private long CalculateRecoveryAttributedCost(InsurancePolicy policy,
            Dictionary<MyDefinitionId, long> componentPrices)
        {
            long value = 0;
            if (policy == null || policy.Grids == null) return value;
            for (int gridIndex = 0; gridIndex < policy.Grids.Count; gridIndex++)
            {
                InsuredGridSnapshot insuredGrid = policy.Grids[gridIndex];
                MyObjectBuilder_CubeGrid blueprint = insuredGrid.Blueprint;
                if (blueprint == null || blueprint.CubeBlocks == null) continue;
                for (int blockIndex = 0; blockIndex < blueprint.CubeBlocks.Count;
                    blockIndex++)
                {
                    MyObjectBuilder_CubeBlock block = blueprint.CubeBlocks[blockIndex];
                    double targetRatio = InsuranceMath.Clamp01(block.IntegrityPercent);
                    long blockValue = InsuranceMath.Scale(
                        GetBlockComponentValue(block.GetId(), componentPrices), targetRatio);
                    value = InsuranceMath.Add(value, CalculateAttributedCost(insuredGrid,
                        (Vector3I)block.Min, blockValue, targetRatio));
                }
            }
            return value;
        }

        private long CalculateAttributedCost(InsuredGridSnapshot insuredGrid,
            Vector3I position, long lossValue, double currentLossRatio)
        {
            double recorded = 0.0;
            double weighted = 0.0;
            BlockLossAttribution attribution;
            if (GetLossAttributionMap(insuredGrid).TryGetValue(position,
                out attribution) && attribution.Buckets != null)
            {
                for (int i = 0; i < attribution.Buckets.Count; i++)
                {
                    LossAttributionBucket bucket = attribution.Buckets[i];
                    if (bucket == null || bucket.IntegrityLossRatio <= 0.0) continue;
                    recorded += bucket.IntegrityLossRatio;
                    weighted += bucket.IntegrityLossRatio * DamageClaimFraction(
                        bucket.Cause, bucket.Relationship);
                }
            }

            double fraction = InsuranceMath.AttributedFraction(currentLossRatio,
                recorded, weighted, DamageClaimFraction(InsuranceDamageCause.Unknown,
                    InsuranceDamageRelationship.Unknown));
            return InsuranceMath.Scale(lossValue, fraction);
        }

        private double DamageClaimFraction(InsuranceDamageCause cause,
            InsuranceDamageRelationship relationship)
        {
            double relationshipFraction;
            switch (relationship)
            {
                case InsuranceDamageRelationship.Owner:
                    relationshipFraction = _config.OwnerDamageClaimValueFraction;
                    break;
                case InsuranceDamageRelationship.Faction:
                    relationshipFraction = _config.FactionDamageClaimValueFraction;
                    break;
                case InsuranceDamageRelationship.Other:
                    relationshipFraction = _config.OtherDamageClaimValueFraction;
                    break;
                case InsuranceDamageRelationship.Environment:
                    relationshipFraction = _config.EnvironmentDamageClaimValueFraction;
                    break;
                default:
                    relationshipFraction = _config.UnknownDamageClaimValueFraction;
                    break;
            }

            double causeMultiplier;
            switch (cause)
            {
                case InsuranceDamageCause.Grinding:
                    causeMultiplier = _config.GrindingDamageClaimMultiplier;
                    break;
                case InsuranceDamageCause.Ramming:
                    causeMultiplier = _config.RammingDamageClaimMultiplier;
                    break;
                case InsuranceDamageCause.Weapon:
                    causeMultiplier = _config.WeaponDamageClaimMultiplier;
                    break;
                case InsuranceDamageCause.Environment:
                    causeMultiplier = _config.EnvironmentDamageClaimMultiplier;
                    break;
                case InsuranceDamageCause.Other:
                    causeMultiplier = _config.OtherDamageClaimMultiplier;
                    break;
                default:
                    causeMultiplier = _config.UnknownDamageClaimMultiplier;
                    break;
            }

            return Math.Max(0.0, _config.ClaimValueFraction) *
                   Math.Max(0.0, relationshipFraction) *
                   Math.Max(0.0, causeMultiplier);
        }

        private long GetBlockComponentValue(MyObjectBuilder_CubeBlock block)
        {
            return GetBlockComponentValue(block.GetId());
        }

        private long GetBlockComponentValue(MyDefinitionId id)
        {
            return GetBlockComponentValue(id, null);
        }

        private long GetBlockComponentValue(MyDefinitionId id,
            Dictionary<MyDefinitionId, long> componentPrices)
        {
            if (componentPrices != null)
                return CalculateBlockComponentValue(id, componentPrices);

            long cached;
            if (_blockComponentValues.TryGetValue(id, out cached)) return cached;

            long value = CalculateBlockComponentValue(id, _componentPrices);
            _blockComponentValues[id] = value;
            return value;
        }

        private long CalculateBlockComponentValue(MyDefinitionId id,
            Dictionary<MyDefinitionId, long> componentPrices)
        {
            long value = 0;
            MyCubeBlockDefinition definition;
            if (MyDefinitionManager.Static.TryGetCubeBlockDefinition(id, out definition) && definition.Components != null)
            {
                for (int i = 0; i < definition.Components.Length; i++)
                {
                    MyCubeBlockDefinition.Component component = definition.Components[i];
                    long unitValue;
                    if (component.Definition == null ||
                        !componentPrices.TryGetValue(component.Definition.Id, out unitValue))
                    {
                        if (component.Definition == null ||
                            !_componentPrices.TryGetValue(component.Definition.Id, out unitValue))
                            unitValue = Math.Max(0, _config.DefaultComponentPrice);
                    }
                    value = InsuranceMath.Add(value, InsuranceMath.Scale(unitValue, component.Count));
                }
            }
            return value;
        }

        private static T Clone<T>(T value)
        {
            return MyAPIGateway.Utilities.SerializeFromBinary<T>(MyAPIGateway.Utilities.SerializeToBinary(value));
        }

        private void OnBeforeDamageApplied(object target, ref MyDamageInformation information)
        {
            if (!_active || !_isServer || information.Amount <= 0f) return;

            IMySlimBlock block = target as IMySlimBlock;
            if (block == null || block.CubeGrid == null ||
                _repairingGrids.Contains(block.CubeGrid.EntityId)) return;

            InsurancePolicy policy = FindPolicyByGrid(block.CubeGrid.EntityId);
            InsuredGridSnapshot insuredGrid;
            MyObjectBuilder_CubeBlock snapshotBlock;
            if (policy == null || !TryGetInsuredBlock(policy, block.CubeGrid.EntityId,
                block.Min, out insuredGrid, out snapshotBlock) ||
                !BlocksMatch(snapshotBlock, block)) return;

            double targetRatio = InsuranceMath.Clamp01(snapshotBlock.IntegrityPercent);
            double currentRatio = block.MaxIntegrity <= 0f ? 0.0 :
                block.Integrity / block.MaxIntegrity;
            if (currentRatio + 0.0001 >= targetRatio)
                ClearLossAttribution(insuredGrid, block.Min);
            _integrityBeforeDamage[block] = block.Integrity;
        }

        private void OnDamageApplied(object target, MyDamageInformation information)
        {
            if (!_active || !_isServer || information.Amount <= 0f) return;

            IMySlimBlock block = target as IMySlimBlock;
            if (block == null || block.CubeGrid == null || _repairingGrids.Contains(block.CubeGrid.EntityId)) return;

            InsurancePolicy policy = FindPolicyByGrid(block.CubeGrid.EntityId);
            if (policy == null) return;

            long attackerIdentity;
            string attackerName;
            IMyEntity attackerEntity = ResolveAttacker(information.AttackerId,
                out attackerIdentity, out attackerName);
            string damageType = information.Type.String ?? information.Type.ToString();
            InsuranceDamageCause damageCause = ClassifyDamageCause(damageType,
                attackerEntity);
            InsuranceDamageRelationship damageRelationship =
                ClassifyDamageRelationship(policy, information.AttackerId,
                    attackerIdentity, attackerEntity, damageCause);
            long now = DateTime.UtcNow.Ticks;

            float integrityBefore;
            if (_integrityBeforeDamage.TryGetValue(block, out integrityBefore))
            {
                _integrityBeforeDamage.Remove(block);
                InsuredGridSnapshot insuredGrid;
                MyObjectBuilder_CubeBlock snapshotBlock;
                double lossRatio = block.MaxIntegrity <= 0f ? 0.0 :
                    Math.Max(0.0, integrityBefore - block.Integrity) /
                    block.MaxIntegrity;
                if (lossRatio > 0.0 && TryGetInsuredBlock(policy,
                    block.CubeGrid.EntityId, block.Min, out insuredGrid,
                    out snapshotBlock) && BlocksMatch(snapshotBlock, block))
                    AddLossAttribution(insuredGrid, block.Min, damageCause,
                        damageRelationship, lossRatio);
            }

            Dictionary<Vector3I, DamageAttribution> blocks;
            if (!_lastDamage.TryGetValue(block.CubeGrid.EntityId, out blocks))
            {
                blocks = new Dictionary<Vector3I, DamageAttribution>();
                _lastDamage[block.CubeGrid.EntityId] = blocks;
            }
            blocks[block.Min] = new DamageAttribution
            {
                UtcTicks = now,
                AttackerEntityId = information.AttackerId,
                AttackerIdentityId = attackerIdentity,
                AttackerName = attackerName,
                DamageType = damageType,
                DamageCause = damageCause,
                DamageRelationship = damageRelationship
            };

            IncidentRecord last = policy.Incidents.Count == 0 ? null : policy.Incidents[policy.Incidents.Count - 1];
            if (last != null && last.EventType == "Damage" && last.AttackerEntityId == information.AttackerId &&
                last.DamageType == damageType && last.DamageCause == damageCause &&
                last.DamageRelationship == damageRelationship &&
                now - last.UtcTicks <= TimeSpan.TicksPerSecond * 10)
            {
                last.UtcTicks = now;
                last.DamageAmount += information.Amount;
                last.EventCount++;
            }
            else
            {
                AddIncident(policy, new IncidentRecord
                {
                    UtcTicks = now,
                    EventType = "Damage",
                    X = block.Min.X,
                    Y = block.Min.Y,
                    Z = block.Min.Z,
                    AttackerEntityId = information.AttackerId,
                    AttackerIdentityId = attackerIdentity,
                    AttackerName = attackerName,
                    DamageType = damageType,
                    DamageCause = damageCause,
                    DamageRelationship = damageRelationship,
                    DamageAmount = information.Amount
                });
            }
            _dirty = true;
        }

        private void OnBlockAdded(IMySlimBlock block)
        {
            if (!_active || block == null || block.CubeGrid == null || _repairingGrids.Contains(block.CubeGrid.EntityId)) return;
            InsurancePolicy policy = FindPolicyByGrid(block.CubeGrid.EntityId);
            if (policy == null) return;

            InsuredGridSnapshot insuredGrid;
            MyObjectBuilder_CubeBlock snapshotBlock;
            if (TryGetInsuredBlock(policy, block.CubeGrid.EntityId, block.Min,
                out insuredGrid, out snapshotBlock) && BlocksMatch(snapshotBlock, block))
                ClearLossAttribution(insuredGrid, block.Min);

            AddIncident(policy, new IncidentRecord
            {
                UtcTicks = DateTime.UtcNow.Ticks,
                EventType = "BlockAdded",
                X = block.Min.X,
                Y = block.Min.Y,
                Z = block.Min.Z,
                AttackerIdentityId = block.BuiltBy,
                AttackerName = ResolveIdentityName(block.BuiltBy)
            });
            _dirty = true;
        }

        private void OnBlockRemoved(IMySlimBlock block)
        {
            if (!_active || block == null || block.CubeGrid == null || _repairingGrids.Contains(block.CubeGrid.EntityId)) return;
            InsurancePolicy policy = FindPolicyByGrid(block.CubeGrid.EntityId);
            if (policy == null) return;

            InsuredGridSnapshot insuredGrid;
            MyObjectBuilder_CubeBlock snapshotBlock;
            if (TryGetInsuredBlock(policy, block.CubeGrid.EntityId, block.Min,
                out insuredGrid, out snapshotBlock) && BlocksMatch(snapshotBlock, block))
            {
                double targetRatio = InsuranceMath.Clamp01(snapshotBlock.IntegrityPercent);
                double currentRatio = block.MaxIntegrity <= 0f ? 0.0 :
                    block.Integrity / block.MaxIntegrity;
                if (currentRatio + 0.0001 >= targetRatio)
                    ClearLossAttribution(insuredGrid, block.Min);
            }

            DamageAttribution attribution = null;
            Dictionary<Vector3I, DamageAttribution> blocks;
            if (_lastDamage.TryGetValue(block.CubeGrid.EntityId, out blocks))
            {
                blocks.TryGetValue(block.Min, out attribution);
                blocks.Remove(block.Min);
            }
            long now = DateTime.UtcNow.Ticks;
            if (attribution != null && now - attribution.UtcTicks >
                TimeSpan.TicksPerSecond * 10) attribution = null;

            AddIncident(policy, new IncidentRecord
            {
                UtcTicks = now,
                EventType = "BlockRemoved",
                X = block.Min.X,
                Y = block.Min.Y,
                Z = block.Min.Z,
                AttackerEntityId = attribution == null ? 0 : attribution.AttackerEntityId,
                AttackerIdentityId = attribution == null ? 0 : attribution.AttackerIdentityId,
                AttackerName = attribution == null ? null : attribution.AttackerName,
                DamageType = attribution == null ? null : attribution.DamageType,
                DamageCause = attribution == null ? InsuranceDamageCause.Unknown :
                    attribution.DamageCause,
                DamageRelationship = attribution == null ?
                    InsuranceDamageRelationship.Unknown : attribution.DamageRelationship
            });
            _dirty = true;
        }

        private void AddIncident(InsurancePolicy policy, IncidentRecord incident)
        {
            if (policy.Incidents == null) policy.Incidents = new List<IncidentRecord>();
            policy.Incidents.Add(incident);
            int maximum = Math.Max(1, _config.MaxIncidentLogEntries);
            while (policy.Incidents.Count > maximum) policy.Incidents.RemoveAt(0);
        }

        private IMyEntity ResolveAttacker(long entityId, out long identityId,
            out string name)
        {
            identityId = 0;
            name = null;
            if (entityId == 0) return null;

            IMyEntity entity;
            if (!MyAPIGateway.Entities.TryGetEntityById(entityId, out entity) ||
                entity == null)
            {
                if (_identityNames.TryGetValue(entityId, out name))
                {
                    identityId = entityId;
                    return null;
                }
                List<IMyIdentity> identities = new List<IMyIdentity>();
                MyAPIGateway.Players.GetAllIdentites(identities,
                    delegate(IMyIdentity value) { return value.IdentityId == entityId; });
                if (identities.Count > 0)
                {
                    identityId = entityId;
                    name = identities[0].DisplayName;
                    _identityNames[identityId] = name;
                }
                return null;
            }

            IMyCharacter character = entity as IMyCharacter;
            if (character != null && character.ControllerInfo != null)
                identityId = character.ControllerInfo.ControllingIdentityId;

            IMyHandheldGunObject<MyDeviceBase> handheld =
                entity as IMyHandheldGunObject<MyDeviceBase>;
            if (handheld != null) identityId = handheld.OwnerIdentityId;

            IMyCubeBlock cubeBlock = entity as IMyCubeBlock;
            if (cubeBlock != null) identityId = cubeBlock.OwnerId;

            IMyCubeGrid grid = entity as IMyCubeGrid;
            if (grid != null && grid.BigOwners.Count > 0) identityId = grid.BigOwners[0];

            name = ResolveIdentityName(identityId);
            if (string.IsNullOrEmpty(name)) name = entity.DisplayName;
            return entity;
        }

        private static InsuranceDamageCause ClassifyDamageCause(string damageType,
            IMyEntity attackerEntity)
        {
            switch (damageType)
            {
                case "Grind":
                    return InsuranceDamageCause.Grinding;
                case "Deformation":
                    return attackerEntity is IMyCubeGrid
                        ? InsuranceDamageCause.Ramming
                        : InsuranceDamageCause.Environment;
                case "Explosion":
                case "Rocket":
                case "Bullet":
                case "Mine":
                case "Weapon":
                case "Fire":
                case "Thruster":
                case "Drill":
                case "Bolt":
                case "Destruction":
                    return InsuranceDamageCause.Weapon;
                case "Environment":
                case "Weather":
                case "Fall":
                case "Squeez":
                case "OutOfBounds":
                    return InsuranceDamageCause.Environment;
                case null:
                case "":
                case "Unknown":
                    return InsuranceDamageCause.Unknown;
                default:
                    return InsuranceDamageCause.Other;
            }
        }

        private static InsuranceDamageRelationship ClassifyDamageRelationship(
            InsurancePolicy policy, long attackerEntityId, long attackerIdentityId,
            IMyEntity attackerEntity, InsuranceDamageCause cause)
        {
            if (attackerIdentityId == policy.OwnerIdentityId)
                return InsuranceDamageRelationship.Owner;

            if (attackerIdentityId != 0)
            {
                IMyFaction ownerFaction = MyAPIGateway.Session.Factions
                    .TryGetPlayerFaction(policy.OwnerIdentityId);
                IMyFaction attackerFaction = MyAPIGateway.Session.Factions
                    .TryGetPlayerFaction(attackerIdentityId);
                return ownerFaction != null && attackerFaction != null &&
                       ownerFaction.FactionId == attackerFaction.FactionId
                    ? InsuranceDamageRelationship.Faction
                    : InsuranceDamageRelationship.Other;
            }

            if (attackerEntityId == 0 || cause == InsuranceDamageCause.Environment ||
                attackerEntity is IMyCubeGrid || attackerEntity is IMyCubeBlock)
                return InsuranceDamageRelationship.Environment;
            return InsuranceDamageRelationship.Unknown;
        }

        private bool TryGetInsuredBlock(InsurancePolicy policy, long gridEntityId,
            Vector3I position, out InsuredGridSnapshot insuredGrid,
            out MyObjectBuilder_CubeBlock block)
        {
            insuredGrid = null;
            block = null;
            if (policy == null || policy.Grids == null) return false;
            for (int i = 0; i < policy.Grids.Count; i++)
            {
                if (policy.Grids[i].GridEntityId != gridEntityId) continue;
                insuredGrid = policy.Grids[i];
                return GetInsuredBlockMap(insuredGrid).TryGetValue(position, out block);
            }
            return false;
        }

        private Dictionary<Vector3I, MyObjectBuilder_CubeBlock> GetInsuredBlockMap(
            InsuredGridSnapshot insuredGrid)
        {
            Dictionary<Vector3I, MyObjectBuilder_CubeBlock> blocks;
            if (_insuredBlocksByGrid.TryGetValue(insuredGrid.PersistentId, out blocks))
                return blocks;

            blocks = new Dictionary<Vector3I, MyObjectBuilder_CubeBlock>();
            MyObjectBuilder_CubeGrid blueprint = insuredGrid.Blueprint;
            if (blueprint != null && blueprint.CubeBlocks != null)
                for (int i = 0; i < blueprint.CubeBlocks.Count; i++)
                    blocks[(Vector3I)blueprint.CubeBlocks[i].Min] =
                        blueprint.CubeBlocks[i];
            _insuredBlocksByGrid[insuredGrid.PersistentId] = blocks;
            return blocks;
        }

        private Dictionary<Vector3I, BlockLossAttribution> GetLossAttributionMap(
            InsuredGridSnapshot insuredGrid)
        {
            Dictionary<Vector3I, BlockLossAttribution> blocks;
            if (_lossAttributionByGrid.TryGetValue(insuredGrid.PersistentId, out blocks))
                return blocks;

            blocks = new Dictionary<Vector3I, BlockLossAttribution>();
            if (insuredGrid.DamageAttributions == null)
                insuredGrid.DamageAttributions = new List<BlockLossAttribution>();
            for (int i = 0; i < insuredGrid.DamageAttributions.Count; i++)
            {
                BlockLossAttribution attribution = insuredGrid.DamageAttributions[i];
                if (attribution == null) continue;
                blocks[new Vector3I(attribution.X, attribution.Y, attribution.Z)] =
                    attribution;
            }
            _lossAttributionByGrid[insuredGrid.PersistentId] = blocks;
            return blocks;
        }

        private void AddLossAttribution(InsuredGridSnapshot insuredGrid,
            Vector3I position, InsuranceDamageCause cause,
            InsuranceDamageRelationship relationship, double lossRatio)
        {
            Dictionary<Vector3I, BlockLossAttribution> blocks =
                GetLossAttributionMap(insuredGrid);
            BlockLossAttribution attribution;
            if (!blocks.TryGetValue(position, out attribution))
            {
                attribution = new BlockLossAttribution
                {
                    X = position.X,
                    Y = position.Y,
                    Z = position.Z
                };
                blocks[position] = attribution;
                insuredGrid.DamageAttributions.Add(attribution);
            }
            if (attribution.Buckets == null)
                attribution.Buckets = new List<LossAttributionBucket>();

            LossAttributionBucket bucket = null;
            for (int i = 0; i < attribution.Buckets.Count; i++)
            {
                LossAttributionBucket candidate = attribution.Buckets[i];
                if (candidate == null || candidate.Cause != cause ||
                    candidate.Relationship != relationship) continue;
                bucket = candidate;
                break;
            }
            if (bucket == null)
            {
                bucket = new LossAttributionBucket
                {
                    Cause = cause,
                    Relationship = relationship
                };
                attribution.Buckets.Add(bucket);
            }
            bucket.IntegrityLossRatio += Math.Max(0.0, lossRatio);
            _dirty = true;
        }

        private void ClearLossAttribution(InsuredGridSnapshot insuredGrid,
            Vector3I position)
        {
            BlockLossAttribution attribution;
            if (!GetLossAttributionMap(insuredGrid).TryGetValue(position,
                out attribution)) return;
            _lossAttributionByGrid[insuredGrid.PersistentId].Remove(position);
            insuredGrid.DamageAttributions.Remove(attribution);
            _dirty = true;
        }

        private string ResolveIdentityName(long identityId)
        {
            if (identityId == 0) return null;
            string cached;
            if (_identityNames.TryGetValue(identityId, out cached)) return cached;

            List<IMyPlayer> players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players, delegate(IMyPlayer value) { return value.IdentityId == identityId; });
            if (players.Count > 0) cached = players[0].DisplayName;

            if (string.IsNullOrEmpty(cached))
            {
                List<IMyIdentity> identities = new List<IMyIdentity>();
                MyAPIGateway.Players.GetAllIdentites(identities, delegate(IMyIdentity value) { return value.IdentityId == identityId; });
                if (identities.Count > 0) cached = identities[0].DisplayName;
            }

            if (string.IsNullOrEmpty(cached)) cached = "Identity " + identityId;
            _identityNames[identityId] = cached;
            return cached;
        }

        private void OnEntityAdded(IMyEntity entity)
        {
            IMyCubeGrid grid = entity as IMyCubeGrid;
            if (!_active || !_isServer || grid == null) return;
            _pendingGridReconciliation.Add(grid.EntityId);
        }

        private void InitializeGridMarkers()
        {
            for (int policyIndex = 0; policyIndex < _state.Policies.Count; policyIndex++)
            {
                InsurancePolicy policy = _state.Policies[policyIndex];
                for (int gridIndex = 0; gridIndex < policy.Grids.Count; gridIndex++)
                {
                    InsuredGridSnapshot snapshot = policy.Grids[gridIndex];
                    SetGridMarker(FindGrid(snapshot.GridEntityId), snapshot.PersistentId);
                }
            }

            HashSet<IMyEntity> entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities,
                delegate(IMyEntity entity) { return entity is IMyCubeGrid; });
            foreach (IMyEntity entity in entities)
                ReconcileGridMarker(entity as IMyCubeGrid);
        }

        private void ReconcilePendingGridMarkers()
        {
            if (_pendingGridReconciliation.Count == 0) return;
            List<long> pending = new List<long>(_pendingGridReconciliation);
            _pendingGridReconciliation.Clear();
            for (int i = 0; i < pending.Count; i++)
                ReconcileGridMarker(FindGrid(pending[i]));
        }

        private InsurancePolicy ReconcileGridMarker(IMyCubeGrid grid)
        {
            string persistentId;
            if (grid == null || !TryGetGridMarker(grid, out persistentId)) return null;

            InsurancePolicy policy;
            InsuredGridSnapshot snapshot;
            int gridIndex;
            if (!TryFindPersistentGrid(persistentId, out policy, out snapshot, out gridIndex))
            {
                RemoveGridMarker(grid, persistentId);
                return null;
            }

            if (snapshot.GridEntityId == grid.EntityId) return policy;
            if (FindGrid(snapshot.GridEntityId) != null)
            {
                RemoveGridMarker(grid, persistentId);
                return null;
            }

            long oldEntityId = snapshot.GridEntityId;
            UnsubscribeGrid(oldEntityId);
            _repairingGrids.Remove(oldEntityId);
            snapshot.GridEntityId = grid.EntityId;
            if (gridIndex == 0) policy.GridEntityId = grid.EntityId;
            SetGridMarker(grid, persistentId);
            SubscribeGrid(grid);
            _dirty = true;
            Log("Policy #" + policy.PolicyId + " reattached grid " +
                oldEntityId + " -> " + grid.EntityId + ".");
            return policy;
        }

        private bool TryFindPersistentGrid(string persistentId, out InsurancePolicy policy,
            out InsuredGridSnapshot snapshot, out int gridIndex)
        {
            policy = null;
            snapshot = null;
            gridIndex = -1;
            if (string.IsNullOrWhiteSpace(persistentId)) return false;

            for (int policyIndex = 0; policyIndex < _state.Policies.Count; policyIndex++)
            {
                InsurancePolicy candidate = _state.Policies[policyIndex];
                for (int candidateGridIndex = 0;
                    candidateGridIndex < candidate.Grids.Count; candidateGridIndex++)
                {
                    InsuredGridSnapshot candidateGrid = candidate.Grids[candidateGridIndex];
                    if (!string.Equals(candidateGrid.PersistentId, persistentId,
                        StringComparison.Ordinal)) continue;
                    policy = candidate;
                    snapshot = candidateGrid;
                    gridIndex = candidateGridIndex;
                    return true;
                }
            }
            return false;
        }

        private static string NewPersistentGridId()
        {
            return Guid.NewGuid().ToString("N");
        }

        private static bool TryGetGridMarker(IMyCubeGrid grid, out string persistentId)
        {
            persistentId = null;
            return grid != null && grid.Storage != null &&
                   grid.Storage.TryGetValue(GridMarkerStorageKey, out persistentId);
        }

        private static void SetGridMarker(IMyCubeGrid grid, string persistentId)
        {
            if (grid == null || string.IsNullOrWhiteSpace(persistentId)) return;
            if (grid.Storage == null) grid.Storage = new MyModStorageComponent();
            grid.Storage.SetValue(GridMarkerStorageKey, persistentId);
        }

        private static void RemoveGridMarker(IMyCubeGrid grid, string expectedPersistentId)
        {
            string current;
            if (grid == null || grid.Storage == null ||
                !grid.Storage.TryGetValue(GridMarkerStorageKey, out current)) return;
            if (expectedPersistentId != null && !string.Equals(current,
                expectedPersistentId, StringComparison.Ordinal)) return;
            grid.Storage.RemoveValue(GridMarkerStorageKey);
        }

        private void RefreshGridSubscriptions()
        {
            ReconcilePendingGridMarkers();
            List<long> stale = new List<long>();
            foreach (KeyValuePair<long, IMyCubeGrid> pair in _subscribedGrids)
                if (FindGrid(pair.Key) == null) stale.Add(pair.Key);
            for (int i = 0; i < stale.Count; i++) UnsubscribeGrid(stale[i]);

            for (int i = 0; i < _state.Policies.Count; i++)
            {
                InsurancePolicy policy = _state.Policies[i];
                for (int gridIndex = 0; gridIndex < policy.Grids.Count; gridIndex++)
                {
                    InsuredGridSnapshot snapshot = policy.Grids[gridIndex];
                    IMyCubeGrid grid = FindGrid(snapshot.GridEntityId);
                    if (grid == null) continue;
                    SetGridMarker(grid, snapshot.PersistentId);
                    SubscribeGrid(grid);
                }
            }
        }

        private void SubscribeGrid(IMyCubeGrid grid)
        {
            if (grid == null || _subscribedGrids.ContainsKey(grid.EntityId)) return;
            grid.OnBlockAdded += OnBlockAdded;
            grid.OnBlockRemoved += OnBlockRemoved;
            _subscribedGrids[grid.EntityId] = grid;
        }

        private void UnsubscribeGrid(long gridId)
        {
            IMyCubeGrid grid;
            if (!_subscribedGrids.TryGetValue(gridId, out grid)) return;
            UnhookGrid(grid);
            _subscribedGrids.Remove(gridId);
            _lastDamage.Remove(gridId);
        }

        private void UnhookGrid(IMyCubeGrid grid)
        {
            if (grid == null) return;
            grid.OnBlockAdded -= OnBlockAdded;
            grid.OnBlockRemoved -= OnBlockRemoved;
        }

        private void UpdateLastKnownPoses()
        {
            for (int i = 0; i < _state.Policies.Count; i++)
            {
                InsurancePolicy policy = _state.Policies[i];
                for (int gridIndex = 0; gridIndex < policy.Grids.Count; gridIndex++)
                {
                    InsuredGridSnapshot snapshot = policy.Grids[gridIndex];
                    IMyCubeGrid grid = FindGrid(snapshot.GridEntityId);
                    if (grid == null) continue;
                    MatrixD anchorMatrix = MatrixD.Invert(snapshot.RelativePose.GetMatrix()) * grid.WorldMatrix;
                    policy.LastKnownPose = new MyPositionAndOrientation(anchorMatrix);
                    _dirty = true;
                    break;
                }
            }
        }

        private InsurancePolicy ResolveOwnedPolicy(IMyPlayer player, long clientGridId, long policyId)
        {
            InsurancePolicy policy = policyId > 0 ? FindPolicyById(policyId) : FindPolicyByGrid(clientGridId);
            return policy != null && policy.OwnerIdentityId == player.IdentityId ? policy : null;
        }

        private InsurancePolicy FindPolicyById(long policyId)
        {
            for (int i = 0; i < _state.Policies.Count; i++)
                if (_state.Policies[i].PolicyId == policyId) return _state.Policies[i];
            return null;
        }

        private InsurancePolicy FindPolicyByGrid(long gridId)
        {
            if (gridId == 0) return null;
            for (int i = 0; i < _state.Policies.Count; i++)
            {
                InsurancePolicy policy = _state.Policies[i];
                for (int gridIndex = 0; gridIndex < policy.Grids.Count; gridIndex++)
                    if (policy.Grids[gridIndex].GridEntityId == gridId) return policy;
            }
            return ReconcileGridMarker(FindGrid(gridId));
        }

        private static bool HasLivePolicyGrid(InsurancePolicy policy)
        {
            if (policy == null || policy.Grids == null) return false;
            for (int i = 0; i < policy.Grids.Count; i++)
                if (FindGrid(policy.Grids[i].GridEntityId) != null) return true;
            return false;
        }

        private static int CountSnapshotBlocks(InsurancePolicy policy)
        {
            int count = 0;
            if (policy == null || policy.Grids == null) return count;
            for (int i = 0; i < policy.Grids.Count; i++)
            {
                MyObjectBuilder_CubeGrid blueprint = policy.Grids[i].Blueprint;
                if (blueprint != null && blueprint.CubeBlocks != null) count += blueprint.CubeBlocks.Count;
            }
            return count;
        }

        internal static List<IMyCubeGrid> GetMechanicalGroup(IMyCubeGrid grid)
        {
            List<IMyCubeGrid> group = new List<IMyCubeGrid>();
            if (grid != null && !grid.Closed)
                MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Mechanical, group);
            if (group.Count == 0 && grid != null) group.Add(grid);
            group.RemoveAll(delegate(IMyCubeGrid member) { return member == null || member.Closed; });
            Dictionary<long, int> blockCounts = new Dictionary<long, int>();
            List<IMySlimBlock> blocks = new List<IMySlimBlock>();
            for (int i = 0; i < group.Count; i++)
            {
                blocks.Clear();
                group[i].GetBlocks(blocks);
                blockCounts[group[i].EntityId] = blocks.Count;
            }
            group.Sort(delegate(IMyCubeGrid left, IMyCubeGrid right)
            {
                int countComparison = blockCounts[right.EntityId].CompareTo(blockCounts[left.EntityId]);
                return countComparison != 0 ? countComparison : left.EntityId.CompareTo(right.EntityId);
            });
            return group;
        }

        private static IMyCubeGrid FindGrid(long entityId)
        {
            IMyEntity entity;
            if (entityId == 0 || !MyAPIGateway.Entities.TryGetEntityById(entityId, out entity)) return null;
            IMyCubeGrid grid = entity as IMyCubeGrid;
            return grid == null || grid.Closed ? null : grid;
        }

        private EconomyPricingContext BuildEconomyPricingContext(IMyPlayer player,
            IMyFunctionalBlock terminal)
        {
            EconomyPricingContext pricing = new EconomyPricingContext();
            if ((!_config.UseEconomyFactionPricing && !_config.UseDynamicRecoveryPricing) ||
                player == null || terminal == null || terminal.CubeGrid == null)
                return pricing;

            IMyFaction faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(
                terminal.OwnerId);
            if (faction == null)
            {
                List<long> owners = terminal.CubeGrid.BigOwners;
                for (int i = 0; i < owners.Count && faction == null; i++)
                    faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(owners[i]);
            }
            if (faction == null || !faction.IsEveryoneNpc()) return pricing;

            IMyFactionStation station = null;
            foreach (IMyFactionStation candidate in faction.Stations)
            {
                if (candidate.StationEntityId != terminal.CubeGrid.EntityId) continue;
                station = candidate;
                break;
            }
            if (station == null) return pricing;

            pricing.FactionTag = faction.Tag;
            pricing.Reputation = MyAPIGateway.Session.Factions
                .GetReputationBetweenPlayerAndFaction(player.IdentityId, faction.FactionId);
            if (_config.UseEconomyFactionPricing)
            {
                pricing.Discount = InsuranceMath.ReputationDiscount(pricing.Reputation,
                    _config.EconomyFriendlyReputationMin,
                    _config.EconomyFriendlyReputationMax,
                    _config.EconomyMaximumFactionDiscount);
            }

            if (!_config.UseDynamicRecoveryPricing || station.StoreItems == null)
                return pricing;

            for (int i = 0; i < station.StoreItems.Count; i++)
            {
                IMyStoreItem item = station.StoreItems[i];
                if (item == null || !item.IsActive || item.PricePerUnit <= 0 ||
                    item.ItemType != ItemTypes.PhysicalItem ||
                    item.StoreItemType != StoreItemTypes.Offer || !item.Item.HasValue)
                    continue;

                MyDefinitionId itemId = item.Item.Value;
                if (itemId.TypeId != typeof(MyObjectBuilder_Component)) continue;
                long oldPrice;
                if (!pricing.ComponentPrices.TryGetValue(itemId, out oldPrice) ||
                    item.PricePerUnit < oldPrice)
                    pricing.ComponentPrices[itemId] = item.PricePerUnit;
            }
            pricing.DynamicRecoveryPrice = pricing.ComponentPrices.Count > 0;
            return pricing;
        }

        internal static IMyFunctionalBlock FindServiceTerminal(long entityId)
        {
            IMyEntity entity;
            if (entityId == 0 || !MyAPIGateway.Entities.TryGetEntityById(entityId, out entity)) return null;
            IMyFunctionalBlock terminal = entity as IMyFunctionalBlock;
            return terminal == null || terminal.Closed || !IsServicesTerminal(terminal) ? null : terminal;
        }

        internal static bool IsServicesTerminal(IMyTerminalBlock block)
        {
            return block != null && block.BlockDefinition.SubtypeId == "ServicesTerminal";
        }

        private static bool IsPolicyWithinServiceRange(InsurancePolicy policy, IMyFunctionalBlock terminal)
        {
            if (policy == null || terminal == null) return false;
            for (int i = 0; i < policy.Grids.Count; i++)
            {
                IMyCubeGrid grid = FindGrid(policy.Grids[i].GridEntityId);
                if (grid == null) continue;
                double range = ServiceGridDiscoveryRange + grid.WorldAABB.HalfExtents.Length();
                if (Vector3D.DistanceSquared(grid.WorldAABB.Center, terminal.GetPosition()) <= range * range)
                    return true;
            }
            return false;
        }

        private static bool TryGetServiceRecoveryPose(IMyFunctionalBlock terminal, InsurancePolicy policy,
            out MyPositionAndOrientation pose)
        {
            pose = policy.LastKnownPose;
            if (terminal == null) return false;

            float radius = (float)Math.Max(5.0, policy.ClearanceRadius);
            Vector3D start = terminal.GetPosition() + terminal.WorldMatrix.Forward * (radius + 10.0);
            Vector3D? position = MyAPIGateway.Entities.FindFreePlace(start, radius, 100, 5,
                Math.Max(5f, radius));
            if (!position.HasValue) return false;

            pose = new MyPositionAndOrientation(position.Value, terminal.WorldMatrix.Forward, terminal.WorldMatrix.Up);
            return true;
        }

        internal static IMyPlayer FindPlayer(ulong steamId)
        {
            List<IMyPlayer> players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players, delegate(IMyPlayer value) { return value.SteamUserId == steamId; });
            return players.Count == 0 ? null : players[0];
        }

        private void LoadConfig()
        {
            try
            {
                bool created = !MyAPIGateway.Utilities.FileExistsInWorldStorage(
                    ConfigFile, typeof(InsuranceSession));
                bool damagePricingFieldsMissing = false;
                if (!created)
                {
                    using (TextReader reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(ConfigFile, typeof(InsuranceSession)))
                    {
                        string xml = reader.ReadToEnd();
                        damagePricingFieldsMissing =
                            xml.IndexOf("<OwnerDamageClaimValueFraction>",
                                StringComparison.Ordinal) < 0 ||
                            xml.IndexOf("<UnknownDamageClaimMultiplier>",
                                StringComparison.Ordinal) < 0;
                        _config = MyAPIGateway.Utilities.SerializeFromXML<InsuranceConfig>(xml) ??
                            new InsuranceConfig();
                    }
                }
                else
                {
                    _config = new InsuranceConfig();
                }
                ValidateConfig();
                bool pricesChanged = RebuildComponentPrices();
                if (created || pricesChanged || damagePricingFieldsMissing) SaveConfig();
            }
            catch (Exception exception)
            {
                _config = new InsuranceConfig();
                ValidateConfig();
                RebuildComponentPrices();
                Log("Config load failed; defaults active", exception);
            }
        }

        private void ValidateConfig()
        {
            _config.EnrollmentFlatFee = Math.Max(0, _config.EnrollmentFlatFee);
            _config.EnrollmentValueFraction = Math.Max(0.0, _config.EnrollmentValueFraction);
            _config.CancellationRefundFraction = InsuranceMath.Clamp01(
                _config.CancellationRefundFraction);
            _config.ClaimValueFraction = Math.Max(0.0, _config.ClaimValueFraction);
            _config.OwnerDamageClaimValueFraction = Math.Max(0.0,
                _config.OwnerDamageClaimValueFraction);
            _config.FactionDamageClaimValueFraction = Math.Max(0.0,
                _config.FactionDamageClaimValueFraction);
            _config.OtherDamageClaimValueFraction = Math.Max(0.0,
                _config.OtherDamageClaimValueFraction);
            _config.EnvironmentDamageClaimValueFraction = Math.Max(0.0,
                _config.EnvironmentDamageClaimValueFraction);
            _config.UnknownDamageClaimValueFraction = Math.Max(0.0,
                _config.UnknownDamageClaimValueFraction);
            _config.GrindingDamageClaimMultiplier = Math.Max(0.0,
                _config.GrindingDamageClaimMultiplier);
            _config.RammingDamageClaimMultiplier = Math.Max(0.0,
                _config.RammingDamageClaimMultiplier);
            _config.WeaponDamageClaimMultiplier = Math.Max(0.0,
                _config.WeaponDamageClaimMultiplier);
            _config.EnvironmentDamageClaimMultiplier = Math.Max(0.0,
                _config.EnvironmentDamageClaimMultiplier);
            _config.OtherDamageClaimMultiplier = Math.Max(0.0,
                _config.OtherDamageClaimMultiplier);
            _config.UnknownDamageClaimMultiplier = Math.Max(0.0,
                _config.UnknownDamageClaimMultiplier);
            _config.MinimumLossRatio = InsuranceMath.Clamp01(_config.MinimumLossRatio);
            _config.MinimumClaimFee = Math.Max(0, _config.MinimumClaimFee);
            _config.DefaultComponentPrice = Math.Max(0, _config.DefaultComponentPrice);
            _config.MaxPoliciesPerPlayer = Math.Max(1, _config.MaxPoliciesPerPlayer);
            _config.MaxIncidentLogEntries = Math.Max(1, _config.MaxIncidentLogEntries);
            _config.TotalLossExtraClearance = Math.Max(0.0, _config.TotalLossExtraClearance);
            _config.MaximumClaimLinearSpeed = Math.Max(0.0, _config.MaximumClaimLinearSpeed);
            _config.RemoteRecoveryFeePerKilometer = Math.Max(0,
                _config.RemoteRecoveryFeePerKilometer);
            _config.RemoteRecoverySecondsPerKilometer = Math.Max(0.0,
                _config.RemoteRecoverySecondsPerKilometer);
            _config.RemoteRecoveryMinimumSeconds = Math.Max(0,
                _config.RemoteRecoveryMinimumSeconds);
            _config.RemoteRecoveryMaximumSeconds = Math.Max(
                _config.RemoteRecoveryMinimumSeconds, _config.RemoteRecoveryMaximumSeconds);
            _config.RemoteRecoveryExpediteCostPerSecond = Math.Max(0,
                _config.RemoteRecoveryExpediteCostPerSecond);
            _config.RemoteRecoveryExpediteFactor = InsuranceMath.Clamp01(
                _config.RemoteRecoveryExpediteFactor);
            if (_config.EconomyFriendlyReputationMin == int.MaxValue)
                _config.EconomyFriendlyReputationMin = int.MaxValue - 1;
            if (_config.EconomyFriendlyReputationMax <=
                _config.EconomyFriendlyReputationMin)
            {
                _config.EconomyFriendlyReputationMax =
                    _config.EconomyFriendlyReputationMin + 1;
            }
            _config.EconomyMaximumFactionDiscount = InsuranceMath.Clamp01(
                _config.EconomyMaximumFactionDiscount);
            _config.InsuranceCooldownCreditsPerSecond = Math.Max(0,
                _config.InsuranceCooldownCreditsPerSecond);
            _config.InsuranceCooldownMinimumSeconds = Math.Max(0,
                _config.InsuranceCooldownMinimumSeconds);
            _config.InsuranceCooldownMaximumSeconds = Math.Max(
                _config.InsuranceCooldownMinimumSeconds,
                _config.InsuranceCooldownMaximumSeconds);
        }

        private bool RebuildComponentPrices()
        {
            List<InsuranceComponentPrice> source = _config.ComponentPrices;
            Dictionary<string, long> configured = new Dictionary<string, long>(
                StringComparer.OrdinalIgnoreCase);
            if (source != null)
            {
                for (int i = 0; i < source.Count; i++)
                {
                    InsuranceComponentPrice entry = source[i];
                    if (entry == null || string.IsNullOrWhiteSpace(entry.SubtypeId)) continue;
                    configured[entry.SubtypeId.Trim()] = Math.Max(0, entry.Price);
                }
            }

            var definitions = MyDefinitionManager.Static.GetDefinitionsOfType<MyComponentDefinition>();
            for (int i = 0; i < definitions.Count; i++)
            {
                MyComponentDefinition definition = definitions[i];
                string subtype = definition.Id.SubtypeName;
                if (!configured.ContainsKey(subtype))
                    configured[subtype] = definition.MinimalPricePerUnit > 0
                        ? definition.MinimalPricePerUnit
                        : Math.Max(0, _config.DefaultComponentPrice);
            }

            List<InsuranceComponentPrice> normalized = new List<InsuranceComponentPrice>(
                configured.Count);
            foreach (KeyValuePair<string, long> pair in configured)
            {
                normalized.Add(new InsuranceComponentPrice
                {
                    SubtypeId = pair.Key,
                    Price = pair.Value
                });
            }
            normalized.Sort(delegate(InsuranceComponentPrice left,
                InsuranceComponentPrice right)
            {
                return string.Compare(left.SubtypeId, right.SubtypeId,
                    StringComparison.OrdinalIgnoreCase);
            });

            bool changed = source == null || source.Count != normalized.Count;
            if (!changed)
            {
                for (int i = 0; i < normalized.Count; i++)
                {
                    InsuranceComponentPrice oldEntry = source[i];
                    InsuranceComponentPrice newEntry = normalized[i];
                    if (oldEntry == null || oldEntry.Price != newEntry.Price ||
                        !string.Equals(oldEntry.SubtypeId, newEntry.SubtypeId,
                            StringComparison.Ordinal))
                    {
                        changed = true;
                        break;
                    }
                }
            }
            _config.ComponentPrices = normalized;

            _componentPrices.Clear();
            for (int i = 0; i < definitions.Count; i++)
            {
                long price;
                if (configured.TryGetValue(definitions[i].Id.SubtypeName, out price))
                    _componentPrices[definitions[i].Id] = price;
            }
            _blockComponentValues.Clear();
            return changed;
        }

        private void SaveConfig()
        {
            using (TextWriter writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(ConfigFile, typeof(InsuranceSession)))
                writer.Write(MyAPIGateway.Utilities.SerializeToXML(_config));
        }

        private void LoadState()
        {
            try
            {
                if (!MyAPIGateway.Utilities.FileExistsInWorldStorage(StateFile, typeof(InsuranceSession)))
                {
                    _state = new InsuranceState();
                    return;
                }

                using (TextReader reader = MyAPIGateway.Utilities.ReadFileInWorldStorage(StateFile, typeof(InsuranceSession)))
                {
                    byte[] bytes = Convert.FromBase64String(reader.ReadToEnd());
                    _state = MyAPIGateway.Utilities.SerializeFromBinary<InsuranceState>(bytes) ?? new InsuranceState();
                }

                if (_state.Policies == null) _state.Policies = new List<InsurancePolicy>();
                if (_state.Cooldowns == null) _state.Cooldowns = new List<InsuranceCooldown>();
                _state.Cooldowns.RemoveAll(delegate(InsuranceCooldown value)
                {
                    return value == null || value.OwnerIdentityId == 0;
                });
                for (int i = 0; i < _state.Policies.Count; i++) NormalizePolicy(_state.Policies[i]);
                NormalizePersistentGridIds();
                if (_state.NextPolicyId < 1) _state.NextPolicyId = 1;
            }
            catch (Exception exception)
            {
                _state = new InsuranceState();
                Log("State load failed; starting empty", exception);
            }
        }

        private void NormalizePolicy(InsurancePolicy policy)
        {
            if (policy.Grids == null) policy.Grids = new List<InsuredGridSnapshot>();
            if (policy.Grids.Count == 0 && policy.Blueprint != null)
            {
                policy.Grids.Add(new InsuredGridSnapshot
                {
                    GridEntityId = policy.GridEntityId,
                    Blueprint = policy.Blueprint,
                    RelativePose = new MyPositionAndOrientation(MatrixD.Identity)
                });
                _dirty = true;
            }
            if (policy.Incidents == null) policy.Incidents = new List<IncidentRecord>();
            for (int i = 0; i < policy.Grids.Count; i++)
            {
                InsuredGridSnapshot snapshot = policy.Grids[i];
                if (snapshot.DamageAttributions == null)
                {
                    snapshot.DamageAttributions = new List<BlockLossAttribution>();
                    _dirty = true;
                }
                for (int attributionIndex = snapshot.DamageAttributions.Count - 1;
                    attributionIndex >= 0; attributionIndex--)
                {
                    BlockLossAttribution attribution =
                        snapshot.DamageAttributions[attributionIndex];
                    if (attribution == null)
                    {
                        snapshot.DamageAttributions.RemoveAt(attributionIndex);
                        _dirty = true;
                        continue;
                    }
                    if (attribution.Buckets == null)
                    {
                        attribution.Buckets = new List<LossAttributionBucket>();
                        _dirty = true;
                    }
                    int bucketCount = attribution.Buckets.Count;
                    attribution.Buckets.RemoveAll(delegate(LossAttributionBucket value)
                    {
                        return value == null || value.IntegrityLossRatio <= 0.0;
                    });
                    if (attribution.Buckets.Count != bucketCount) _dirty = true;
                }
            }
            if (policy.GridEntityId == 0 && policy.Grids.Count > 0)
                policy.GridEntityId = policy.Grids[0].GridEntityId;
            if (HasRecoveryOrder(policy)) policy.Consumed = true;
        }

        private void NormalizePersistentGridIds()
        {
            HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
            for (int policyIndex = 0; policyIndex < _state.Policies.Count; policyIndex++)
            {
                InsurancePolicy policy = _state.Policies[policyIndex];
                for (int gridIndex = 0; gridIndex < policy.Grids.Count; gridIndex++)
                {
                    InsuredGridSnapshot snapshot = policy.Grids[gridIndex];
                    Guid parsed;
                    string normalized = null;
                    if (!string.IsNullOrWhiteSpace(snapshot.PersistentId) &&
                        Guid.TryParse(snapshot.PersistentId, out parsed))
                        normalized = parsed.ToString("N");

                    if (normalized == null || !used.Add(normalized))
                    {
                        do normalized = NewPersistentGridId();
                        while (!used.Add(normalized));
                    }

                    if (string.Equals(snapshot.PersistentId, normalized,
                        StringComparison.Ordinal)) continue;
                    snapshot.PersistentId = normalized;
                    _dirty = true;
                }
            }
        }

        private void SaveState()
        {
            if (!_isServer || _state == null) return;
            try
            {
                string data = Convert.ToBase64String(MyAPIGateway.Utilities.SerializeToBinary(_state));
                using (TextWriter writer = MyAPIGateway.Utilities.WriteFileInWorldStorage(StateFile, typeof(InsuranceSession)))
                    writer.Write(data);
                _dirty = false;
            }
            catch (Exception exception)
            {
                Log("State save failed", exception);
            }
        }

        private void SendResponse(ulong steamId, string text)
        {
            if (_sendResponse != null) _sendResponse(steamId, text);
        }

        internal static string Money(long value)
        {
            return Math.Max(0, value).ToString("N0", CultureInfo.InvariantCulture);
        }

        private static string Percent(double value)
        {
            return InsuranceMath.Clamp01(value).ToString("P1", CultureInfo.InvariantCulture);
        }

        internal static void Log(string message, Exception exception = null)
        {
            MyLog.Default.WriteLineAndConsole("[ShipInsurance] " + message +
                (exception == null ? string.Empty : ": " + exception));
        }
    }
}
