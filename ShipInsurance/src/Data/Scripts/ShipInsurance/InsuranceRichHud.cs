using System;
using System.Collections.Generic;
using System.Globalization;
using RichHudFramework.Client;
using RichHudFramework.UI;
using RichHudFramework.UI.Client;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace ShipInsurance
{
    internal sealed class InsuranceRichHud
    {
        private const long RichHudMasterId = 1965654081;

        private readonly InsuranceTerminalControls _terminalControls;
        private NativeTerminalScaleNode _scaleRoot;
        private InsuranceRichHudWindow _window;
        private bool _registered;
        private bool _stopping;

        internal static InsuranceRichHud Instance { get; private set; }

        internal InsuranceRichHud(InsuranceTerminalControls terminalControls)
        {
            _terminalControls = terminalControls;
        }

        internal void Start()
        {
            Instance = this;
            _terminalControls.ServiceChoicesChanged += OnServiceChoicesChanged;
            RichHudClient.Init("Ship Insurance", OnHudReady, OnHudReset);
            InsuranceRuntime.Log("Rich HUD registration requested (master=" + RichHudMasterId + ")");
        }

        internal void Stop()
        {
            _stopping = true;
            _terminalControls.ServiceChoicesChanged -= OnServiceChoicesChanged;
            if (_window != null)
            {
                _window.Shutdown();
            }
            if (_scaleRoot != null) _scaleRoot.Unregister();

            _window = null;
            _scaleRoot = null;
            _registered = false;
            if (Instance == this) Instance = null;
        }

        internal static void Open(IMyTerminalBlock terminal, Action closed = null)
        {
            InsuranceRichHud instance = Instance;
            if (instance == null || !instance._registered || instance._window == null)
            {
                MyAPIGateway.Utilities.ShowNotification(
                    "Ship Insurance requires Rich HUD Master (Workshop 1965654081).", 5000);
                if (closed != null) closed();
                return;
            }

            instance._window.Open(terminal, closed);
        }

        internal static void Close()
        {
            InsuranceRichHud instance = Instance;
            if (instance != null && instance._window != null) instance._window.Close();
        }

        private void OnHudReady()
        {
            if (_stopping) return;
            HudMain.Init();
            _scaleRoot = new NativeTerminalScaleNode(HudMain.Root);
            _window = new InsuranceRichHudWindow(_terminalControls, _scaleRoot);
            _registered = true;
            InsuranceRuntime.Log("Rich HUD unified insurance window registered");
        }

        private void OnHudReset()
        {
            if (_window != null) _window.Shutdown();
            _registered = false;
            _window = null;
            _scaleRoot = null;
            InsuranceRuntime.Log("Rich HUD insurance window reset");
        }

        private sealed class NativeTerminalScaleNode : HudSpaceNodeBase
        {
            internal NativeTerminalScaleNode(HudParentBase parent) : base(parent) { }

            protected override void Layout()
            {
                IReadOnlyHudSpaceNode parentSpace = Parent.HudSpace;
                float safeWidth = Math.Min(HudMain.ScreenWidth,
                    HudMain.ScreenHeight * (4f / 3f));
                float scaleX = (safeWidth + 1f) / 1441f;
                float scaleY = HudMain.ScreenHeight / 1080f;

                PlaneToWorldRef[0] = parentSpace.PlaneToWorldRef[0];
                PlaneToWorldRef[0].Right *= scaleX;
                PlaneToWorldRef[0].Up *= scaleY;
                IsInFront = parentSpace.IsInFront;
                IsFacingCamera = parentSpace.IsFacingCamera;
                CursorPos = new Vector3(parentSpace.CursorPos.X / scaleX,
                    parentSpace.CursorPos.Y / scaleY, parentSpace.CursorPos.Z);
            }
        }

        private void OnServiceChoicesChanged()
        {
            if (_registered && _window != null) _window.Refresh(false);
        }
    }

    internal sealed class InsuranceRichHudWindow : WindowBase
    {
        private readonly InsuranceTerminalControls _terminalControls;
        private readonly List<InsuranceServiceChoice> _choices = new List<InsuranceServiceChoice>();
        private readonly List<BorderedButton> _policyButtons = new List<BorderedButton>();
        private readonly Label _balanceLabel;
        private readonly ListBox<long> _targetList;
        private readonly BorderedButton _insureButton;
        private readonly BorderedButton _expediteButton;
        private readonly InsuranceLedgerWindow _ledgerWindow;
        private IMyTerminalBlock _terminal;
        private Action _closed;
        private bool _refreshing;

        internal InsuranceRichHudWindow(InsuranceTerminalControls terminalControls, HudParentBase parent)
            : base(parent)
        {
            _terminalControls = terminalControls;
            _terminalControls.LedgerReceived += OnLedgerReceived;
            HeaderText = "SHIP INSURANCE";
            // MyGuiScreenTerminal uses 1.0157 x 0.9172 of SE's 4:3 safe GUI area.
            Size = new Vector2(1463.62f, 990.576f);
            MinimumSize = Size;
            AllowResizing = false;
            CanDrag = true;
            BorderColor = new Color(70, 86, 95);
            BodyColor = new Color(25, 34, 40, 245);
            Visible = false;
            InputEnabled = false;

            _balanceLabel = new Label(body)
            {
                AutoResize = false,
                Size = new Vector2(1200f, 36f),
                ParentAlignment = ParentAlignments.InnerTopLeft,
                Offset = new Vector2(18f, -17f),
                Format = TerminalFormatting.HeaderFormat.WithAlignment(TextAlignment.Left),
                Text = "ACCOUNT BALANCE   --"
            };

            BorderedButton refreshButton = CreateButton(body, "Refresh", delegate { Refresh(true); });
            refreshButton.Size = new Vector2(180f, 34f);
            refreshButton.ParentAlignment = ParentAlignments.InnerTopRight;
            refreshButton.Offset = new Vector2(-18f, -13f);

            _targetList = new ListBox<long>(body)
            {
                Size = new Vector2(1427.62f, 750f),
                ParentAlignment = ParentAlignments.InnerTop,
                Offset = new Vector2(0f, -62f),
                Color = new Color(32, 43, 50),
                Format = TerminalFormatting.ControlFormat,
                LineHeight = 34f,
                MemberPadding = new Vector2(18f, 6f),
                ListPadding = new Vector2(4f),
                UpdateValueCallback = OnTargetChanged
            };

            BorderedButton status = AddPolicyButton("Status / quote", "status");
            _insureButton = CreateButton(body, "Insure selected grid", delegate { Execute("insure"); });
            BorderedButton claim = AddPolicyButton("Claim or recover", "claim");
            PositionButton(status, -456f, 58f, 440f);
            PositionButton(_insureButton, 0f, 58f, 440f);
            PositionButton(claim, 456f, 58f, 440f);

            _expediteButton = AddPolicyButton("Expedite recovery", "expedite");
            BorderedButton ledger = CreateButton(body, "Repair cost ledger", OpenLedger);
            _policyButtons.Add(ledger);
            BorderedButton history = AddPolicyButton("Damage history", "history");
            BorderedButton cancel = AddPolicyButton("Cancel policy", "cancel");
            PositionButton(_expediteButton, -522f, 12f, 330f);
            PositionButton(ledger, -174f, 12f, 330f);
            PositionButton(history, 174f, 12f, 330f);
            PositionButton(cancel, 522f, 12f, 330f);

            _ledgerWindow = new InsuranceLedgerWindow(parent, CloseLedger);

            UpdateButtonState();
        }

        internal void Open(IMyTerminalBlock terminal, Action closed)
        {
            if (!_terminalControls.CanOpenServiceTerminal(terminal))
            {
                MyAPIGateway.Utilities.ShowNotification(
                    "Services Terminal must be functional, friendly, and within reach.", 4000);
                if (closed != null) closed();
                return;
            }

            _terminal = terminal;
            _closed = closed;
            Visible = true;
            InputEnabled = true;
            GetWindowFocus();
            HudMain.EnableCursor = true;
            Refresh(true);
        }

        internal void Close()
        {
            Action closed = _closed;
            _closed = null;
            _ledgerWindow.Hide();
            Visible = false;
            InputEnabled = false;
            _terminal = null;
            HudMain.EnableCursor = false;
            if (closed != null) closed();
        }

        internal void Shutdown()
        {
            _terminalControls.LedgerReceived -= OnLedgerReceived;
            Close();
            _ledgerWindow.Unregister();
            Unregister();
        }

        internal void Refresh(bool force)
        {
            if (!Visible || _terminal == null) return;
            _refreshing = true;
            try
            {
                UpdateBalance();
                _choices.Clear();
                _choices.AddRange(_terminalControls.GetServiceChoices(_terminal, force));
                _targetList.ClearEntries();

                for (int i = 0; i < _choices.Count; i++)
                {
                    Color color = _choices[i].PolicyId != 0
                        ? new Color(190, 215, 235)
                        : _choices[i].GridId != 0
                            ? new Color(135, 220, 165)
                            : new Color(155, 160, 165);
                    _targetList.Add(new RichText(_choices[i].Label,
                        TerminalFormatting.ControlFormat.WithColor(color)), _choices[i].Key);
                }

                _targetList.SetSelection(_terminalControls.SelectedServiceChoice);
                UpdateButtonState();
            }
            finally
            {
                _refreshing = false;
            }
        }

        private BorderedButton AddPolicyButton(string text, string command)
        {
            BorderedButton button = CreateButton(body, text, delegate { Execute(command); });
            _policyButtons.Add(button);
            return button;
        }

        private static BorderedButton CreateButton(HudParentBase parent, string text, Action action)
        {
            BorderedButton button = new BorderedButton(parent)
            {
                Text = text,
                Size = new Vector2(185f, 38f),
                AutoResize = false
            };
            button.MouseInput.LeftClicked += delegate { action(); };
            return button;
        }

        private static void PositionButton(BorderedButton button, float x, float y, float width)
        {
            button.Size = new Vector2(width, 38f);
            button.ParentAlignment = ParentAlignments.InnerBottom;
            button.Offset = new Vector2(x, y);
        }

        private void OnTargetChanged(object sender, EventArgs args)
        {
            if (_refreshing || _targetList.Value == null) return;
            _terminalControls.SelectServiceChoice(_targetList.Value.AssocMember);
            UpdateButtonState();
        }

        private void UpdateBalance()
        {
            IMyPlayer player = MyAPIGateway.Session == null ? null : MyAPIGateway.Session.Player;
            long balance;
            _balanceLabel.Text = player != null && player.TryGetBalanceInfo(out balance)
                ? "ACCOUNT BALANCE   " + InsuranceRuntime.Money(balance) + " SC"
                : "ACCOUNT BALANCE   unavailable";
        }

        private void UpdateButtonState()
        {
            bool policy = _terminal != null && _terminalControls.HasSelectedPolicy(_terminal);
            bool group = _terminal != null && _terminalControls.HasSelectedGroup(_terminal);
            for (int i = 0; i < _policyButtons.Count; i++)
                SetButtonEnabled(_policyButtons[i], policy);
            SetButtonEnabled(_insureButton, group);
            SetButtonEnabled(_expediteButton,
                _terminal != null && _terminalControls.CanExpedite(_terminal));
        }

        private static void SetButtonEnabled(BorderedButton button, bool enabled)
        {
            button.InputEnabled = enabled;
            button.Color = enabled ? TerminalFormatting.OuterSpace : new Color(37, 43, 47);
            button.Format = TerminalFormatting.ControlFormat.WithColor(
                enabled ? new Color(220, 235, 242) : new Color(105, 112, 116));
        }

        private void Execute(string command)
        {
            if (_terminal == null) return;
            _terminalControls.ExecuteServiceAction(_terminal, command);
        }

        private void OpenLedger()
        {
            if (_terminal == null || !_terminalControls.HasSelectedPolicy(_terminal)) return;
            InputEnabled = false;
            _ledgerWindow.ShowLoading();
            _terminalControls.RequestSelectedLedger(_terminal);
        }

        private void OnLedgerReceived(ClaimLedger ledger)
        {
            if (_ledgerWindow.Visible) _ledgerWindow.ShowLedger(ledger);
        }

        private void CloseLedger()
        {
            _ledgerWindow.Hide();
            if (!Visible) return;
            InputEnabled = true;
            GetWindowFocus();
        }
    }

    internal sealed class InsuranceLedgerWindow : WindowBase
    {
        private readonly ListBox<long> _rows;
        private readonly Action _closed;

        internal InsuranceLedgerWindow(HudParentBase parent, Action closed) : base(parent)
        {
            _closed = closed;
            HeaderText = "REPAIR COST LEDGER";
            Size = new Vector2(1180f, 760f);
            MinimumSize = Size;
            AllowResizing = false;
            CanDrag = true;
            BorderColor = new Color(82, 106, 119);
            BodyColor = new Color(19, 27, 32, 252);
            ZOffset = 20;
            Visible = false;
            InputEnabled = false;

            _rows = new ListBox<long>(body)
            {
                Size = new Vector2(1144f, 635f),
                ParentAlignment = ParentAlignments.InnerTop,
                Offset = new Vector2(0f, -18f),
                Color = new Color(28, 39, 46),
                Format = TerminalFormatting.ControlFormat,
                LineHeight = 34f,
                MemberPadding = new Vector2(16f, 6f),
                ListPadding = new Vector2(4f)
            };

            BorderedButton close = new BorderedButton(body)
            {
                Text = "Close ledger",
                Size = new Vector2(300f, 38f),
                AutoResize = false,
                ParentAlignment = ParentAlignments.InnerBottom,
                Offset = new Vector2(0f, 12f)
            };
            close.MouseInput.LeftClicked += delegate
            {
                if (_closed != null) _closed();
            };
        }

        internal void ShowLoading()
        {
            HeaderText = "REPAIR COST LEDGER";
            _rows.ClearEntries();
            AddRow("Loading server quote...");
            Visible = true;
            InputEnabled = true;
            GetWindowFocus();
        }

        internal void ShowLedger(ClaimLedger ledger)
        {
            _rows.ClearEntries();
            if (ledger == null || !string.IsNullOrWhiteSpace(ledger.Error))
            {
                AddRow(ledger == null ? "Ledger response was empty." : ledger.Error);
                return;
            }

            HeaderText = "REPAIR COST LEDGER — POLICY #" + ledger.PolicyId;
            AddRow(ledger.GridName + "  |  " +
                (ledger.Recovery ? "FULL RECOVERY" : "LOCAL REPAIR") +
                (ledger.DynamicRecoveryPrice ? "  |  MARKET PRICED" : string.Empty) +
                (ledger.RecoveryPriceLocked ? "  |  QUOTE LOCKED" : string.Empty));
            if (!string.IsNullOrWhiteSpace(ledger.FactionTag))
                AddRow("Economy faction " + ledger.FactionTag + "  |  reputation " +
                    ledger.FactionReputation.ToString(CultureInfo.InvariantCulture));
            AddRow("DAMAGE SOURCE / PARTY     REPAIR VALUE     RATE     CHARGE");

            if (ledger.Entries == null || ledger.Entries.Count == 0)
                AddRow("No billable repair damage.");
            else
                for (int i = 0; i < ledger.Entries.Count; i++)
                {
                    ClaimLedgerEntry entry = ledger.Entries[i];
                    string source = entry.Cause + " / " + entry.Relationship;
                    AddRow(source.PadRight(28) +
                        InsuranceRuntime.Money(entry.RepairValue).PadLeft(13) + " SC  " +
                        entry.Rate.ToString("P1", CultureInfo.InvariantCulture).PadLeft(7) + "  " +
                        InsuranceRuntime.Money(entry.Cost).PadLeft(13) + " SC");
                }

            AddRow("Attributed subtotal".PadRight(48) +
                InsuranceRuntime.Money(ledger.AttributedSubtotal).PadLeft(13) + " SC");
            if (ledger.Adjustments != null)
                for (int i = 0; i < ledger.Adjustments.Count; i++)
                {
                    ClaimLedgerAdjustment adjustment = ledger.Adjustments[i];
                    string amount = adjustment.Amount < 0
                        ? "-" + InsuranceRuntime.Money(-adjustment.Amount)
                        : "+" + InsuranceRuntime.Money(adjustment.Amount);
                    AddRow(adjustment.Label.PadRight(48) + amount.PadLeft(13) + " SC");
                }
            AddRow("FINAL REPAIR COST".PadRight(48) +
                InsuranceRuntime.Money(ledger.FinalCost).PadLeft(13) + " SC");
            AddRow("Repair component value".PadRight(48) +
                InsuranceRuntime.Money(ledger.RepairValue).PadLeft(13) + " SC");
            if (ledger.UnrepairableValue > 0)
                AddRow("Conflicting blocks (not repaired/charged)".PadRight(48) +
                    InsuranceRuntime.Money(ledger.UnrepairableValue).PadLeft(13) + " SC");
        }

        internal void Hide()
        {
            Visible = false;
            InputEnabled = false;
        }

        private void AddRow(string text)
        {
            _rows.Add(new RichText(text ?? string.Empty,
                TerminalFormatting.ControlFormat), _rows.Count + 1L);
        }
    }
}
