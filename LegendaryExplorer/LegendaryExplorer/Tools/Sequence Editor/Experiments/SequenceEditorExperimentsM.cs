﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Kismet;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Packages.CloningImportingAndRelinking;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.ObjectInfo;

namespace LegendaryExplorer.Tools.Sequence_Editor.Experiments
{

    /// <summary>
    /// Experiments in Sequence Editor (Mgamerz' stuff)
    /// </summary>
    public static class SequenceEditorExperimentsM
    {
        /// <summary>
        /// Might not be that reliable it seems
        /// </summary>
        /// <param name="seqEd"></param>
        public static void CheckSequenceSets(SequenceEditorWPF seqEd)
        {
            if (seqEd.CurrentObjects.Any)
            {
                foreach (var seqObj in seqEd.CurrentObjects)
                {
                    //if (seqObj.Export.ClassName == "SeqAct_SetInt")
                    //Debug.WriteLine("hi");
                    if (seqObj.Export.IsA("SeqAct_SetSequenceVariable"))
                    {
                        var varLinks = SeqTools.GetVariableLinksOfNode(seqObj.Export);
                        foreach (var link in varLinks)
                        {
                            //link.

                            foreach (var linkedNode in link.LinkedNodes.OfType<ExportEntry>())
                            {
                                if (!linkedNode.IsA(link.ExpectedTypeName))
                                {
                                    Debug.WriteLine("NOT A THING");
                                }

                                var propertyInfo = GlobalUnrealObjectInfo.GetPropertyInfo(seqEd.Pcc.Game, link.PropertyName, linkedNode.ClassName, containingExport: linkedNode);
                                if (propertyInfo == null)
                                {
                                    Debug.WriteLine($"SEQCHECK: {seqObj.Export.UIndex} {seqObj.Export.ObjectName.Instanced} writes a property named {link.PropertyName}, but it doesn't exist on class {linkedNode.ClassName}!");
                                }

                            }

                        }
                    }
                }
            }
        }

        public static void ConvertSeqAct_Log_objComments(IMEPackage package)
        {
            PackageCache cache = new PackageCache(); // So we don't have to open file like 50 times
            foreach (var seqLog in package.Exports.Where(x => x.ClassName == "SeqAct_Log").ToList())
            {
                bool alreadyAdded = false; // Has this objcomment been added already (has this been run on this file already?)
                var owningSequence = seqLog.GetProperty<ObjectProperty>("ParentSequence").ResolveToEntry(seqLog.FileRef) as ExportEntry;
                var existingObjComment = seqLog.GetProperty<ArrayProperty<StrProperty>>("m_aObjComment");
                if (existingObjComment == null || existingObjComment.IsEmpty())
                    continue; // Nothing to do with this object

                var objComment = existingObjComment[0];

                // How this works is we just delete all existing var links and re-attach the originals.
                // This is simpler than trying to figure out what needs to be done to add links and such

                var existingVarLinks = SeqTools.GetVariableLinksOfNode(seqLog);
                var existingOutLinks = SeqTools.GetOutboundLinksOfNode(seqLog);

                // First we check if this has already been done so we don't add duplicates
                var strVarLink = existingVarLinks.FirstOrDefault(x => x.LinkDesc == "String");
                if (strVarLink != null)
                {
                    // Check it
                    foreach (var strNode in strVarLink.LinkedNodes.OfType<ExportEntry>())
                    {
                        var strValue = strNode.GetProperty<StrProperty>("StrValue");
                        if (objComment.Equals(strValue))
                        {
                            alreadyAdded = true;
                            break;
                        }
                    }
                }

                if (alreadyAdded)
                    continue;

                // Not a duplicate so we need to replace and add ours
                var newProps = SequenceObjectCreator.GetSequenceObjectDefaults(seqLog.FileRef, seqLog.ClassName, seqLog.FileRef.Game, cache);
                newProps.AddOrReplaceProp(existingObjComment);

                // Reattach existing var links, replacing ones of same name
                var newVarLinks = SeqTools.GetVariableLinks(newProps, seqLog.FileRef);
                for (int i = 0; i < newVarLinks.Count; i++)
                {
                    var existingLink = existingVarLinks.FirstOrDefault(x => x.LinkDesc == newVarLinks[i].LinkDesc);
                    if (existingLink != null)
                    {
                        newVarLinks[i] = existingLink; // Replace
                    }
                }

                SeqTools.WriteVariableLinksToProperties(newVarLinks, newProps);
                SeqTools.WriteOutboundLinksToProperties(existingOutLinks, newProps);

                seqLog.WriteProperties(newProps); // Write it out as ObjectCreator doesn't use propcollection

                // Create our strObject
                var newStrNode = SequenceObjectCreator.CreateSequenceObject(seqLog.FileRef, "SeqVar_String", seqLog.FileRef.Game, cache);
                newStrNode.WriteProperty(new StrProperty(objComment, "StrValue"));
                KismetHelper.AddObjectToSequence(newStrNode, owningSequence);
                KismetHelper.CreateVariableLink(seqLog, "String", newStrNode);
            }
        }
    }
}
