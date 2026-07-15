using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Entity.UseObject;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace ServiceTerminalFramework
{
    internal sealed class ServicesUseObject : IMyUseObject
    {
        private readonly IMyUseObject _original;

        internal ServicesUseObject(IMyUseObject original)
        {
            _original = original;
        }

        public IMyEntity Owner { get { return _original.Owner; } }
        public IMyModelDummy Dummy { get { return _original.Dummy; } }
        public float InteractiveDistance { get { return _original.InteractiveDistance; } }
        public MatrixD ActivationMatrix { get { return _original.ActivationMatrix; } }
        public MatrixD WorldMatrix { get { return _original.WorldMatrix; } }
        public uint RenderObjectID { get { return _original.RenderObjectID; } }
        public int InstanceID { get { return _original.InstanceID; } }
        public bool ShowOverlay { get { return _original.ShowOverlay; } }
        public UseActionEnum SupportedActions { get { return _original.SupportedActions; } }
        public UseActionEnum PrimaryAction { get { return _original.PrimaryAction; } }
        public UseActionEnum SecondaryAction { get { return _original.SecondaryAction; } }
        public bool ContinuousUsage { get { return _original.ContinuousUsage; } }
        public bool PlayIndicatorSound { get { return _original.PlayIndicatorSound; } }
        public bool ShouldUpdateTooltips { get { return _original.ShouldUpdateTooltips; } }

        public void Use(UseActionEnum actionEnum, IMyEntity user)
        {
            if (actionEnum == PrimaryAction)
            {
                FrameworkSession.Log("Services detector used (terminal=" + Owner.EntityId + ")");
                _original.Use(actionEnum, user);
                ServicesMenuHud.Show(Owner as IMyTerminalBlock, user);
                return;
            }

            _original.Use(actionEnum, user);
        }

        public MyActionDescription GetActionInfo(UseActionEnum actionEnum)
        {
            return _original.GetActionInfo(actionEnum);
        }

        public bool HandleInput() { return _original.HandleInput(); }
        public void OnSelectionLost() { _original.OnSelectionLost(); }
        public void SetRenderID(uint id) { _original.SetRenderID(id); }
        public void SetInstanceID(int id) { _original.SetInstanceID(id); }
    }
}
