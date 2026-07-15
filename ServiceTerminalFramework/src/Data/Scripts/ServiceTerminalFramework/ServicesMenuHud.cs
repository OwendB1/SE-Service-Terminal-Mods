using System;
using System.Collections.Generic;
using RichHudFramework.Client;
using RichHudFramework.UI;
using RichHudFramework.UI.Client;
using Sandbox.Game;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace ServiceTerminalFramework
{
    internal sealed class ServicesMenuHud
    {
        private readonly FrameworkApi _api;
        private NativeTerminalScaleNode _scaleRoot;
        private ServicesSidebar _sidebar;
        private bool _registered;
        private bool _stopping;

        internal static ServicesMenuHud Instance { get; private set; }

        internal ServicesMenuHud(FrameworkApi api)
        {
            _api = api;
        }

        internal void Start()
        {
            Instance = this;
            RichHudClient.Init("Service Terminal Framework", OnHudReady, OnHudReset);
            FrameworkSession.Log("Rich HUD registration requested (master=1965654081)");
        }

        internal void Stop()
        {
            _stopping = true;
            if (_sidebar != null)
            {
                _sidebar.Dispose();
                _sidebar.Unregister();
            }
            if (_scaleRoot != null) _scaleRoot.Unregister();
            _sidebar = null;
            _scaleRoot = null;
            _registered = false;
            if (Instance == this) Instance = null;
        }

        internal void Update()
        {
            if (_sidebar != null) _sidebar.Update();
        }

        internal static bool Show(IMyTerminalBlock terminal, IMyEntity user)
        {
            ServicesMenuHud instance = Instance;
            if (instance == null || !instance._registered || instance._sidebar == null)
                return false;

            instance._sidebar.Show(terminal, user);
            return true;
        }

        private void OnHudReady()
        {
            if (_stopping) return;
            HudMain.Init();
            _scaleRoot = new NativeTerminalScaleNode(HudMain.Root);
            _sidebar = new ServicesSidebar(_api, _scaleRoot);
            _registered = true;
            FrameworkSession.Log("Rich HUD services sidebar registered");
        }

        private void OnHudReset()
        {
            if (_sidebar != null) _sidebar.Dispose();
            _registered = false;
            _sidebar = null;
            _scaleRoot = null;
            FrameworkSession.Log("Rich HUD services sidebar reset");
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
    }

    internal sealed class ServicesSidebar : HudElementBase
    {
        private const float ExpandedWidth = 210f;
        private const float FoldedWidth = 52f;
        private const float EdgeGap = 12f;
        private const float HeaderHeight = 36f;
        private const float ButtonHeight = 40f;
        private const float ButtonGap = 6f;
        private readonly FrameworkApi _api;
        private readonly TexturedBox _background;
        private readonly BorderBox _border;
        private readonly List<HudNodeBase> _content = new List<HudNodeBase>();
        private IMyTerminalBlock _terminal;
        private IMyEntity _user;
        private ServiceEntry _activeEntry;
        private int? _previousHudState;
        private bool _automaticallyFolded;
        private bool _drawerOpen;
        private float _designScreenWidth;
        private float _lastScreenWidth;
        private float _lastScreenHeight;

        internal ServicesSidebar(FrameworkApi api, HudParentBase parent) : base(parent)
        {
            _api = api;
            _api.Changed += OnServicesChanged;
            MyAPIGateway.Gui.GuiControlRemoved += OnGuiControlRemoved;
            ParentAlignment = ParentAlignments.Center;
            ZOffset = 20;

            _background = new TexturedBox(this)
            {
                DimAlignment = DimAlignments.Size,
                Color = new Color(25, 34, 40, 245)
            };
            _border = new BorderBox(this)
            {
                DimAlignment = DimAlignments.Size,
                Color = new Color(70, 86, 95),
                Thickness = 1f
            };

            Visible = false;
            InputEnabled = false;
        }

        internal void Show(IMyTerminalBlock terminal, IMyEntity user)
        {
            if (_activeEntry != null && (_terminal != terminal || _user != user))
                DeactivateCurrent();

            _terminal = terminal;
            _user = user;
            _drawerOpen = false;
            _lastScreenWidth = -1f;
            _lastScreenHeight = -1f;
            RefreshLayout(true);
            Visible = true;
            InputEnabled = true;
            HudMain.EnableCursor = true;
        }

        internal void Update()
        {
            if (!Visible) return;

            RefreshLayout(false);
            if (SharedBinds.Escape.IsNewPressed)
            {
                Hide();
                return;
            }
        }

        internal void Dispose()
        {
            _api.Changed -= OnServicesChanged;
            MyAPIGateway.Gui.GuiControlRemoved -= OnGuiControlRemoved;
            Hide();
            ClearContent();
        }

        private void Hide()
        {
            DeactivateCurrent();
            Visible = false;
            InputEnabled = false;
            _terminal = null;
            _user = null;
            _drawerOpen = false;
            HudMain.EnableCursor = false;
        }

        private void RefreshLayout(bool force)
        {
            float screenWidth = HudMain.ScreenWidth;
            float screenHeight = HudMain.ScreenHeight;
            if (!force && Math.Abs(screenWidth - _lastScreenWidth) < .5f &&
                Math.Abs(screenHeight - _lastScreenHeight) < .5f)
                return;

            _lastScreenWidth = screenWidth;
            _lastScreenHeight = screenHeight;
            float safeWidth = Math.Min(screenWidth, screenHeight * (4f / 3f));
            float scaleX = (safeWidth + 1f) / 1441f;
            float designScreenWidth = screenWidth / scaleX;
            _designScreenWidth = designScreenWidth;
            float sideMargin = Math.Max(0f, (designScreenWidth - 1463.62f) * .5f);
            bool automaticallyFolded = sideMargin < ExpandedWidth + EdgeGap;
            if (automaticallyFolded != _automaticallyFolded)
            {
                _automaticallyFolded = automaticallyFolded;
                _drawerOpen = false;
            }

            Rebuild();
        }

        private void Rebuild()
        {
            ClearContent();
            List<ServiceEntry> entries = _api.GetEntries();
            bool folded = _automaticallyFolded && !_drawerOpen;
            float width = folded ? FoldedWidth : ExpandedWidth;
            int optionCount = entries.Count + 1;
            float height = folded
                ? FoldedWidth
                : HeaderHeight + 16f + optionCount * ButtonHeight +
                    Math.Max(0, optionCount - 1) * ButtonGap;
            Size = new Vector2(width, height);
            float x = _automaticallyFolded
                ? _designScreenWidth * .5f - EdgeGap - width * .5f
                : 1463.62f * .5f + EdgeGap + width * .5f;
            Offset = new Vector2(x, 0f);

            if (folded)
            {
                AddButton("<", ToggleDrawer, 0f, FoldedWidth, FoldedWidth);
                return;
            }

            Label title = new Label(this)
            {
                Text = "SERVICES",
                AutoResize = false,
                Size = new Vector2(width - 20f, HeaderHeight),
                ParentAlignment = ParentAlignments.InnerTopLeft,
                Offset = new Vector2(10f, -8f),
                Format = TerminalFormatting.HeaderFormat.WithAlignment(TextAlignment.Left)
            };
            _content.Add(title);

            if (_automaticallyFolded)
            {
                BorderedButton fold = AddButton(">", ToggleDrawer,
                    width * .5f - 25f, 38f, 30f);
                fold.ParentAlignment = ParentAlignments.InnerTopRight;
                fold.Offset = new Vector2(-6f, -7f);
            }

            float firstY = -HeaderHeight - 8f;
            BorderedButton vanilla = AddButton("Vanilla services", ActivateVanilla,
                firstY, width - 16f, ButtonHeight);
            SetSelected(vanilla, _activeEntry == null);
            for (int i = 0; i < entries.Count; i++)
            {
                ServiceEntry entry = entries[i];
                float y = firstY - (i + 1) * (ButtonHeight + ButtonGap);
                BorderedButton service = AddButton(entry.Name,
                    delegate { Activate(entry); }, y, width - 16f, ButtonHeight);
                SetSelected(service, _activeEntry != null && _activeEntry.Id == entry.Id);
            }
        }

        private BorderedButton AddButton(string text, Action action, float y,
            float width, float height)
        {
            BorderedButton button = new BorderedButton(this)
            {
                Text = text,
                Size = new Vector2(width, height),
                AutoResize = false,
                ParentAlignment = ParentAlignments.InnerTop,
                Offset = new Vector2(0f, y)
            };
            button.MouseInput.LeftClicked += delegate { action(); };
            _content.Add(button);
            return button;
        }

        private static void SetSelected(BorderedButton button, bool selected)
        {
            if (!selected) return;
            button.Color = new Color(48, 76, 88);
            button.BorderColor = TerminalFormatting.Mint;
        }

        private void ToggleDrawer()
        {
            _drawerOpen = !_drawerOpen;
            Rebuild();
        }

        private void ActivateVanilla()
        {
            DeactivateCurrent();
            if (_automaticallyFolded) _drawerOpen = false;
            Rebuild();
            HudMain.EnableCursor = true;
        }

        private void Activate(ServiceEntry entry)
        {
            IMyTerminalBlock terminal = _terminal;
            IMyEntity user = _user;
            if (terminal == null || user == null) return;

            if (_activeEntry != null) DeactivateCurrent();
            _activeEntry = entry;
            try
            {
                entry.SetActive(terminal, user, true,
                    delegate { OnProviderClosed(entry); });
                if (_activeEntry == entry) HideGameplayHud();
            }
            catch (Exception exception)
            {
                FrameworkSession.Log("Service " + entry.Id + " failed: " + exception);
                _activeEntry = null;
                MyAPIGateway.Utilities.ShowNotification(
                    "The selected terminal service failed to open.", 4000);
            }

            if (_automaticallyFolded) _drawerOpen = false;
            Rebuild();
            HudMain.EnableCursor = true;
        }

        private void DeactivateCurrent()
        {
            ServiceEntry entry = _activeEntry;
            IMyTerminalBlock terminal = _terminal;
            IMyEntity user = _user;
            _activeEntry = null;
            if (entry == null || terminal == null || user == null) return;

            try
            {
                entry.SetActive(terminal, user, false, null);
            }
            catch (Exception exception)
            {
                FrameworkSession.Log("Service " + entry.Id + " failed to close: " + exception);
            }
            RestoreGameplayHud();
        }

        private void OnProviderClosed(ServiceEntry entry)
        {
            if (_activeEntry != entry) return;
            _activeEntry = null;
            RestoreGameplayHud();
            if (_automaticallyFolded) _drawerOpen = false;
            Rebuild();
            HudMain.EnableCursor = true;
        }

        private void HideGameplayHud()
        {
            if (_previousHudState.HasValue) return;
            _previousHudState = MyAPIGateway.Session.Config.HudState;
            MyVisualScriptLogicProvider.SetHudState(0, 0L);
        }

        private void RestoreGameplayHud()
        {
            if (!_previousHudState.HasValue) return;
            int state = _previousHudState.Value;
            _previousHudState = null;
            MyVisualScriptLogicProvider.SetHudState(state, 0L);
        }

        private void OnServicesChanged()
        {
            if (!Visible) return;

            if (_activeEntry != null)
            {
                bool stillRegistered = false;
                List<ServiceEntry> entries = _api.GetEntries();
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i].Id == _activeEntry.Id)
                    {
                        stillRegistered = true;
                        break;
                    }
                }
                if (!stillRegistered) DeactivateCurrent();
            }

            Rebuild();
        }

        private void OnGuiControlRemoved(object control)
        {
            if (Visible && control != null && string.Equals(control.GetType().Name,
                "MyGuiScreenServicesTerminal", StringComparison.Ordinal))
                Hide();
        }

        private void ClearContent()
        {
            for (int i = 0; i < _content.Count; i++) _content[i].Unregister();
            _content.Clear();
        }
    }
}
