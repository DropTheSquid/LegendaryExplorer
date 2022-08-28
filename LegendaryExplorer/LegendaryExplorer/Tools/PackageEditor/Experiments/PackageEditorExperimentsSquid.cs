using LegendaryExplorer.Dialogs;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.UnrealScript.Compiling.Errors;
using LegendaryExplorerCore.UnrealScript;
using System.Text;
using System.Windows;
using System.Linq;
using System.Collections.Generic;
using System;
using System.Numerics;

namespace LegendaryExplorer.Tools.PackageEditor.Experiments
{
    static internal class PackageEditorExperimentsSquid
    {
        public static void MakeCustomMorphTargetSet(PackageEditorWindow pew)
        {
            if (pew.SelectedItem == null || pew.SelectedItem.Entry == null || pew.Pcc == null) { return; }

            if (!(pew.SelectedItem.Entry.ClassName == "MorphTargetSet" || pew.SelectedItem.Entry.ClassName == "SkeletalMesh"))
            {
                ShowError("Selected item is not a MorphTargetSet or SkeletalMesh");
                return;
            }

            var SelectedExport = (ExportEntry)pew.SelectedItem.Entry;
            ExportEntry morphTargetSet = null;
            ExportEntry headMesh;

            if (SelectedExport.ClassName == "MorphTargetSet")
            {
                morphTargetSet = SelectedExport;
                headMesh = (ExportEntry)morphTargetSet.GetProperty<ObjectProperty>("BaseSkelMesh").ResolveToEntry(pew.Pcc);
            }
            else
            {
                headMesh = SelectedExport;
            }

            EnsureParentClassExists(pew);
            var newClass = CreateCustomMorphTargetSet(pew, morphTargetSet, headMesh);
            pew.GoToNumber(newClass.UIndex);
        }

        private static ExportEntry CreateCustomMorphTargetSet(PackageEditorWindow pew, ExportEntry morphTargetSet, ExportEntry headMesh)
        {
            var sb = new StringBuilder();

            var className = morphTargetSet == null ? headMesh.ObjectName : morphTargetSet.ObjectName;

            sb.AppendLine($"Class {className} extends CustomMorphTargetSet config(game);");
            sb.AppendLine("defaultproperties {");
            sb.AppendLine(HandleSkeletalMesh(pew, headMesh));
            if (morphTargetSet != null)
            {
                sb.AppendLine(HandleVanillaMorphTargetSet(pew, morphTargetSet));
            }
            sb.AppendLine("}");

            return MakeNewClass(pew, null, sb.ToString(), className);
        }

        private static string HandleVanillaMorphTargetSet(PackageEditorWindow pew, ExportEntry morphTargetSet)
        {
            var sb = new StringBuilder();

            var targets = morphTargetSet.GetProperty<ArrayProperty<ObjectProperty>>("Targets");

            sb.AppendLine("\tBaseMorphTargets = (");
            for (int k = 0; k < targets.Count; k++)
            {
                var target = targets[k];
                var expEntryTarget = (ExportEntry)target.ResolveToEntry(pew.Pcc);
                // get the binary data from the export
                var targetBinary = expEntryTarget.GetBinaryData<MorphTarget>();

                // add the bone offsets from this target
                sb.AppendLine($"\t\t{{TargetName = '{expEntryTarget.ObjectNameString}',");

                sb.Append("\t\t\tBoneOffsets=(");
                for (int i = 0; i < targetBinary.BoneOffsets.Length; i++)
                {
                    var boneOffset = targetBinary.BoneOffsets[i];
                    sb.Append($"{{Bone = '{boneOffset.Bone}',Offset = {{X = {boneOffset.Offset.X:F8}, Y = {boneOffset.Offset.Y:F8}, Z = {boneOffset.Offset.Z:F8}}}}}{(i < targetBinary.BoneOffsets.Length - 1 ? "," : "")}");
                }
                sb.AppendLine("),");

                sb.Append("\t\t\tLodModels = (");
                for (int i = 0; i < targetBinary.MorphLODModels.Length; i++)
                {
                    var lodModel = targetBinary.MorphLODModels[i];
                    sb.Append($"{{NumBaseMeshVertices={lodModel.NumBaseMeshVerts},vertices = (");

                    for (int j = 0; j < lodModel.Vertices.Length; j++)
                    {
                        var vert = lodModel.Vertices[j];
                        sb.Append($"{{sourceIndex = {vert.SourceIdx},PositionDelta = {{X = {vert.PositionDelta.X:F8}, Y = {vert.PositionDelta.Y:F8}, Z = {vert.PositionDelta.Z:F8}}}}}{(j < lodModel.Vertices.Length - 1 ? "," : "")}");
                    }
                    sb.Append($")}}{(i < targetBinary.MorphLODModels.Length - 1 ? "," : "")}");
                }
                sb.Append(")");

                sb.AppendLine().AppendLine($"\t\t}}{(k < targets.Count - 1 ? "," : "")}");
            }

            // close targets
            sb.AppendLine("\t)");

            return sb.ToString();
        }

        private static ExportEntry GetOrCreatePackageFolder(PackageEditorWindow pew, string packageName)
        {
            var folder = pew.Pcc.FindExport(packageName);

            if (folder == null)
            {
                IEntry packageClass = pew.Pcc.getEntryOrAddImport("Core.Package");
                folder = new ExportEntry(pew.Pcc, 0, packageName)
                {
                    Class = packageClass
                };
                pew.Pcc.AddExport(folder);
                folder = pew.Pcc.FindExport(packageName);
            }

            return folder;
        }

        private static ExportEntry CreateBioMorphFace(PackageEditorWindow pew, string objectName)
        {
            IEntry BioMorphFaceClass = pew.Pcc.getEntryOrAddImport("SFXGame.BioMorphFace");
            var morphFace = new ExportEntry(pew.Pcc, 0, objectName)
            {
                Class = BioMorphFaceClass
            };
            pew.Pcc.AddExport(morphFace);
            morphFace = pew.Pcc.FindExport(objectName);

            return morphFace;
        }

        private static ExportEntry EnsureParentClassExists(PackageEditorWindow pew)
        {
            const string ParentClassText = @"Class CustomMorphTargetSet
    config(game);

// Types
struct BoneOffset 
{
    var Name Bone;
    var Vector Offset;
};
struct CustomMorphTarget 
{
    struct VertexOffset 
    {
        var int sourceIndex;
        var Vector PositionDelta;
    };
    struct LodModel 
    {
        var int NumBaseMeshVertices;
        var array<VertexOffset> vertices;
        
        structdefaultproperties
        {
            vertices = ()
        }
    };
    var array<BoneOffset> BoneOffsets;
    var array<LodModel> LodModels;
    var Name TargetName;
    
    structdefaultproperties
    {
        BoneOffsets = ()
        LodModels = ()
    }
};
struct MeshVertices 
{
    var array<Vector> vertices;
    
    structdefaultproperties
    {
        vertices = ()
    }
};

// Variables
var array<CustomMorphTarget> BaseMorphTargets;
var config array<CustomMorphTarget> CustomMorphTargets;
var array<BoneOffset> OriginalMeshBoneOffsets;
var array<MeshVertices> OriginalMeshLodModels;

//class default properties can be edited in the Properties tab for the class's Default__ object.
defaultproperties
{
}";
            const string ParentClassPackage = "MeshTools";
            const string ParentClassName = "CustomMorphTargetSet";

            var parentClass = pew.Pcc.FindExport($"{ParentClassPackage}.{ParentClassName}");

            if (parentClass != null)
            {
                return parentClass;
            }

            var parentFolder = GetOrCreatePackageFolder(pew, ParentClassPackage);

            return MakeNewClass(pew, parentFolder, ParentClassText, ParentClassName);
        }

        private static ExportEntry MakeNewClass(PackageEditorWindow pew, IEntry parent, string classText, string className)
        {
            var fileLib = new FileLib(pew.Pcc);
            if (!fileLib.Initialize())
            {
                var dlg = new ListDialog(fileLib.InitializationLog.AllErrors.Select(msg => msg.ToString()), "Script Error", "Could not build script database for this file!", pew);
                dlg.Show();
                throw new System.Exception("fileLib failed to initialize");
            }
            (_, MessageLog log) = UnrealScriptCompiler.CompileClass(pew.Pcc, classText, fileLib, parent: parent);
            if (log.HasErrors)
            {
                var dlg = new ListDialog(log.AllErrors.Select(msg => msg.ToString()), "Script Error", "Could not create class!", pew);
                dlg.Show();
                throw new System.Exception("class failed to compile");
            }

            string fullPath = parent is null ? className : $"{parent.InstancedFullPath}.{className}";
            return (ExportEntry)pew.Pcc.FindEntry(fullPath);
        }

        private static string HandleSkeletalMesh(PackageEditorWindow pew, ExportEntry headMesh)
        {
            var meshBinary = headMesh.GetBinaryData<SkeletalMesh>();
            var morphHeadBinary = new BioMorphFace();
            var morphHeadProps = new PropertyCollection();
            var morphHeadSkeleton = new ArrayProperty<StructProperty>("m_aFinalSkeleton");

            morphHeadProps.Add(new ObjectProperty(headMesh, "m_oBaseHead"));

            var MorphHeadExcludeBones = new List<string> { "God", "Root", "LowerBack", "Chest", "Chest1", "Chest2", "Neck", "Neck1", "Head", };
            // m_aFinalSkeleton, m_oBaseHead

            var sb = new StringBuilder();

            // add the original mesh bone offsets (ref skeleton)
            sb.AppendLine("\tOriginalMeshBoneOffsets = (");
            for (int i = 0; i < meshBinary.RefSkeleton.Length; i++)
            {
                var refBone = meshBinary.RefSkeleton[i];
                sb.AppendLine($"\t\t{{Bone = '{refBone.Name}',Offset = {{X = {refBone.Position.X:F8}, Y = {refBone.Position.Y:F8}, Z = {refBone.Position.Z:F8}}}}}{(i < meshBinary.RefSkeleton.Length - 1 ? "," : "")}");

                if (!MorphHeadExcludeBones.Contains(refBone.Name))
                {
                    morphHeadSkeleton.Add(new StructProperty("OffsetBonePos",
                        false,
                        new NameProperty(refBone.Name, "nName"),
                        new StructProperty("Vector", true,
                            new FloatProperty(refBone.Position.X, "X"),
                            new FloatProperty(refBone.Position.Y, "Y"),
                            new FloatProperty(refBone.Position.Z, "Z")
                            )
                        { Name = "vPos" }));
                }
            }
            sb.AppendLine("\t)");

            morphHeadProps.Add(morphHeadSkeleton);

            // add the original mesh vertices
            sb.AppendLine("\tOriginalMeshLodModels = (");
            morphHeadBinary.LODs = new System.Numerics.Vector3[meshBinary.LODModels.Length][];
            for (int i = 0; i < meshBinary.LODModels.Length; i++)
            {
                var lodModel = meshBinary.LODModels[i];
                morphHeadBinary.LODs[i] = new System.Numerics.Vector3[lodModel.VertexBufferGPUSkin.VertexData.Length];
                var morphLod = morphHeadBinary.LODs[i];

                sb.Append("\t\t{vertices = (");
                for (int j = 0; j < lodModel.VertexBufferGPUSkin.VertexData.Length; j++)
                {
                    var vert = lodModel.VertexBufferGPUSkin.VertexData[j];
                    sb.Append($"{{X = {vert.Position.X:F8},Y = {vert.Position.Y:F8}, Z = {vert.Position.Z:F8}}}{(j < lodModel.VertexBufferGPUSkin.VertexData.Length - 1 ? "," : "")}");
                    morphLod[j] = new System.Numerics.Vector3(vert.Position.X, vert.Position.Y, vert.Position.Z);
                }
                sb.AppendLine($")}}{(i < meshBinary.LODModels.Length - 1 ? "," : "")}");
            }
            sb.AppendLine("\t)");

            var morphHead = CreateBioMorphFace(pew, headMesh.ObjectName + "_MorphHead");

            morphHead.WritePropertiesAndBinary(morphHeadProps, morphHeadBinary);

            return sb.ToString();
        }

        public static void MakeHeterochromiaMesh(PackageEditorWindow pew)
        {
            if (pew.SelectedItem == null || pew.SelectedItem.Entry == null || pew.Pcc == null) { return; }

            if (pew.SelectedItem.Entry.ClassName != "SkeletalMesh")
            {
                ShowError("Selected item is not a SkeletalMesh");
                return;
            }

            var headMesh = (ExportEntry)pew.SelectedItem.Entry;
            var meshBinary = headMesh.GetBinaryData<SkeletalMesh>();

            var eyeMatIndex = GetEyeMaterialIndex(pew, meshBinary);
            var newEyeMaterialIndex = AddMaterialSlot(meshBinary);

            // from there, find the section we need to modify
            var LOD0 = meshBinary.LODModels[0];
            HandleLOD(LOD0, eyeMatIndex, newEyeMaterialIndex);

            headMesh.WriteBinary(meshBinary);
        }

        private static void HandleLOD(StaticLODModel lod, int eyeMatIndex, int newEyeMatIndex)
        {
            SkelMeshSection eyeSection = null;
            int EyeSectionIndex = -1;
            for (int i = 0; i < lod.Sections.Length; i++)
            {
                var section = lod.Sections[i];
                if (section.MaterialIndex == eyeMatIndex)
                {
                    EyeSectionIndex = i;
                    eyeSection = section;
                    break;
                }
            }

            if (eyeSection == null)
            {
                return;
            }

            var newSections = new List<SkelMeshSection>();

            for (int i = 0; i < EyeSectionIndex; i++)
            {
                newSections.Add(lod.Sections[i]);
            }

            bool right = IsRightEyeTriangle(lod, (int)eyeSection.BaseIndex);
            int currentTriangleCount = 0;
            int currentBaseIndex = (int)eyeSection.BaseIndex;
            for (int i = (int)eyeSection.BaseIndex; i < (int)eyeSection.BaseIndex + eyeSection.NumTriangles * 3; i += 3)
            {
                if (IsRightEyeTriangle(lod, i) == right)
                {
                    currentTriangleCount++;
                    continue;
                }
                
                newSections.Add(new SkelMeshSection()
                {
                    BaseIndex = (uint)currentBaseIndex,
                    ChunkIndex = eyeSection.ChunkIndex,
                    MaterialIndex = (ushort)(right ? newEyeMatIndex : eyeMatIndex),
                    NumTriangles = currentTriangleCount,
                    TriangleSorting = eyeSection.TriangleSorting
                });

                right = !right;
                currentBaseIndex = i;
                currentTriangleCount = 1;
            }

            newSections.Add(new SkelMeshSection()
            {
                BaseIndex = (uint)currentBaseIndex,
                ChunkIndex = eyeSection.ChunkIndex,
                MaterialIndex = (ushort)(right ? newEyeMatIndex : eyeMatIndex),
                NumTriangles = currentTriangleCount,
                TriangleSorting = eyeSection.TriangleSorting
            });

            for (int i = EyeSectionIndex + 1; i < lod.Sections.Length; i++)
            {
                newSections.Add(lod.Sections[i]);
            }

            lod.Sections = newSections.ToArray();
        }

        private static (int, int, int) GetTriangle(StaticLODModel lod, int triangleIndex)
        {
            return (lod.IndexBuffer[triangleIndex], lod.IndexBuffer[triangleIndex + 1], lod.IndexBuffer[triangleIndex + 2]);
        }

        private static Vector3 GetVertex(StaticLODModel lod, int vertIndex)
        {
            return lod.VertexBufferGPUSkin.VertexData[vertIndex].Position;
        }

        private static bool IsRightEyeTriangle(StaticLODModel lod, int triangleIndex)
        {
            var (v1, v2, v3) = GetTriangle(lod, triangleIndex);
            // TODO validate that the others are also right side?
            return IsRightEyeVertex(lod, v1);
        }

        private static bool IsRightEyeVertex(StaticLODModel lod, int vertIndex)
        {
            return GetVertex(lod, vertIndex).Y > 0;
        }

        private static int GetEyeMaterialIndex(PackageEditorWindow pew, SkeletalMesh meshBinary)
        {
            var materialChoices = meshBinary.Materials.Select<int, IEntry>(x => x > 0 ? pew.Pcc.GetUExport(x) : pew.Pcc.GetImport(x)).ToList();
            var mat = EntrySelector.GetEntry<IEntry>(pew, pew.Pcc, "Which material is the eye material?",
                    exp => materialChoices.Contains(exp));
            return materialChoices.IndexOf(mat);
        }

        private static int AddMaterialSlot(SkeletalMesh meshBinary)
        {
            var tempMaterials = meshBinary.Materials;
            meshBinary.Materials = new int[meshBinary.Materials.Length + 1];
            for (int i = 0; i < tempMaterials.Length; i++)
            {
                meshBinary.Materials[i] = tempMaterials[i];
            }

            return meshBinary.Materials.Length - 1;
        }

        private static void ShowError(string errMsg)
        {
            MessageBox.Show(errMsg, "Warning", MessageBoxButton.OK);
        }
    }
}
