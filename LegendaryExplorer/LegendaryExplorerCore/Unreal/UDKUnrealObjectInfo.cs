﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LegendaryExplorerCore.DebugTools;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Gammtek.IO;
using LegendaryExplorerCore.Memory;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Unreal.ObjectInfo;
using Newtonsoft.Json;

namespace LegendaryExplorerCore.Unreal
{
    public static class UDKUnrealObjectInfo
    {
        public static bool IsLoaded;
        internal static Dictionary<string, ClassInfo> Classes = new();
        internal static Dictionary<string, ClassInfo> Structs = new();
        internal static Dictionary<string, List<NameReference>> Enums = new();
        internal static Dictionary<string, SequenceObjectInfo> SequenceObjects = new();

        private static readonly string[] ImmutableStructs = { "Vector", "Color", "LinearColor", "TwoVectors", "Vector4", "Vector2D", "Rotator", "Guid", "Plane", "Box",
            "Quat", "Matrix", "IntPoint", "ActorReference", "ActorReference", "ActorReference", "PolyReference", "AimComponent", "AimTransform", "AimOffsetProfile", "FontCharacter",
            "CoverReference", "CoverInfo", "CoverSlot", "RwVector2", "RwVector3", "RwVector4" };

        public static bool IsImmutableStruct(string structName)
        {
            return ImmutableStructs.Contains(structName);
        }

        public static void loadfromJSON(string jsonTextOverride = null)
        {
            if (!IsLoaded)
            {
                LECLog.Information(@"Loading property db for UDK");
                try
                {
                    var infoText = jsonTextOverride ?? ObjectInfoLoader.LoadEmbeddedJSONText(MEGame.UDK);
                    if (infoText != null)
                    {
                        var blob = JsonConvert.DeserializeAnonymousType(infoText, new { SequenceObjects, Classes, Structs, Enums });
                        SequenceObjects = blob.SequenceObjects;
                        Classes = blob.Classes;
                        Structs = blob.Structs;
                        Enums = blob.Enums;
                        AddCustomAndNativeClasses(Classes, SequenceObjects);
                        foreach ((string className, ClassInfo classInfo) in Classes)
                        {
                            classInfo.ClassName = className;
                        }
                        foreach ((string className, ClassInfo classInfo) in Structs)
                        {
                            classInfo.ClassName = className;
                        }
                        IsLoaded = true;
                    }
                }
                catch (Exception ex)
                {
                    LECLog.Error($@"Property database load failed for UDK: {ex.Message}");
                }
            }
        }

        public static PropertyInfo getPropertyInfo(string className, NameReference propName, bool inStruct = false, ClassInfo nonVanillaClassInfo = null, bool reSearch = true, ExportEntry containingExport = null)
        {
            if (className.StartsWith("Default__", StringComparison.OrdinalIgnoreCase))
            {
                className = className[9..];
            }
            Dictionary<string, ClassInfo> temp = inStruct ? Structs : Classes;
            bool infoExists = temp.TryGetValue(className, out ClassInfo info);
            if (!infoExists && nonVanillaClassInfo != null)
            {
                info = nonVanillaClassInfo;
                infoExists = true;
            }

            // 07/18/2022 - If during property lookup we are passed a class 
            // that we don't know about, generate and use it, since it will also have superclass info
            // For example looking at a custom subclass in Interpreter, this code will resolve the ???'s
            // - Mgamerz
            if (!infoExists && !inStruct && containingExport is { IsDefaultObject: true, Class: ExportEntry classExp })
            {
                info = generateClassInfo(classExp, false);
                Classes[className] = info;
                infoExists = true;
            }

            if (infoExists) //|| (temp = !inStruct ? Structs : Classes).ContainsKey(className))
            {
                //look in class properties
                if (info.properties.TryGetValue(propName, out var propInfo))
                {
                    return propInfo;
                }
                else if (nonVanillaClassInfo != null && nonVanillaClassInfo.properties.TryGetValue(propName, out var nvPropInfo))
                {
                    return nvPropInfo;
                }
                //look in structs

                if (inStruct)
                {
                    foreach (PropertyInfo p in info.properties.Values())
                    {
                        if ((p.Type is PropertyType.StructProperty or PropertyType.ArrayProperty) && reSearch)
                        {
                            reSearch = false;
                            PropertyInfo val = getPropertyInfo(p.Reference, propName, true, nonVanillaClassInfo, reSearch);
                            if (val != null)
                            {
                                return val;
                            }
                        }
                    }
                }
                //look in base class
                if (temp.ContainsKey(info.baseClass))
                {
                    PropertyInfo val = getPropertyInfo(info.baseClass, propName, inStruct, nonVanillaClassInfo);
                    if (val != null)
                    {
                        return val;
                    }
                }
                else
                {
                    //Baseclass may be modified as well...
                    if (containingExport?.SuperClass is ExportEntry parentExport)
                    {
                        //Class parent is in this file. Generate class parent info and attempt refetch
                        return getPropertyInfo(parentExport.SuperClassName, propName, inStruct, generateClassInfo(parentExport), reSearch: true, parentExport);
                    }
                }
            }

            //if (reSearch)
            //{
            //    PropertyInfo reAttempt = getPropertyInfo(className, propName, !inStruct, nonVanillaClassInfo, reSearch: false);
            //    return reAttempt; //will be null if not found.
            //}
            return null;
        }

        /// <summary>
        /// Ensures the object info is loaded
        /// </summary>
        internal static void EnsureLoaded()
        {
            if (!IsLoaded)
            {
                loadfromJSON();
            }
        }

        internal static readonly ConcurrentDictionary<string, PropertyCollection> defaultStructValuesUDK = new();

        #region Generating

        public static void generateInfo(string outpath, bool usePooledMemory = true, Action<int, int> progressDelegate = null)
        {
            MemoryManager.SetUsePooledMemory(usePooledMemory);
            Enums.Clear();
            Structs.Clear();
            Classes.Clear();
            SequenceObjects.Clear();

            var allFiles = Directory.EnumerateFiles(UDKDirectory.ScriptPath).Where(x => Path.GetExtension(x) == ".u").ToList();
            int totalFiles = allFiles.Count * 2;
            int numDone = 0;
            foreach (string filePath in allFiles)
            {
                using IMEPackage pcc = MEPackageHandler.OpenUDKPackage(filePath);
                foreach (ExportEntry exportEntry in pcc.Exports)
                {
                    string className = exportEntry.ClassName;
                    string objectName = exportEntry.ObjectName.Instanced;
                    if (className == "Enum")
                    {
                        generateEnumValues(exportEntry, Enums);
                    }
                    else if (className == "Class" && !Classes.ContainsKey(objectName))
                    {
                        Classes.Add(objectName, generateClassInfo(exportEntry));
                    }
                    else if (className == "ScriptStruct")
                    {
                        if (!Structs.ContainsKey(objectName))
                        {
                            Structs.Add(objectName, generateClassInfo(exportEntry, isStruct: true));
                        }
                    }
                }
                // System.Diagnostics.Debug.WriteLine($"{i} of {length} processed");
                numDone++;
                progressDelegate?.Invoke(numDone, totalFiles);
            }

            foreach (string filePath in allFiles)
            {
                using IMEPackage pcc = MEPackageHandler.OpenUDKPackage(filePath);
                foreach (ExportEntry exportEntry in pcc.Exports)
                {
                    if (exportEntry.IsA("SequenceObject"))
                    {
                        string className = exportEntry.ClassName;
                        if (!SequenceObjects.TryGetValue(className, out SequenceObjectInfo seqObjInfo))
                        {
                            seqObjInfo = new SequenceObjectInfo();
                            SequenceObjects.Add(className, seqObjInfo);
                        }

                        int objInstanceVersion = exportEntry.GetProperty<IntProperty>("ObjInstanceVersion");
                        if (objInstanceVersion > seqObjInfo.ObjInstanceVersion)
                        {
                            seqObjInfo.ObjInstanceVersion = objInstanceVersion;
                        }

                        if (seqObjInfo.inputLinks is null && exportEntry.IsDefaultObject)
                        {
                            List<string> inputLinks = generateSequenceObjectInfo(exportEntry);
                            seqObjInfo.inputLinks = inputLinks;
                        }
                    }
                }
                numDone++;
                progressDelegate?.Invoke(numDone, totalFiles);
            }

            var jsonText = JsonConvert.SerializeObject(new { SequenceObjects, Classes, Structs, Enums }, Formatting.Indented);
            File.WriteAllText(outpath, jsonText);
            MemoryManager.SetUsePooledMemory(false);
            Enums.Clear();
            Structs.Clear();
            Classes.Clear();
            SequenceObjects.Clear();
            loadfromJSON(jsonText);
        }

        private static void AddCustomAndNativeClasses(Dictionary<string, ClassInfo> classes, Dictionary<string, SequenceObjectInfo> sequenceObjects)
        {
            ME3UnrealObjectInfo.AddIntrinsicClasses(classes, MEGame.UDK);

            classes["LightMapTexture2D"] = new ClassInfo
            {
                baseClass = "Texture2D",
                pccPath = @"CookedPCConsole\Engine.pcc"
            };

            classes["StaticMesh"] = new ClassInfo
            {
                baseClass = "Object",
                pccPath = @"CookedPCConsole\Engine.pcc",
                properties =
                {
                    new KeyValuePair<NameReference, PropertyInfo>("UseSimpleRigidBodyCollision", new PropertyInfo(PropertyType.BoolProperty)),
                    new KeyValuePair<NameReference, PropertyInfo>("UseSimpleLineCollision", new PropertyInfo(PropertyType.BoolProperty)),
                    new KeyValuePair<NameReference, PropertyInfo>("UseSimpleBoxCollision", new PropertyInfo(PropertyType.BoolProperty)),
                    new KeyValuePair<NameReference, PropertyInfo>("bUsedForInstancing", new PropertyInfo(PropertyType.BoolProperty)),
                    new KeyValuePair<NameReference, PropertyInfo>("ForceDoubleSidedShadowVolumes", new PropertyInfo(PropertyType.BoolProperty)),
                    new KeyValuePair<NameReference, PropertyInfo>("UseFullPrecisionUVs", new PropertyInfo(PropertyType.BoolProperty)),
                    new KeyValuePair<NameReference, PropertyInfo>("BodySetup", new PropertyInfo(PropertyType.ObjectProperty, "RB_BodySetup")),
                    new KeyValuePair<NameReference, PropertyInfo>("LODDistanceRatio", new PropertyInfo(PropertyType.FloatProperty)),
                    new KeyValuePair<NameReference, PropertyInfo>("LightMapCoordinateIndex", new PropertyInfo(PropertyType.IntProperty)),
                    new KeyValuePair<NameReference, PropertyInfo>("LightMapResolution", new PropertyInfo(PropertyType.IntProperty)),
                }
            };

            classes["FracturedStaticMesh"] = new ClassInfo
            {
                baseClass = "StaticMesh",
                pccPath = @"CookedPCConsole\Engine.pcc",
                properties =
                {
                    new KeyValuePair<NameReference, PropertyInfo>("LoseChunkOutsideMaterial", new PropertyInfo(PropertyType.ObjectProperty, "MaterialInterface")),
                    new KeyValuePair<NameReference, PropertyInfo>("bSpawnPhysicsChunks", new PropertyInfo(PropertyType.BoolProperty)),
                    new KeyValuePair<NameReference, PropertyInfo>("bCompositeChunksExplodeOnImpact", new PropertyInfo(PropertyType.BoolProperty)),
                    new KeyValuePair<NameReference, PropertyInfo>("ExplosionVelScale", new PropertyInfo(PropertyType.FloatProperty)),
                    new KeyValuePair<NameReference, PropertyInfo>("FragmentMinHealth", new PropertyInfo(PropertyType.FloatProperty)),
                    new KeyValuePair<NameReference, PropertyInfo>("FragmentDestroyEffects", new PropertyInfo(PropertyType.ArrayProperty, "ParticleSystem")),
                    new KeyValuePair<NameReference, PropertyInfo>("FragmentMaxHealth", new PropertyInfo(PropertyType.FloatProperty)),
                    new KeyValuePair<NameReference, PropertyInfo>("bAlwaysBreakOffIsolatedIslands", new PropertyInfo(PropertyType.BoolProperty)),
                    new KeyValuePair<NameReference, PropertyInfo>("DynamicOutsideMaterial", new PropertyInfo(PropertyType.ObjectProperty, "MaterialInterface")),
                    new KeyValuePair<NameReference, PropertyInfo>("ChunkLinVel", new PropertyInfo(PropertyType.FloatProperty)),
                    new KeyValuePair<NameReference, PropertyInfo>("ChunkAngVel", new PropertyInfo(PropertyType.FloatProperty)),
                    new KeyValuePair<NameReference, PropertyInfo>("ChunkLinHorizontalScale", new PropertyInfo(PropertyType.FloatProperty)),
                }
            };
        }

        //call on the _Default object
        private static List<string> generateSequenceObjectInfo(ExportEntry export)
        {
            var inLinks = export.GetProperty<ArrayProperty<StructProperty>>("InputLinks");
            if (inLinks != null)
            {
                var inputLinks = new List<string>();
                foreach (var seqOpInputLink in inLinks)
                {
                    inputLinks.Add(seqOpInputLink.GetProp<StrProperty>("LinkDesc").Value);
                }
                return inputLinks;
            }

            return null;
        }

        public static ClassInfo generateClassInfo(ExportEntry export, bool isStruct = false)
        {
            IMEPackage pcc = export.FileRef;
            ClassInfo info = new()
            {
                baseClass = export.SuperClassName,
                exportIndex = export.UIndex,
                ClassName = export.ObjectName.Instanced
            };
            if (export.IsClass)
            {
                var classBinary = ObjectBinary.From<UClass>(export);
                info.isAbstract = classBinary.ClassFlags.Has(UnrealFlags.EClassFlags.Abstract);
            }
            if (pcc.FilePath.Contains("UDKGame", StringComparison.OrdinalIgnoreCase))
            {
                info.pccPath = new string(pcc.FilePath.Skip(pcc.FilePath.LastIndexOf("UDKGame", StringComparison.OrdinalIgnoreCase) + 8).ToArray());
            }
            else
            {
                info.pccPath = pcc.FilePath; //used for dynamic resolution of files outside the game directory.
            }
            
            int nextExport = EndianReader.ToInt32(export.DataReadOnly, isStruct ? 0x18 : 0x10, export.FileRef.Endian);
            while (nextExport > 0)
            {
                var entry = pcc.GetUExport(nextExport);
                //Debug.WriteLine($"GenerateClassInfo parsing child {nextExport} {entry.InstancedFullPath}");
                if (entry.ClassName != "ScriptStruct" && entry.ClassName != "Enum"
                    && entry.ClassName != "Function" && entry.ClassName != "Const" && entry.ClassName != "State")
                {
                    if (!info.properties.ContainsKey(entry.ObjectName))
                    {
                        PropertyInfo p = getProperty(entry);
                        if (p != null)
                        {
                            info.properties.Add(entry.ObjectName, p);
                        }
                    }
                }
                nextExport = EndianReader.ToInt32(entry.DataReadOnly, 0xC, export.FileRef.Endian);
            }
            return info;
        }

        private static void generateEnumValues(ExportEntry export, Dictionary<string, List<NameReference>> NewEnums = null)
        {
            var enumTable = NewEnums ?? Enums;
            string enumName = export.ObjectName.Instanced;
            if (!enumTable.ContainsKey(enumName))
            {
                var values = new List<NameReference>();
                var buff = export.DataReadOnly;
                //subtract 1 so that we don't get the MAX value, which is an implementation detail
                int count = EndianReader.ToInt32(buff, 0x10, export.FileRef.Endian) - 1;
                for (int i = 0; i < count; i++)
                {
                    int enumValIndex = 0x14 + i * 8;
                    values.Add(new NameReference(export.FileRef.Names[EndianReader.ToInt32(buff, enumValIndex, export.FileRef.Endian)], EndianReader.ToInt32(buff, enumValIndex + 4, export.FileRef.Endian)));
                }
                enumTable.Add(enumName, values);
            }
        }

        private static PropertyInfo getProperty(ExportEntry entry)
        {
            IMEPackage pcc = entry.FileRef;

            string reference = null;
            PropertyType type;
            switch (entry.ClassName)
            {
                case "IntProperty":
                    type = PropertyType.IntProperty;
                    break;
                case "StringRefProperty":
                    type = PropertyType.StringRefProperty;
                    break;
                case "FloatProperty":
                    type = PropertyType.FloatProperty;
                    break;
                case "BoolProperty":
                    type = PropertyType.BoolProperty;
                    break;
                case "StrProperty":
                    type = PropertyType.StrProperty;
                    break;
                case "NameProperty":
                    type = PropertyType.NameProperty;
                    break;
                case "DelegateProperty":
                    type = PropertyType.DelegateProperty;
                    break;
                case "ObjectProperty":
                case "ClassProperty":
                case "ComponentProperty":
                    type = PropertyType.ObjectProperty;
                    reference = pcc.getObjectName(EndianReader.ToInt32(entry.DataReadOnly, entry.DataSize - 4, entry.FileRef.Endian));
                    break;
                case "StructProperty":
                    type = PropertyType.StructProperty;
                    reference = pcc.getObjectName(EndianReader.ToInt32(entry.DataReadOnly, entry.DataSize - 4, entry.FileRef.Endian));
                    break;
                case "BioMask4Property":
                case "ByteProperty":
                    type = PropertyType.ByteProperty;
                    reference = pcc.getObjectName(EndianReader.ToInt32(entry.DataReadOnly, entry.DataSize - 4, entry.FileRef.Endian));
                    break;
                case "ArrayProperty":
                    type = PropertyType.ArrayProperty;
                    PropertyInfo arrayTypeProp = getProperty(pcc.GetUExport(EndianReader.ToInt32(entry.DataReadOnly, 0x28, entry.FileRef.Endian)));
                    if (arrayTypeProp != null)
                    {
                        switch (arrayTypeProp.Type)
                        {
                            case PropertyType.ObjectProperty:
                            case PropertyType.StructProperty:
                            case PropertyType.ArrayProperty:
                                reference = arrayTypeProp.Reference;
                                break;
                            case PropertyType.ByteProperty:
                                reference = arrayTypeProp.Reference == "Class" ? arrayTypeProp.Type.ToString() : arrayTypeProp.Reference;
                                break;
                            case PropertyType.IntProperty:
                            case PropertyType.FloatProperty:
                            case PropertyType.NameProperty:
                            case PropertyType.BoolProperty:
                            case PropertyType.StrProperty:
                            case PropertyType.StringRefProperty:
                            case PropertyType.DelegateProperty:
                                reference = arrayTypeProp.Type.ToString();
                                break;
                            case PropertyType.None:
                            case PropertyType.Unknown:
                            default:
                                Debugger.Break();
                                return null;
                        }
                    }
                    else
                    {
                        return null;
                    }
                    break;
                case "InterfaceProperty":
                default:
                    return null;
            }

            bool transient = ((UnrealFlags.EPropertyFlags)EndianReader.ToUInt64(entry.DataReadOnly, 0x14, entry.FileRef.Endian)).Has(UnrealFlags.EPropertyFlags.Transient);
            int arrayLength = EndianReader.ToInt32(entry.DataReadOnly, 0x10, entry.FileRef.Endian);
            return new PropertyInfo(type, reference, transient, arrayLength);
        }
        #endregion

        public static bool IsAKnownGameSpecificNativeClass(string className) => NativeClasses.Contains(className);

        /// <summary>
        /// List of all known classes that are only defined in native code that are UDK specific
        /// </summary>
        public static readonly string[] NativeClasses =
        {
            @"Core.Package",
            @"Core.MetaData",
            @"Core.TextBuffer"
        };
    }
}
