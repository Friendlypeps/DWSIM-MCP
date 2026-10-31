// Copyright 2026 friendlypeps and contributors. Mirrors the internal SpecialOpLinks of DWSIM 10's
// engine/DWSIM.FluentAPI/Builders/SpecialOpLinks.cs (copyright 2008-2026 Daniel Wagner Oliveira de
// Medeiros and contributors), which this assembly cannot call.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.Linq;
using DWSIM.Automation.FluentAPI;
using DWSIM.Interfaces;
using DWSIM.Interfaces.Enums;
using DWSIM.UnitOperations.SpecialOps;
using DWSIM.UnitOperations.SpecialOps.Helpers;
using FluentFlowsheet = DWSIM.Automation.FluentAPI.Flowsheet;
using Shapes = DWSIM.Drawing.SkiaSharp.GraphicObjects.Shapes;

namespace DWSIM.MCPServer.Tools.Common
{
    /// <summary>Wiring of Adjust and Spec blocks to the variables they read and write, as the editors do it.</summary>
    public static class Links
    {
        /// <summary>Describes property <paramref name="propertyId"/> of the object tagged <paramref name="tag"/>, checking the id exists.</summary>
        public static SpecialOpObjectInfo Describe(FluentFlowsheet fs, string tag, string propertyId, bool mustBeWritable, out ISimulationObject obj)
        {
            obj = Find.ByTagOrId(fs, tag, "object");
            if (string.IsNullOrWhiteSpace(propertyId)) throw new ArgumentException($"Name the property of '{tag}' (a PROP_* id from dwsim_object_list_properties).");
            var su = fs.Inner.FlowsheetOptions.SelectedUnitSystem;

            PropertyCatalog.EnsureDynamicProperties(obj);
            var isDynamic = obj.IsDynamicProperty(propertyId);
            if (!isDynamic)
            {
                var all = obj.GetProperties(PropertyType.ALL) ?? Array.Empty<string>();
                var hit = all.FirstOrDefault(p => string.Equals(p, propertyId, StringComparison.OrdinalIgnoreCase));
                if (hit == null)
                    throw new ArgumentException($"'{propertyId}' is not a property of '{tag}'. Its properties: {string.Join(", ", all.Take(40))}{(all.Length > 40 ? ", ..." : "")}. See dwsim_object_list_properties.");
                propertyId = hit;
                if (mustBeWritable)
                {
                    var wr = obj.GetProperties(PropertyType.WR) ?? Array.Empty<string>();
                    var rw = obj.GetProperties(PropertyType.RW) ?? Array.Empty<string>();
                    if (!wr.Contains(propertyId) && !rw.Contains(propertyId))
                        throw new ArgumentException($"'{propertyId}' of '{tag}' is a calculated result, not a specification; a manipulated variable must be writable. Writable: {string.Join(", ", wr.Concat(rw).Distinct())}.");
                }
            }

            var units = isDynamic
                ? su.GetCurrentUnits(obj.GetDynamicPropertyUnitType(propertyId))
                : obj.GetPropertyUnit(propertyId, su);

            return new SpecialOpObjectInfo
            {
                ID = obj.Name,
                Name = obj.GraphicObject.Tag,
                PropertyName = propertyId,
                ObjectType = obj.GetDisplayName(),
                Units = units ?? "",
                UnitsType = isDynamic ? obj.GetDynamicPropertyUnitType(propertyId) : UnitOfMeasure.none
            };
        }

        public static void AttachAdjust(Adjust block, ISimulationObject target, AdjustVarType role, SpecialOpObjectInfo info)
        {
            var asBase = target as DWSIM.SharedClasses.UnitOperations.BaseClass;
            switch (role)
            {
                case AdjustVarType.Manipulated: block.ManipulatedObjectData = info; block.ManipulatedObject = asBase; break;
                case AdjustVarType.Controlled: block.ControlledObjectData = info; block.ControlledObject = asBase; break;
                case AdjustVarType.Reference: block.ReferencedObjectData = info; block.ReferenceObject = asBase; block.Referenced = true; break;
            }
            target.IsAdjustAttached = true;
            target.AttachedAdjustId = block.Name;
            target.AdjustVarType = role;
            if (block.GraphicObject is Shapes.AdjustGraphic g)
            {
                var shape = target.GraphicObject as DWSIM.Drawing.SkiaSharp.GraphicObjects.GraphicObject;
                switch (role)
                {
                    case AdjustVarType.Manipulated: g.ConnectedToMv = shape; break;
                    case AdjustVarType.Controlled: g.ConnectedToCv = shape; break;
                    case AdjustVarType.Reference: g.ConnectedToRv = shape; break;
                }
            }
        }

        public static void AttachSpec(Spec block, ISimulationObject target, SpecVarType role, SpecialOpObjectInfo info)
        {
            var asBase = target as DWSIM.SharedClasses.UnitOperations.BaseClass;
            if (role == SpecVarType.Source) { block.SourceObjectData = info; block.SourceObject = asBase; }
            else { block.TargetObjectData = info; block.TargetObject = asBase; }
            target.IsSpecAttached = true;
            target.AttachedSpecId = block.Name;
            target.SpecVarType = role;
            if (block.GraphicObject is Shapes.SpecGraphic g)
            {
                var shape = target.GraphicObject as DWSIM.Drawing.SkiaSharp.GraphicObjects.GraphicObject;
                if (role == SpecVarType.Source) g.ConnectedToSv = shape; else g.ConnectedToTv = shape;
            }
        }

        /// <summary>The current value of a linked variable in SI, or NaN.</summary>
        public static double ValueOf(FluentFlowsheet fs, ISpecialOpObjectInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.ID) || !fs.Inner.SimulationObjects.TryGetValue(info.ID, out var obj)) return double.NaN;
            try
            {
                var v = obj.GetPropertyValue(info.PropertyName);
                return v == null ? double.NaN : Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return double.NaN; }
        }
    }
}
