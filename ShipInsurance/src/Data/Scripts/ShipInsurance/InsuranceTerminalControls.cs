using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;

namespace ShipInsurance
{
    internal sealed class InsuranceServiceChoice
    {
        internal long Key;
        internal long PolicyId;
        internal long GridId;
        internal string Label;
    }

    internal sealed class InsuranceTerminalControls
    {
        private readonly InsuranceCommands _commands;
        private readonly List<PolicySummary> _servicePolicies = new List<PolicySummary>();
        private readonly Dictionary<long, long> _serviceChoicePolicies = new Dictionary<long, long>();
        private readonly Dictionary<long, long> _serviceChoiceGrids = new Dictionary<long, long>();
        private long _selectedServiceGridId;
        private long _selectedPolicyId;
        private long _selectedServiceChoice;
        private long _servicePolicyTerminalId;
        private long _lastServicePolicyRequestTicks;

        internal event Action ServiceChoicesChanged;
        internal event Action<ClaimLedger> LedgerReceived;

        internal long SelectedServiceChoice => _selectedServiceChoice;

        internal InsuranceTerminalControls(InsuranceCommands commands)
        {
            _commands = commands;
            _commands.PolicyListReceived += ApplyServicePolicyList;
            _commands.LedgerReceived += ApplyLedger;
        }

        internal void Stop()
        {
            _commands.PolicyListReceived -= ApplyServicePolicyList;
            _commands.LedgerReceived -= ApplyLedger;
            _servicePolicies.Clear();
            _serviceChoicePolicies.Clear();
            _serviceChoiceGrids.Clear();
        }

        private long GetSelectedServiceChoice(IMyTerminalBlock block)
        {
            RequestServicePolicyList(block, false);
            RebuildServiceChoices();
            return _selectedServiceChoice;
        }

        private void SetSelectedServiceChoice(IMyTerminalBlock block, long choice)
        {
            _selectedServiceChoice = choice;
            long value;
            _selectedPolicyId = _serviceChoicePolicies.TryGetValue(choice, out value) ? value : 0;
            _selectedServiceGridId = _serviceChoiceGrids.TryGetValue(choice, out value) ? value : 0;
        }

        private List<InsuranceServiceChoice> RebuildServiceChoices()
        {
            List<InsuranceServiceChoice> choices = new List<InsuranceServiceChoice>();
            _serviceChoicePolicies.Clear();
            _serviceChoiceGrids.Clear();
            long nextChoice = 1;
            long selectedChoice = 0;

            for (int i = 0; i < _servicePolicies.Count; i++)
            {
                PolicySummary policy = _servicePolicies[i];
                long choice = nextChoice++;
                if (policy.PolicyId == 0)
                {
                    _serviceChoiceGrids[choice] = policy.SelectionGridId;
                    if (policy.SelectionGridId == _selectedServiceGridId)
                        selectedChoice = choice;
                    choices.Add(new InsuranceServiceChoice
                    {
                        Key = choice,
                        GridId = policy.SelectionGridId,
                        Label = "NEW POLICY  |  INSURE " +
                            InsuranceRuntime.Money(policy.EnrollmentCost) + " SC  |  " +
                            "VALUE " + InsuranceRuntime.Money(policy.ShipValue) + " SC  |  " +
                            policy.GridName + "  |  " +
                            policy.DistanceMeters.ToString("0", CultureInfo.InvariantCulture) + " m" +
                            EconomyPriceLabel(policy)
                    });
                    continue;
                }

                _serviceChoicePolicies[choice] = policy.PolicyId;
                if (policy.PolicyId == _selectedPolicyId) selectedChoice = choice;
                long remaining = InsuranceMath.RemainingSeconds(policy.RecoveryReadyUtcTicks,
                    DateTime.UtcNow.Ticks);
                string state = policy.RecoveryReadyUtcTicks > 0
                    ? remaining > 0 ? "recovery " + InsuranceRuntime.Duration(remaining) : "recovery ready"
                    : policy.TotalLoss ? "total loss" : policy.Remote ? "remote recovery" :
                        policy.LossRatio > 0.0
                            ? "damage " + policy.LossRatio.ToString("P0", CultureInfo.InvariantCulture)
                            : "active";
                string price = (policy.Recovery ? "RESTORE " : "REPAIR ") +
                    InsuranceRuntime.Money(policy.ClaimCost) + " SC";
                if (policy.TransportCost > 0)
                {
                    price += policy.RecoveryReadyUtcTicks > 0
                        ? "  |  TRANSPORT PAID " +
                            InsuranceRuntime.Money(policy.TransportCost) + " SC"
                        : " + TRANSPORT " + InsuranceRuntime.Money(policy.TransportCost) + " SC";
                }
                choices.Add(new InsuranceServiceChoice
                {
                    Key = choice,
                    PolicyId = policy.PolicyId,
                    Label = "POLICY #" + policy.PolicyId + "  |  " + price + "  |  " +
                        "VALUE " + InsuranceRuntime.Money(policy.ShipValue) + " SC  |  " +
                        policy.GridName + "  |  " + state.ToUpperInvariant() +
                        EconomyPriceLabel(policy)
                });
            }

            if (nextChoice == 1)
            {
                choices.Add(new InsuranceServiceChoice
                {
                    Key = 0,
                    Label = "No policies or owned groups available"
                });
                _selectedServiceChoice = 0;
                _selectedPolicyId = 0;
                _selectedServiceGridId = 0;
                return choices;
            }

            if (selectedChoice == 0) selectedChoice = 1;
            SetSelectedServiceChoice(null, selectedChoice);
            return choices;
        }

        private static string EconomyPriceLabel(PolicySummary policy)
        {
            long cooldownSeconds = policy == null ? 0 : InsuranceMath.RemainingSeconds(
                policy.InsuranceCooldownReadyUtcTicks, DateTime.UtcNow.Ticks);
            if (policy == null || (string.IsNullOrWhiteSpace(policy.FactionTag) &&
                !policy.DynamicRecoveryPrice && !policy.RecoveryPriceLocked &&
                cooldownSeconds <= 0))
                return string.Empty;

            StringBuilder text = new StringBuilder("  |  ");
            if (!string.IsNullOrWhiteSpace(policy.FactionTag))
            {
                text.Append(policy.FactionTag).Append(" REP ")
                    .Append(policy.FactionReputation);
                if (policy.FactionDiscountPercent > 0)
                    text.Append(" -").Append(policy.FactionDiscountPercent).Append("%");
            }
            if (policy.DynamicRecoveryPrice)
                text.Append(string.IsNullOrWhiteSpace(policy.FactionTag) ? "MARKET" : " MARKET");
            if (policy.RecoveryPriceLocked)
                text.Append(" LOCKED");
            if (cooldownSeconds > 0)
                text.Append(string.IsNullOrWhiteSpace(policy.FactionTag) &&
                            !policy.DynamicRecoveryPrice && !policy.RecoveryPriceLocked
                    ? "COOLDOWN "
                    : " COOLDOWN ").Append(InsuranceRuntime.Duration(cooldownSeconds));
            return text.ToString();
        }

        internal List<InsuranceServiceChoice> GetServiceChoices(IMyTerminalBlock terminal, bool force)
        {
            if (!CanAccessServiceTerminal(terminal)) return new List<InsuranceServiceChoice>();
            RequestServicePolicyList(terminal, force);
            return RebuildServiceChoices();
        }

        internal void SelectServiceChoice(long choice)
        {
            SetSelectedServiceChoice(null, choice);
        }

        internal bool CanOpenServiceTerminal(IMyTerminalBlock terminal)
        {
            return CanAccessServiceTerminal(terminal);
        }

        internal bool HasSelectedPolicy(IMyTerminalBlock terminal)
        {
            return CanAccessServiceTerminal(terminal) && _selectedPolicyId != 0;
        }

        internal bool HasSelectedGroup(IMyTerminalBlock terminal)
        {
            return CanAccessServiceTerminal(terminal) && _selectedServiceGridId != 0;
        }

        internal bool CanExpedite(IMyTerminalBlock terminal)
        {
            return CanExpediteSelectedPolicy(terminal);
        }

        internal void ExecuteServiceAction(IMyTerminalBlock terminal, string command)
        {
            SendServiceTerminalCommand(terminal, command);
        }

        internal void RequestSelectedLedger(IMyTerminalBlock terminal)
        {
            GetSelectedServiceChoice(terminal);
            if (_selectedPolicyId == 0)
            {
                if (LedgerReceived != null)
                    LedgerReceived(new ClaimLedger { Error = "Select an existing policy first." });
                return;
            }

            try
            {
                _commands.RequestLedger(_selectedPolicyId, terminal.EntityId);
            }
            catch (Exception exception)
            {
                if (LedgerReceived != null)
                    LedgerReceived(new ClaimLedger { Error = "Ledger request failed: " +
                        exception.Message });
                InsuranceCommands.Log("Ledger request failed", exception);
            }
        }

        private bool CanUseSelectedPolicy(IMyTerminalBlock block)
        {
            if (!CanAccessServiceTerminal(block)) return false;
            GetSelectedServiceChoice(block);
            return _selectedPolicyId != 0;
        }

        private bool CanExpediteSelectedPolicy(IMyTerminalBlock block)
        {
            if (!CanUseSelectedPolicy(block)) return false;
            PolicySummary policy = FindSelectedServicePolicy();
            return policy != null && policy.RecoveryReadyUtcTicks > DateTime.UtcNow.Ticks &&
                   policy.RecoveryTerminalEntityId == block.EntityId && !policy.RecoveryExpedited &&
                   policy.ExpediteReductionPercent > 0;
        }

        private PolicySummary FindSelectedServicePolicy()
        {
            for (int i = 0; i < _servicePolicies.Count; i++)
                if (_servicePolicies[i].PolicyId == _selectedPolicyId) return _servicePolicies[i];
            return null;
        }

        private bool CanAccessServiceTerminal(IMyTerminalBlock block)
        {
            IMyPlayer player = MyAPIGateway.Session == null ? null : MyAPIGateway.Session.Player;
            IMyFunctionalBlock functional = block as IMyFunctionalBlock;
            if (player == null || functional == null || !InsuranceRuntime.IsServicesTerminal(block) ||
                !functional.IsFunctional || !functional.Enabled) return false;
            if (block.GetUserRelationToOwner(player.IdentityId) == MyRelationsBetweenPlayerAndBlock.Enemies) return false;
            if (Vector3D.DistanceSquared(player.GetPosition(), block.GetPosition()) >
                InsuranceRuntime.ServiceTerminalUseDistance *
                InsuranceRuntime.ServiceTerminalUseDistance) return false;
            return true;
        }

        private void RequestServicePolicyList(IMyTerminalBlock terminal, bool force)
        {
            if (terminal == null || !CanAccessServiceTerminal(terminal)) return;

            long now = DateTime.UtcNow.Ticks;
            if (!force && _servicePolicyTerminalId == terminal.EntityId &&
                now - _lastServicePolicyRequestTicks < TimeSpan.TicksPerSecond * 2) return;

            _servicePolicyTerminalId = terminal.EntityId;
            _lastServicePolicyRequestTicks = now;

            try
            {
                _commands.RequestPolicyList(terminal.EntityId);
            }
            catch (Exception exception)
            {
                InsuranceCommands.ShowClientText("Insurance target refresh failed: " +
                    exception.Message);
                InsuranceCommands.Log("Insurance target refresh failed", exception);
            }
        }

        private void ApplyServicePolicyList(NetworkPacket packet)
        {
            if (packet == null || packet.ServiceTerminalId != _servicePolicyTerminalId) return;

            _servicePolicies.Clear();
            if (packet.Policies != null) _servicePolicies.AddRange(packet.Policies);
            _servicePolicies.Sort(delegate(PolicySummary left, PolicySummary right)
            {
                int kind = (left.PolicyId == 0).CompareTo(right.PolicyId == 0);
                if (kind != 0) return kind;
                int name = string.Compare(left.GridName, right.GridName,
                    StringComparison.OrdinalIgnoreCase);
                return name != 0 ? name : left.PolicyId.CompareTo(right.PolicyId);
            });

            bool selectedPolicyExists = false;
            for (int i = 0; i < _servicePolicies.Count; i++)
                if (_servicePolicies[i].PolicyId == _selectedPolicyId) selectedPolicyExists = true;
            if (!selectedPolicyExists)
            {
                _selectedPolicyId = 0;
                _selectedServiceChoice = 0;
            }

            if (ServiceChoicesChanged != null) ServiceChoicesChanged();
        }

        private void ApplyLedger(NetworkPacket packet)
        {
            if (packet == null || packet.ServiceTerminalId != _servicePolicyTerminalId) return;
            if (LedgerReceived != null) LedgerReceived(packet.Ledger ??
                new ClaimLedger { Error = "Ledger response was empty." });
        }

        private void SendServiceTerminalCommand(IMyTerminalBlock terminal, string command)
        {
            GetSelectedServiceChoice(terminal);
            bool insuring = command == "insure";
            long policyId = insuring ? 0 : _selectedPolicyId;
            long gridId = insuring ? _selectedServiceGridId : 0;
            if (insuring && gridId == 0)
            {
                InsuranceCommands.ShowClientText("Select a 'New policy' mechanical group first.");
                return;
            }

            if (!insuring && policyId == 0)
            {
                InsuranceCommands.ShowClientText("Select an existing policy first.");
                return;
            }

            try
            {
                _commands.ExecuteTerminalCommand(command, policyId, gridId, terminal.EntityId);
            }
            catch (Exception exception)
            {
                InsuranceCommands.ShowClientText("Service terminal action failed: " + exception.Message);
                InsuranceCommands.Log("Service terminal action failed", exception);
            }
        }
    }
}
