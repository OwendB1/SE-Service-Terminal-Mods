using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
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
        private readonly List<IMyTerminalControl> _serviceTerminalControls = new List<IMyTerminalControl>();
        private readonly List<PolicySummary> _servicePolicies = new List<PolicySummary>();
        private readonly Dictionary<long, long> _serviceChoicePolicies = new Dictionary<long, long>();
        private readonly Dictionary<long, long> _serviceChoiceGrids = new Dictionary<long, long>();
        private long _selectedServiceGridId;
        private long _selectedPolicyId;
        private long _selectedServiceChoice;
        private long _servicePolicyTerminalId;
        private long _lastServicePolicyRequestTicks;
        private IMyTerminalControlCombobox _serviceGridSelector;
        private IMyTerminalControlButton _serviceExpediteButton;
        private bool _serviceTerminalControlsRegistered;

        internal event Action ServiceChoicesChanged;

        internal long SelectedServiceChoice => _selectedServiceChoice;

        internal InsuranceTerminalControls(InsuranceCommands commands)
        {
            _commands = commands;
            _commands.PolicyListReceived += ApplyServicePolicyList;
        }

        internal void Register()
        {
            if (_serviceTerminalControlsRegistered || MyAPIGateway.TerminalControls == null) return;

            IMyTerminalControlSeparator separator =
                MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyFunctionalBlock>(
                    "ShipInsurance_Separator");
            AddServiceTerminalControl(separator);

            IMyTerminalControlLabel label =
                MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyFunctionalBlock>(
                    "ShipInsurance_Label");
            label.Label = MyStringId.GetOrCompute("Ship Insurance");
            AddServiceTerminalControl(label);

            _serviceGridSelector =
                MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyFunctionalBlock>(
                    "ShipInsurance_Grid");
            _serviceGridSelector.Title = MyStringId.GetOrCompute("Insurance target");
            _serviceGridSelector.Tooltip = MyStringId.GetOrCompute(
                "Select an existing policy or a nearby uninsured mechanical group.");
            _serviceGridSelector.ComboBoxContent = FillServiceChoices;
            _serviceGridSelector.Getter = GetSelectedServiceChoice;
            _serviceGridSelector.Setter = SetSelectedServiceChoice;
            _serviceGridSelector.Enabled = CanAccessServiceTerminal;
            AddServiceTerminalControl(_serviceGridSelector);

            IMyTerminalControlButton refresh = CreateServiceButton("Refresh", "Refresh insurance targets",
                "Refresh policies and rescan nearby uninsured groups.", RefreshServiceChoices);
            refresh.Enabled = CanAccessServiceTerminal;
            AddServiceTerminalControl(refresh);
            IMyTerminalControlButton status = CreateServiceButton("Status", "Policy status / quote",
                "Show loss, claim price, and any remote recovery countdown or expedite quote.", delegate(IMyTerminalBlock block)
                {
                    SendServiceTerminalCommand(block, "status");
                });
            status.Enabled = CanUseSelectedPolicy;
            AddServiceTerminalControl(status);
            IMyTerminalControlButton insure = CreateServiceButton("Insure", "Insure snapshot",
                "Pay enrollment and snapshot the selected mechanical group as its truth state.", delegate(IMyTerminalBlock block)
                {
                    SendServiceTerminalCommand(block, "insure");
                });
            insure.Enabled = CanUseSelectedGroup;
            AddServiceTerminalControl(insure);
            IMyTerminalControlButton claim = CreateServiceButton("Claim", "Claim / recover",
                "Repair locally, order remote recovery, or deploy an arrived recovery.", delegate(IMyTerminalBlock block)
                {
                    SendServiceTerminalCommand(block, "claim");
                });
            claim.Enabled = CanUseSelectedPolicy;
            AddServiceTerminalControl(claim);
            _serviceExpediteButton = CreateServiceButton("Expedite", "Expedite remote recovery",
                "Select an en-route recovery to see its current expedite price.", delegate(IMyTerminalBlock block)
                {
                    SendServiceTerminalCommand(block, "expedite");
                });
            _serviceExpediteButton.Enabled = CanExpediteSelectedPolicy;
            AddServiceTerminalControl(_serviceExpediteButton);
            IMyTerminalControlButton history = CreateServiceButton("History", "Damage history",
                "Show recent damage/removal attribution for selected group.", delegate(IMyTerminalBlock block)
                {
                    SendServiceTerminalCommand(block, "history");
                });
            history.Enabled = CanUseSelectedPolicy;
            AddServiceTerminalControl(history);
            IMyTerminalControlButton cancel = CreateServiceButton("Cancel", "Cancel selected policy",
                "Cancel selected group's policy without an enrollment refund.", delegate(IMyTerminalBlock block)
                {
                    SendServiceTerminalCommand(block, "cancel");
                });
            cancel.Enabled = CanUseSelectedPolicy;
            AddServiceTerminalControl(cancel);

            _serviceTerminalControlsRegistered = true;
        }

        private void AddServiceTerminalControl(IMyTerminalControl control)
        {
            control.SupportsMultipleBlocks = false;
            control.Visible = InsuranceRuntime.IsServicesTerminal;
            MyAPIGateway.TerminalControls.AddControl<IMyFunctionalBlock>(control);
            _serviceTerminalControls.Add(control);
        }

        private IMyTerminalControlButton CreateServiceButton(string id, string title, string tooltip,
            Action<IMyTerminalBlock> action)
        {
            IMyTerminalControlButton button =
                MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyFunctionalBlock>(
                    "ShipInsurance_" + id);
            button.Title = MyStringId.GetOrCompute(title);
            button.Tooltip = MyStringId.GetOrCompute(tooltip);
            button.Action = action;
            button.Enabled = CanAccessServiceTerminal;
            return button;
        }

        internal void Stop()
        {
            _commands.PolicyListReceived -= ApplyServicePolicyList;
            if (!_serviceTerminalControlsRegistered || MyAPIGateway.TerminalControls == null) return;

            for (int i = 0; i < _serviceTerminalControls.Count; i++)
                MyAPIGateway.TerminalControls.RemoveControl<IMyFunctionalBlock>(_serviceTerminalControls[i]);

            _serviceTerminalControls.Clear();
            _servicePolicies.Clear();
            _serviceChoicePolicies.Clear();
            _serviceChoiceGrids.Clear();
            _serviceGridSelector = null;
            _serviceExpediteButton = null;
            _serviceTerminalControlsRegistered = false;
        }

        private void FillServiceChoices(List<MyTerminalControlComboBoxItem> items)
        {
            List<InsuranceServiceChoice> choices = RebuildServiceChoices();
            for (int i = 0; i < choices.Count; i++)
            {
                items.Add(new MyTerminalControlComboBoxItem
                {
                    Key = choices[i].Key,
                    Value = MyStringId.GetOrCompute(choices[i].Label)
                });
            }
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
            UpdateExpeditePresentation();
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

        private bool CanUseSelectedGroup(IMyTerminalBlock block)
        {
            if (!CanAccessServiceTerminal(block)) return false;
            GetSelectedServiceChoice(block);
            return _selectedServiceGridId != 0;
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

        private void UpdateExpeditePresentation()
        {
            if (_serviceExpediteButton == null) return;
            PolicySummary policy = FindSelectedServicePolicy();
            if (policy != null && policy.RecoveryReadyUtcTicks > DateTime.UtcNow.Ticks &&
                policy.RecoveryTerminalEntityId == _servicePolicyTerminalId &&
                !policy.RecoveryExpedited && policy.ExpediteReductionPercent > 0)
            {
                _serviceExpediteButton.Tooltip = MyStringId.GetOrCompute("Pay " +
                    InsuranceRuntime.Money(policy.ExpeditePrice) +
                    " SC to reduce current remaining time by " +
                    policy.ExpediteReductionPercent + "% (one use per recovery).");
            }
            else
            {
                _serviceExpediteButton.Tooltip = MyStringId.GetOrCompute(
                    "Available once while a remote recovery is en route from this terminal.");
            }
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

        private void RefreshServiceChoices(IMyTerminalBlock block)
        {
            _lastServicePolicyRequestTicks = 0;
            RequestServicePolicyList(block, true);
            if (_serviceGridSelector != null)
            {
                _serviceGridSelector.RedrawControl();
                _serviceGridSelector.UpdateVisual();
            }
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

            if (_serviceGridSelector != null)
            {
                _serviceGridSelector.RedrawControl();
                _serviceGridSelector.UpdateVisual();
            }
            UpdateExpeditePresentation();
            if (ServiceChoicesChanged != null) ServiceChoicesChanged();
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
