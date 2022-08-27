using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using System.Text;
using System.Windows;

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
            ExportEntry MorphTargetSet = null;
            ExportEntry HeadMesh;

            if (SelectedExport.ClassName == "MorphTargetSet")
            {
                MorphTargetSet = SelectedExport;
                HeadMesh = (ExportEntry)MorphTargetSet.GetProperty<ObjectProperty>("BaseSkelMesh").ResolveToEntry(pew.Pcc);
            }
            else
            {
                HeadMesh = SelectedExport;
            }

            HandleMorphTargetSet(MorphTargetSet);
            HandleSkeletalMesh(HeadMesh);
        }

        private static void HandleMorphTargetSet(ExportEntry MorphTargetSet)
        {

        }

        private static void HandleSkeletalMesh(ExportEntry HeadMesh)
        {

        }

        private static void ShowError(string errMsg)
        {
            MessageBox.Show(errMsg, "Warning", MessageBoxButton.OK);
        }

        /// <summary>
        /// Converts the binary data in a morphTargetSet into text you can put in your coalesced file for reading by unrealscript.
        /// </summary>
        /// <param name="pew">Current PE window</param>
        public static void ConvertMorphTargetSetToCoalesced(PackageEditorWindow pew)
        {
            if (pew.SelectedItem == null || pew.SelectedItem.Entry == null || pew.Pcc == null) { return; }

            if (pew.SelectedItem.Entry.ClassName != "MorphTargetSet")
            {
                ShowError("Selected item is not a MorphTargetSet");
                return;
            }

            // get the selected export so we can get information from it
            var morphTargetSet = (ExportEntry)pew.SelectedItem.Entry;

            // get and enumerate the targets in the morph target set
            var targets = morphTargetSet.GetProperty<ArrayProperty<ObjectProperty>>("Targets");
            var meshExpEntry = (ExportEntry)morphTargetSet.GetProperty<ObjectProperty>("BaseSkelMesh").ResolveToEntry(pew.Pcc);
            var meshBinary = meshExpEntry.GetBinaryData<SkeletalMesh>();

            var sb = new StringBuilder();

            // add the section opening tag
            sb.AppendLine($"\t\t<Section name=\"morphtargets.{morphTargetSet.ObjectNameString.ToLowerInvariant()}\">");

            // add the original mesh bone offsets (ref skeleton)
            sb.AppendLine("\t\t\t<Property name=\"originalmeshboneoffsets\">");
            for (int i = 0; i < meshBinary.RefSkeleton.Length; i++)
            {
                var refBone = meshBinary.RefSkeleton[i];
                sb.AppendLine($"\t\t\t\t<Value type=\"3\">(Bone=\"{refBone.Name}\",Offset=(X={refBone.Position.X:F8},Y={refBone.Position.Y:F8},Z={refBone.Position.Z:F8}))</Value>");
            }
            sb.AppendLine("\t\t\t</Property>");

            // add the original mesh vertices
            sb.AppendLine("\t\t\t<Property name=\"originalmesh\">");
            for (int i = 0; i < meshBinary.LODModels.Length; i++)
            {
                var lodModel = meshBinary.LODModels[i];
                sb.Append($"\t\t\t\t<Value type=\"3\">(");
                for (int j = 0; j < lodModel.VertexBufferGPUSkin.VertexData.Length; j++)
                {
                    var vert = lodModel.VertexBufferGPUSkin.VertexData[j];
                    sb.Append($"vertices[{j}]=(X={vert.Position.X:F8},Y={vert.Position.Y:F8},Z={vert.Position.Z:F8}),");
                }
                sb.AppendLine(")</Value>");
            }
            sb.AppendLine("\t\t\t</Property>");

            // start adding the individual targets
            sb.AppendLine("\t\t\t<Property name=\"morphtargets\">");
            foreach (var target in targets)
            {
                var expEntryTarget = (ExportEntry)target.ResolveToEntry(pew.Pcc);
                // get the binary data from the export
                var targetBinary = expEntryTarget.GetBinaryData<MorphTarget>();

                // add the bone offsets from this target
                sb.AppendLine($"\t\t\t\t<Value type=\"3\">(TargetName=\"{expEntryTarget.ObjectNameString}\",");
                sb.Append("\t\t\t\t\t");
                for (int i = 0; i < targetBinary.BoneOffsets.Length; i++)
                {
                    var boneOffset = targetBinary.BoneOffsets[i];
                    sb.Append($"BoneOffsets[{i}]=(Bone=\"{boneOffset.Bone}\",Offset=(x={boneOffset.Offset.X:F8},y={boneOffset.Offset.Y:F8},z={boneOffset.Offset.Z:F8})),");
                }

                sb.AppendLine();
                sb.Append("\t\t\t\t\t");
                for (int i = 0; i < targetBinary.MorphLODModels.Length; i++)
                {
                    var lodModel = targetBinary.MorphLODModels[i];
                    sb.Append($"LodModels[{i}]=(NumBaseMeshVertices={lodModel.NumBaseMeshVerts},");

                    for (int j = 0; j < lodModel.Vertices.Length; j++)
                    {
                        var vert = lodModel.Vertices[j];
                        sb.Append($"vertices[{j}]=(sourceIndex={vert.SourceIdx}, PositionDelta=(x={vert.PositionDelta.X:F8},y={vert.PositionDelta.Y:F8},z={vert.PositionDelta.Z:F8})),");
                    }
                    sb.Append($"),");
                }
                sb.AppendLine().AppendLine("\t\t\t\t)</Value>");
            }

            // close targets
            sb.AppendLine("\t\t\t</Property>");
            // close sections
            sb.AppendLine("\t\t</Section>");
            var finalString = sb.ToString();
            Clipboard.SetText(finalString);

            MessageBox.Show("The data has been copied to the cliboard.", "Success", MessageBoxButton.OK);
        }

        /// <summary>
        /// Converts the binary data in a morphTargetSet into text you can put in the script compiler.
        /// </summary>
        /// <param name="pew">Current PE window</param>
        public static void ConvertMorphTargetSetToUnreal(PackageEditorWindow pew)
        {
            if (pew.SelectedItem == null || pew.SelectedItem.Entry == null || pew.Pcc == null) { return; }

            if (pew.SelectedItem.Entry.ClassName != "MorphTargetSet")
            {
                ShowError("Selected item is not a MorphTargetSet");
                return;
            }

            var sanityLimit = 3000000;

            // get the selected export so we can get information from it
            var morphTargetSet = (ExportEntry)pew.SelectedItem.Entry;

            // get and enumerate the targets in the morph target set
            var targets = morphTargetSet.GetProperty<ArrayProperty<ObjectProperty>>("Targets");
            var meshExpEntry = (ExportEntry)morphTargetSet.GetProperty<ObjectProperty>("BaseSkelMesh").ResolveToEntry(pew.Pcc);
            var meshBinary = meshExpEntry.GetBinaryData<SkeletalMesh>();

            var sb = new StringBuilder();

            // add the header
            sb.AppendLine("defaultproperties {");

            // add the original mesh bone offsets (ref skeleton)
            sb.AppendLine("\tOriginalMeshBoneOffsets = (");
            for (int i = 0; i < meshBinary.RefSkeleton.Length; i++)
            {
                var refBone = meshBinary.RefSkeleton[i];
                sb.AppendLine($"\t\t{{Bone = '{refBone.Name}',Offset = {{X = {refBone.Position.X:F8}, Y = {refBone.Position.Y:F8}, Z = {refBone.Position.Z:F8}}}}}{(i < meshBinary.RefSkeleton.Length - 1 ? "," : "")}");
            }
            sb.AppendLine("\t)");

            // add the original mesh vertices
            sb.AppendLine("\tOriginalMeshLodModels = (");
            for (int i = 0; i < meshBinary.LODModels.Length; i++)
            {
                var lodModel = meshBinary.LODModels[i];
                sb.Append("\t\t{vertices = (");
                for (int j = 0; j < lodModel.VertexBufferGPUSkin.VertexData.Length && j < sanityLimit; j++)
                {
                    var vert = lodModel.VertexBufferGPUSkin.VertexData[j];
                    sb.Append($"{{X = {vert.Position.X:F8},Y = {vert.Position.Y:F8}, Z = {vert.Position.Z:F8}}}{(j < lodModel.VertexBufferGPUSkin.VertexData.Length - 1 && j < sanityLimit - 1 ? "," : "")}");
                }
                sb.AppendLine($")}}{(i < meshBinary.LODModels.Length - 1 ? "," : "")}");
            }
            sb.AppendLine("\t)");

            // start adding the individual targets
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

                    for (int j = 0; j < lodModel.Vertices.Length && j < sanityLimit; j++)
                    {
                        var vert = lodModel.Vertices[j];
                        sb.Append($"{{sourceIndex = {vert.SourceIdx},PositionDelta = {{X = {vert.PositionDelta.X:F8}, Y = {vert.PositionDelta.Y:F8}, Z = {vert.PositionDelta.Z:F8}}}}}{(j < lodModel.Vertices.Length - 1 && j < sanityLimit - 1 ? "," : "")}");
                    }
                    sb.Append($")}}{(i < targetBinary.MorphLODModels.Length - 1 ? "," : "")}");
                }
                sb.Append(")");

                sb.AppendLine().AppendLine($"\t\t}}{(k < targets.Count - 1 ? "," : "")}");
            }

            // close targets
            sb.AppendLine("\t)");
            // end defaults block
            sb.AppendLine("}");
            var finalString = sb.ToString();
            Clipboard.SetText(finalString);

            MessageBox.Show("The data has been copied to the cliboard.", "Success", MessageBoxButton.OK);
        }
    }
}
