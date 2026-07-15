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
        // Each modeled half is (1 - 0.04) / 2 = 12 / 25 of the original height.
        // This must match ScreenGapFraction in ServiceTerminalModelPatcher.
        private const int SplitReferenceScale = 25;
        private const int SplitHeightScale = 12;

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
            ScreenArea splitScreen = _replacementScreenAreas[0];
            InsuranceRuntime.Log("Services Terminal split-screen model override applied: " +
                _replacementModel + " (screens=" + _replacementScreenAreas.Count +
                ", resolution=" + splitScreen.TextureResolution +
                ", reference=" + splitScreen.ScreenWidth + "x" +
                splitScreen.ScreenHeight + ")");
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
                CloneScreen(original, "CockpitScreen_01", "DisplayName_Screen_TopPanel"),
                CloneScreen(original, "CockpitScreen_02", "DisplayName_Screen_BottomPanel")
            };
            int screenWidth = Math.Max(1, original.ScreenWidth * SplitReferenceScale);
            int screenHeight = Math.Max(1, original.ScreenHeight * SplitHeightScale);
            int originalLongestSide = Math.Max(original.ScreenWidth, original.ScreenHeight) *
                SplitReferenceScale;
            int splitLongestSide = Math.Max(screenWidth, screenHeight);
            int textureResolution = Math.Max(1, (int)Math.Round(
                original.TextureResolution * (double)splitLongestSide /
                originalLongestSide));

            for (int i = 0; i < screens.Count; i++)
            {
                screens[i].TextureResolution = textureResolution;
                screens[i].ScreenWidth = screenWidth;
                screens[i].ScreenHeight = screenHeight;
            }
            return screens;
        }

        private static ScreenArea CloneScreen(ScreenArea source, string name, string displayName)
        {
            return new ScreenArea
            {
                Name = name,
                DisplayName = displayName,
                TextureResolution = source.TextureResolution,
                ScreenWidth = source.ScreenWidth,
                ScreenHeight = source.ScreenHeight,
                Script = source.Script
            };
        }
    }
}
