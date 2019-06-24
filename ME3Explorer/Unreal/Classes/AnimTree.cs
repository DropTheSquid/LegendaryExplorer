﻿//This class was generated by ME3Explorer
//Author: Warranty Voider
//URL: http://sourceforge.net/projects/me3explorer/
//URL: http://me3explorer.freeforums.org/
//URL: http://www.facebook.com/pages/Creating-new-end-for-Mass-Effect-3/145902408865659
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using ME3Explorer.Unreal;
using ME3Explorer.Packages;

namespace ME3Explorer.Unreal.Classes
{
    public class AnimTree
    {
        #region Unreal Props

        //Float Properties

        public float NodeTotalWeight;

        //Array Properties

        public List<AnimGroupEntry> AnimGroups;
        public List<string> ComposePrePassBoneNames;
        public List<SkelControlListEntry> SkelControlLists;
        public List<ChildrenEntry> Children;

        public struct ChildrenEntry
        {
            public string Name;
            public float Weight;
            public int Anim;
            public bool bMirrorSkeleton;
            public bool bIsAdditive;
        }
        public struct SkelControlListEntry
        {
            public string BoneName;
            public int ControlHead;
        }
        public struct AnimGroupEntry
        {
            public string GroupName;
            public float RateScale;
            public float SynchPctPosition;
        }

        #endregion
        
        public IExportEntry Export;
        public IMEPackage pcc;
        public byte[] data;

        public AnimTree(IExportEntry export)
        {
            pcc = export.FileRef;
            Export = export;
            data = export.Data;

            PropertyCollection props = export.GetProperties();
            NodeTotalWeight = props.GetPropOrDefault<FloatProperty>("NodeTotalWeight").Value;
            ComposePrePassBoneNames = props.GetPropOrDefault<ArrayProperty<NameProperty>>("ComposePrePassBoneNames").Select(n => n.Value.InstancedString).ToList();
            AnimGroups = props.GetPropOrDefault<ArrayProperty<StructProperty>>("AnimGroups").Select(prop => new AnimGroupEntry
            {
                GroupName = prop.GetPropOrDefault<NameProperty>("GroupName").Value.InstancedString,
                RateScale = prop.GetPropOrDefault<FloatProperty>("RateScale").Value,
                SynchPctPosition = prop.GetPropOrDefault<FloatProperty>("SynchPctPosition").Value
            }).ToList();
            SkelControlLists = props.GetPropOrDefault<ArrayProperty<StructProperty>>("SkelControlLists").Select(prop => new SkelControlListEntry
            {
                BoneName = prop.GetPropOrDefault<NameProperty>("BoneName").Value.InstancedString,
                ControlHead = prop.GetPropOrDefault<ObjectProperty>("ControlHead").Value
            }).ToList();
            Children = props.GetPropOrDefault<ArrayProperty<StructProperty>>("Children").Select(prop => new ChildrenEntry
            {
                Name = prop.GetPropOrDefault<NameProperty>("Name").Value.InstancedString,
                Weight = prop.GetPropOrDefault<FloatProperty>("Weight").Value,
                Anim = prop.GetPropOrDefault<ObjectProperty>("Anim").Value,
                bIsAdditive = prop.GetPropOrDefault<BoolProperty>("bIsAdditive").Value,
                bMirrorSkeleton = prop.GetPropOrDefault<BoolProperty>("bMirrorSkeleton").Value
            }).ToList();
        }

        public TreeNode ToTree()
        {
            TreeNode res = new TreeNode($"AnimTree : {Export.ObjectName}(#{Export.UIndex})");
            res.Nodes.Add("NodeTotalWeight : " + NodeTotalWeight);
            res.Nodes.Add(AnimGroupsToTree());
            res.Nodes.Add(PrePassBoneNamesToTree());
            res.Nodes.Add(SkelControlListsToTree());
            res.Nodes.Add(ChildrenToTree());
            return res;
        }

        public TreeNode AnimGroupsToTree()
        {
            TreeNode res = new TreeNode("Animation Groups");
            for (int i = 0; i < AnimGroups.Count; i++)
            {
                TreeNode t = new TreeNode(i.ToString());
                t.Nodes.Add("Group Name : " + AnimGroups[i].GroupName);
                t.Nodes.Add("Rate Scale : " + AnimGroups[i].RateScale);
                t.Nodes.Add("SynchPctPosition : " + AnimGroups[i].SynchPctPosition);
                res.Nodes.Add(t);
            }
            return res;
        }

        public TreeNode PrePassBoneNamesToTree()
        {
            TreeNode res = new TreeNode("Compose Pre Pass Bone Names");
            for (int i = 0; i < ComposePrePassBoneNames.Count; i++)
                res.Nodes.Add(i + " : " + ComposePrePassBoneNames[i]);
            return res;
        }

        public TreeNode SkelControlListsToTree()
        {
            TreeNode res = new TreeNode("Skel Control Lists");
            for (int i = 0; i < SkelControlLists.Count; i++)
            {
                TreeNode t = new TreeNode(i.ToString());
                t.Nodes.Add("Bone Name : " + SkelControlLists[i].BoneName);
                t.Nodes.Add("Control Head : " + SkelControlLists[i].ControlHead);
                res.Nodes.Add(t);
            }
            return res;
        }

        public TreeNode ChildrenToTree()
        {
            TreeNode res = new TreeNode("Children");
            for (int i = 0; i < Children.Count; i++)
            {
                int idx = Children[i].Anim;
                TreeNode t = new TreeNode(i.ToString());
                t.Nodes.Add("Name : " + Children[i].Name);
                t.Nodes.Add("Weight : " + Children[i].Weight);
                t.Nodes.Add("Anim : " + Children[i].Anim);
                if (pcc.isUExport(idx))
                    switch (pcc.getUExport(idx).ClassName)
                    {
                        case "AnimNodeSlot":
                            AnimNodeSlot ans = new AnimNodeSlot(pcc.getUExport(idx));
                            t.Nodes.Add(ans.ToTree());
                            break;
                    }
                t.Nodes.Add("bIsMirrorSkeleton : " + Children[i].bMirrorSkeleton);
                t.Nodes.Add("bIsAdditive : " + Children[i].bIsAdditive);
                res.Nodes.Add(t);
            }
            return res;
        }

    }
}