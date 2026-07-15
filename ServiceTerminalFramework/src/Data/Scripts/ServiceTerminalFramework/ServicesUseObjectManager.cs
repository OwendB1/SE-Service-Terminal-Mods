using System;
using System.Collections.Generic;
using Sandbox.Game.Components;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;

namespace ServiceTerminalFramework
{
    internal sealed class ServicesUseObjectManager
    {
        private const int RetryFrames = 300;

        private readonly Dictionary<long, List<Hook>> _hooks =
            new Dictionary<long, List<Hook>>();
        private readonly Dictionary<long, Pending> _pending =
            new Dictionary<long, Pending>();
        private readonly Dictionary<long, IMyCubeGrid> _grids =
            new Dictionary<long, IMyCubeGrid>();

        internal void Start()
        {
            MyAPIGateway.Entities.OnEntityAdd += OnEntityAdd;
            MyAPIGateway.Entities.OnEntityRemove += OnEntityRemove;

            HashSet<IMyEntity> entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities);
            foreach (IMyEntity entity in entities)
            {
                IMyCubeGrid grid = entity as IMyCubeGrid;
                if (grid != null) Track(grid);
            }
        }

        internal void Stop()
        {
            MyAPIGateway.Entities.OnEntityAdd -= OnEntityAdd;
            MyAPIGateway.Entities.OnEntityRemove -= OnEntityRemove;

            foreach (IMyCubeGrid grid in _grids.Values)
            {
                grid.OnBlockAdded -= OnBlockAdded;
                grid.OnBlockRemoved -= OnBlockRemoved;
            }
            _grids.Clear();

            List<long> entityIds = new List<long>(_hooks.Keys);
            for (int i = 0; i < entityIds.Count; i++) Restore(entityIds[i]);
            _pending.Clear();
        }

        internal void Update()
        {
            if (_pending.Count == 0) return;

            List<long> entityIds = new List<long>(_pending.Keys);
            for (int i = 0; i < entityIds.Count; i++)
            {
                long entityId = entityIds[i];
                Pending pending = _pending[entityId];
                IMyEntity entity = pending.Entity;
                if (entity == null || entity.Closed)
                {
                    _pending.Remove(entityId);
                    continue;
                }

                if (Install(entity))
                {
                    _pending.Remove(entityId);
                    continue;
                }

                pending.Frames--;
                if (pending.Frames <= 0)
                {
                    _pending.Remove(entityId);
                    FrameworkSession.Log("Services detector unavailable (terminal=" +
                        entityId + ")");
                }
            }
        }

        private void OnEntityAdd(IMyEntity entity)
        {
            IMyCubeGrid grid = entity as IMyCubeGrid;
            if (grid != null) Track(grid);
        }

        private void OnEntityRemove(IMyEntity entity)
        {
            IMyCubeGrid grid = entity as IMyCubeGrid;
            if (grid == null) return;
            grid.OnBlockAdded -= OnBlockAdded;
            grid.OnBlockRemoved -= OnBlockRemoved;
            _grids.Remove(grid.EntityId);
        }

        private void Track(IMyCubeGrid grid)
        {
            if (_grids.ContainsKey(grid.EntityId)) return;
            _grids.Add(grid.EntityId, grid);
            grid.OnBlockAdded += OnBlockAdded;
            grid.OnBlockRemoved += OnBlockRemoved;

            List<IMySlimBlock> blocks = new List<IMySlimBlock>();
            grid.GetBlocks(blocks, IsServicesTerminal);
            for (int i = 0; i < blocks.Count; i++) Queue(blocks[i].FatBlock);
        }

        private static bool IsServicesTerminal(IMySlimBlock block)
        {
            IMyTerminalBlock terminal = block == null ? null : block.FatBlock as IMyTerminalBlock;
            return terminal != null && terminal.BlockDefinition.SubtypeId == "ServicesTerminal";
        }

        private void OnBlockAdded(IMySlimBlock block)
        {
            if (block != null) Queue(block.FatBlock);
        }

        private void OnBlockRemoved(IMySlimBlock block)
        {
            if (block == null || block.FatBlock == null) return;
            _pending.Remove(block.FatBlock.EntityId);
            Restore(block.FatBlock.EntityId);
        }

        private void Queue(IMyEntity entity)
        {
            IMyTerminalBlock terminal = entity as IMyTerminalBlock;
            if (terminal == null || terminal.BlockDefinition.SubtypeId != "ServicesTerminal" ||
                _hooks.ContainsKey(entity.EntityId))
                return;

            if (!Install(entity))
                _pending[entity.EntityId] = new Pending(entity, RetryFrames);
        }

        private bool Install(IMyEntity entity)
        {
            if (_hooks.ContainsKey(entity.EntityId)) return true;

            MyUseObjectsComponent component =
                entity.Components.Get<MyUseObjectsComponentBase>() as MyUseObjectsComponent;
            if (component == null) return false;

            List<KeyValuePair<uint, MyUseObjectsComponent.DetectorData>> detectors =
                new List<KeyValuePair<uint, MyUseObjectsComponent.DetectorData>>();
            lock (component.DetectorInteractiveObjects)
            {
                foreach (KeyValuePair<uint, MyUseObjectsComponent.DetectorData> detector in
                    component.DetectorInteractiveObjects)
                {
                    if (string.Equals(detector.Value.DetectorName, "services",
                        StringComparison.OrdinalIgnoreCase) && detector.Value.UseObject != null)
                        detectors.Add(detector);
                }

                if (detectors.Count == 0) return false;

                List<Hook> hooks = new List<Hook>(detectors.Count);
                for (int i = 0; i < detectors.Count; i++)
                {
                    KeyValuePair<uint, MyUseObjectsComponent.DetectorData> detector = detectors[i];
                    ServicesUseObject proxy = new ServicesUseObject(detector.Value.UseObject);
                    component.DetectorInteractiveObjects[detector.Key] =
                        new MyUseObjectsComponent.DetectorData(proxy, detector.Value.Matrix,
                            detector.Value.DetectorName);
                    hooks.Add(new Hook(component, detector.Key, detector.Value, proxy));
                }

                _hooks.Add(entity.EntityId, hooks);
                FrameworkSession.Log("Services detector proxy installed (terminal=" +
                    entity.EntityId + ", detectors=" + hooks.Count + ")");
                return true;
            }
        }

        private void Restore(long entityId)
        {
            List<Hook> hooks;
            if (!_hooks.TryGetValue(entityId, out hooks)) return;

            for (int i = 0; i < hooks.Count; i++)
            {
                Hook hook = hooks[i];
                lock (hook.Component.DetectorInteractiveObjects)
                {
                    MyUseObjectsComponent.DetectorData current;
                    if (hook.Component.DetectorInteractiveObjects.TryGetValue(
                        hook.DetectorId, out current) &&
                        ReferenceEquals(current.UseObject, hook.Proxy))
                        hook.Component.DetectorInteractiveObjects[hook.DetectorId] =
                            hook.Original;
                }
            }

            _hooks.Remove(entityId);
        }

        private sealed class Hook
        {
            internal readonly MyUseObjectsComponent Component;
            internal readonly uint DetectorId;
            internal readonly MyUseObjectsComponent.DetectorData Original;
            internal readonly ServicesUseObject Proxy;

            internal Hook(MyUseObjectsComponent component, uint detectorId,
                MyUseObjectsComponent.DetectorData original, ServicesUseObject proxy)
            {
                Component = component;
                DetectorId = detectorId;
                Original = original;
                Proxy = proxy;
            }
        }

        private sealed class Pending
        {
            internal readonly IMyEntity Entity;
            internal int Frames;

            internal Pending(IMyEntity entity, int frames)
            {
                Entity = entity;
                Frames = frames;
            }
        }
    }
}
