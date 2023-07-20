﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LegendaryExplorerCore.DebugTools;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Gammtek.IO;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Memory;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using Newtonsoft.Json;

namespace LegendaryExplorerCore.Unreal.ObjectInfo
{
    public static class ME2UnrealObjectInfo
    {
        public static Dictionary<string, ClassInfo> Classes = new();
        public static Dictionary<string, ClassInfo> Structs = new();
        public static Dictionary<string, List<NameReference>> Enums = new();
        public static Dictionary<string, SequenceObjectInfo> SequenceObjects = new();

        public static bool IsLoaded;
        public static void loadfromJSON(string jsonTextOverride = null)
        {
            if (!IsLoaded)
            {
                LECLog.Information(@"Loading property db for ME2");
                try
                {
                    var infoText = jsonTextOverride ?? ObjectInfoLoader.LoadEmbeddedJSONText(MEGame.ME2);
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
                    LECLog.Information($@"Property database load failed for ME2: {ex.Message}");
                    return;
                }
            }
        }

        public static PropertyInfo getPropertyInfo(string className, NameReference propName, bool inStruct = false, ClassInfo nonVanillaClassInfo = null, bool reSearch = true, ExportEntry containingExport = null)
        {
            if (className.StartsWith("Default__", StringComparison.OrdinalIgnoreCase))
            {
                className = className.Substring(9);
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
            // //09/23/2022 - Fixed Classes[] = assignment using the class name rather than the containing name. This would make future
            // lookups wrong.
            // - Mgamerz
            if (!infoExists && !inStruct && containingExport != null && containingExport.IsDefaultObject && containingExport.Class is ExportEntry classExp)
            {
                info = generateClassInfo(classExp, false);
                Classes[containingExport.ClassName] = info;
                infoExists = true;
            }

            if (infoExists) //|| (temp = !inStruct ? Structs : Classes).ContainsKey(className))
            {
                //look in class properties
                if (info.properties.TryGetValue(propName, out var propInfo))
                {
                    return propInfo;
                }
                //look in structs

                foreach (PropertyInfo p in info.properties.Values())
                {
                    if ((p.Type is PropertyType.StructProperty or PropertyType.ArrayProperty) && reSearch)
                    {
                        PropertyInfo val = getPropertyInfo(p.Reference, propName, true, nonVanillaClassInfo, reSearch: false);
                        if (val != null)
                        {
                            return val;
                        }
                    }
                }
                //look in base class
                if (temp.ContainsKey(info.baseClass))
                {
                    PropertyInfo val = getPropertyInfo(info.baseClass, propName, inStruct, nonVanillaClassInfo, reSearch: true);
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

        private static readonly string[] ImmutableStructs = { "Vector", "Color", "LinearColor", "TwoVectors", "Vector4", "Vector2D", "Rotator", "Guid", "Plane", "Box",
            "Quat", "Matrix", "IntPoint", "ActorReference", "PolyReference", "NavReference", "FontCharacter", "CovPosInfo",
            "CoverReference", "CoverInfo", "CoverSlot", "BioRwBox", "BioMask4Property", "RwVector2", "RwVector3", "RwVector4", "BioRwBox44" };

        public static bool IsImmutableStruct(string structName)
        {
            return ImmutableStructs.Contains(structName);
        }

        internal static readonly ConcurrentDictionary<string, PropertyCollection> defaultStructValuesME2 = new();
        
        #region Generating
        //call this method to regenerate ME2ObjectInfo.json
        //Takes a long time (10 minutes maybe?). Application will be completely unresponsive during that time.
        public static void generateInfo(string outpath, bool usePooledMemory = true, Action<int, int> progressDelegate = null)
        {
            MemoryManager.SetUsePooledMemory(usePooledMemory);
            Enums.Clear();
            Structs.Clear();
            Classes.Clear();
            SequenceObjects.Clear();

            var allFiles = MELoadedFiles.GetOfficialFiles(MEGame.ME2).Where(x => Path.GetExtension(x) == ".pcc").ToList();
            int totalFiles = allFiles.Count * 2;
            int numDone = 0;
            foreach (string filePath in allFiles)
            {
                using IMEPackage pcc = MEPackageHandler.OpenME2Package(filePath);
                if (pcc.Localization != MELocalization.None && pcc.Localization != MELocalization.INT)
                    continue; // DO NOT LOOK AT NON-INT AS SOME GAMES WILL BE MISSING THESE FILES (due to backup/storage)
                for (int j = 1; j <= pcc.ExportCount; j++)
                {
                    ExportEntry exportEntry = pcc.GetUExport(j);
                    string className = exportEntry.ClassName;
                    if (className == "Enum")
                    {
                        generateEnumValues(exportEntry, Enums);
                    }
                    else if (className == "Class")
                    {
                        string objectName = exportEntry.ObjectName;
                        if (!Classes.ContainsKey(objectName))
                        {
                            Classes.Add(objectName, generateClassInfo(exportEntry));
                        }
                    }
                    else if (className == "ScriptStruct")
                    {
                        string objectName = exportEntry.ObjectName;
                        if (!Structs.ContainsKey(objectName))
                        {
                            Structs.Add(objectName, generateClassInfo(exportEntry, isStruct: true));

                        }
                    }
                }
                numDone++;
                progressDelegate?.Invoke(numDone, totalFiles);
            }

            //native classes not defined in data files
            Classes["LightMapTexture2D"] = new ClassInfo
            {
                baseClass = "Texture2D",
                exportIndex = 0,
                pccPath = GlobalUnrealObjectInfo.Me3ExplorerCustomNativeAdditionsName
            };

            Classes["StaticMesh"] = new ClassInfo
            {
                baseClass = "Object",
                exportIndex = 0,
                pccPath = GlobalUnrealObjectInfo.Me3ExplorerCustomNativeAdditionsName,
                properties =
                    {
                        new KeyValuePair<NameReference, PropertyInfo>("UseSimpleRigidBodyCollision", new PropertyInfo(PropertyType.BoolProperty)),
                        new KeyValuePair<NameReference, PropertyInfo>("UseSimpleLineCollision", new PropertyInfo(PropertyType.BoolProperty)),
                        new KeyValuePair<NameReference, PropertyInfo>("UseSimpleBoxCollision", new PropertyInfo(PropertyType.BoolProperty)),
                        new KeyValuePair<NameReference, PropertyInfo>("UseFullPrecisionUVs", new PropertyInfo(PropertyType.BoolProperty)),
                        new KeyValuePair<NameReference, PropertyInfo>("BodySetup", new PropertyInfo(PropertyType.ObjectProperty, "RB_BodySetup")),
                        new KeyValuePair<NameReference, PropertyInfo>("LODDistanceRatio", new PropertyInfo(PropertyType.FloatProperty)),
                        new KeyValuePair<NameReference, PropertyInfo>("LightMapCoordinateIndex", new PropertyInfo(PropertyType.IntProperty)),
                        new KeyValuePair<NameReference, PropertyInfo>("LightMapResolution", new PropertyInfo(PropertyType.IntProperty)),
                    }
            };

            //SFXPhysicalMaterialDecals missing items
            ClassInfo sfxpmd = Classes["SFXPhysicalMaterialDecals"];
            string[] decalComponentArrays = { "HeavyPistol", "AutoPistol", "HandCannon", "SMG", "Shotgun", "HeavyShotgun", "FlakGun", "AssaultRifle", "Needler", "Machinegun", "SniperRifle", "AntiMatRifle", "MassCannon", "ParticleBeam" };
            foreach (string decal in decalComponentArrays)
            {
                sfxpmd.properties.Add(decal, new PropertyInfo(PropertyType.ArrayProperty, "DecalComponent"));
            }

            Classes["SFXWeapon"].properties.Add("InstantHitDamageTypes", new PropertyInfo(PropertyType.ArrayProperty, "Class"));

            foreach (string filePath in allFiles)
            {
                using IMEPackage pcc = MEPackageHandler.OpenME2Package(filePath);
                if (pcc.Localization != MELocalization.None && pcc.Localization != MELocalization.INT)
                    continue; // DO NOT LOOK AT NON-INT AS SOME GAMES WILL BE MISSING THESE FILES (due to backup/storage)
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
            //CUSTOM ADDITIONS
            classes["SeqAct_SendMessageToME3Explorer"] = new ClassInfo
            {
                baseClass = "SequenceAction",
                exportIndex = 2,
                pccPath = GlobalUnrealObjectInfo.Me3ExplorerCustomNativeAdditionsName
            };
            sequenceObjects["SeqAct_SendMessageToME3Explorer"] = new SequenceObjectInfo { ObjInstanceVersion = 2 };

            classes["SeqAct_ME3ExpDumpActors"] = new ClassInfo
            {
                baseClass = "SequenceAction",
                exportIndex = 4,
                pccPath = GlobalUnrealObjectInfo.Me3ExplorerCustomNativeAdditionsName
            };
            sequenceObjects["SeqAct_ME3ExpDumpActors"] = new SequenceObjectInfo { ObjInstanceVersion = 2 };

            classes["SeqAct_ME3ExpAcessDumpedActorsList"] = new ClassInfo
            {
                baseClass = "SequenceAction",
                exportIndex = 6,
                pccPath = GlobalUnrealObjectInfo.Me3ExplorerCustomNativeAdditionsName
            };
            sequenceObjects["SeqAct_ME3ExpAcessDumpedActorsList"] = new SequenceObjectInfo { ObjInstanceVersion = 2 };

            classes["SeqAct_ME3ExpGetPlayerCamPOV"] = new ClassInfo
            {
                baseClass = "SequenceAction",
                exportIndex = 8,
                pccPath = GlobalUnrealObjectInfo.Me3ExplorerCustomNativeAdditionsName
            };
            sequenceObjects["SeqAct_ME3ExpGetPlayerCamPOV"] = new SequenceObjectInfo { ObjInstanceVersion = 2 };

            classes["SeqAct_GetLocationAndRotation"] = new ClassInfo
            {
                baseClass = "SequenceAction",
                exportIndex = 10,
                pccPath = GlobalUnrealObjectInfo.Me3ExplorerCustomNativeAdditionsName,
                properties =
                {
                    new KeyValuePair<NameReference, PropertyInfo>("m_oTarget", new PropertyInfo(PropertyType.ObjectProperty, "Actor")),
                    new KeyValuePair<NameReference, PropertyInfo>("Location", new PropertyInfo(PropertyType.StructProperty, "Vector")),
                    new KeyValuePair<NameReference, PropertyInfo>("RotationVector", new PropertyInfo(PropertyType.StructProperty, "Vector"))
                }
            };
            sequenceObjects["SeqAct_GetLocationAndRotation"] = new SequenceObjectInfo { ObjInstanceVersion = 0 };

            classes["SeqAct_SetLocationAndRotation"] = new ClassInfo
            {
                baseClass = "SequenceAction",
                exportIndex = 16,
                pccPath = GlobalUnrealObjectInfo.Me3ExplorerCustomNativeAdditionsName,
                properties =
                {
                    new KeyValuePair<NameReference, PropertyInfo>("bSetRotation", new PropertyInfo(PropertyType.BoolProperty)),
                    new KeyValuePair<NameReference, PropertyInfo>("bSetLocation", new PropertyInfo(PropertyType.BoolProperty)),
                    new KeyValuePair<NameReference, PropertyInfo>("m_oTarget", new PropertyInfo(PropertyType.ObjectProperty, "Actor")),
                    new KeyValuePair<NameReference, PropertyInfo>("Location", new PropertyInfo(PropertyType.StructProperty, "Vector")),
                    new KeyValuePair<NameReference, PropertyInfo>("RotationVector", new PropertyInfo(PropertyType.StructProperty, "Vector")),
                }
            };
            sequenceObjects["SeqAct_SetLocationAndRotation"] = new SequenceObjectInfo { ObjInstanceVersion = 0 };


            ME3UnrealObjectInfo.AddIntrinsicClasses(classes, MEGame.ME2);

            classes["LightMapTexture2D"] = new ClassInfo
            {
                baseClass = "Texture2D",
                pccPath = @"CookedPC\Engine.pcc"
            };

            classes["StaticMesh"] = new ClassInfo
            {
                baseClass = "Object",
                pccPath = @"CookedPC\Engine.pcc",
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
        }

        public static ClassInfo generateClassInfo(ExportEntry export, bool isStruct = false)
        {
            IMEPackage pcc = export.FileRef;
            ClassInfo info = new()
            {
                baseClass = export.SuperClassName,
                exportIndex = export.UIndex,
                ClassName = export.ObjectName
            };
            if (!isStruct)
            {
                var classBinary = ObjectBinary.From<UClass>(export);
                info.isAbstract = classBinary.ClassFlags.Has(UnrealFlags.EClassFlags.Abstract);
            }

            if (pcc.FilePath != null)
            {
                if (pcc.FilePath.Contains("BioGame"))
                {
                    info.pccPath = new string(pcc.FilePath.Skip(pcc.FilePath.LastIndexOf("BioGame") + 8).ToArray());
                }
                else
                {
                    info.pccPath = pcc.FilePath; //used for dynamic resolution of files outside the game directory.
                }
            }

            int nextExport = EndianReader.ToInt32(export.DataReadOnly, isStruct ? 0x18 : 0x10, export.FileRef.Endian);
            while (nextExport > 0)
            {
                var entry = pcc.GetUExport(nextExport);
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
                nextExport = EndianReader.ToInt32(entry.DataReadOnly, 0x10, export.FileRef.Endian);
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
                int count = EndianReader.ToInt32(buff, 20, export.FileRef.Endian) - 1;
                for (int i = 0; i < count; i++)
                {
                    int enumValIndex = 24 + i * 8;
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
                case "ClassProperty":
                case "ObjectProperty":
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
                    PropertyInfo arrayTypeProp = getProperty(pcc.GetUExport(EndianReader.ToInt32(entry.DataReadOnly, 44, entry.FileRef.Endian)));
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
                                //if (arrayTypeProp.reference == "")
                                if (arrayTypeProp.Reference == "Class")
                                    reference = arrayTypeProp.Type.ToString();
                                else
                                    reference = arrayTypeProp.Reference;
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

            bool transient = ((UnrealFlags.EPropertyFlags)EndianReader.ToUInt64(entry.DataReadOnly, 24, entry.FileRef.Endian)).Has(UnrealFlags.EPropertyFlags.Transient);
            int arrayLength = EndianReader.ToInt32(entry.DataReadOnly, 20, entry.FileRef.Endian);
            return new PropertyInfo(type, reference, transient, arrayLength);
        }
        #endregion

        public static bool IsAKnownGameSpecificNativeClass(string className) => NativeClasses.Contains(className);

        /// <summary>
        /// List of all known classes that are only defined in native code that are ME2 specific
        /// </summary>
        public static readonly string[] NativeClasses =
        {
            
        };
    }
}
