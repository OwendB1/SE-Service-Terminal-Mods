using System;
using System.Collections.Generic;
using System.IO;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Definitions;
using VRage.Game;
using VRage.Utils;

namespace ShipInsurance
{
    internal sealed class InsuranceTerminalModel
    {
        private MyFunctionalBlockDefinition _definition;
        private List<ScreenArea> _originalScreenAreas;
        private List<ScreenArea> _replacementScreenAreas;
        private string _originalModel;
        private string _replacementModel;

        internal void Apply(string modPath)
        {
            MyDefinitionId id = new MyDefinitionId(typeof(MyObjectBuilder_FunctionalBlock),
                MyStringHash.GetOrCompute("ServicesTerminal"));
            MyCubeBlockDefinition definition;
            if (!MyDefinitionManager.Static.TryGetCubeBlockDefinition(id, out definition))
            {
                InsuranceRuntime.Log("Services Terminal definition unavailable; model override skipped");
                return;
            }

            MyFunctionalBlockDefinition functionalDefinition =
                definition as MyFunctionalBlockDefinition;
            if (functionalDefinition == null)
            {
                InsuranceRuntime.Log("Services Terminal functional definition unavailable; " +
                    "model override skipped");
                return;
            }

            _definition = functionalDefinition;
            _originalModel = definition.Model;
            _replacementModel = Path.Combine(modPath,
                @"Models\Cubes\Large\ServicesTerminalInsurance.mwm");
            definition.Model = _replacementModel;
            _originalScreenAreas = functionalDefinition.ScreenAreas;
            _replacementScreenAreas = BuildSplitScreenAreas(_originalScreenAreas);
            functionalDefinition.ScreenAreas = _replacementScreenAreas;
            InsuranceRuntime.Log("Services Terminal split-screen model override applied: " +
                _replacementModel + " (screens=" + _replacementScreenAreas.Count + ")");
        }

        internal void Restore()
        {
            if (_definition != null && string.Equals(_definition.Model, _replacementModel,
                StringComparison.Ordinal))
                _definition.Model = _originalModel;
            if (_definition != null && ReferenceEquals(_definition.ScreenAreas,
                _replacementScreenAreas))
                _definition.ScreenAreas = _originalScreenAreas;

            _definition = null;
            _originalScreenAreas = null;
            _replacementScreenAreas = null;
            _originalModel = null;
            _replacementModel = null;
        }

        private static List<ScreenArea> BuildSplitScreenAreas(List<ScreenArea> source)
        {
            if (source == null || source.Count == 0)
                throw new InvalidOperationException("Services Terminal has no screen area to split.");

            ScreenArea original = source[0];
            List<ScreenArea> screens = new List<ScreenArea>(2)
            {
                CloneScreen(original, "CockpitScreen_01"),
                CloneScreen(original, "CockpitScreen_02")
            };
            screens[0].ScreenHeight = Math.Max(1, original.ScreenHeight / 2);
            screens[1].ScreenHeight = screens[0].ScreenHeight;
            return screens;
        }

        private static ScreenArea CloneScreen(ScreenArea source, string name)
        {
            return new ScreenArea
            {
                Name = name,
                DisplayName = source.DisplayName,
                TextureResolution = source.TextureResolution,
                ScreenWidth = source.ScreenWidth,
                ScreenHeight = source.ScreenHeight,
                Script = source.Script
            };
        }
    }
}
