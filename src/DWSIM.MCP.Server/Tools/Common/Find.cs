// Copyright 2026 friendlypeps and contributors.
//
// This file is part of DWSIM-MCP, free software under the GNU General Public License version 3 or
// (at your option) any later version. See LICENSE.

using System;
using System.Collections.Generic;
using System.Linq;
using DWSIM.Interfaces;
using FluentFlowsheet = DWSIM.Automation.FluentAPI.Flowsheet;

namespace DWSIM.MCPServer.Tools.Common
{
    /// <summary>
    /// Object lookup shared by the extension tools. Mirrors the private FindByTag in the upstream
    /// UnitOpTools.cs (tag match) and the id fallback dwsim_unitop_set uses, and lists what exists
    /// when nothing matches, so the model's next call can be right.
    /// </summary>
    public static class Find
    {
        /// <summary>A simulation object by its tag (the user-visible name) or its internal id.</summary>
        public static ISimulationObject ByTagOrId(FluentFlowsheet fs, string tagOrId, string what)
        {
            if (string.IsNullOrWhiteSpace(tagOrId))
                throw new ArgumentException($"Name the {what}.");
            foreach (var o in fs.Inner.SimulationObjects.Values)
                if (o.GraphicObject != null && string.Equals(o.GraphicObject.Tag, tagOrId, StringComparison.Ordinal)) return o;
            foreach (var o in fs.Inner.SimulationObjects.Values)
                if (o.GraphicObject != null && string.Equals(o.GraphicObject.Tag, tagOrId, StringComparison.OrdinalIgnoreCase)) return o;
            if (fs.Inner.SimulationObjects.TryGetValue(tagOrId, out var byId)) return byId;
            throw new ArgumentException($"No {what} with tag or id '{tagOrId}'. Objects: {Tags(fs)}.");
        }

        /// <summary>An object that must be of type <typeparamref name="T"/>; the error names the actual type.</summary>
        public static T As<T>(FluentFlowsheet fs, string tagOrId, string what) where T : class
        {
            var obj = ByTagOrId(fs, tagOrId, what);
            if (obj is T typed) return typed;
            throw new ArgumentException($"'{tagOrId}' is a {obj.GraphicObject?.ObjectType.ToString() ?? obj.GetType().Name}, not a {what}.");
        }

        /// <summary>Every tag in the flowsheet, with its type, for error messages.</summary>
        public static string Tags(FluentFlowsheet fs)
        {
            var tags = fs.Inner.SimulationObjects.Values
                .Where(o => o.GraphicObject != null)
                .Select(o => $"{o.GraphicObject.Tag} ({o.GraphicObject.ObjectType})")
                .ToList();
            return tags.Count == 0 ? "none yet" : string.Join(", ", tags);
        }

        /// <summary>
        /// A property package by tag or UniqueID; with no name, the only package, or the error lists them.
        /// </summary>
        public static IPropertyPackage Package(FluentFlowsheet fs, string tagOrId = null)
        {
            var packages = fs.Inner.PropertyPackages;
            if (packages.Count == 0)
                throw new ArgumentException("The flowsheet has no property package. Call dwsim_thermo_set_property_package first.");
            if (string.IsNullOrWhiteSpace(tagOrId))
            {
                if (packages.Count == 1) return packages.Values.First();
                throw new ArgumentException($"The flowsheet has {packages.Count} property packages; name one: {PackageList(fs)}.");
            }
            if (packages.TryGetValue(tagOrId, out var byId)) return byId;
            var byTag = packages.Values.FirstOrDefault(p => string.Equals(p.Tag, tagOrId, StringComparison.OrdinalIgnoreCase));
            if (byTag != null) return byTag;
            throw new ArgumentException($"No property package '{tagOrId}'. Packages: {PackageList(fs)}.");
        }

        public static string PackageList(FluentFlowsheet fs) =>
            string.Join(", ", fs.Inner.PropertyPackages.Values.Select(p => $"{p.Tag} [{p.UniqueID}]"));

        /// <summary>A compound selected in the flowsheet, by name (case-insensitive).</summary>
        public static ICompoundConstantProperties Compound(FluentFlowsheet fs, string name)
        {
            if (fs.Inner.SelectedCompounds.TryGetValue(name ?? "", out var cp)) return cp;
            var ci = fs.Inner.SelectedCompounds.Values.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (ci != null) return ci;
            throw new ArgumentException($"'{name}' is not in the flowsheet. Compounds: {string.Join(", ", fs.Inner.SelectedCompounds.Keys)}. Add it with dwsim_thermo_add_compounds (before streams exist) or dwsim_compound_add.");
        }

        /// <summary>The compound names of the flowsheet, in their stored order.</summary>
        public static List<string> CompoundNames(FluentFlowsheet fs) => fs.Inner.SelectedCompounds.Keys.ToList();
    }
}
