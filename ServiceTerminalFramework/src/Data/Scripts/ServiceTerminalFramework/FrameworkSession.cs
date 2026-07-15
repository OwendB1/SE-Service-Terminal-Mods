using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Utils;

namespace ServiceTerminalFramework
{
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public sealed class FrameworkSession : MySessionComponentBase
    {
        private FrameworkApi _api;
        private ServicesMenuHud _hud;
        private ServicesUseObjectManager _useObjects;

        public override void BeforeStart()
        {
            if (MyAPIGateway.Utilities.IsDedicated)
            {
                Log("Initialized (client=False)");
                return;
            }

            _api = new FrameworkApi();
            _api.Start();
            _hud = new ServicesMenuHud(_api);
            _hud.Start();
            _useObjects = new ServicesUseObjectManager();
            _useObjects.Start();
            Log("Initialized (client=True, api=True, richHud=True, useObjectProxy=True)");
        }

        public override void UpdateAfterSimulation()
        {
            if (_hud != null) _hud.Update();
            if (_useObjects != null) _useObjects.Update();
        }

        protected override void UnloadData()
        {
            if (_useObjects != null) _useObjects.Stop();
            if (_hud != null) _hud.Stop();
            if (_api != null) _api.Stop();
            _useObjects = null;
            _hud = null;
            _api = null;
            base.UnloadData();
        }

        internal static void Log(string text)
        {
            MyLog.Default.WriteLineAndConsole("[ServiceTerminalFramework] " + text);
        }
    }
}
