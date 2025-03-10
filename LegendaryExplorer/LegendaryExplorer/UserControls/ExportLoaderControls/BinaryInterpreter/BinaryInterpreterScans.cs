﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using LegendaryExplorer.Misc;
using LegendaryExplorer.SharedUI.Interfaces;
using LegendaryExplorerCore.Gammtek.IO;
using LegendaryExplorerCore.Gammtek.Extensions;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Unreal.Classes;
using static LegendaryExplorer.Tools.TlkManagerNS.TLKManagerWPF;
using Newtonsoft.Json;

namespace LegendaryExplorer.UserControls.ExportLoaderControls
{
    public partial class BinaryInterpreterWPF
    {
        private BinInterpNode MakeStringNode(EndianReader bin, string nodeName)
        {
            int pos = (int)bin.Position;
            int strLen = bin.ReadInt32();
            string str;
            if (Pcc.Game is MEGame.ME3 or MEGame.LE3)
            {
                strLen *= -2;
                str = bin.BaseStream.ReadStringUnicodeNull(strLen);
            }
            else
            {
                str = bin.BaseStream.ReadStringLatin1Null(strLen);
            }
            return new BinInterpNode(pos, $"{nodeName}: {str}", NodeType.StructLeafStr) { Length = strLen + 4 };
        }

        private BinInterpNode MakeStringUTF8Node(EndianReader bin, string nodeName)
        {
            int pos = (int)bin.Position;
            int strLen = bin.ReadInt32();
            string str = bin.BaseStream.ReadStringUtf8(strLen);
            return new BinInterpNode(pos, $"{nodeName}: {str}", NodeType.StructLeafStr) { Length = strLen + 4 };
        }

        private BinInterpNode ReadMaterialUniformExpression(EndianReader bin, string prefix = "")
        {
            NameReference expressionType = bin.ReadNameReference(Pcc);
            var node = new BinInterpNode(bin.Position - 8, $"{prefix}{(string.IsNullOrEmpty(prefix) ? "" : ": ")}{expressionType.Instanced}");

            switch (expressionType.Name)
            {
                case "FMaterialUniformExpressionAbs":
                case "FMaterialUniformExpressionCeil":
                case "FMaterialUniformExpressionFloor":
                case "FMaterialUniformExpressionFrac":
                case "FMaterialUniformExpressionPeriodic":
                case "FMaterialUniformExpressionSquareRoot":
                    node.Items.Add(ReadMaterialUniformExpression(bin, "X"));
                    break;
                case "FMaterialUniformExpressionAppendVector":
                    node.Items.Add(ReadMaterialUniformExpression(bin, "A"));
                    node.Items.Add(ReadMaterialUniformExpression(bin, "B"));
                    node.Items.Add(MakeUInt32Node(bin, "NumComponentsA:"));
                    break;
                case "FMaterialUniformExpressionClamp":
                    node.Items.Add(ReadMaterialUniformExpression(bin, "Input"));
                    node.Items.Add(ReadMaterialUniformExpression(bin, "Min"));
                    node.Items.Add(ReadMaterialUniformExpression(bin, "Max"));
                    break;
                case "FMaterialUniformExpressionConstant":
                    node.Items.Add(MakeFloatNode(bin, "R"));
                    node.Items.Add(MakeFloatNode(bin, "G"));
                    node.Items.Add(MakeFloatNode(bin, "B"));
                    node.Items.Add(MakeFloatNode(bin, "A"));
                    node.Items.Add(MakeByteNode(bin, "ValueType"));
                    break;
                case "FMaterialUniformExpressionFmod":
                case "FMaterialUniformExpressionMax":
                case "FMaterialUniformExpressionMin":
                    node.Items.Add(ReadMaterialUniformExpression(bin, "A"));
                    node.Items.Add(ReadMaterialUniformExpression(bin, "B"));
                    break;
                case "FMaterialUniformExpressionFoldedMath":
                    node.Items.Add(ReadMaterialUniformExpression(bin, "A"));
                    node.Items.Add(ReadMaterialUniformExpression(bin, "B"));
                    node.Items.Add(new BinInterpNode(bin.Position, $"Op: {(EFoldedMathOperation)bin.ReadByte()}"));
                    break;
                case "FMaterialUniformExpressionRealTime":
                    //intentionally left blank. outputs current real-time, has no parameters
                    break;
                case "FMaterialUniformExpressionScalarParameter":
                    node.Items.Add(new BinInterpNode(bin.Position, $"ParameterName: {bin.ReadNameReference(Pcc).Instanced}"));
                    node.Items.Add(MakeFloatNode(bin, "DefaultValue"));
                    break;
                case "FMaterialUniformExpressionSine":
                    node.Items.Add(ReadMaterialUniformExpression(bin, "X"));
                    node.Items.Add(MakeBoolIntNode(bin, "bIsCosine"));
                    break;
                case "FMaterialUniformExpressionTexture":
                case "FMaterialUniformExpressionFlipBookTextureParameter":
                    if (Pcc.Game >= MEGame.ME3)
                    {
                        node.Items.Add(MakeInt32Node(bin, "TextureIndex"));
                    }
                    else
                    {
                        node.Items.Add(MakeEntryNode(bin, "TextureIndex"));
                    }
                    break;
                case "FMaterialUniformExpressionFlipbookParameter":
                    node.Items.Add(MakeInt32Node(bin, "Index:"));
                    node.Items.Add(MakeEntryNode(bin, "TextureIndex"));
                    break;
                case "FMaterialUniformExpressionTextureParameter":
                    node.Items.Add(new BinInterpNode(bin.Position, $"ParameterName: {bin.ReadNameReference(Pcc).Instanced}"));
                    node.Items.Add(MakeInt32Node(bin, "TextureIndex"));
                    break;
                case "FMaterialUniformExpressionTime":
                    //intentionally left blank. outputs current scene time, has no parameters
                    break;
                case "FMaterialUniformExpressionVectorParameter":
                    node.Items.Add(new BinInterpNode(bin.Position, $"ParameterName: {bin.ReadNameReference(Pcc).Instanced}"));
                    node.Items.Add(MakeFloatNode(bin, "Default R"));
                    node.Items.Add(MakeFloatNode(bin, "Default G"));
                    node.Items.Add(MakeFloatNode(bin, "Default B"));
                    node.Items.Add(MakeFloatNode(bin, "Default A"));
                    break;
                case "FMaterialUniformExpressionFractionOfEffectEnabled":
                    //Not sure what it does, but it doesn't seem to have any parameters
                    break;
                default:
                    throw new ArgumentException(expressionType.Instanced);
            }

            return node;
        }

        enum EFoldedMathOperation : byte
        {
            Add,
            Sub,
            Mul,
            Div,
            Dot
        }

        private List<ITreeItem> StartStaticMeshComponentScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);

                bool bLoadVertexColorData;
                uint numVertices;

                int lodDataCount = bin.ReadInt32();
                subnodes.Add(new BinInterpNode(bin.Position - 4, $"LODData count: {lodDataCount}"));
                subnodes.AddRange(ReadList(lodDataCount, i =>
                {
                    BinInterpNode node = new BinInterpNode(bin.Position, $"LODData {i}")
                    {
                        IsExpanded = true
                    };
                    node.Items.Add(new BinInterpNode(bin.Position, $"ShadowMaps ({bin.ReadInt32()})")
                    {
                        Items = ReadList(bin.Skip(-4).ReadInt32(), j => MakeEntryNode(bin, $"{j}"))
                    });
                    node.Items.Add(new BinInterpNode(bin.Position, $"ShadowVertexBuffers ({bin.ReadInt32()})")
                    {
                        Items = ReadList(bin.Skip(-4).ReadInt32(), j => MakeEntryNode(bin, $"{j}"))
                    });
                    node.Items.Add(MakeLightMapNode(bin));
                    node.Items.Add(ListInitHelper.ConditionalAdd(Pcc.Game >= MEGame.ME3, () => new List<ITreeItem>
                    {
                        new BinInterpNode(bin.Position, $"bLoadVertexColorData ({bLoadVertexColorData = bin.ReadBoolByte()})"),
                        ListInitHelper.ConditionalAdd(bLoadVertexColorData, () => new ITreeItem[]
                        {
                            new BinInterpNode(bin.Position, "OverrideVertexColors ")
                            {
                                Items =
                                {
                                    MakeUInt32Node(bin, "Stride:"),
                                    new BinInterpNode(bin.Position, $"NumVertices: {numVertices = bin.ReadUInt32()}"),
                                    ListInitHelper.ConditionalAdd(numVertices > 0, () => new ITreeItem[]
                                    {
                                        MakeInt32Node(bin, "FColor size"),
                                        new BinInterpNode(bin.Position, $"VertexData ({bin.ReadInt32()})")
                                        {
                                            Items = ReadList(bin.Skip(-4).ReadInt32(), j => MakeColorNode(bin, $"{j}"))
                                        },
                                    }),
                                }
                            }
                        })
                    }));
                    return node;
                }));

                binarystart = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartFluidSurfaceComponentScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);

                if (Pcc.Game == MEGame.ME3)
                {
                    subnodes.Add(MakeLightMapNode(bin));
                }

                binarystart = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartForceFeedbackWaveformScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            if (CurrentLoadedExport.Game < MEGame.ME3)
            {
                try
                {
                    var bin = new EndianReader(data) { Endian = CurrentLoadedExport.FileRef.Endian };
                    bin.JumpTo(binarystart);

                    subnodes.Add(MakeBoolIntNode(bin, "bIsLooping"));
                    subnodes.Add(MakeArrayNode(bin, "Samples", i => new BinInterpNode(bin.Position, $"Sample #{i}")
                    {
                        IsExpanded = true,
                        Items =
                        {
                            MakeByteNode(bin, "Left amplitude"),
                            MakeByteNode(bin, "Right amplitude"),
                            MakeByteNode(bin, "Left function"),
                            MakeByteNode(bin, "Right function"),
                            MakeFloatNode(bin, "Duration")
                        }
                    }, true));

                    binarystart = (int)bin.Position;
                }
                catch (Exception ex)
                {
                    subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
                }
            }

            return subnodes;
        }

        private List<ITreeItem> StartTerrainComponentScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);
                bool bIsLeaf;
                subnodes.Add(MakeArrayNode(bin, "CollisionVertices", i => MakeVectorNode(bin, $"{i}")));
                subnodes.Add(new BinInterpNode(bin.Position, "BVTree")
                {
                    IsExpanded = true,
                    Items =
                    {
                        MakeArrayNode(bin, "Nodes", i => new BinInterpNode(bin.Position, $"{i}")
                        {
                            Items =
                            {
                                MakeBoxNode(bin, "BoundingVolume"),
                                new BinInterpNode(bin.Position, $"bIsLeaf: {bIsLeaf = bin.ReadBoolInt()}"),
                                ListInitHelper.ConditionalAdd(bIsLeaf, () => new ITreeItem[]
                                {
                                    MakeUInt16Node(bin, "XPos"),
                                    MakeUInt16Node(bin, "YPos"),
                                    MakeUInt16Node(bin, "XSize"),
                                    MakeUInt16Node(bin, "YSize"),
                                }, () => new ITreeItem[]
                                {
                                    MakeUInt16Node(bin, "NodeIndex[0]"),
                                    MakeUInt16Node(bin, "NodeIndex[1]"),
                                    MakeUInt16Node(bin, "NodeIndex[2]"),
                                    MakeUInt16Node(bin, "NodeIndex[3]"),
                                }),
                                MakeFloatNodeConditional(bin, "Unknown float", CurrentLoadedExport.Game != MEGame.UDK),
                            }
                        })
                    }
                });
                subnodes.Add(MakeArrayNode(bin, "PatchBounds", i => new BinInterpNode(bin.Position, $"{i}")
                {
                    Items =
                    {
                        MakeFloatNode(bin, "MinHeight"),
                        MakeFloatNode(bin, "MaxHeight"),
                        MakeFloatNode(bin, "MaxDisplacement"),
                    }
                }));
                subnodes.Add(MakeLightMapNode(bin));

                binarystart = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartTerrainScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var materialMapFile = Path.Combine(AppDirectories.ObjectDatabasesFolder, $"{CurrentLoadedExport.Game}MaterialMap.json");
                Dictionary<Guid, string> materialGuidMap = null;
                if (File.Exists(materialMapFile))
                {
                    materialGuidMap = JsonConvert.DeserializeObject<Dictionary<Guid, string>>(File.ReadAllText(materialMapFile));
                }
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);

                subnodes.Add(MakeArrayNode(bin, "Heights", i => MakeUInt16Node(bin, $"{i}")));
                subnodes.Add(MakeArrayNode(bin, "InfoData", i => new BinInterpNode(bin.Position, $"{i}: {(EInfoFlags)bin.ReadByte()}")));
                subnodes.Add(MakeArrayNode(bin, "AlphaMaps", i => MakeArrayNode(bin, $"{i}: Data", j => new BinInterpNode(bin.Position, $"{j}: {bin.ReadByte()}"))));
                subnodes.Add(MakeArrayNode(bin, "WeightedTextureMaps", i => MakeEntryNode(bin, $"{i}")));
                for (int k = Pcc.Game is MEGame.ME1 or MEGame.UDK ? 1 : 2; k > 0; k--)
                {
                    subnodes.Add(MakeArrayNode(bin, "CachedTerrainMaterials", i =>
                    {
                        var node = MakeMaterialResourceNode(bin, $"{i}", materialGuidMap);

                        node.Items.Add(MakeEntryNode(bin, "Terrain"));
                        node.Items.Add(new BinInterpNode(bin.Position, "Mask")
                        {
                            IsExpanded = true,
                            Items =
                            {
                                MakeInt32Node(bin, "NumBits"),
                                new BinInterpNode(bin.Position, $"BitMask: {Convert.ToString(bin.ReadInt64(), 2).PadLeft(64, '0')}")
                            }
                        });
                        node.Items.Add(MakeArrayNode(bin, "MaterialIds", j => MakeMaterialGuidNode(bin, $"{j}", materialGuidMap), true));
                        if (Pcc.Game >= MEGame.ME3)
                        {
                            node.Items.Add(MakeGuidNode(bin, "LightingGuid"));
                        }

                        if (Pcc.Game == MEGame.UDK)
                        {
                            node.Items.Add(MakeBoolIntNode(bin, "bEnableSpecular"));
                        }
                        return node;
                    }));
                }
                if (Pcc.Game != MEGame.ME1 && Pcc.Game != MEGame.UDK)
                {
                    subnodes.Add(MakeArrayNode(bin, "CachedDisplacements", i => new BinInterpNode(bin.Position, $"{i}: {bin.ReadByte()}")));
                    subnodes.Add(MakeFloatNode(bin, "MaxCollisionDisplacement"));
                }

                binarystart = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        [Flags]
        enum EInfoFlags : byte
        {
            TID_Visibility_Off = 1,
            TID_OrientationFlip = 2,
            TID_Unreachable = 4,
            TID_Locked = 8,
        }

        private BinInterpNode MakeLightMapNode(EndianReader bin, List<(int, int)> lightmapChunksToRemove = null)
        {
            ELightMapType lightMapType;
            int bulkSerializeElementCount;
            int bulkSerializeDataSize;

            var retvalue = new BinInterpNode(bin.Position, "LightMap ")
            {
                IsExpanded = true,
                Items =
                {
                    new BinInterpNode(bin.Position, $"LightMapType: {lightMapType = (ELightMapType) bin.ReadInt32()}"),
                    ListInitHelper.ConditionalAdd(lightMapType != ELightMapType.LMT_None, () =>
                    {
                        //chunk starts at 0 - postion of LM type
                        var chunk = ((int)bin.Position - 4,0);
                        var tree = new List<ITreeItem>
                        {
                            new BinInterpNode(bin.Position, $"LightGuids ({bin.ReadInt32()})")
                            {
                                Items = ReadList(bin.Skip(-4).ReadInt32(), j => new BinInterpNode(bin.Position, $"{j}: {bin.ReadGuid()}"))
                            },
                            ListInitHelper.ConditionalAdd(lightMapType == ELightMapType.LMT_1D, () => new ITreeItem[]
                            {
                                MakeEntryNode(bin, "Owner"),
                                MakeUInt32Node(bin, "BulkDataFlags:"),
                                new BinInterpNode(bin.Position, $"ElementCount: {bulkSerializeElementCount = bin.ReadInt32()}"),
                                new BinInterpNode(bin.Position, $"BulkDataSizeOnDisk: {bulkSerializeDataSize = bin.ReadInt32()}"),
                                MakeInt32Node(bin, "BulkDataOffsetInFile"),
                                new BinInterpNode(bin.Position, $"DirectionalSamples: ({bulkSerializeElementCount})")
                                {
                                    Items = ReadList(bulkSerializeElementCount, j => new BinInterpNode(bin.Position, $"{j}")
                                    {
                                        Items = ReadList(bulkSerializeDataSize / bulkSerializeElementCount / 4, k => new BinInterpNode(bin.Position,
                                            $"(B: {bin.ReadByte()}, G: {bin.ReadByte()}, R: {bin.ReadByte()}, A: {bin.ReadByte()})"))
                                    })
                                },
                                MakeVectorNode(bin, "ScaleVector 1"),
                                MakeVectorNode(bin, "ScaleVector 2"),
                                MakeVectorNode(bin, "ScaleVector 3"),
                                Pcc.Game < MEGame.ME3 ? MakeVectorNode(bin, "ScaleVector 4") : null,
                                MakeUInt32Node(bin, "BulkDataFlags:"),
                                new BinInterpNode(bin.Position, $"ElementCount: {bulkSerializeElementCount = bin.ReadInt32()}"),
                                new BinInterpNode(bin.Position, $"BulkDataSizeOnDisk: {bulkSerializeDataSize = bin.ReadInt32()}"),
                                MakeInt32Node(bin, "BulkDataOffsetInFile"),
                                new BinInterpNode(bin.Position, $"SimpleSamples: ({bulkSerializeElementCount})")
                                {
                                    Items = ReadList(bulkSerializeElementCount, j => new BinInterpNode(bin.Position, $"{j}")
                                    {
                                        Items = ReadList(bulkSerializeDataSize / bulkSerializeElementCount / 4, k => new BinInterpNode(bin.Position,
                                            $"(B: {bin.ReadByte()}, G: {bin.ReadByte()}, R: {bin.ReadByte()}, A: {bin.ReadByte()})"))
                                    })
                                },
                            }.NonNull()),
                            ListInitHelper.ConditionalAdd(lightMapType == ELightMapType.LMT_2D, () => new List<ITreeItem>
                            {
                                MakeEntryNode(bin, "Texture 1"),
                                MakeVectorNodeEditable(bin, "ScaleVector 1", true),
                                MakeEntryNode(bin, "Texture 2"),
                                MakeVectorNodeEditable(bin, "ScaleVector 2", true),
                                MakeEntryNode(bin, "Texture 3"),
                                MakeVectorNodeEditable(bin, "ScaleVector 3", true),
                                ListInitHelper.ConditionalAdd(Pcc.Game < MEGame.ME3, () => new ITreeItem[]
                                {
                                    MakeEntryNode(bin, "Texture 4"),
                                    MakeVectorNodeEditable(bin, "ScaleVector 4", true),
                                }),
                                MakeVector2DNodeEditable(bin, "CoordinateScale", true),
                                MakeVector2DNodeEditable(bin, "CoordinateBias", true)
                            }),
                            ListInitHelper.ConditionalAdd(lightMapType == ELightMapType.LMT_3, () => new ITreeItem[]
                            {
                                MakeInt32Node(bin, "Unknown"),
                                MakeUInt32Node(bin, "BulkDataFlags:"),
                                new BinInterpNode(bin.Position, $"ElementCount: {bulkSerializeElementCount = bin.ReadInt32()}"),
                                new BinInterpNode(bin.Position, $"BulkDataSizeOnDisk: {bulkSerializeDataSize = bin.ReadInt32()}"),
                                MakeInt32Node(bin, "BulkDataOffsetInFile"),
                                new BinInterpNode(bin.Position, $"DirectionalSamples?: ({bulkSerializeElementCount})")
                                {
                                    Items = ReadList(bulkSerializeElementCount, j => new BinInterpNode(bin.Position, $"{j}")
                                    {
                                        Items = ReadList(bulkSerializeDataSize / bulkSerializeElementCount / 4, k => new BinInterpNode(bin.Position,
                                            $"(B: {bin.ReadByte()}, G: {bin.ReadByte()}, R: {bin.ReadByte()}, A: {bin.ReadByte()})"))
                                    })
                                },
                                MakeVectorNode(bin, "ScaleVector?"),
                                MakeVectorNode(bin, "ScaleVector?")
                            }),
                            ListInitHelper.ConditionalAdd(lightMapType == ELightMapType.LMT_4 || lightMapType == ELightMapType.LMT_6, () => new List<ITreeItem>
                            {
                                MakeEntryNode(bin, "Texture 1"),
                                new ListInitHelper.InitCollection<ITreeItem>(ReadList(8, j => MakeFloatNode(bin, "Unknown float"))),
                                MakeEntryNode(bin, "Texture 2"),
                                new ListInitHelper.InitCollection<ITreeItem>(ReadList(8, j => MakeFloatNode(bin, "Unknown float"))),
                                MakeEntryNode(bin, "Texture 3"),
                                new ListInitHelper.InitCollection<ITreeItem>(ReadList(8, j => MakeFloatNode(bin, "Unknown float"))),
                                new ListInitHelper.InitCollection<ITreeItem>(ReadList(4, j => MakeFloatNode(bin, "Unknown float"))),
                            }),
                            ListInitHelper.ConditionalAdd(lightMapType == ELightMapType.LMT_5, () => new ITreeItem[]
                            {
                                MakeInt32Node(bin, "Unknown"),
                                MakeUInt32Node(bin, "BulkDataFlags:"),
                                new BinInterpNode(bin.Position, $"ElementCount: {bulkSerializeElementCount = bin.ReadInt32()}"),
                                new BinInterpNode(bin.Position, $"BulkDataSizeOnDisk: {bulkSerializeDataSize = bin.ReadInt32()}"),
                                MakeInt32Node(bin, "BulkDataOffsetInFile"),
                                new BinInterpNode(bin.Position, $"SimpleSamples?: ({bulkSerializeElementCount})")
                                {
                                    Items = ReadList(bulkSerializeElementCount, j => new BinInterpNode(bin.Position, $"{j}")
                                    {
                                        Items = ReadList(bulkSerializeDataSize / bulkSerializeElementCount / 4, k => new BinInterpNode(bin.Position,
                                            $"(B: {bin.ReadByte()}, G: {bin.ReadByte()}, R: {bin.ReadByte()}, A: {bin.ReadByte()})"))
                                    })
                                },
                                MakeVectorNode(bin, "ScaleVector?")
                            }),
                        };
                        chunk.Item2 = (int)bin.Position;
                        lightmapChunksToRemove?.Add(chunk);
                        return tree;
                    })
                }
            };

            return retvalue;
        }

        private List<ITreeItem> StartBrushComponentScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);

                int cachedConvexElementsCount;
                subnodes.Add(new BinInterpNode(bin.Position, "CachedPhysBrushData")
                {
                    IsExpanded = true,
                    Items =
                    {
                        new BinInterpNode(bin.Position, $"CachedConvexElements ({cachedConvexElementsCount = bin.ReadInt32()})")
                        {
                            Items = ReadList(cachedConvexElementsCount, j =>
                            {
                                int size;
                                var item = new BinInterpNode(bin.Position, $"{j}: ConvexElementData (size of byte: {bin.ReadInt32()}) (number of bytes: {size = bin.ReadInt32()})")
                                {
                                    Length = size + 8
                                };
                                bin.Skip(size);
                                return item;
                            })
                        }
                    }
                });

                binarystart = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartRB_BodySetupScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);

                int preCachedPhysDataCount;
                int cachedConvexElementsCount;
                subnodes.Add(new BinInterpNode(bin.Position, $"PreCachedPhysData ({preCachedPhysDataCount = bin.ReadInt32()})")
                {
                    Items = ReadList(preCachedPhysDataCount, i => new BinInterpNode(bin.Position, $"{i} CachedConvexElements ({cachedConvexElementsCount = bin.ReadInt32()})")
                    {
                        Items = ReadList(cachedConvexElementsCount, j =>
                        {
                            int size;
                            var item = new BinInterpNode(bin.Position, $"{j}: ConvexElementData (size of byte: {bin.ReadInt32()}) (number of bytes: {size = bin.ReadInt32()})")
                            {
                                Length = size + 8
                            };
                            bin.Skip(size);
                            return item;
                        })
                    })
                });

                binarystart = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartModelComponentScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
            try
            {
                int count;
                bin.JumpTo(binarystart);
                subnodes.Add(MakeEntryNode(bin, "Model"));
                subnodes.Add(MakeInt32Node(bin, "ZoneIndex"));
                subnodes.Add(new BinInterpNode(bin.Position, $"Elements ({count = bin.ReadInt32()})")
                {
                    Items = ReadList(count, i => new BinInterpNode(bin.Position, $"{i}: FModelElement")
                    {
                        Items =
                        {
                            MakeLightMapNode(bin),
                            MakeEntryNode(bin, "Component"),
                            MakeEntryNode(bin, "Material"),
                            new BinInterpNode(bin.Position, $"Nodes ({count = bin.ReadInt32()})")
                            {
                                Items = ReadList(count, j => new BinInterpNode(bin.Position, $"{j}: {bin.ReadUInt16()}"))
                            },
                            new BinInterpNode(bin.Position, $"ShadowMaps ({count = bin.ReadInt32()})")
                            {
                                Items = ReadList(count, j => MakeEntryNode(bin, $"{j}"))
                            },
                            new BinInterpNode(bin.Position, $"IrrelevantLights ({count = bin.ReadInt32()})")
                            {
                                Items = ReadList(count, j => new BinInterpNode(bin.Position, $"{j}: {bin.ReadGuid()}"))
                            }
                        }
                    })
                });
                subnodes.Add(new BinInterpNode(bin.Position, $"ComponentIndex: {bin.ReadUInt16()}"));
                subnodes.Add(new BinInterpNode(bin.Position, $"Nodes ({count = bin.ReadInt32()})")
                {
                    Items = ReadList(count, i => new BinInterpNode(bin.Position, $"{i}: {bin.ReadUInt16()}"))
                });

                binarystart = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartLightComponentScan(byte[] data, int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            if (Pcc.Game == MEGame.UDK)
            {
                return subnodes;
            }
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);

                int count;
                foreach (string propName in new[] { "InclusionConvexVolumes", "ExclusionConvexVolumes" })
                {
                    subnodes.Add(new BinInterpNode(bin.Position, $"{propName} ({count = bin.ReadInt32()})")
                    {
                        Items = ReadList(count, i => new BinInterpNode(bin.Position, $"{i}")
                        {
                            Items =
                            {
                                new BinInterpNode(bin.Position, $"Planes ({count = bin.ReadInt32()})")
                                {
                                    Items = ReadList(count, j =>
                                                         new BinInterpNode(bin.Position, $"{j}: (X: {bin.ReadSingle()}, Y: {bin.ReadSingle()}, Z: {bin.ReadSingle()}, W: {bin.ReadSingle()})"))
                                },
                                new BinInterpNode(bin.Position, $"PermutedPlanes ({count = bin.ReadInt32()})")
                                {
                                    Items = ReadList(count, j =>
                                                         new BinInterpNode(bin.Position, $"{j}: (X: {bin.ReadSingle()}, Y: {bin.ReadSingle()}, Z: {bin.ReadSingle()}, W: {bin.ReadSingle()})"))
                                }
                            }
                        })
                    });
                }
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartDominantLightScan()
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(CurrentLoadedExport.GetReadOnlyDataStream()) { Endian = CurrentLoadedExport.FileRef.Endian };

                if (Pcc.Game >= MEGame.ME3)
                {
                    int count;
                    subnodes.Add(new BinInterpNode(bin.Position, $"DominantLightShadowMap ({count = bin.ReadInt32()})")
                    {
                        Items = ReadList(count, i => new BinInterpNode(bin.Position, $"{i}: {bin.ReadUInt16()}"))
                    });
                }
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartBioPawnScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);

                int count;
                subnodes.Add(new BinInterpNode(bin.Position, $"AnimationMap? ({count = bin.ReadInt32()})")
                {
                    Items = ReadList(count, i => new BinInterpNode(bin.Position, $"{bin.ReadNameReference(Pcc)}: {entryRefString(bin)}", NodeType.StructLeafObject) { Length = 4 })
                });

                binarystart = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartPhysicsAssetInstanceScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);

                int count;
                subnodes.Add(new BinInterpNode(bin.Position, $"CollisionDisableTable ({count = bin.ReadInt32()})")
                {
                    IsExpanded = true,
                    Items = ReadList(count, i => new BinInterpNode(bin.Position, $"{i}")
                    {
                        Items =
                        {
                            MakeInt32Node(bin, "BodyAIndex"),
                            MakeInt32Node(bin, "BodyBIndex"),
                            MakeBoolIntNode(bin, "False")
                        }
                    })
                });

                binarystart = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartCookedBulkDataInfoContainerScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);

                int count;
                subnodes.Add(new BinInterpNode(bin.Position, $"Global Mip Data? ({count = bin.ReadInt32()})")
                {
                    IsExpanded = true,
                    Items = ReadList(count, i => new BinInterpNode(bin.Position, $"{bin.BaseStream.ReadStringLatin1Null(bin.ReadInt32())}")
                    {
                        Items =
                        {
                            new BinInterpNode(bin.Position, $"Storage Type: {(StorageTypes)bin.ReadInt32()}", NodeType.StructLeafInt) { Length = 4 },
                            MakeInt32Node(bin, "Uncompressed Size"),
                            MakeInt32Node(bin, "Offset"),
                            MakeInt32Node(bin, "Compressed Size"),
                        }
                    })
                });

                binarystart = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartMorphTargetScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);

                subnodes.Add(MakeArrayNode(bin, "MorphLODModels", i => new BinInterpNode(bin.Position, $"{i}")
                {
                    Items =
                    {
                        MakeArrayNode(bin, "Vertices", j => new BinInterpNode(bin.Position, $"{j}")
                        {
                            Items =
                            {
                                MakeVectorNode(bin, "PositionDelta"),
                                MakePackedNormalNode(bin, "TangentZDelta"),
                                MakeUInt16Node(bin, "SourceIdx")
                            }
                        }),
                        MakeInt32Node(bin, "NumBaseMeshVerts")
                    }
                }));
                subnodes.Add(MakeArrayNode(bin, "BoneOffsets", i => new BinInterpNode(bin.Position, $"{i}")
                {
                    Items =
                    {
                        MakeVectorNode(bin, "Offset"),
                        MakeNameNode(bin, "Bone")
                    }
                }));

                binarystart = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartSFXMorphFaceFrontEndDataSourceScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);

                subnodes.Add(MakeArrayNode(bin, "DefaultSettingsNames", i => new BinInterpNode(bin.Position, $"{i}")
                {
                    IsExpanded = true,
                    Items =
                    {
                        MakeStringNode(bin, "Name"),
                        MakeInt32Node(bin, "Index")
                    }
                }, true));

                binarystart = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartBioCreatureSoundSetScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);

                subnodes.Add(MakeArrayNode(bin, "UnkToCueMap?", i => new BinInterpNode(bin.Position, $"{i}")
                {
                    IsExpanded = true,
                    Items =
                    {
                        MakeByteNode(bin, "Unknown byte"),
                        MakeInt32Node(bin, "index into m_aAllCues?")
                    }
                }, true));

                binarystart = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartShadowMap1DScan(byte[] data, int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);
                if (Pcc.Game.IsGame3())
                {
                    subnodes.Add(new BinInterpNode(bin.Position, $"float size ({bin.ReadInt32()})"));
                }

                int sampleCount = bin.ReadInt32();
                subnodes.Add(new BinInterpNode(bin.Position - 4, $"Samples ({sampleCount})")
                {
                    Items = ReadList(sampleCount, i => MakeFloatNode(bin, $"{i}"))
                });
                subnodes.Add(new BinInterpNode(bin.Position, $"LightGuid ({bin.ReadGuid()})"));
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartPolysScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);

                int polysCount = bin.ReadInt32();
                subnodes.Add(new BinInterpNode(bin.Position - 4, $"Count: {polysCount}"));
                subnodes.Add(MakeInt32Node(bin, "Max"));
                subnodes.Add(MakeEntryNode(bin, "Owner (self)"));
                if (polysCount > 0)
                {
                    subnodes.Add(new BinInterpNode(bin.Position, $"Elements ({polysCount})")
                    {
                        Items = ReadList(polysCount, i => new BinInterpNode(bin.Position, $"{i}")
                        {
                            Items = new List<ITreeItem>
                            {
                                MakeVectorNode(bin, "Base"),
                                MakeVectorNode(bin, "Normal"),
                                MakeVectorNode(bin, "TextureU"),
                                MakeVectorNode(bin, "TextureV"),
                                new BinInterpNode(bin.Position, $"Vertices ({bin.ReadInt32()})")
                                {
                                    Items = ReadList(bin.Skip(-4).ReadInt32(), j =>
                                                         new BinInterpNode(bin.Position, $"{j}: (X: {bin.ReadSingle()}, Y: {bin.ReadSingle()}, Z: {bin.ReadSingle()})"))
                                },
                                MakeInt32Node(bin, "PolyFlags"),
                                MakeEntryNode(bin, "Actor"),
                                new BinInterpNode(bin.Position, $"ItemName: {bin.ReadNameReference(Pcc)}"),
                                MakeEntryNode(bin, "Material"),
                                MakeInt32Node(bin, "iLink"),
                                MakeInt32Node(bin, "iBrushPoly"),
                                MakeFloatNode(bin, "ShadowMapScale"),
                                MakeInt32Node(bin, "LightingChannels"),
                                ListInitHelper.ConditionalAdd(Pcc.Game >= MEGame.ME3, () => new ITreeItem[]
                                {
                                    MakeBoolIntNode(bin, "bUseTwoSidedLighting"),
                                    MakeBoolIntNode(bin, "bShadowIndirectOnly"),
                                    MakeFloatNode(bin, "FullyOccludedSamplesFraction"),
                                    MakeBoolIntNode(bin, "bUseEmissiveForStaticLighting"),
                                    MakeFloatNode(bin, "EmissiveLightFalloffExponent"),
                                    MakeFloatNode(bin, "EmissiveLightExplicitInfluenceRadius"),
                                    MakeFloatNode(bin, "EmissiveBoost"),
                                    MakeFloatNode(bin, "DiffuseBoost"),
                                    MakeFloatNode(bin, "SpecularBoost"),
                                    MakeNameNode(bin, "RulesetVariation")
                                }),
                            }
                        })
                    });
                }

                binarystart = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private string entryRefString(EndianReader bin)
        {
            int n = bin.ReadInt32();
            return $"#{n} {CurrentLoadedExport.FileRef.GetEntryString(n)}";
        }

        private List<ITreeItem> StartModelScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            //data = data.Slice(binarystart, data.Length - binarystart);
            var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
            try
            {
                //uncomment when removing slice
                bin.JumpTo(binarystart);
                subnodes.Add(MakeBoxSphereBoundsNode(bin, "Bounds"));

                subnodes.Add(MakeInt32Node(bin, "FVector Size"));
                int vectorsCount = bin.ReadInt32();
                subnodes.Add(new BinInterpNode(bin.Position - 4, $"Vectors ({vectorsCount})")
                {
                    Items = ReadList(vectorsCount, i => new BinInterpNode(bin.Position, $"{i}: (X: {bin.ReadSingle()}, Y: {bin.ReadSingle()}, Z: {bin.ReadSingle()})"))
                });

                subnodes.Add(MakeInt32Node(bin, "FVector Size"));
                int pointsCount = bin.ReadInt32();
                subnodes.Add(new BinInterpNode(bin.Position - 4, $"Points ({pointsCount})")
                {
                    Items = ReadList(pointsCount, i => new BinInterpNode(bin.Position, $"{i}: (X: {bin.ReadSingle()}, Y: {bin.ReadSingle()}, Z: {bin.ReadSingle()})"))
                });

                subnodes.Add(MakeInt32Node(bin, "FBspNode Size"));
                int nodesCount = bin.ReadInt32();
                subnodes.Add(new BinInterpNode(bin.Position - 4, $"Nodes ({nodesCount})")
                {
                    Items = ReadList(nodesCount, i => new BinInterpNode(bin.Position, $"{i}")
                    {
                        Items = new List<ITreeItem>
                        {
                            new BinInterpNode(bin.Position, $"Plane: (X: {bin.ReadSingle()}, Y: {bin.ReadSingle()}, Z: {bin.ReadSingle()}, W: {bin.ReadSingle()})"),
                            MakeInt32Node(bin, "iVertPool"),
                            MakeInt32Node(bin, "iSurf"),
                            MakeInt32Node(bin, "iVertexIndex"),
                            new BinInterpNode(bin.Position, $"ComponentIndex: {bin.ReadUInt16()}"),
                            new BinInterpNode(bin.Position, $"ComponentNodeIndex: {bin.ReadUInt16()}"),
                            MakeInt32Node(bin, "ComponentElementIndex"),
                            MakeInt32Node(bin, "iBack"),
                            MakeInt32Node(bin, "iFront"),
                            MakeInt32Node(bin, "iPlane"),
                            MakeInt32Node(bin, "iCollisionBound"),
                            new BinInterpNode(bin.Position, $"iZone[0]: {bin.ReadByte()}"),
                            new BinInterpNode(bin.Position, $"iZone[1]: {bin.ReadByte()}"),
                            new BinInterpNode(bin.Position, $"NumVertices: {bin.ReadByte()}"),
                            new BinInterpNode(bin.Position, $"NodeFlags: {bin.ReadByte()}"),
                            MakeInt32Node(bin, "iLeaf[0]"),
                            new BinInterpNode(bin.Position, $"iLeaf[1]: {bin.ReadInt32()}")
                        }
                    })
                });

                subnodes.Add(MakeEntryNode(bin, "Owner (self)"));
                int surfsCount = bin.ReadInt32();
                subnodes.Add(new BinInterpNode(bin.Position - 4, $"Surfaces ({surfsCount})")
                {
                    Items = ReadList(surfsCount, i => new BinInterpNode(bin.Position, $"{i}")
                    {
                        Items = new List<ITreeItem>
                        {
                            MakeEntryNode(bin, "Material"),
                            MakeInt32Node(bin, "PolyFlags"),
                            MakeInt32Node(bin, "pBase"),
                            MakeInt32Node(bin, "vNormal"),
                            MakeInt32Node(bin, "vTextureU"),
                            MakeInt32Node(bin, "vTextureV"),
                            MakeInt32Node(bin, "iBrushPoly"),
                            MakeEntryNode(bin, "Actor"),
                            new BinInterpNode(bin.Position, $"Plane: (X: {bin.ReadSingle()}, Y: {bin.ReadSingle()}, Z: {bin.ReadSingle()}, W: {bin.ReadSingle()})"),
                            MakeFloatNode(bin, "ShadowMapScale"),
                            MakeInt32Node(bin, "LightingChannels(Bitfield)"),
                            Pcc.Game >= MEGame.ME3 ? new BinInterpNode(bin.Position, $"iLightmassIndex: {bin.ReadInt32()}") : null,
                        }.NonNull().ToList()
                    })
                });

                int fVertSize = bin.ReadInt32();
                subnodes.Add(new BinInterpNode(bin.Position - 4, $"FVert Size: {fVertSize}"));
                int vertsCount = bin.ReadInt32();
                subnodes.Add(new BinInterpNode(bin.Position - 4, $"Verts ({vertsCount})")
                {
                    Items = ReadList(vertsCount, i => new BinInterpNode(bin.Position, $"{i}")
                    {
                        Items = new List<ITreeItem>
                        {
                            MakeInt32Node(bin, "pVertex"),
                            MakeInt32Node(bin, "iSide"),
                            MakeVector2DNode(bin, "ShadowTexCoord"),
                            fVertSize == 24 ? MakeVector2DNode(bin, "BackfaceShadowTexCoord") : null
                        }.NonNull().ToList()
                    })
                });

                subnodes.Add(MakeInt32Node(bin, "NumSharedSides"));
                int numZones = bin.ReadInt32();
                subnodes.Add(new BinInterpNode(bin.Position - 4, $"NumZones: {numZones}")
                {
                    Items = ReadList(numZones, i => new BinInterpNode(bin.Position, $"Zone {i}")
                    {
                        Items = new List<ITreeItem>
                        {
                            MakeEntryNode(bin, "ZoneActor"),
                            MakeFloatNode(bin, "LastRenderTime"),
                            new BinInterpNode(bin.Position, $"Connectivity: {Convert.ToString(bin.ReadInt64(), 2).PadLeft(64, '0')}"),
                            new BinInterpNode(bin.Position, $"Visibility: {Convert.ToString(bin.ReadInt64(), 2).PadLeft(64, '0')}"),
                        }
                    })
                });

                subnodes.Add(MakeEntryNode(bin, "Polys"));
                subnodes.Add(MakeInt32Node(bin, "integer Size"));
                int leafHullsCount = bin.ReadInt32();
                subnodes.Add(new BinInterpNode(bin.Position - 4, $"LeafHulls ({leafHullsCount})")
                {
                    Items = ReadList(leafHullsCount, i => MakeInt32Node(bin, $"{i}"))
                });

                subnodes.Add(MakeInt32Node(bin, "FLeaf Size"));
                int leavesCount = bin.ReadInt32();
                subnodes.Add(new BinInterpNode(bin.Position - 4, $"Leaves ({leavesCount})")
                {
                    Items = ReadList(leavesCount, i => MakeInt32Node(bin, $"{i}: iZone"))
                });

                subnodes.Add(MakeBoolIntNode(bin, "RootOutside"));
                subnodes.Add(MakeBoolIntNode(bin, "Linked"));

                subnodes.Add(MakeInt32Node(bin, "integer Size"));
                int portalNodesCount = bin.ReadInt32();
                subnodes.Add(new BinInterpNode(bin.Position - 4, $"PortalNodes ({portalNodesCount})")
                {
                    Items = ReadList(portalNodesCount, i => MakeInt32Node(bin, $"{i}"))
                });

                if (Pcc.Game < MEGame.UDK)
                {
                    subnodes.Add(MakeInt32Node(bin, "FMeshEdge Size"));
                    int legacyedgesCount = bin.ReadInt32();
                    subnodes.Add(new BinInterpNode(bin.Position - 4, $"ShadowVolume? ({legacyedgesCount})")
                    {
                        Items = ReadList(legacyedgesCount, i => new BinInterpNode(bin.Position, $"MeshEdge {i}")
                        {
                            Items = new List<ITreeItem>
                            {
                                MakeInt32Node(bin, "Vertices[0]"),
                                MakeInt32Node(bin, "Vertices[1]"),
                                MakeInt32Node(bin, "Faces[0]"),
                                new BinInterpNode(bin.Position, $"Faces[1]: {bin.ReadInt32()}")
                            }
                        })
                    });
                }

                subnodes.Add(MakeUInt32Node(bin, "NumVertices:"));

                subnodes.Add(MakeInt32Node(bin, "FModelVertex Size"));
                int verticesCount = bin.ReadInt32();
                subnodes.Add(new BinInterpNode(bin.Position - 4, $"VertexBuffer Vertices({verticesCount})")
                {
                    Items = ReadList(verticesCount, i => new BinInterpNode(bin.Position, $"{i}")
                    {
                        Items = new List<ITreeItem>
                        {
                            MakeVectorNode(bin, "Position"),
                            MakePackedNormalNode(bin, "TangentX"),
                            MakePackedNormalNode(bin, "TangentZ"),
                            MakeVector2DNode(bin, "TexCoord"),
                            MakeVector2DNode(bin, "ShadowTexCoord")
                        }
                    })
                });

                if (Pcc.Game >= MEGame.ME3)
                {
                    subnodes.Add(new BinInterpNode(bin.Position, $"LightingGuid: {bin.ReadGuid()}") { Length = 16 });

                    int lightmassSettingsCount = bin.ReadInt32();
                    subnodes.Add(new BinInterpNode(bin.Position - 4, $"LightmassSettings ({lightmassSettingsCount})")
                    {
                        Items = ReadList(lightmassSettingsCount, i => new BinInterpNode(bin.Position, $"{i}")
                        {
                            Items = new List<ITreeItem>
                            {
                                MakeBoolIntNode(bin, "bUseTwoSidedLighting"),
                                MakeBoolIntNode(bin, "bShadowIndirectOnly"),
                                MakeFloatNode(bin, "FullyOccludedSamplesFraction"),
                                MakeBoolIntNode(bin, "bUseEmissiveForStaticLighting"),
                                MakeFloatNode(bin, "EmissiveLightFalloffExponent"),
                                MakeFloatNode(bin, "EmissiveLightExplicitInfluenceRadius"),
                                MakeFloatNode(bin, "EmissiveBoost"),
                                MakeFloatNode(bin, "DiffuseBoost"),
                                new BinInterpNode(bin.Position, $"SpecularBoost: {bin.ReadSingle()}")
                            }
                        })
                    });
                }

                binarystart = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }

        private List<ITreeItem> StartDecalComponentScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);

                int numStaticRecievers;
                int count;
                int fDecalVertexSize;
                var item = new BinInterpNode(bin.Position, $"StaticReceivers: {numStaticRecievers = bin.ReadInt32()}")
                {
                    IsExpanded = true
                };
                item.Items = ReadList(numStaticRecievers, i =>
                {
                    var node = new BinInterpNode(bin.Position, $"{i}");
                    try
                    {
                        node.Items.Add(MakeEntryNode(bin, "Component"));
                        node.Items.Add(new BinInterpNode(bin.Position, $"FDecalVertex Size: {fDecalVertexSize = bin.ReadInt32()}"));
                        BinInterpNode interpNode = new BinInterpNode(bin.Position, $"Vertices ({count = bin.ReadInt32()})")
                        {
                            Length = 4 + fDecalVertexSize * count
                        };
                        interpNode.Items = ReadList(count, j => new BinInterpNode(bin.Position, $"{j}")
                        {
                            Length = fDecalVertexSize,
                            Items =
                            {
                                MakeVectorNode(bin, "Position"),
                                MakePackedNormalNode(bin, "TangentX"),
                                MakePackedNormalNode(bin, "TangentZ"),
                                ListInitHelper.ConditionalAdd(Pcc.Game < MEGame.ME3, () => new ITreeItem[]
                                {
                                    MakeVector2DNode(bin, "LegacyProjectedUVs")
                                }),
                                MakeVector2DNode(bin, "LightMapCoordinate"),
                                ListInitHelper.ConditionalAdd(Pcc.Game < MEGame.ME3, () => new ITreeItem[]
                                {
                                    MakeVector2DNode(bin, "LegacyNormalTransform[0]"),
                                    MakeVector2DNode(bin, "LegacyNormalTransform[1]")
                                }),
                            }
                        });
                        node.Items.Add(interpNode);
                        node.Items.Add(MakeInt32Node(bin, "unsigned short size"));
                        node.Items.Add(new BinInterpNode(bin.Position, $"Indices ({count = bin.ReadInt32()})")
                        {
                            Length = 4 + count * 2,
                            Items = ReadList(count, j => new BinInterpNode(bin.Position, $"{j}: {bin.ReadUInt16()}") { Length = 2 })
                        });
                        node.Items.Add(MakeUInt32Node(bin, "NumTriangles"));
                        node.Items.Add(MakeLightMapNode(bin));
                        if (Pcc.Game >= MEGame.ME3)
                        {
                            node.Items.Add(new BinInterpNode(bin.Position, $"ShadowMap1D ({count = bin.ReadInt32()})")
                            {
                                Length = 4 + count * 4,
                                Items = ReadList(count, j => new BinInterpNode(bin.Position, $"{j}: {entryRefString(bin)}") { Length = 4 })
                            });
                            node.Items.Add(MakeInt32Node(bin, "Data"));
                            node.Items.Add(MakeInt32Node(bin, "InstanceIndex"));
                        }
                    }
                    catch (Exception e)
                    {
                        node.Items.Add(new BinInterpNode { Header = $"Error reading binary data: {e}" });
                    }
                    return node;
                });
                subnodes.Add(item);

                binarystart = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private static List<ITreeItem> ReadList(int count, Func<int, ITreeItem> selector)
        {
            //sanity check. if this number is too small, feel free to increase
            if (count > 5097152)
            {
                throw new Exception($"Is this actually a list? {count} seems like an incorrect count");
            }
            var list = new List<ITreeItem>();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    list.Add(selector(i));
                }
            }
            catch (Exception ex)
            {
                new BinInterpNode { Header = $"Error reading binary data: {ex}" };
            }

            return list;
        }

        private List<ITreeItem> StartWorldScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);

                subnodes.Add(MakeEntryNode(bin, "PersistentLevel"));
                if (Pcc.Game == MEGame.ME3 || Pcc.Game.IsLEGame())
                {
                    subnodes.Add(MakeEntryNode(bin, "PersistentFaceFXAnimSet"));
                }
                subnodes.AddRange(ReadList(4, i => new BinInterpNode(bin.Position, $"EditorView {i}")
                {
                    Items =
                    {
                        MakeVectorNode(bin, "CamPosition"),
                        new BinInterpNode(bin.Position, $"CamRotation: (Pitch: {bin.ReadInt32()}, Yaw: {bin.ReadInt32()}, Roll: {bin.ReadInt32()})"),
                        new BinInterpNode(bin.Position, $"CamOrthoZoom: {bin.ReadSingle()}")
                    }
                }));
                if (Pcc.Game == MEGame.UDK)
                {
                    subnodes.Add(MakeFloatNode(bin, "unkFloat"));
                }
                subnodes.Add(MakeEntryNode(bin, "Null"));
                if (Pcc.Game is MEGame.ME1 or MEGame.LE1)
                {
                    subnodes.Add(MakeEntryNode(bin, "DecalManager"));
                }

                int extraObjsCount;
                subnodes.Add(new BinInterpNode(bin.Position, $"ExtraReferencedObjects: {extraObjsCount = bin.ReadInt32()}")
                {
                    ArrayAddAlgorithm = BinInterpNode.ArrayPropertyChildAddAlgorithm.FourBytes,
                    Items = ReadList(extraObjsCount, i => new BinInterpNode(bin.Position, $"{entryRefString(bin)}", NodeType.ArrayLeafObject))
                });

                binarystart = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartStackScan(out int endPos)
        {
            endPos = 0;
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(CurrentLoadedExport.GetReadOnlyDataStream()) { Endian = CurrentLoadedExport.FileRef.Endian };

                string nodeString;
                subnodes.Add(new BinInterpNode(bin.Position, "Stack")
                {
                    IsExpanded = true,
                    Items =
                    {
                        new BinInterpNode(bin.Position, $"Node: {nodeString = entryRefString(bin)}", NodeType.StructLeafObject) {Length = 4},
                        ListInitHelper.ConditionalAddOne<ITreeItem>(Pcc.Game != MEGame.UDK, () => MakeEntryNode(bin, "StateNode")),
                        new BinInterpNode(bin.Position, $"ProbeMask: {bin.ReadUInt64():X16}"),
                        ListInitHelper.ConditionalAdd(Pcc.Game >= MEGame.ME3 || Pcc.Platform == MEPackage.GamePlatform.PS3, () => new ITreeItem[]
                        {
                            MakeUInt16Node(bin, "LatentAction")
                        }, () => new ITreeItem[]
                        {
                            MakeUInt32Node(bin, "LatentAction")
                        }),
                        MakeArrayNode(bin, "StateStack", i => new BinInterpNode(bin.Position, $"{i}")
                        {
                            Items =
                            {
                                MakeEntryNode(bin, "State"),
                                MakeEntryNode(bin, "Node"),
                                MakeInt32Node(bin, "Offset")
                            }
                        }),
                        ListInitHelper.ConditionalAdd(nodeString != "Null", () => new ITreeItem[]
                        {
                            MakeInt32Node(bin, "Offset")
                        })
                    }
                });
                endPos = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartMetaDataScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(data) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);
                subnodes.Add(MakeArrayNode(bin, "Object to Metadata Map", i =>
                {
                    var node = Pcc.Game is MEGame.UDK ? MakeEntryNode(bin, "Object") : MakeStringNode(bin, "Object");
                    node.IsExpanded = true;
                    int count = bin.ReadInt32();
                    while (count-- > 0)
                    {
                        var metadataType = bin.ReadNameReference(Pcc);
                        node.Items.Add(MakeStringNode(bin, metadataType.Instanced));
                    }
                    return node;
                }, true));
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode() { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }

        private List<ITreeItem> StartTextBufferScan(byte[] data, int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(data) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);
                subnodes.Add(MakeInt32Node(bin, "Position"));
                subnodes.Add(MakeInt32Node(bin, "Top"));
                subnodes.Add(MakeStringNode(bin, "Text"));
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode() { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }

        private List<ITreeItem> StartBioTlkFileSetScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                int offset = binarystart;
                if (data.Length > binarystart)
                {
                    int count = BitConverter.ToInt32(data, offset);
                    subnodes.Add(new BinInterpNode
                    {
                        Header = $"0x{offset:X4} Count: {count}",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    });
                    offset += 4;

                    //offset += 4;
                    //offset += 8; //skip 8

                    for (int i = 0; i < count; i++)
                    {
                        int langRef = BitConverter.ToInt32(data, offset);
                        int langTlkCount = BitConverter.ToInt32(data, offset + 8);
                        var languageNode = new BinInterpNode
                        {
                            Header = $"0x{offset:X4} {CurrentLoadedExport.FileRef.GetNameEntry(langRef)} - {langTlkCount} entries",
                            Offset = offset,
                            Tag = NodeType.StructLeafName,
                            IsExpanded = true
                        };
                        subnodes.Add(languageNode);
                        offset += 12;

                        for (int k = 0; k < langTlkCount; k++)
                        {
                            int tlkIndex = BitConverter.ToInt32(data, offset); //-1 in reader
                            languageNode.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X4} TLK #{k} export: {tlkIndex} {CurrentLoadedExport.FileRef.GetEntryString(tlkIndex)}",
                                Offset = offset,
                                Tag = NodeType.StructLeafObject
                            });
                            offset += 4;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode() { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }

        private List<ITreeItem> StartBioStateEventMapScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                int offset = binarystart;

                int eCount = BitConverter.ToInt32(data, offset);
                var EventCountNode = new BinInterpNode
                {
                    Header = $"0x{offset:X4} State Event Count: {eCount}",
                    Offset = offset,
                    Tag = NodeType.StructLeafInt
                };
                offset += 4;
                subnodes.Add(EventCountNode);

                for (int e = 0; e < eCount; e++) //EVENTS
                {
                    int iEventID = BitConverter.ToInt32(data, offset);  //EVENT ID
                    var EventIDs = new BinInterpNode
                    {
                        Header = $"0x{offset:X5} [{e}] State Transition ID: {iEventID} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    };
                    offset += 4;
                    EventCountNode.Items.Add(EventIDs);

                    int EventMapInstVer = BitConverter.ToInt32(data, offset); //Event Instance Version
                    EventIDs.Items.Add(new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Instance Version: {EventMapInstVer} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    });
                    offset += 4;

                    int nTransitions = BitConverter.ToInt32(data, offset); //Count of State Events
                    var TransitionsIDs = new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Transitions: {nTransitions} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    };
                    offset += 4;
                    EventIDs.Items.Add(TransitionsIDs);

                    for (int t = 0; t < nTransitions; t++) //TRANSITIONS
                    {
                        int transTYPE = BitConverter.ToInt32(data, offset); //Get TYPE
                        if (transTYPE == 0)  // TYPE 0 = BOOL STATE EVENT
                        {
                            offset += 8;
                            int tPlotID = BitConverter.ToInt32(data, offset);  //Get Plot
                            offset -= 8;
                            var nTransition = new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Type: {transTYPE} Transition on Bool {tPlotID}",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            };
                            offset += 4;
                            TransitionsIDs.Items.Add(nTransition);

                            int TransInstVersion = BitConverter.ToInt32(data, offset);  //Instance Version
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Instance Version: {TransInstVersion} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            tPlotID = BitConverter.ToInt32(data, offset);  //Plot
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Plot ID: {tPlotID} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            int tNewValue = BitConverter.ToInt32(data, offset);  //NewValue
                            bool bNewValue = false;
                            if (tNewValue == 1) { bNewValue = true; }
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} New Value: {tNewValue}  {bNewValue} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            int tUseParam = BitConverter.ToInt32(data, offset);  //Use Parameter bool
                            bool bUseParam = false;
                            if (tUseParam == 1) { bUseParam = true; }
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Use parameter: {tUseParam}  {bUseParam} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;
                        }
                        else if (transTYPE == 1) //TYPE 1 = CONSEQUENCE
                        {
                            var nTransition = new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Type: {transTYPE} Consequence",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            };
                            offset += 4;
                            TransitionsIDs.Items.Add(nTransition);

                            int TransInstVersion = BitConverter.ToInt32(data, offset);  //Instance Version
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Instance Version: {TransInstVersion} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            int tConsequenceParam = BitConverter.ToInt32(data, offset);  //Consequence parameter
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Consequence Parameter: {tConsequenceParam} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;
                        }
                        else if (transTYPE == 2)  // TYPE 2 = FLOAT TRANSITION
                        {
                            offset += 8;
                            int tPlotID = BitConverter.ToInt32(data, offset);  //Get Plot
                            offset -= 8;
                            var nTransition = new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Type: {transTYPE} transition on Float {tPlotID}",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            };
                            offset += 4;
                            TransitionsIDs.Items.Add(nTransition);

                            int TransInstVersion = BitConverter.ToInt32(data, offset);  //Instance Version
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Instance Version: {TransInstVersion} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            if (Pcc.Game.IsGame2())
                            {
                                int tIncrement = BitConverter.ToInt32(data, offset);  //Increment bool
                                bool bIncrement = false;
                                if (tIncrement == 1) { bIncrement = true; }
                                nTransition.Items.Add(new BinInterpNode
                                {
                                    Header = $"0x{offset:X5} Increment value: {tIncrement}  {bIncrement} ",
                                    Offset = offset,
                                    Tag = NodeType.StructLeafInt
                                });
                                offset += 4;
                            }

                            tPlotID = BitConverter.ToInt32(data, offset);  //Plot
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Plot ID: {tPlotID} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            float tNewValue = BitConverter.ToInt32(data, offset);  //NewValue
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} New Value: {tNewValue} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafFloat
                            });
                            offset += 4;

                            int tUseParam = BitConverter.ToInt32(data, offset);  //Use Parameter bool
                            bool bUseParam = false;
                            if (tUseParam == 1) { bUseParam = true; }
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Use parameter: {tUseParam}  {bUseParam} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            if (!Pcc.Game.IsGame2())
                            {
                                int tIncrement = BitConverter.ToInt32(data, offset);  //Increment bool
                                bool bIncrement = false;
                                if (tIncrement == 1) { bIncrement = true; }
                                nTransition.Items.Add(new BinInterpNode
                                {
                                    Header = $"0x{offset:X5} Increment value: {tIncrement}  {bIncrement} ",
                                    Offset = offset,
                                    Tag = NodeType.StructLeafInt
                                });
                                offset += 4;
                            }
                        }
                        else if (transTYPE == 3)  // TYPE 3 = FUNCTION
                        {
                            var nTransition = new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Type: {transTYPE} Function",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            };
                            offset += 4;
                            TransitionsIDs.Items.Add(nTransition);

                            int TransInstVersion = BitConverter.ToInt32(data, offset);  //Instance Version
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Instance Version: {TransInstVersion} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            int PackageName = BitConverter.ToInt32(data, offset);  //Package name
                            offset += 4;
                            int PackageIdx = BitConverter.ToInt32(data, offset);  //Package name idx
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Package Name: {new NameReference(CurrentLoadedExport.FileRef.GetNameEntry(PackageName), PackageIdx).Instanced}",
                                Offset = offset,
                                Tag = NodeType.StructLeafName
                            });
                            offset += 4;

                            int ClassName = BitConverter.ToInt32(data, offset);  //Class name
                            offset += 4;
                            int ClassIdx = BitConverter.ToInt32(data, offset);  //Class name idx
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Class Name: {new NameReference(CurrentLoadedExport.FileRef.GetNameEntry(ClassName), ClassIdx).Instanced}",
                                Offset = offset,
                                Tag = NodeType.StructLeafName
                            });
                            offset += 4;

                            int FunctionName = BitConverter.ToInt32(data, offset);  //Function name
                            offset += 4;
                            int FunctionIdx = BitConverter.ToInt32(data, offset);  //Function name idx
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Function Name: {new NameReference(CurrentLoadedExport.FileRef.GetNameEntry(FunctionName), FunctionIdx).Instanced}",
                                Offset = offset,
                                Tag = NodeType.StructLeafName
                            });
                            offset += 4;

                            int Parameter = BitConverter.ToInt32(data, offset);  //Parameter
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Parameter: {Parameter} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;
                        }
                        else if (transTYPE == 4)  // TYPE 4 = INT TRANSITION
                        {
                            offset += 8;
                            int tPlotID = BitConverter.ToInt32(data, offset);  //Get Plot
                            offset -= 8;
                            var nTransition = new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Type: {transTYPE} transition on INT {tPlotID}",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            };
                            offset += 4;
                            TransitionsIDs.Items.Add(nTransition);

                            int TransInstVersion = BitConverter.ToInt32(data, offset);  //Instance Version
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Instance Version: {TransInstVersion} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            tPlotID = BitConverter.ToInt32(data, offset);  //Plot
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Plot ID: {tPlotID} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            int tNewValue = BitConverter.ToInt32(data, offset);  //NewValue
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} New Value: {tNewValue} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafFloat
                            });
                            offset += 4;

                            int tUseParam = BitConverter.ToInt32(data, offset);  //Use Parameter bool
                            bool bUseParam = false;
                            if (tUseParam == 1) { bUseParam = true; }
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Use parameter: {tUseParam}  {bUseParam} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            int tIncrement = BitConverter.ToInt32(data, offset);  //Increment bool
                            bool bIncrement = false;
                            if (tIncrement == 1) { bIncrement = true; }
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Increment value: {tIncrement}  {bIncrement} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;
                        }
                        else if (transTYPE == 5)  // TYPE 5 = LOCAL BOOL
                        {
                            var nTransition = new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Type: {transTYPE} Local Bool",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            };
                            offset += 36;
                            TransitionsIDs.Items.Add(nTransition);
                        }
                        else if (transTYPE == 6)  // TYPE 6 = LOCAL FLOAT
                        {
                            var nTransition = new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Type: {transTYPE} Local Float",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            };
                            offset += 36;
                            TransitionsIDs.Items.Add(nTransition);
                        }
                        else if (transTYPE == 7)  // TYPE 7 = LOCAL INT
                        {
                            var nTransition = new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Type: {transTYPE} Local Int",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            };
                            offset += 4;
                            TransitionsIDs.Items.Add(nTransition);

                            int TransInstVersion = BitConverter.ToInt32(data, offset);  //Instance Version
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Instance Version: {TransInstVersion} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            int tObjtag = BitConverter.ToInt32(data, offset);  //Use Object tag??
                            bool bObjtag = false;
                            if (tObjtag == 1) { bObjtag = true; }
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Object Tag: {tObjtag}  {bObjtag} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            int FunctionName = BitConverter.ToInt32(data, offset);  //Function name
                            offset += 4;
                            int FunctionIdx = BitConverter.ToInt32(data, offset);  //Function name
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Function Name: {new NameReference(CurrentLoadedExport.FileRef.GetNameEntry(FunctionName), FunctionIdx).Instanced}",
                                Offset = offset,
                                Tag = NodeType.StructLeafName
                            });
                            offset += 4;

                            int TagName = BitConverter.ToInt32(data, offset);  //Object name
                            offset += 4;
                            int TagIdx = BitConverter.ToInt32(data, offset);  //Object idx
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Object Name: {new NameReference(CurrentLoadedExport.FileRef.GetNameEntry(TagName), TagIdx).Instanced}",
                                Offset = offset,
                                Tag = NodeType.StructLeafName
                            });
                            offset += 4;

                            int tUseParam = BitConverter.ToInt32(data, offset);  //Use Parameter bool
                            bool bUseParam = false;
                            if (tUseParam == 1) { bUseParam = true; }
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Use parameter: {tUseParam}  {bUseParam} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            int tNewValue = BitConverter.ToInt32(data, offset);  //NewValue
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} New Value: {tNewValue} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafFloat
                            });
                            offset += 4;
                        }
                        else if (transTYPE == 8)  // TYPE 8 = SUBSTATE
                        {
                            offset += 8;
                            int tPlotID = BitConverter.ToInt32(data, offset);  //Get Plot
                            offset -= 8;
                            var nTransition = new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Type: {transTYPE} Substate Transition on Bool {tPlotID}",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            };
                            offset += 4;
                            TransitionsIDs.Items.Add(nTransition);

                            int TransInstVersion = BitConverter.ToInt32(data, offset);  //Instance Version
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Instance Version: {TransInstVersion} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            tPlotID = BitConverter.ToInt32(data, offset);  //Plot
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Plot ID: {tPlotID} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            int tNewValue = BitConverter.ToInt32(data, offset);  //NewState Bool
                            bool bNewValue = false;
                            if (tNewValue == 1) { bNewValue = true; }
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} New State: {tNewValue}  {bNewValue}",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            int tUseParam = BitConverter.ToInt32(data, offset);  //Use Parameter bool
                            bool bUseParam = false;
                            if (tUseParam == 1) { bUseParam = true; }
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Use parameter: {tUseParam}  {bUseParam} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            int tParentType = BitConverter.ToInt32(data, offset);  //Parent OR type flag
                            bool bParentType = false;
                            string sParentType = "ALL of siblings TRUE => Parent TRUE";
                            if (tParentType == 1)
                            {
                                bParentType = true;
                                sParentType = "ANY of siblings TRUE => Parent TRUE";
                            }
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Parent OR type: {tParentType}  {bParentType} {sParentType}",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            int ParentIdx = BitConverter.ToInt32(data, offset);  //Parent Bool
                            nTransition.Items.Add(new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Parent Bool: {ParentIdx} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            });
                            offset += 4;

                            int sibCount = BitConverter.ToInt32(data, offset); //Sibling Substates
                            var SiblingIDs = new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Sibling Substates Count: {sibCount} ",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            };
                            offset += 4;
                            nTransition.Items.Add(SiblingIDs);

                            for (int s = 0; s < sibCount; s++)  //SIBLING SUBSTATE BOOLS
                            {
                                int nSibling = BitConverter.ToInt32(data, offset);
                                var nSiblings = new BinInterpNode
                                {
                                    Header = $"0x{offset:X5} Sibling: {s}  Bool: {nSibling}",
                                    Offset = offset,
                                    Tag = NodeType.StructLeafInt
                                };
                                SiblingIDs.Items.Add(nSiblings);
                                offset += 4;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode() { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }

        private List<ITreeItem> StartBioQuestMapScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            var game = CurrentLoadedExport.FileRef.Game;
            try
            {
                int offset = binarystart;

                int qCount = BitConverter.ToInt32(data, offset);
                var QuestNode = new BinInterpNode
                {
                    Header = $"0x{offset:X4} Quest Count: {qCount}",
                    Offset = offset,
                    Tag = NodeType.StructLeafInt
                };
                offset += 4;
                subnodes.Add(QuestNode);

                for (int i = 0; i < qCount; i++) //QUESTS
                {
                    int iQuestID = BitConverter.ToInt32(data, offset);  //QUEST ID
                    var QuestIDs = new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Quest ID: {iQuestID} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    };
                    offset += 4;
                    QuestNode.Items.Add(QuestIDs);

                    int instanceVersion = BitConverter.ToInt32(data, offset); //Unknown1
                    QuestIDs.Items.Add(new BinInterpNode
                    {
                        Header = $"0x{offset:X5} InstanceVersion: {instanceVersion} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    });
                    offset += 4;

                    int isMission = BitConverter.ToInt32(data, offset); //Unknown2
                    bool isMissionB = isMission == 1;
                    QuestIDs.Items.Add(new BinInterpNode
                    {
                        Header = $"0x{offset:X5} IsMission: {isMission} {isMissionB} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    });
                    offset += 4;

                    int gCount = BitConverter.ToInt32(data, offset); //Goal Count
                    var GoalsIDs = new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Goals: {gCount} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    };
                    offset += 4;
                    QuestIDs.Items.Add(GoalsIDs);

                    for (int g = 0; g < gCount; g++) //GOALS
                    {
                        //Add either state or Conditional as starting node
                        offset += 12;
                        int gConditional = BitConverter.ToInt32(data, offset); //Conditional
                        offset += 4;
                        int gState = BitConverter.ToInt32(data, offset); //State
                        offset -= 16;
                        int goalStart = gState;
                        string startType = "Bool";
                        if (gState == -1)
                        {
                            goalStart = gConditional;
                            startType = "Conditional";
                        }
                        var nGoalIDs = new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Goal start plot/cnd: {goalStart} {startType}",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        };
                        GoalsIDs.Items.Add(nGoalIDs);

                        int iGoalInstVersion = BitConverter.ToInt32(data, offset);  //Goal Instance Version
                        nGoalIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Goal Instance Version: {iGoalInstVersion} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        int gTitle = BitConverter.ToInt32(data, offset); //Goal Name
                        string gttlkLookup = GlobalFindStrRefbyID(gTitle, game, CurrentLoadedExport.FileRef);
                        nGoalIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Goal Name StrRef: {gTitle} {gttlkLookup}",
                            Offset = offset,
                            Tag = NodeType.StructLeafObject
                        });
                        offset += 4;

                        int gDescription = BitConverter.ToInt32(data, offset); //Goal Description
                        string gdtlkLookup = GlobalFindStrRefbyID(gDescription, game, CurrentLoadedExport.FileRef);
                        nGoalIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Goal Description StrRef: {gDescription} {gdtlkLookup}",
                            Offset = offset,
                            Tag = NodeType.StructLeafObject
                        });
                        offset += 4;

                        gConditional = BitConverter.ToInt32(data, offset); //Conditional
                        nGoalIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Conditional: {gConditional} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        gState = BitConverter.ToInt32(data, offset); //State
                        nGoalIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Bool State: {gState} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;
                    }

                    int tCount = BitConverter.ToInt32(data, offset); //Task Count
                    var TaskIDs = new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Tasks Count: {tCount} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    };
                    offset += 4;
                    QuestIDs.Items.Add(TaskIDs);

                    for (int t = 0; t < tCount; t++)  //TASKS
                    {
                        var nTaskIDs = new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Task: {t}",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        };
                        TaskIDs.Items.Add(nTaskIDs);

                        int iTaskInstVersion = BitConverter.ToInt32(data, offset);  //Task Instance Version
                        nTaskIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Task Instance Version: {iTaskInstVersion} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        int tFinish = BitConverter.ToInt32(data, offset); //Primary Codex
                        bool bFinish = tFinish == 1;
                        nTaskIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Task Finishes Quest: {tFinish}  {bFinish}",
                            Offset = offset,
                            Tag = NodeType.StructLeafObject
                        });
                        offset += 4;

                        int tTitle = BitConverter.ToInt32(data, offset); //Task Name
                        string tttlkLookup = GlobalFindStrRefbyID(tTitle, game, CurrentLoadedExport.FileRef);
                        nTaskIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Task Name StrRef: {tTitle} {tttlkLookup}",
                            Offset = offset,
                            Tag = NodeType.StructLeafObject
                        });
                        offset += 4;

                        int tDescription = BitConverter.ToInt32(data, offset); //Task Description
                        string tdtlkLookup = GlobalFindStrRefbyID(tDescription, game, CurrentLoadedExport.FileRef);
                        nTaskIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Task Description StrRef: {tDescription} {tdtlkLookup}",
                            Offset = offset,
                            Tag = NodeType.StructLeafObject
                        });
                        offset += 4;

                        int piCount = BitConverter.ToInt32(data, offset); //Plot item Count
                        var PlotIDs = new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Plot Item Count: {piCount} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        };
                        offset += 4;
                        nTaskIDs.Items.Add(PlotIDs);

                        for (int pi = 0; pi < piCount; pi++)  //TASK PLOT ITEMS
                        {
                            int iPlotItem = BitConverter.ToInt32(data, offset);  //Plot item index
                            var nPlotItems = new BinInterpNode
                            {
                                Header = $"0x{offset:X5} Plot items: {pi}  Index: {iPlotItem}",
                                Offset = offset,
                                Tag = NodeType.StructLeafInt
                            };
                            PlotIDs.Items.Add(nPlotItems);
                            offset += 4;
                        }

                        int planetName = BitConverter.ToInt32(data, offset); //Planet name
                        offset += 4;
                        int planetIdx = BitConverter.ToInt32(data, offset); //Name index
                        offset -= 4;
                        nTaskIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Planet Name: {new NameReference(CurrentLoadedExport.FileRef.GetNameEntry(planetName), planetIdx).Instanced} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafName
                        });
                        offset += 8;

                        int wpStrLgth = BitConverter.ToInt32(data, offset); //String length for waypoint
                        offset += 4;
                        string wpRef = "No Waypoint data";
                        if (wpStrLgth > 0)
                        {
                            //offset += 1;
                            MemoryStream ms = new MemoryStream(data);
                            ms.Position = offset;
                            wpRef = ms.ReadStringLatin1Null(wpStrLgth);
                        }
                        nTaskIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Waypoint ref: {wpRef} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafStr
                        });
                        offset += wpStrLgth;
                    }

                    int pCount = BitConverter.ToInt32(data, offset); //Plot Item Count
                    var PlotItemIDs = new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Plot Items: {pCount} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    };
                    offset += 4;
                    QuestIDs.Items.Add(PlotItemIDs);

                    for (int p = 0; p < pCount; p++) //PLOT ITEM
                    {
                        //Add count starting node
                        var nPlotItemIDs = new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Plot Item: {p} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        };
                        PlotItemIDs.Items.Add(nPlotItemIDs);

                        int iPlotInstVersion = BitConverter.ToInt32(data, offset);  //Plot Item Instance Version
                        nPlotItemIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Plot item Instance Version: {iPlotInstVersion} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        int pTitle = BitConverter.ToInt32(data, offset); //Plot item Name
                        string pitlkLookup = GlobalFindStrRefbyID(pTitle, game, CurrentLoadedExport.FileRef);
                        nPlotItemIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Goal Name StrRef: {pTitle} {pitlkLookup}",
                            Offset = offset,
                            Tag = NodeType.StructLeafObject
                        });
                        offset += 4;

                        int pIcon = BitConverter.ToInt32(data, offset); //Icon Index
                        nPlotItemIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Icon Index: {pIcon} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        int pConditional = BitConverter.ToInt32(data, offset); //Conditional
                        nPlotItemIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Conditional: {pConditional} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        int pState = BitConverter.ToInt32(data, offset); //Int
                        nPlotItemIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Integer State: {pState} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        int pTarget = BitConverter.ToInt32(data, offset); //Target Index
                        nPlotItemIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Item Count Target: {pTarget} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;
                    }
                }

                int bsCount = BitConverter.ToInt32(data, offset);
                var bsNode = new BinInterpNode
                {
                    Header = $"0x{offset:X4} Bool Journal Events: {bsCount}",
                    Offset = offset,
                    Tag = NodeType.StructLeafInt
                };
                offset += 4;
                subnodes.Add(bsNode);

                for (int b = 0; b < bsCount; b++)
                {
                    int iBoolEvtID = BitConverter.ToInt32(data, offset);  //BOOL STATE ID
                    var BoolEvtIDs = new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Bool Journal Event: {iBoolEvtID} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    };
                    offset += 4;
                    bsNode.Items.Add(BoolEvtIDs);

                    int bsInstVersion = BitConverter.ToInt32(data, offset); //Instance Version
                    var BoolQuestIDs = new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Instance Version: {bsInstVersion} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    };
                    offset += 4;
                    BoolEvtIDs.Items.Add(BoolQuestIDs);

                    int bqstCount = BitConverter.ToInt32(data, offset); //Related Quests Count
                    var bqstNode = new BinInterpNode
                    {
                        Header = $"0x{offset:X4} Related Quests: {bqstCount}",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    };
                    offset += 4;
                    BoolQuestIDs.Items.Add(bqstNode);

                    for (int bq = 0; bq < bqstCount; bq++) //Related Quests
                    {
                        offset += 16;
                        int bqQuest = BitConverter.ToInt32(data, offset);  //Bool quest ID
                        var bquestIDs = new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Related Quest: {bqQuest} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        };
                        offset -= 16;
                        bqstNode.Items.Add(bquestIDs);

                        int bqInstVersion = BitConverter.ToInt32(data, offset);  //Bool quest Instance Version
                        bquestIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Instance Version: {bqInstVersion} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        int bqTask = BitConverter.ToInt32(data, offset);  //Bool quest Instance Version
                        bquestIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Related Task Link: {bqTask} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        int bqConditional = BitConverter.ToInt32(data, offset);  //Bool quest Conditional
                        bquestIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Conditional: {bqConditional} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        int bqState = BitConverter.ToInt32(data, offset);  //Bool quest State
                        bquestIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Bool State: {bqState} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        bqQuest = BitConverter.ToInt32(data, offset);  //Bool quest ID
                        bquestIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Quest Link: {bqQuest} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;
                    }
                }

                int isCount = BitConverter.ToInt32(data, offset);
                var isNode = new BinInterpNode
                {
                    Header = $"0x{offset:X4} Int Journal Events: {isCount}",
                    Offset = offset,
                    Tag = NodeType.StructLeafInt
                };
                offset += 4;
                subnodes.Add(isNode);

                for (int iEvt = 0; iEvt < isCount; iEvt++)  //INTEGER STATE EVENTS
                {
                    int iInttEvtID = BitConverter.ToInt32(data, offset);
                    var IntEvtIDs = new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Int Journal Event: {iInttEvtID} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    };
                    offset += 4;
                    isNode.Items.Add(IntEvtIDs);

                    int isInstVersion = BitConverter.ToInt32(data, offset); //Instance Version
                    var IntQuestIDs = new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Instance Version: {isInstVersion} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    };
                    offset += 4;
                    IntEvtIDs.Items.Add(IntQuestIDs);

                    int iqstCount = BitConverter.ToInt32(data, offset); //Related Quests Count
                    var iqstNode = new BinInterpNode
                    {
                        Header = $"0x{offset:X4} Related Quests: {iqstCount}",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    };
                    offset += 4;
                    IntQuestIDs.Items.Add(iqstNode);

                    for (int iq = 0; iq < iqstCount; iq++) //Related Quests
                    {
                        offset += 16;
                        int iqQuest = BitConverter.ToInt32(data, offset);  //int quest ID
                        var iquestIDs = new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Related Quest: {iqQuest} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        };
                        offset -= 16;
                        iqstNode.Items.Add(iquestIDs);

                        int iqInstVersion = BitConverter.ToInt32(data, offset);  //Int quest Instance Version
                        iquestIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Instance Version: {iqInstVersion} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        int iqTask = BitConverter.ToInt32(data, offset);  //Int quest Instance Version
                        iquestIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Related Task Link: {iqTask} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        int iqConditional = BitConverter.ToInt32(data, offset);  //Int quest Conditional
                        iquestIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Conditional: {iqConditional} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        int iqState = BitConverter.ToInt32(data, offset);  //Int quest State
                        iquestIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Bool State: {iqState} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        iqQuest = BitConverter.ToInt32(data, offset);  //Int quest ID
                        iquestIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Quest Link: {iqQuest} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;
                    }
                }

                int fsCount = BitConverter.ToInt32(data, offset);
                var fsNode = new BinInterpNode
                {
                    Header = $"0x{offset:X4} Float Journal Events: {fsCount}",
                    Offset = offset,
                    Tag = NodeType.StructLeafInt
                };
                offset += 4;
                subnodes.Add(fsNode);

                for (int f = 0; f < fsCount; f++)  //FLOAT STATE EVENTS
                {
                    int iFloatEvtID = BitConverter.ToInt32(data, offset);  //FLOAT STATE ID
                    var FloatEvtIDs = new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Float Journal Event: {iFloatEvtID} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    };
                    offset += 4;
                    fsNode.Items.Add(FloatEvtIDs);

                    int fsInstVersion = BitConverter.ToInt32(data, offset); //Instance Version
                    var FloatQuestIDs = new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Instance Version: {fsInstVersion} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    };
                    offset += 4;
                    FloatEvtIDs.Items.Add(FloatQuestIDs);

                    int fqstCount = BitConverter.ToInt32(data, offset); //Related Quests Count
                    var fqstNode = new BinInterpNode
                    {
                        Header = $"0x{offset:X4} Related Quests: {fqstCount}",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    };
                    offset += 4;
                    FloatQuestIDs.Items.Add(fqstNode);

                    for (int fq = 0; fq < fqstCount; fq++) //Related Quests
                    {
                        offset += 16;
                        int fqQuest = BitConverter.ToInt32(data, offset);  //float quest ID
                        var fquestIDs = new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Related Quest: {fqQuest} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        };
                        offset -= 16;
                        fqstNode.Items.Add(fquestIDs);

                        int fqInstVersion = BitConverter.ToInt32(data, offset);  //float quest Instance Version
                        fquestIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Instance Version: {fqInstVersion} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        int fqTask = BitConverter.ToInt32(data, offset);  //Float quest Instance Version
                        fquestIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Related Task Link: {fqTask} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        int fqConditional = BitConverter.ToInt32(data, offset);  //Float quest Conditional
                        fquestIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Conditional: {fqConditional} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        int fqState = BitConverter.ToInt32(data, offset);  //Float quest State
                        fquestIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Bool State: {fqState} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        fqQuest = BitConverter.ToInt32(data, offset);  //Float quest ID
                        fquestIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Quest Link: {fqQuest} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;
                    }
                }
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode() { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }

        private List<ITreeItem> StartBioCodexMapScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            var game = CurrentLoadedExport.FileRef.Game;
            try
            {
                int offset = binarystart;

                int sCount = BitConverter.ToInt32(data, offset);
                var SectionsNode = new BinInterpNode
                {
                    Header = $"0x{offset:X4} Codex Section Count: {sCount}",
                    Offset = offset,
                    Tag = NodeType.StructLeafInt
                };
                offset += 4;
                subnodes.Add(SectionsNode);

                for (int i = 0; i < sCount; i++)
                {
                    int iSectionID = BitConverter.ToInt32(data, offset);  //Section ID
                    var SectionIDs = new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Section ID: {iSectionID} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    };
                    offset += 4;
                    SectionsNode.Items.Add(SectionIDs);

                    int instVersion = BitConverter.ToInt32(data, offset); //Instance Version
                    SectionIDs.Items.Add(new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Instance Version: {instVersion} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    });
                    offset += 4;

                    int sTitle = BitConverter.ToInt32(data, offset); //Codex Title
                    string ttlkLookup = GlobalFindStrRefbyID(sTitle, game, CurrentLoadedExport.FileRef);
                    SectionIDs.Items.Add(new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Section Title StrRef: {sTitle} {ttlkLookup}",
                        Offset = offset,
                        Tag = NodeType.StructLeafObject
                    });
                    offset += 4;

                    int sDescription = BitConverter.ToInt32(data, offset); //Codex Description
                    string dtlkLookup = GlobalFindStrRefbyID(sDescription, game, CurrentLoadedExport.FileRef);
                    SectionIDs.Items.Add(new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Section Description StrRef: {sDescription} {dtlkLookup}",
                        Offset = offset,
                        Tag = NodeType.StructLeafObject
                    });
                    offset += 4;

                    int sTexture = BitConverter.ToInt32(data, offset); //Texture ID
                    SectionIDs.Items.Add(new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Section Texture ID: {sTexture} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafObject
                    });
                    offset += 4;

                    int sPriority = BitConverter.ToInt32(data, offset); //Priority
                    SectionIDs.Items.Add(new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Section Priority: {sPriority}  (5 is low, 1 is high)",
                        Offset = offset,
                        Tag = NodeType.StructLeafObject
                    });
                    offset += 4;

                    if (instVersion >= 3)
                    {
                        int sndExport = BitConverter.ToInt32(data, offset);
                        SectionIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X8} Codex Sound: {sndExport} {CurrentLoadedExport.FileRef.GetEntryString(sndExport)}",
                            Offset = offset,
                            Tag = NodeType.StructLeafObject
                        });
                        offset += 4;
                    }

                    int sPrimary = BitConverter.ToInt32(data, offset); //Primary Codex
                    bool bPrimary = false;
                    if (sPrimary == 1) { bPrimary = true; }
                    SectionIDs.Items.Add(new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Is Primary Codex: {sPrimary}  {bPrimary}",
                        Offset = offset,
                        Tag = NodeType.StructLeafObject
                    });
                    offset += 4;
                }
                //START OF CODEX PAGES SECTION
                int pCount = BitConverter.ToInt32(data, offset);
                var PagesNode = new BinInterpNode
                {
                    Header = $"0x{offset:X4} Codex Page Count: {pCount}",
                    Offset = offset,
                    Tag = NodeType.StructLeafInt
                };
                offset += 4;
                subnodes.Add(PagesNode);

                for (int i = 0; i < pCount; i++)
                {
                    int iPageID = BitConverter.ToInt32(data, offset);  //Page ID
                    var PageIDs = new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Page Bool: {iPageID} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    };
                    offset += 4;
                    PagesNode.Items.Add(PageIDs);

                    int instVersion = BitConverter.ToInt32(data, offset); //Instance Version
                    PageIDs.Items.Add(new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Instance Version: {instVersion} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    });
                    offset += 4;

                    int pTitle = BitConverter.ToInt32(data, offset); //Codex Title
                    string ttlkLookup = GlobalFindStrRefbyID(pTitle, game, CurrentLoadedExport.FileRef);
                    PageIDs.Items.Add(new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Page Title StrRef: {pTitle} {ttlkLookup}",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    });
                    offset += 4;

                    int pDescription = BitConverter.ToInt32(data, offset); //Codex Description
                    string dtlkLookup = GlobalFindStrRefbyID(pDescription, game, CurrentLoadedExport.FileRef);
                    PageIDs.Items.Add(new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Page Description StrRef: {pDescription} {dtlkLookup}",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    });
                    offset += 4;

                    int pTexture = BitConverter.ToInt32(data, offset); //Texture ID
                    PageIDs.Items.Add(new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Section Texture ID: {pTexture} ",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    });
                    offset += 4;

                    int pPriority = BitConverter.ToInt32(data, offset); //Priority
                    PageIDs.Items.Add(new BinInterpNode
                    {
                        Header = $"0x{offset:X5} Section Priority: {pPriority}  (5 is low, 1 is high)",
                        Offset = offset,
                        Tag = NodeType.StructLeafInt
                    });
                    offset += 4;

                    if (instVersion == 4) //ME3 use object reference found sound then section
                    {
                        int sndExport = BitConverter.ToInt32(data, offset);
                        PageIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X8} Codex Sound: {sndExport} {CurrentLoadedExport.FileRef.GetEntryString(sndExport)}",
                            Offset = offset,
                            Tag = NodeType.StructLeafObject
                        });
                        offset += 4;

                        int pSection = BitConverter.ToInt32(data, offset); //Section ID
                        PageIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Section Reference: {pSection} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;
                    }
                    else if (instVersion == 3 && Pcc.Game != MEGame.LE1) //ME2 use Section then no sound reference //LE1 uses something else...
                    {
                        int pSection = BitConverter.ToInt32(data, offset); //Section ID
                        PageIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Section Reference: {pSection} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;
                    }
                    else if (instVersion == 3 && Pcc.Game == MEGame.LE1)
                    {
                        int unkSection = BitConverter.ToInt32(data, offset); //Unknown ID
                        PageIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Unknown Int: {unkSection} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        int pSection = BitConverter.ToInt32(data, offset); //Section ID
                        PageIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Section Reference: {pSection} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        int sndStrLgth = BitConverter.ToInt32(data, offset); //String length for sound
                        string sndRef = "No sound data";
                        if (sndStrLgth > 0)
                        {
                            MemoryStream ms = new MemoryStream(data);
                            ms.Position = offset + 4;
                            sndRef = ms.ReadStringLatin1Null(sndStrLgth);
                        }
                        PageIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} SoundRef String: {sndRef} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafObject
                        });
                        offset += 4;
                        offset += sndStrLgth;
                    }
                    else  //ME1 has different order (section ID then codex sound) and uses a string reference.
                    {
                        int pSection = BitConverter.ToInt32(data, offset); //Section ID
                        PageIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} Section Reference: {pSection} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafInt
                        });
                        offset += 4;

                        int sndStrLgth = BitConverter.ToInt32(data, offset); //String length for sound
                        offset += 4;
                        string sndRef = "No sound data";
                        if (sndStrLgth > 0)
                        {
                            MemoryStream ms = new MemoryStream(data);
                            ms.Position = offset;
                            sndRef = ms.ReadStringLatin1Null(sndStrLgth);
                        }
                        PageIDs.Items.Add(new BinInterpNode
                        {
                            Header = $"0x{offset:X5} SoundRef String: {sndRef} ",
                            Offset = offset,
                            Tag = NodeType.StructLeafObject
                        });
                        offset += sndStrLgth;
                    }
                }
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode() { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }

        private List<ITreeItem> StartAnimSequenceScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();

            #region UDK AKF_PerTrackCompression

            if (Pcc.Game == MEGame.UDK && CurrentLoadedExport.GetProperty<EnumProperty>("KeyEncodingFormat")?.Value.Name == "AKF_PerTrackCompression")
            {
                try
                {
                    var TrackOffsets = CurrentLoadedExport.GetProperty<ArrayProperty<IntProperty>>("CompressedTrackOffsets");
                    var numFrames = CurrentLoadedExport.GetProperty<IntProperty>("NumFrames")?.Value ?? 0;

                    List<string> boneList = ((ExportEntry)CurrentLoadedExport.Parent).GetProperty<ArrayProperty<NameProperty>>("TrackBoneNames").Select(np => $"{np}").ToList();

                    var bin = new EndianReader(new MemoryStream(data)) { Endian = Pcc.Endian };
                    bin.JumpTo(binarystart);

                    int numTracks = bin.ReadInt32() * 2;
                    bin.Skip(-4);

                    BinInterpNode rawAnimDataNode = MakeInt32Node(bin, "RawAnimationData: NumTracks");
                    subnodes.Add(rawAnimDataNode);
                    for (int i = 0; i < numTracks; i++)
                    {
                        int keySize = bin.ReadInt32();
                        int numKeys = bin.ReadInt32();
                        for (int j = 0; j < numKeys; j++)
                        {
                            if (keySize == 12)
                            {
                                rawAnimDataNode.Items.Add(MakeVectorNode(bin, $"{boneList[i / 2]}, PosKey {j}"));
                            }
                            else if (keySize == 16)
                            {
                                rawAnimDataNode.Items.Add(MakeQuatNode(bin, $"{boneList[i / 2]}, RotKey {j}"));
                            }
                            else
                            {
                                throw new NotImplementedException($"Unexpected key size: {keySize}");
                            }
                        }
                    }

                    subnodes.Add(MakeInt32Node(bin, "AnimBinary length"));
                    var startOff = bin.Position;
                    for (int i = 0; i < boneList.Count; i++)
                    {
                        var boneNode = new BinInterpNode(bin.Position, boneList[i]);
                        subnodes.Add(boneNode);

                        int posOff = TrackOffsets[i * 2];

                        if (posOff >= 0)
                        {
                            bin.JumpTo(startOff + posOff);
                            int header = bin.ReadInt32();
                            int numKeys = header & 0x00FFFFFF;
                            int formatFlags = (header >> 24) & 0x0F;
                            AnimationCompressionFormat keyFormat = (AnimationCompressionFormat)((header >> 28) & 0x0F);

                            boneNode.Items.Add(new BinInterpNode(bin.Position - 4, $"PosKey Header: {numKeys} keys, Compression: {keyFormat}, FormatFlags:{formatFlags:X}") { Length = 4 });

                            for (int j = 0; j < numKeys; j++)
                            {
                                switch (keyFormat)
                                {
                                    case AnimationCompressionFormat.ACF_None:
                                    case AnimationCompressionFormat.ACF_Float96NoW:
                                        if ((formatFlags & 7) == 0)
                                        {
                                            boneNode.Items.Add(MakeVectorNode(bin, $"PosKey {j}"));
                                        }
                                        else
                                        {
                                            int binPosition = (int)bin.Position;
                                            int keyLength = 4 * ((formatFlags & 1) + ((formatFlags >> 1) & 1) + ((formatFlags >> 2) & 1));
                                            float x = (formatFlags & 1) != 0 ? bin.ReadFloat() : 0,
                                                  y = (formatFlags & 2) != 0 ? bin.ReadFloat() : 0,
                                                  z = (formatFlags & 4) != 0 ? bin.ReadFloat() : 0;
                                            boneNode.Items.Add(new BinInterpNode(binPosition, $"PosKey {j}: (X: {x}, Y: {y}, Z: {z})")
                                            {
                                                Length = keyLength
                                            });
                                        }
                                        break;
                                    case AnimationCompressionFormat.ACF_Fixed48NoW:
                                    case AnimationCompressionFormat.ACF_IntervalFixed32NoW:
                                    case AnimationCompressionFormat.ACF_Fixed32NoW:
                                    case AnimationCompressionFormat.ACF_Float32NoW:
                                    case AnimationCompressionFormat.ACF_BioFixed48:
                                    default:
                                        throw new NotImplementedException($"{keyFormat} is not supported yet!");
                                }
                            }
                        }

                        int rotOff = TrackOffsets[i * 2 + 1];

                        if (rotOff >= 0)
                        {
                            bin.JumpTo(startOff + rotOff);
                            int header = bin.ReadInt32();
                            int numKeys = header & 0x00FFFFFF;
                            int formatFlags = (header >> 24) & 0x0F;
                            AnimationCompressionFormat keyFormat = (AnimationCompressionFormat)((header >> 28) & 0x0F);

                            boneNode.Items.Add(new BinInterpNode(bin.Position - 4, $"RotKey Header: {numKeys} keys, Compression: {keyFormat}, FormatFlags:{formatFlags:X}") { Length = 4 });
                            switch (keyFormat)
                            {
                                case AnimationCompressionFormat.ACF_None:
                                    {
                                        for (int j = 0; j < numKeys; j++)
                                        {
                                            boneNode.Items.Add(MakeQuatNode(bin, $"RotKey {j}"));
                                        }
                                        break;
                                    }
                                case AnimationCompressionFormat.ACF_Fixed48NoW:
                                    {
                                        const float scale = 32767.0f;
                                        const ushort unkConst = 32767;
                                        int keyLength = 2 * ((formatFlags & 1) + ((formatFlags >> 1) & 1) + ((formatFlags >> 2) & 1));
                                        for (int j = 0; j < numKeys; j++)
                                        {
                                            int binPosition = (int)bin.Position;
                                            float x = (formatFlags & 1) != 0 ? (bin.ReadUInt16() - unkConst) / scale : 0,
                                                  y = (formatFlags & 2) != 0 ? (bin.ReadUInt16() - unkConst) / scale : 0,
                                                  z = (formatFlags & 4) != 0 ? (bin.ReadUInt16() - unkConst) / scale : 0;
                                            boneNode.Items.Add(new BinInterpNode(binPosition, $"RotKey {j}: (X: {x}, Y: {y}, Z: {z}, W: {getW(x, y, z)})")
                                            {
                                                Length = keyLength
                                            });
                                        }
                                        break;
                                    }
                                case AnimationCompressionFormat.ACF_Float96NoW:
                                    {
                                        float x, y, z;
                                        for (int j = 0; j < numKeys; j++)
                                        {
                                            boneNode.Items.Add(new BinInterpNode(bin.Position, $"RotKey {j}: (X: {x = bin.ReadFloat()}, Y: {y = bin.ReadFloat()}, Z: {z = bin.ReadFloat()}, W: {getW(x, y, z)})")
                                            {
                                                Length = 12
                                            });
                                        }
                                        break;
                                    }
                                case AnimationCompressionFormat.ACF_IntervalFixed32NoW:
                                case AnimationCompressionFormat.ACF_Fixed32NoW:
                                case AnimationCompressionFormat.ACF_Float32NoW:
                                case AnimationCompressionFormat.ACF_BioFixed48:
                                default:
                                    throw new NotImplementedException($"{keyFormat} is not supported yet!");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
                }
                return subnodes;
            }

            #endregion

            try
            {
                var TrackOffsets = CurrentLoadedExport.GetProperty<ArrayProperty<IntProperty>>("CompressedTrackOffsets");
                var numFrames = CurrentLoadedExport.GetProperty<IntProperty>("NumFrames")?.Value ?? 0;

                List<string> boneList;
                if (Pcc.Game == MEGame.UDK)
                {
                    boneList = ((ExportEntry)CurrentLoadedExport.Parent)?.GetProperty<ArrayProperty<NameProperty>>("TrackBoneNames")?.Select(np => $"{np}").ToList();
                }
                else
                {
                    var animsetData = CurrentLoadedExport.GetProperty<ObjectProperty>("m_pBioAnimSetData");
                    //In ME2, BioAnimSetData can sometimes be in a different package. 
                    boneList = animsetData != null && Pcc.IsUExport(animsetData.Value)
                        ? Pcc.GetUExport(animsetData.Value).GetProperty<ArrayProperty<NameProperty>>("TrackBoneNames")?.Select(np => $"{np}").ToList()
                        : null;
                }

                boneList ??= Enumerable.Repeat("???", TrackOffsets.Count / 4).ToList();
                Enum.TryParse(CurrentLoadedExport.GetProperty<EnumProperty>("KeyEncodingFormat")?.Value.Name, out AnimationKeyFormat keyEncoding);
                Enum.TryParse(CurrentLoadedExport.GetProperty<EnumProperty>("RotationCompressionFormat")?.Value.Name, out AnimationCompressionFormat rotCompression);
                Enum.TryParse(CurrentLoadedExport.GetProperty<EnumProperty>("TranslationCompressionFormat")?.Value.Name, out AnimationCompressionFormat posCompression);

                var bin = new EndianReader(new MemoryStream(data)) { Endian = Pcc.Endian };
                bin.JumpTo(binarystart);
                if (Pcc.Game is MEGame.ME2 or MEGame.LE2 && Pcc.Platform != MEPackage.GamePlatform.PS3)
                {
                    bin.Skip(12);
                    subnodes.Add(MakeInt32Node(bin, "AnimBinary Offset"));
                }
                else if (Pcc.Game == MEGame.UDK)
                {
                    int numTracks = bin.ReadInt32() * 2;
                    bin.Skip(-4);

                    BinInterpNode rawAnimDataNode = MakeInt32Node(bin, "RawAnimationData: NumTracks");
                    subnodes.Add(rawAnimDataNode);
                    for (int i = 0; i < numTracks; i++)
                    {
                        int keySize = bin.ReadInt32();
                        int numKeys = bin.ReadInt32();
                        for (int j = 0; j < numKeys; j++)
                        {
                            if (keySize == 12)
                            {
                                rawAnimDataNode.Items.Add(MakeVectorNode(bin, $"{boneList[i / 2]}, PosKey {j}"));
                            }
                            else if (keySize == 16)
                            {
                                rawAnimDataNode.Items.Add(MakeQuatNode(bin, $"{boneList[i / 2]}, RotKey {j}"));
                            }
                            else
                            {
                                throw new NotImplementedException($"Unexpected key size: {keySize}");
                            }
                        }
                    }
                }

                subnodes.Add(MakeInt32Node(bin, "AnimBinary length"));
                var startOffset = bin.Position;
                for (int i = 0; i < boneList.Count; i++)
                {
                    var boneNode = new BinInterpNode(bin.Position, boneList[i]);
                    subnodes.Add(boneNode);

                    int posOff = TrackOffsets[i * 4];
                    int posKeys = TrackOffsets[i * 4 + 1];
                    int rotOff = TrackOffsets[i * 4 + 2];
                    int rotKeys = TrackOffsets[i * 4 + 3];

                    if (posKeys > 0)
                    {
                        bin.JumpTo(startOffset + posOff);

                        AnimationCompressionFormat compressionFormat = posCompression;

                        if (posKeys == 1)
                        {
                            compressionFormat = AnimationCompressionFormat.ACF_None;
                        }
                        for (int j = 0; j < posKeys; j++)
                        {
                            BinInterpNode posKeyNode;
                            switch (compressionFormat)
                            {
                                case AnimationCompressionFormat.ACF_None:
                                case AnimationCompressionFormat.ACF_Float96NoW:
                                    posKeyNode = MakeVectorNode(bin, $"PosKey {j}");
                                    break;
                                case AnimationCompressionFormat.ACF_IntervalFixed32NoW:
                                case AnimationCompressionFormat.ACF_Fixed48NoW:
                                case AnimationCompressionFormat.ACF_Fixed32NoW:
                                case AnimationCompressionFormat.ACF_Float32NoW:
                                case AnimationCompressionFormat.ACF_BioFixed48:
                                default:
                                    throw new NotImplementedException($"Translation keys in format {compressionFormat} cannot be read!");
                            }
                            boneNode.Items.Add(posKeyNode);
                        }

                        readTrackTable(posKeys);
                    }

                    if (rotKeys > 0)
                    {
                        bin.JumpTo(startOffset + rotOff);

                        AnimationCompressionFormat compressionFormat = rotCompression;

                        if (rotKeys == 1)
                        {
                            compressionFormat = AnimationCompressionFormat.ACF_Float96NoW;
                        }
                        else if (Pcc.Game != MEGame.UDK)
                        {
                            boneNode.Items.Add(MakeVectorNode(bin, "Mins"));
                            boneNode.Items.Add(MakeVectorNode(bin, "Ranges"));
                        }

                        for (int j = 0; j < rotKeys; j++)
                        {
                            BinInterpNode rotKeyNode;
                            switch (compressionFormat)
                            {
                                case AnimationCompressionFormat.ACF_None:
                                    rotKeyNode = MakeQuatNode(bin, $"RotKey {j}");
                                    break;
                                case AnimationCompressionFormat.ACF_Float96NoW:
                                    {
                                        float x, y, z;
                                        rotKeyNode = new BinInterpNode(bin.Position, $"RotKey {j}: (X: {x = bin.ReadFloat()}, Y: {y = bin.ReadFloat()}, Z: {z = bin.ReadFloat()}, W: {getW(x, y, z)})")
                                        {
                                            Length = 12
                                        };
                                        break;
                                    }
                                case AnimationCompressionFormat.ACF_BioFixed48:
                                    {
                                        const float shift = 0.70710678118f;
                                        const float scale = 1.41421356237f;
                                        const float precisionMult = 32767.0f;
                                        var pos = bin.Position;
                                        ushort a = bin.ReadUInt16();
                                        ushort b = bin.ReadUInt16();
                                        ushort c = bin.ReadUInt16();
                                        float x = (a & 0x7FFF) / precisionMult * scale - shift;
                                        float y = (b & 0x7FFF) / precisionMult * scale - shift;
                                        float z = (c & 0x7FFF) / precisionMult * scale - shift;
                                        float w = getW(x, y, z);
                                        int wPos = ((a >> 14) & 2) | ((b >> 15) & 1);
                                        var rot = wPos switch
                                        {
                                            0 => new Quaternion(w, x, y, z),
                                            1 => new Quaternion(x, w, y, z),
                                            2 => new Quaternion(x, y, w, z),
                                            _ => new Quaternion(x, y, z, w)
                                        };
                                        rotKeyNode = new BinInterpNode(pos, $"RotKey {j}: (X: {rot.X}, Y: {rot.Y}, Z: {rot.Z}, W: {rot.W})")
                                        {
                                            Length = 6
                                        };
                                        break;
                                    }
                                case AnimationCompressionFormat.ACF_Fixed48NoW:
                                case AnimationCompressionFormat.ACF_IntervalFixed32NoW:
                                case AnimationCompressionFormat.ACF_Fixed32NoW:
                                case AnimationCompressionFormat.ACF_Float32NoW:
                                default:
                                    throw new NotImplementedException($"Rotation keys in format {compressionFormat} cannot be read!");
                            }
                            boneNode.Items.Add(rotKeyNode);
                        }

                        readTrackTable(rotKeys);
                    }

                    void readTrackTable(int numKeys)
                    {
                        if (keyEncoding == AnimationKeyFormat.AKF_VariableKeyLerp && numKeys > 1)
                        {
                            bin.JumpTo((bin.Position - startOffset).Align(4) + startOffset);
                            var trackTable = new BinInterpNode(bin.Position, "TrackTable");
                            boneNode.Items.Add(trackTable);

                            for (int j = 0; j < numKeys; j++)
                            {
                                trackTable.Items.Add(numFrames > 0xFF ? MakeUInt16Node(bin, $"{j}") : MakeByteNode(bin, $"{j}"));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;

            static float getW(float x, float y, float z)
            {
                float wSquared = 1.0f - (x * x + y * y + z * z);
                return (float)(wSquared > 0 ? Math.Sqrt(wSquared) : 0);
            }
        }

        /// <summary>
        /// Reads the common header for FaceFX archives.
        /// </summary>
        private (int version, MEGame game) ReadFaceFXHeader(EndianReader bin, List<ITreeItem> subnodes)
        {
            var archiveSize = bin.ReadInt32();
            subnodes.Add(new BinInterpNode(bin.Position - 4, $"Archive size: {archiveSize} ({FileSize.FormatSize(archiveSize)})"));
            subnodes.Add(new BinInterpNode(bin.Position, $"Magic: {bin.ReadInt32():X8}") { Length = 4 });
            int versionID = bin.ReadInt32(); //1710 = ME1, 1610 = ME2, 1731 = ME3.
            var game = versionID == 1710 ? MEGame.ME1 :
                versionID == 1610 ? MEGame.ME2 :
                versionID == 1731 ? MEGame.ME3 :
                MEGame.Unknown;
            var vIdStr = versionID.ToString();
            var vers = new Version(vIdStr[0] - '0', vIdStr[1] - '0', vIdStr[2] - '0', vIdStr[3] - '0'); //Mega hack
            subnodes.Add(new BinInterpNode(bin.Position - 4, $"SDK Version: {versionID} ({vers})") { Length = 4 });
            if (versionID == 1731)
            {
                subnodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt32():X8}") { Length = 4 });
            }

            subnodes.Add(new BinInterpNode(bin.Position, $"Licensee: {bin.ReadFaceFXString(game)}"));
            subnodes.Add(new BinInterpNode(bin.Position, $"Project: {bin.ReadFaceFXString(game)}"));

            var licenseeVersion = bin.ReadInt32();
            vIdStr = licenseeVersion.ToString();
            vers = new Version(vIdStr[0] - '0', vIdStr[1] - '0', vIdStr[2] - '0', vIdStr[3] - '0'); //Mega hack
            subnodes.Add(new BinInterpNode(bin.Position - 4, $"Licensee version: {vIdStr} ({vers})") { Length = 4 });

            return (versionID, game);
        }

        private List<ITreeItem> StartFaceFXAnimSetScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);
                ReadFaceFXHeader(bin, subnodes);

                if (Pcc.Game == MEGame.ME2)
                {
                    subnodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt32():X8}") { Length = 4 });
                }
                else
                {
                    subnodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt16()}") { Length = 2 });
                }

                if (Pcc.Game != MEGame.ME2)
                {
                    int hNodeCount = bin.ReadInt32();
                    var hNodes = new List<ITreeItem>();
                    subnodes.Add(new BinInterpNode(bin.Position - 4, $"Nodes: {hNodeCount} items")
                    {
                        Items = hNodes
                    });
                    for (int i = 0; i < hNodeCount; i++)
                    {
                        var hNodeNodes = new List<ITreeItem>();
                        hNodes.Add(new BinInterpNode(bin.Position, $"{i}")
                        {
                            Items = hNodeNodes
                        });
                        hNodeNodes.Add(MakeInt32Node(bin, "Unknown"));
                        var nNameCount = bin.ReadInt32();
                        hNodeNodes.Add(new BinInterpNode(bin.Position, $"Name Count: {nNameCount}") { Length = 4 });
                        for (int n = 0; n < nNameCount; n++)
                        {
                            hNodeNodes.Add(new BinInterpNode(bin.Position, $"Name: {bin.BaseStream.ReadStringLatin1(bin.ReadInt32())}"));
                            hNodeNodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt16()}") { Length = 2 });
                        }
                    }
                }

                int nameCount = bin.ReadInt32();
                subnodes.Add(new BinInterpNode(bin.Position - 4, $"Names: {nameCount} items")
                {
                    //ME2 different to ME3/1
                    Items = ReadList(nameCount, i => new BinInterpNode(bin.Skip(Pcc.Game != MEGame.ME2 ? 0 : 4).Position, $"{i}: {bin.BaseStream.ReadStringLatin1(bin.ReadInt32())}"))
                });

                subnodes.Add(MakeInt32Node(bin, "Unknown"));
                subnodes.Add(MakeInt32Node(bin, "Unknown"));
                subnodes.Add(MakeInt32Node(bin, "Unknown"));
                subnodes.Add(MakeInt32Node(bin, "Unknown"));
                if (Pcc.Game == MEGame.ME2)
                {
                    subnodes.Add(MakeInt32Node(bin, "Unknown"));
                    subnodes.Add(MakeInt32Node(bin, "Unknown"));
                    subnodes.Add(MakeInt32Node(bin, "Unknown"));
                    subnodes.Add(MakeInt32Node(bin, "Unknown"));
                    subnodes.Add(MakeInt32Node(bin, "Unknown"));
                }

                int lineCount = bin.ReadInt32();
                var lines = new List<ITreeItem>();

                subnodes.Add(new BinInterpNode(bin.Position - 4, $"FaceFXLines: {lineCount} items")
                {
                    Items = lines
                });
                for (int i = 0; i < lineCount; i++)
                {
                    var nodes = new List<ITreeItem>();
                    lines.Add(new BinInterpNode(bin.Position, $"{i}")
                    {
                        Items = nodes
                    });
                    if (Pcc.Game == MEGame.ME2)
                    {
                        nodes.Add(MakeInt32Node(bin, "Unknown"));
                        nodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt16()}") { Length = 2 });
                    }
                    nodes.Add(MakeInt32Node(bin, "Name"));
                    if (Pcc.Game == MEGame.ME2)
                    {
                        nodes.Add(MakeInt32Node(bin, "Unknown"));
                    }
                    int animationCount = bin.ReadInt32();
                    var anims = new List<ITreeItem>();
                    nodes.Add(new BinInterpNode(bin.Position - 4, $"Animations: {animationCount} items")
                    {
                        Items = anims
                    });
                    for (int j = 0; j < animationCount; j++)
                    {
                        var animNodes = new List<ITreeItem>();
                        anims.Add(new BinInterpNode(bin.Position, $"{j}")
                        {
                            Items = animNodes
                        });
                        if (Pcc.Game == MEGame.ME2)
                        {
                            animNodes.Add(MakeInt32Node(bin, "Unknown"));
                            animNodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt16()}") { Length = 2 });
                        }
                        animNodes.Add(MakeInt32Node(bin, "Name"));
                        animNodes.Add(MakeInt32Node(bin, "Unknown"));
                        if (Pcc.Game == MEGame.ME2)
                        {
                            animNodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt16()}") { Length = 2 });
                        }
                    }

                    int pointsCount = bin.ReadInt32();
                    nodes.Add(new BinInterpNode(bin.Position - 4, $"Points: {pointsCount} items")
                    {
                        Items = ReadList(pointsCount, j => new BinInterpNode(bin.Position, $"{j}")
                        {
                            Items = new List<ITreeItem>
                            {
                                new BinInterpNode(bin.Position, $"Time: {bin.ReadFloat()}") {Length = 4},
                                new BinInterpNode(bin.Position, $"Weight: {bin.ReadFloat()}") {Length = 4},
                                new BinInterpNode(bin.Position, $"InTangent: {bin.ReadFloat()}") {Length = 4},
                                new BinInterpNode(bin.Position, $"LeaveTangent: {bin.ReadFloat()}") {Length = 4}
                            }
                        })
                    });

                    if (pointsCount > 0)
                    {
                        if (Pcc.Game == MEGame.ME2)
                        {
                            nodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt16()}") { Length = 2 });
                        }
                        nodes.Add(new BinInterpNode(bin.Position, $"NumKeys: {bin.ReadInt32()} items")
                        {
                            Items = ReadList(bin.Skip(-4).ReadInt32(), j => new BinInterpNode(bin.Position, $"{bin.ReadInt32()} keys"))
                        });
                    }
                    nodes.Add(new BinInterpNode(bin.Position, $"Fade In Time: {bin.ReadFloat()}") { Length = 4 });
                    nodes.Add(new BinInterpNode(bin.Position, $"Fade Out Time: {bin.ReadFloat()}") { Length = 4 });
                    nodes.Add(MakeInt32Node(bin, "Unknown"));
                    if (Pcc.Game == MEGame.ME2)
                    {
                        nodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt16()}") { Length = 2 });
                        nodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt16()}") { Length = 2 });
                    }
                    nodes.Add(new BinInterpNode(bin.Position, $"Path: {bin.BaseStream.ReadStringLatin1(bin.ReadInt32())}"));
                    if (Pcc.Game == MEGame.ME2)
                    {
                        nodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt16()}") { Length = 2 });
                    }
                    nodes.Add(new BinInterpNode(bin.Position, $"ID: {bin.BaseStream.ReadStringLatin1(bin.ReadInt32())}"));
                    nodes.Add(MakeInt32Node(bin, "index"));
                }
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }

        private List<ITreeItem> StartFaceFXAssetScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);
                var (version, game) = ReadFaceFXHeader(bin, subnodes);

                if (game == MEGame.ME2)
                {
                    subnodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt32():X8}") { Length = 4 });
                }
                else
                {
                    subnodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt16()}") { Length = 2 });
                }
                //Node Table
                if (game != MEGame.ME2)
                {
                    int hNodeCount = bin.ReadInt32();
                    var hNodes = new List<ITreeItem>();
                    subnodes.Add(new BinInterpNode(bin.Position - 4, $"Nodes: {hNodeCount} items")
                    {
                        Items = hNodes
                    });
                    for (int i = 0; i < hNodeCount; i++)
                    {
                        var hNodeNodes = new List<ITreeItem>();
                        hNodes.Add(new BinInterpNode(bin.Position, $"{i}")
                        {
                            Items = hNodeNodes
                        });
                        hNodeNodes.Add(MakeInt32Node(bin, "Unknown"));
                        var nNameCount = bin.ReadInt32();
                        hNodeNodes.Add(new BinInterpNode(bin.Position, $"Name Count: {nNameCount}") { Length = 4 });
                        for (int n = 0; n < nNameCount; n++)
                        {
                            hNodeNodes.Add(new BinInterpNode(bin.Position, $"Name: {bin.BaseStream.ReadStringLatin1(bin.ReadInt32())}"));
                            hNodeNodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt16()}") { Length = 2 });
                        }
                    }
                }

                //Name Table

                var nameTable = new List<string>();
                int nameCount = bin.ReadInt32();
                var nametablePos = bin.Position - 4;
                var nametabObj = new List<ITreeItem>();

                // Does this need byte aligned? ME2 seems like it does...
                //if (game == MEGame.ME2)
                //{
                //    bin.ReadByte(); // Align to bytes?
                //}

                for (int m = 0; m < nameCount; m++)
                {
                    var pos = bin.Position;
                    var mName = bin.ReadFaceFXString(game, true);
                    nameTable.Add(mName);
                    nametabObj.Add(new BinInterpNode(pos, $"{m}: {mName}"));
                    //if (game != MEGame.ME2)
                    //{
                    //    bin.Skip(4);
                    //}
                }

                subnodes.Add(new BinInterpNode(nametablePos, $"Names: {nameCount} items")
                {
                    //ME1 and ME3 same, ME2 different
                    Items = nametabObj
                });

                subnodes.Add(MakeInt32Node(bin, "Unknown"));
                subnodes.Add(MakeInt32Node(bin, "Unknown"));

                if (game == MEGame.ME2)
                {
                    subnodes.Add(MakeInt32Node(bin, "Unknown"));
                    subnodes.Add(MakeInt16Node(bin, "Unknown"));
                    subnodes.Add(MakeInt16Node(bin, "Unknown"));
                    subnodes.Add(MakeInt16Node(bin, "Unknown"));
                }

                //LIST A - BONES
                var bonesList = new List<ITreeItem>();
                var bonesCount = bin.ReadInt32();
                if (game == MEGame.ME2)
                {
                    subnodes.Add(MakeInt32Node(bin, "Unknown"));
                    subnodes.Add(MakeInt16Node(bin, "Unknown"));
                }
                subnodes.Add(new BinInterpNode(((game == MEGame.ME2) ? (bin.Position - 10) : (bin.Position - 4)), $"Bone Nodes: {bonesCount} items")
                {
                    Items = bonesList
                });

                for (int a = 0; a < bonesCount; a++) //NOT EXACT??
                {
                    var boneNode = new List<ITreeItem>();
                    bonesList.Add(new BinInterpNode(bin.Position, $"{nameTable[bin.ReadInt32()]}")
                    {
                        Items = boneNode
                    });
                    boneNode.Add(MakeFloatNode(bin, "X"));
                    boneNode.Add(MakeFloatNode(bin, "Y"));
                    boneNode.Add(MakeFloatNode(bin, "Z"));

                    boneNode.Add(MakeFloatNode(bin, "Unknown float"));
                    boneNode.Add(MakeFloatNode(bin, "Unknown float"));
                    boneNode.Add(MakeFloatNode(bin, "Unknown float"));

                    boneNode.Add(MakeFloatNode(bin, "Unknown float"));
                    boneNode.Add(MakeFloatNode(bin, "Unknown float"));
                    boneNode.Add(MakeFloatNode(bin, "Unknown float"));

                    boneNode.Add(MakeFloatNode(bin, "Unknown float"));
                    boneNode.Add(MakeFloatNode(bin, "Unknown float"));
                    boneNode.Add(MakeFloatNode(bin, "Unknown float"));

                    boneNode.Add(MakeFloatNode(bin, "Unknown float"));
                    boneNode.Add(MakeFloatNode(bin, "Unknown float"));
                    if (game != MEGame.ME2)
                    {
                        boneNode.Add(MakeFloatNode(bin, "Unknown float"));
                        boneNode.Add(MakeFloatNode(bin, "Bone weight?"));
                        //while (true)
                        //{
                        //    var item = bin.ReadInt32();
                        //    if (item == 2147483647)
                        //    {
                        //        tableItems.Add(new BinInterpNode(bin.Position - 4, $"End Marker: FF FF FF 7F") { Length = 4 });
                        //        tableItems.Add(new BinInterpNode(bin.Position, $"Unknown float: {bin.ReadSingle()}") { Length = 4 });
                        //        break;
                        //    }

                        //    bin.Skip(-4);
                        //    tableItems.Add(MakeFloatNode(bin, "Unknown float"));

                        //}
                        //Name list to Bones and other facefx?
                        var unkNameList1 = new List<ITreeItem>();
                        var countUk1 = bin.ReadInt32();
                        boneNode.Add(new BinInterpNode(bin.Position - 4, $"Functions?: {countUk1} items")
                        {
                            Items = unkNameList1
                        });
                        for (int b = 0; b < countUk1; b++)
                        {
                            var unameVal = bin.ReadInt32();
                            var unkNameList1items = new List<ITreeItem>();
                            unkNameList1.Add(new BinInterpNode(bin.Position - 4, $"{b},{unameVal}")
                            {
                                Items = unkNameList1items
                            });
                            unkNameList1items.Add(new BinInterpNode(bin.Position, $"{nameTable[bin.ReadInt32()]}"));
                            unkNameList1items.Add(new BinInterpNode(bin.Position, $"Unknown float: {bin.ReadSingle()}") { Length = 4 });
                            unkNameList1items.Add(new BinInterpNode(bin.Position, $"Unknown float: {bin.ReadSingle()}") { Length = 4 });
                            unkNameList1items.Add(new BinInterpNode(bin.Position, $"Unknown float: {bin.ReadSingle()}") { Length = 4 });
                            unkNameList1items.Add(new BinInterpNode(bin.Position, $"Unknown float: {bin.ReadSingle()}") { Length = 4 });
                            unkNameList1items.Add(new BinInterpNode(bin.Position, $"Unknown float: {bin.ReadSingle()}") { Length = 4 });
                            unkNameList1items.Add(new BinInterpNode(bin.Position, $"Unknown float: {bin.ReadSingle()}") { Length = 4 });
                            unkNameList1items.Add(new BinInterpNode(bin.Position, $"Unknown float: {bin.ReadSingle()}") { Length = 4 });
                            unkNameList1items.Add(MakeInt32Node(bin, "Unknown"));
                            unkNameList1items.Add(MakeInt32Node(bin, "Unknown"));
                            unkNameList1items.Add(MakeInt32Node(bin, "Unknown"));
                        }
                    }
                }

                //LIST B - COMBINER NODES
                //FROM HERE ME3 ONLY WIP

                //I have literally no idea how this works in ME2

                var combinerList = new List<ITreeItem>();
                var combinerListNames = new List<string>();
                var combinerCount = bin.ReadInt32();

                if (game == MEGame.ME2)
                {
                    subnodes.Add(MakeInt32Node(bin, "Unknown"));
                }

                subnodes.Add(new BinInterpNode(bin.Position - (game == MEGame.ME2 ? 8 : 4), $"Combiner nodes: {combinerCount} items")
                {
                    Items = combinerList
                });

                for (int b = 0, i = 0; i < combinerCount; b++, i++)
                {
                    var bLocation = bin.Position;
                    // There seem to be several types, known types are 0, 4, 6, 8. 
                    int formatType;
                    if (game == MEGame.ME2)
                    {
                        formatType = bin.ReadInt16();
                    }
                    else formatType = bin.ReadInt32();

                    var nameIdx = bin.ReadInt32();

                    var combinerNode = new List<ITreeItem>();
                    combinerList.Add(new BinInterpNode(bin.Position - 4, $"{b}: {nameTable[nameIdx]} - {(FxNodeType)formatType}")
                    {
                        Items = combinerNode
                    });
                    combinerListNames.Add(nameTable[nameIdx]);

                    combinerNode.Add(new BinInterpNode(bin.Position - 8, $"Format: {formatType} - {(FxNodeType)formatType}"));
                    combinerNode.Add(new BinInterpNode(bin.Position - 4, $"Table index: {nameIdx}"));
                    combinerNode.Add(new BinInterpNode(bin.Position, $"Minimum Value: {bin.ReadSingle()}") { Length = 4 });
                    combinerNode.Add(new BinInterpNode(bin.Position, $"Unknown float: {bin.ReadSingle()}") { Length = 4 });
                    combinerNode.Add(new BinInterpNode(bin.Position, $"Maximum Value: {bin.ReadSingle()}") { Length = 4 });
                    combinerNode.Add(new BinInterpNode(bin.Position, $"Unknown float: {bin.ReadSingle()}") { Length = 4 });
                    var inputOp = bin.ReadInt32();
                    combinerNode.Add(new BinInterpNode(bin.Position - 4, $"Input Operation: {inputOp} - {(FxInputOperation)inputOp}"));

                    // Parent links section
                    var parentLinks = new List<ITreeItem>(); //Name list to Bones and other facefx phenomes?
                    var parentLinksCount = bin.ReadInt32();
                    combinerNode.Add(new BinInterpNode(bin.Position - 4, $"Parent Links: {parentLinksCount} items")
                    {
                        Items = parentLinks
                    });
                    for (int n2 = 0; n2 < parentLinksCount; n2++)
                    {
                        var combinerIdx = bin.ReadInt32();
                        var linkedNode = combinerIdx < b ? combinerListNames[combinerIdx] : "";
                        var parentLinkItems = new List<ITreeItem>();
                        parentLinks.Add(new BinInterpNode(bin.Position - 4, $"Combiner Idx: {combinerIdx} {linkedNode}")
                        {
                            Items = parentLinkItems
                        });
                        var linkFunction = bin.ReadInt32();
                        parentLinkItems.Add(new BinInterpNode(bin.Position - 4, $"Link Function: {(FxLinkFunction)linkFunction}"));
                        var n3count = bin.ReadInt32();
                        parentLinkItems.Add(new BinInterpNode(bin.Position - 4, $"Parameter Count: {n3count}"));
                        for (int n3 = 0; n3 < n3count; n3++)
                        {
                            parentLinkItems.Add(new BinInterpNode(bin.Position, $"Function Parameter {n3}: {bin.ReadSingle()}") { Length = 4 });
                        }
                    }

                    // Parameters section
                    int parameterCount = bin.ReadInt32();
                    var fxaParameter = new List<ITreeItem>(parameterCount);
                    combinerNode.Add(new BinInterpNode(bin.Position - 4, $"Parameters: {parameterCount} items")
                    {
                        Items = fxaParameter
                    });
                    for (int fxaIndex = 0; fxaIndex < parameterCount; fxaIndex++)
                    {
                        int fxaIdxVal = bin.ReadInt32();
                        var fxaInfoItem = new List<ITreeItem>();
                        fxaParameter.Add(new BinInterpNode(bin.Position - 4, $"{nameTable[fxaIdxVal]} - {fxaIdxVal}")
                        {
                            Items = fxaInfoItem
                        });
                        int parameterFmt = bin.ReadInt32();
                        fxaInfoItem.Add(new BinInterpNode(bin.Position - 4, $"Parameter Name: {nameTable[fxaIdxVal]} ({fxaIdxVal})"));
                        fxaInfoItem.Add(new BinInterpNode(bin.Position - 4, $"Parameter Format: {(FxNodeParamFormat)parameterFmt} ({parameterFmt})") { Length = 4 });
                        // Parameter format - 0 means first int is the param value, 3 means there is a string on the end that is the param value

                        var firstUnkIntName = parameterFmt == 0 ? "Int Value" : "Unknown int";
                        fxaInfoItem.Add(new BinInterpNode(bin.Position, $"{firstUnkIntName}: {bin.ReadInt32()}") { Length = 4 });
                        fxaInfoItem.Add(new BinInterpNode(bin.Position, $"Float value?: {bin.ReadSingle()}") { Length = 4 });
                        fxaInfoItem.Add(new BinInterpNode(bin.Position, $"Unknown int: {bin.ReadInt32()}") { Length = 4 });

                        if (parameterFmt == 3)
                        {
                            var unkStringLength = bin.ReadInt32();
                            fxaInfoItem.Add(new BinInterpNode(bin.Position - 4, $"Parameter Value: {bin.BaseStream.ReadStringLatin1(unkStringLength)}"));
                        }
                    }
                }

                // Fix names for bone node functions now that we've parsed combiner table - this is terrible code
                foreach (var bone in bonesList)
                {
                    var functions = (bone as BinInterpNode).Items[^1];
                    if (functions is BinInterpNode functionNode && functionNode.Header.Contains("Function"))
                    {
                        foreach (var funcItem in functionNode.Items)
                        {
                            if (funcItem is BinInterpNode func)
                            {
                                var ints = func.Header.Split(',', ' ').Where(str => Int32.TryParse(str, out _)).Select(str => Convert.ToInt32(str)).ToArray();
                                if (ints.Length != 2) break;
                                func.Header = $"{ints[0]}: Combiner Node {ints[1]} ({combinerListNames[ints[1]]})";
                            }
                        }
                    }
                }

                // Unknown Table C First 4 bytes
                // Theory 1: This could refer to a number of "unique" entries, it seems to be a number of entries in the table that have only 1 string reference to the combiner.
                //           Subtracting: (entries in this table that have 2 or more strings) - (total amount of combiner entires) = seems to result in same number that exists in these first 4 bytes.
                byte[] unkHeaderC = bin.ReadBytes(4);
                var unkListC = new List<ITreeItem>();
                subnodes.Add(new BinInterpNode(bin.Position - 4, $"Unknown Table C - Combiner Mapping?")
                {
                    Items = unkListC
                });

                for (int c = 0; c < combinerCount; c++)
                {
                    // Table begins with an unknown ID - this ID seems to be from some kind of global table as same entries with same names use same IDs across different FaceFX files
                    //                                   (not 100% sure as only smallish sample of about 20 files was checked)
                    int entryID = bin.ReadInt32();
                    // Add tree item with entry ID as an idicator
                    var unkListCitems = new List<ITreeItem>();
                    unkListC.Add(new BinInterpNode(bin.Position - 4, $"{c}: {entryID}")
                    {
                        Items = unkListCitems
                    });
                    unkListCitems.Add(new BinInterpNode(bin.Position - 4, $"Unknown int: {entryID}") { Length = 4 });
                    // String count:
                    int stringCount = bin.ReadInt32();
                    unkListCitems.Add(new BinInterpNode(bin.Position - 4, $"String count: {stringCount}") { Length = 4 });
                    // Combiner entry name:
                    int stringLength = bin.ReadInt32();
                    string stringText = bin.ReadEndianASCIIString(stringLength);
                    unkListCitems.Add(new BinInterpNode(bin.Position - stringLength - 4, $"Combiner String: {stringText}") { Length = stringLength + 4 });
                    // Combiner entry ID:
                    int name = bin.ReadInt32();
                    unkListCitems.Add(new BinInterpNode(bin.Position - 4, $"Combiner ID: {name} {combinerListNames[name]}") { Length = 4 });
                    // Do the same for next strings if theres more than one.
                    for (int i = 1; i < stringCount; i++)
                    {
                        c++;
                        stringLength = bin.ReadInt32();
                        stringText = bin.ReadEndianASCIIString(stringLength);
                        unkListCitems.Add(new BinInterpNode(bin.Position - stringLength - 4, $"Combiner String: {stringText}") { Length = stringLength + 4 });
                        name = bin.ReadInt32();
                        unkListCitems.Add(new BinInterpNode(bin.Position - 4, $"Combiner ID: {name} {combinerListNames[name]}") { Length = 4 });
                    }
                }

                if (game == MEGame.ME2)
                {
                    subnodes.Add(MakeInt32Node(bin, "Unknown"));
                    subnodes.Add(MakeInt32Node(bin, "Unknown"));
                    subnodes.Add(MakeInt32Node(bin, "Unknown"));
                    subnodes.Add(MakeInt32Node(bin, "Unknown"));
                    subnodes.Add(MakeInt32Node(bin, "Unknown"));
                }

                subnodes.Add(new BinInterpNode(bin.Position, $"Name: {nameTable[bin.ReadInt32()]}") { Length = 4 });

                int lineCount;
                var lines = new List<ITreeItem>();
                subnodes.Add(new BinInterpNode(bin.Position, $"FaceFXLines: {lineCount = bin.ReadInt32()}")
                {
                    Items = lines
                });
                for (int i = 0; i < lineCount; i++)
                {
                    var nodes = new List<ITreeItem>();
                    lines.Add(new BinInterpNode(bin.Position, $"{i}")
                    {
                        Items = nodes
                    });
                    nodes.Add(new BinInterpNode(bin.Position, $"Name: {nameTable[bin.ReadInt32()]}") { Length = 4 });
                    int animationCount = bin.ReadInt32();
                    var anims = new List<ITreeItem>();
                    nodes.Add(new BinInterpNode(bin.Position - 4, $"Animations: {animationCount} items")
                    {
                        Items = anims
                    });
                    for (int j = 0; j < animationCount; j++)
                    {
                        var animNodes = new List<ITreeItem>();
                        anims.Add(new BinInterpNode(bin.Position, $"{j}")
                        {
                            Items = animNodes
                        });
                        animNodes.Add(new BinInterpNode(bin.Position, $"Name: {nameTable[bin.ReadInt32()]}") { Length = 4 });
                        animNodes.Add(MakeInt32Node(bin, "Unknown"));
                        if (game == MEGame.ME2)
                        {
                            animNodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt16()}") { Length = 2 });
                        }
                    }
                    int pointsCount = bin.ReadInt32();
                    nodes.Add(new BinInterpNode(bin.Position - 4, $"Points: {pointsCount} items")
                    {
                        Items = ReadList(pointsCount, j => new BinInterpNode(bin.Position, $"{j}")
                        {
                            Items = new List<ITreeItem>
                            {
                                new BinInterpNode(bin.Position, $"Time: {bin.ReadFloat()}") {Length = 4},
                                new BinInterpNode(bin.Position, $"Weight: {bin.ReadFloat()}") {Length = 4},
                                new BinInterpNode(bin.Position, $"InTangent: {bin.ReadFloat()}") {Length = 4},
                                new BinInterpNode(bin.Position, $"LeaveTangent: {bin.ReadFloat()}") {Length = 4}
                            }
                        })
                    });
                    if (pointsCount > 0)
                    {
                        if (game == MEGame.ME2)
                        {
                            nodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt16()}") { Length = 2 });
                        }

                        nodes.Add(new BinInterpNode(bin.Position, $"NumKeys: {bin.ReadInt32()} items")
                        {
                            Items = ReadList(bin.Skip(-4).ReadInt32(), j => new BinInterpNode(bin.Position, $"{bin.ReadInt32()} keys"))
                        });
                    }

                    nodes.Add(new BinInterpNode(bin.Position, $"Fade In Time: {bin.ReadFloat()}") { Length = 4 });
                    nodes.Add(new BinInterpNode(bin.Position, $"Fade Out Time: {bin.ReadFloat()}") { Length = 4 });
                    nodes.Add(MakeInt32Node(bin, "Unknown"));
                    if (game == MEGame.ME2)
                    {
                        nodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt16()}") { Length = 2 });
                        nodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt16()}") { Length = 2 });
                    }

                    nodes.Add(new BinInterpNode(bin.Position, $"Path: {bin.BaseStream.ReadStringLatin1(bin.ReadInt32())}"));
                    if (game == MEGame.ME2)
                    {
                        nodes.Add(new BinInterpNode(bin.Position, $"Unknown: {bin.ReadInt16()}") { Length = 2 });
                    }

                    nodes.Add(new BinInterpNode(bin.Position, $"ID: {bin.BaseStream.ReadStringLatin1(bin.ReadInt32())}"));
                    nodes.Add(MakeInt32Node(bin, "index"));
                }

                subnodes.Add(MakeInt32Node(bin, "unknown"));

                subnodes.Add(MakeArrayNode(bin, "Unknown Table D - Mapping?", i => new BinInterpNode(bin.Position, $"{i}")
                {
                    IsExpanded = true,
                    Items =
                    {
                        new BinInterpNode(bin.Position, $"Column Id?: {bin.ReadInt32()}") {Length = 4},
                        new BinInterpNode(bin.Position, $"Name: {nameTable[bin.ReadInt32()]}") {Length = 4},
                        MakeFloatNode(bin, "Unk Float")
                    }
                }));
                subnodes.Add(MakeArrayNode(bin, "Lip sync phoneme list:", i => new BinInterpNode(bin.Position, $"Name: {nameTable[bin.ReadInt32()]}") { Length = 4 }));
                subnodes.Add(MakeInt32Node(bin, "Unknown"));
                if (game is MEGame.LE1 or MEGame.LE2)
                {
                    subnodes.Add(MakeArrayNode(bin, "Unknown Ints", i => new BinInterpNode(bin.Position, $"Unknown: {nameTable[bin.ReadInt32()]}") { Length = 4 }));
                }
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }

        private List<ITreeItem> StartBioGestureRuntimeDataScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);
                subnodes.Add(MakeArrayNode(bin, "m_mapAnimSetOwners", i => new BinInterpNode(bin.Position, $"{bin.ReadNameReference(Pcc)} => {bin.ReadNameReference(Pcc)}")
                {
                    Length = 16
                }));

                int count;
                if (CurrentLoadedExport.Game.IsGame1())
                {
                    var propDataNode = new BinInterpNode(bin.Position, $"m_mapCharTypeOverrides ({count = bin.ReadInt32()} items)");
                    subnodes.Add(propDataNode);
                    for (int i = 0; i < count; i++)
                    {
                        propDataNode.Items.Add(new BinInterpNode(bin.Position, $"{i}: {bin.ReadNameReference(Pcc)}", NodeType.StructLeafName)
                        {
                            Length = 8,
                            IsExpanded = true,
                            Items =
                            {
                                MakeNameNode(bin, "nm_Female"),
                                MakeNameNode(bin, "nm_Asari"),
                                MakeNameNode(bin, "nm_Turian"),
                                MakeNameNode(bin, "nm_Salarian"),
                                MakeNameNode(bin, "nm_Quarian"),
                                MakeNameNode(bin, "nm_Other"),
                                MakeNameNode(bin, "nm_Krogan"),
                                MakeNameNode(bin, "nm_Geth"),
                                MakeNameNode(bin, "nm_Other_Artificial")
                            }
                        });
                    }
                }
                else
                {
                    var propDataNode = new BinInterpNode(bin.Position, $"m_mapMeshProps ({count = bin.ReadInt32()} items)");
                    subnodes.Add(propDataNode);
                    for (int i = 0; i < count; i++)
                    {
                        BinInterpNode node = new BinInterpNode(bin.Position, $"{i}: {bin.ReadNameReference(Pcc)}", NodeType.StructLeafName)
                        {
                            Length = 8
                        };
                        propDataNode.Items.Add(node);
                        node.Items.Add(MakeNameNode(bin, "nmPropName"));
                        node.Items.Add(MakeStringNode(bin, "sMesh"));
                        node.Items.Add(MakeNameNode(bin, "nmAttachTo"));
                        node.Items.Add(MakeVectorNode(bin, "vOffsetLocation"));
                        node.Items.Add(MakeRotatorNode(bin, "rOffsetRotation"));
                        node.Items.Add(MakeVectorNode(bin, "vOffsetScale"));
                        int count2;
                        var propActionsNode = new BinInterpNode(bin.Position, $"mapActions ({count2 = bin.ReadInt32()} items)")
                        {
                            IsExpanded = true
                        };
                        node.Items.Add(propActionsNode);
                        for (int j = 0; j < count2; j++)
                        {
                            BinInterpNode node2 = new BinInterpNode(bin.Position, $"{j}: {bin.ReadNameReference(Pcc)}", NodeType.StructLeafName)
                            {
                                Length = 8
                            };
                            propActionsNode.Items.Add(node2);
                            node2.Items.Add(MakeNameNode(bin, "nmActionName"));
                            if (CurrentLoadedExport.Game.IsGame2())
                            {
                                node2.Items.Add(MakeStringNode(bin, "sEffect"));
                            }

                            node2.Items.Add(MakeBoolIntNode(bin, "bActivate"));
                            node2.Items.Add(MakeNameNode(bin, "nmAttachTo"));
                            node2.Items.Add(MakeVectorNode(bin, "vOffsetLocation"));
                            node2.Items.Add(MakeRotatorNode(bin, "rOffsetRotation"));
                            node2.Items.Add(MakeVectorNode(bin, "vOffsetScale"));
                            if (CurrentLoadedExport.Game.IsGame3())
                            {
                                node2.Items.Add(MakeStringNode(bin, "sParticleSys"));
                                node2.Items.Add(MakeStringNode(bin, "sClientEffect"));
                                node2.Items.Add(MakeBoolIntNode(bin, "bCooldown"));
                                node2.Items.Add(new BinInterpNode(bin.Position, "tSpawnParams")
                                {
                                    Length = 0x38,
                                    IsExpanded = true,
                                    Items =
                                    {
                                        MakeVectorNode(bin, "vHitLocation"),
                                        MakeVectorNode(bin, "vHitNormal"),
                                        MakeNameNode(bin, "nmHitBone"),
                                        MakeVectorNode(bin, "vRayDir"),
                                        MakeVectorNode(bin, "vSpawnValue")
                                    }
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }
        private List<ITreeItem> StartObjectRedirectorScan(byte[] data, ref int binaryStart)
        {
            var subnodes = new List<ITreeItem>();
            var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
            bin.Skip(binaryStart);
            subnodes.Add(MakeEntryNode(bin, "Redirect references to this export to"));
            return subnodes;
        }

        private List<ITreeItem> Scan_Bio2DA(byte[] data)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(data) { Endian = Pcc.Endian };
                bin.JumpTo(CurrentLoadedExport.propsEnd());

                bool isIndexed = !bin.ReadBoolInt();
                bin.Skip(-4);
                if (isIndexed)
                {
                    subnodes.Add(MakeUInt32Node(bin, "Zero"));
                }

                int cellCount;
                subnodes.Add(new BinInterpNode(bin.Position, $"Populated Cell Count: {cellCount = bin.ReadInt32()}", NodeType.StructLeafInt) { Length = 4 });

                for (int i = 0; i < cellCount; i++)
                {
                    Bio2DACell.Bio2DADataType type;
                    subnodes.Add(new BinInterpNode(bin.Position, $"Cell {(isIndexed ? bin.ReadInt32() : i)}", NodeType.StructLeafInt)
                    {
                        Items =
                        {
                            new BinInterpNode(bin.Position, $"Type: {type = (Bio2DACell.Bio2DADataType)bin.ReadByte()}") { Length = 1 },
                            type switch
                            {
                                Bio2DACell.Bio2DADataType.TYPE_INT => MakeInt32Node(bin, "Value"),
                                Bio2DACell.Bio2DADataType.TYPE_NAME => MakeNameNode(bin, "Value"),
                                Bio2DACell.Bio2DADataType.TYPE_FLOAT => MakeFloatNode(bin, "Value"),
                                Bio2DACell.Bio2DADataType.TYPE_NULL => new BinInterpNode("Value: NULL"),
                                _ => throw new ArgumentOutOfRangeException()
                            }
                        }
                    });
                }

                if (!isIndexed)
                {
                    subnodes.Add(MakeUInt32Node(bin, "Zero"));
                }

                int columnCount;
                subnodes.Add(new BinInterpNode(bin.Position, $"Column Count: {columnCount = bin.ReadInt32()}", NodeType.StructLeafInt) { Length = 4 });

                for (int i = 0; i < columnCount; i++)
                {
                    subnodes.Add(new BinInterpNode(bin.Position, $"Name: {bin.ReadNameReference(Pcc)}, Index: {bin.ReadInt32()}", NodeType.StructLeafInt) { Length = 12 });
                }
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }

        private List<ITreeItem> StartBioSquadCombatScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();

            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);
                subnodes.Add(MakeArrayNode(bin, "Count", i =>
                {
                    string entry = null;
                    if (Pcc.Game.IsLEGame())
                    {
                        entry = Pcc.GetEntryString(bin.ReadInt32());
                    }

                    var guid = bin.ReadGuid();
                    int num = bin.ReadInt32();

                    return new BinInterpNode(bin.Position, $"{guid}: {num} {entry}");
                }));
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }

        private List<ITreeItem> StartBioDynamicAnimSetScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();

            try
            {
                int binarypos = binarystart;
                int count = EndianReader.ToInt32(data, binarypos, CurrentLoadedExport.FileRef.Endian);
                subnodes.Add(new BinInterpNode
                {
                    Header = $"0x{binarypos:X4} Count: {count}"
                });
                binarypos += 4; //+ int
                for (int i = 0; i < count; i++)
                {
                    int nameIndex = EndianReader.ToInt32(data, binarypos, CurrentLoadedExport.FileRef.Endian);
                    int nameIndexNum = EndianReader.ToInt32(data, binarypos + 4, CurrentLoadedExport.FileRef.Endian);
                    int shouldBe1 = EndianReader.ToInt32(data, binarypos + 8, CurrentLoadedExport.FileRef.Endian);

                    var name = CurrentLoadedExport.FileRef.GetNameEntry(nameIndex);
                    string nodeValue = $"{new NameReference(name, nameIndexNum).Instanced}";
                    if (shouldBe1 != 1)
                    {
                        //ERROR
                        nodeValue += " - Not followed by 1 (integer)!";
                    }

                    subnodes.Add(new BinInterpNode
                    {
                        Header = $"0x{binarypos:X4} Name: {nodeValue}",
                        Tag = NodeType.StructLeafName,
                        Offset = binarypos,
                    });
                    subnodes.Add(new BinInterpNode
                    {
                        Header = $"0x{(binarypos + 8):X4} Unknown 1: {shouldBe1}",
                        Tag = NodeType.StructLeafInt,
                        Offset = (binarypos + 8),
                    });
                    binarypos += 12;
                }
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }

        private List<ITreeItem> StartBioStageScan(byte[] data, ref int binarystart)
        {
            /*
             * Length (int)
                Name: m_aCameraList
                int unknown 0
                Count + int unknown
                [Camera name
                    unreal property data]*/
            var subnodes = new List<ITreeItem>();
            //if ((CurrentLoadedExport.Header[0x1f] & 0x2) != 0)
            {
                int pos = binarystart;
                if (data.Length > binarystart)
                {
                    int length = BitConverter.ToInt32(data, binarystart);
                    subnodes.Add(new BinInterpNode
                    {
                        Header = $"{binarystart:X4} Length: {length}",
                        Offset = pos
                    });
                    pos += 4;
                    if (length != 0)
                    {
                        int nameindex = BitConverter.ToInt32(data, pos);
                        int num = BitConverter.ToInt32(data, pos + 4);

                        var name = new NameReference(CurrentLoadedExport.FileRef.GetNameEntry(nameindex), num);
                        subnodes.Add(new BinInterpNode
                        {
                            Header = $"{(pos - binarystart):X4} Array Name: {name.Instanced}",
                            Offset = pos,
                            Tag = NodeType.StructLeafName
                        });

                        pos += 8;
                        int shouldbezero = BitConverter.ToInt32(data, pos);
                        if (shouldbezero != 0)
                        {
                            Debug.WriteLine($"NOT ZERO FOUND: {pos}");
                        }
                        pos += 4;

                        int count = BitConverter.ToInt32(data, pos);
                        subnodes.Add(new BinInterpNode
                        {
                            Header = $"{(pos - binarystart):X4} Count: {count}",
                            Offset = pos
                        });
                        pos += 4;

                        shouldbezero = BitConverter.ToInt32(data, pos);
                        if (shouldbezero != 0)
                        {
                            Debug.WriteLine($"NOT ZERO FOUND: {pos}");
                        }
                        pos += 4;
                        try
                        {
                            var stream = new MemoryStream(data);
                            for (int i = 0; i < count; i++)
                            {
                                nameindex = BitConverter.ToInt32(data, pos);
                                num = BitConverter.ToInt32(data, pos + 4);
                                BinInterpNode parentnode = new BinInterpNode
                                {
                                    Header = $"{(pos - binarystart):X4} Camera {i + 1}: {new NameReference(CurrentLoadedExport.FileRef.GetNameEntry(nameindex), num).Instanced}",
                                    Tag = NodeType.StructLeafName,
                                    Offset = pos
                                };
                                subnodes.Add(parentnode);
                                pos += 8;
                                stream.Seek(pos, SeekOrigin.Begin);
                                var props = PropertyCollection.ReadProps(CurrentLoadedExport, stream, "BioStageCamera", includeNoneProperty: true);

                                UPropertyTreeViewEntry topLevelTree = new UPropertyTreeViewEntry(); //not used, just for holding and building data.
                                foreach (Property prop in props)
                                {
                                    InterpreterExportLoader.GenerateUPropertyTreeForProperty(prop, topLevelTree, CurrentLoadedExport);
                                }
                                subnodes.AddRange(topLevelTree.ChildrenProperties);

                                //finish writing function here
                                pos = (int)stream.Position;
                            }
                        }
                        catch (Exception ex)
                        {
                            subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
                        }
                    }
                }
            }
            return subnodes;
        }

        private List<ITreeItem> StartGuidCacheScan(byte[] data, ref int binarystart)
        {
            /*
             *  
             *  count +4
             *      nameentry +8
             *      guid +16
             *      
             */
            var subnodes = new List<ITreeItem>();

            try
            {
                int pos = binarystart;
                int count = BitConverter.ToInt32(data, pos);
                subnodes.Add(new BinInterpNode
                {
                    Header = $"{(pos - binarystart):X4} count: {count}",
                    Offset = pos,
                });
                pos += 4;
                for (int i = 0; i < count && pos < data.Length; i++)
                {
                    int nameRef = BitConverter.ToInt32(data, pos);
                    int nameIdx = BitConverter.ToInt32(data, pos + 4);
                    Guid guid = new Guid(data.Skip(pos + 8).Take(16).ToArray());
                    subnodes.Add(new BinInterpNode
                    {
                        Header = $"{(pos - binarystart):X4} {new NameReference(CurrentLoadedExport.FileRef.GetNameEntry(nameRef), nameIdx).Instanced}: {{{guid}}}",
                        Offset = pos,

                        Tag = NodeType.StructLeafName
                    });
                    //Debug.WriteLine($"{pos:X4} {new NameReference(CurrentLoadedExport.FileRef.getNameEntry(nameRef), nameIdx).Instanced}: {{{guid}}}");
                    pos += 24;
                }
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode() { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }

        private List<ITreeItem> StartLevelScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);

                subnodes.Add(MakeEntryNode(bin, "Self"));
                int actorsCount;
                BinInterpNode levelActorsNode;
                subnodes.Add(levelActorsNode = new BinInterpNode(bin.Position, $"Level Actors: ({actorsCount = bin.ReadInt32()})", NodeType.StructLeafInt)
                {
                    ArrayAddAlgorithm = BinInterpNode.ArrayPropertyChildAddAlgorithm.FourBytes,
                    IsExpanded = true
                });
                levelActorsNode.Items = ReadList(actorsCount, i => new BinInterpNode(bin.Position, $"{i}: {entryRefString(bin)}", NodeType.ArrayLeafObject)
                {
                    ArrayAddAlgorithm = BinInterpNode.ArrayPropertyChildAddAlgorithm.FourBytes,
                    Parent = levelActorsNode,
                });

                subnodes.Add(new BinInterpNode(bin.Position, "URL")
                {
                    Items =
                    {
                        MakeStringNode(bin, "Protocol"),
                        MakeStringNode(bin, "Host"),
                        MakeStringNode(bin, "Map"),
                        MakeStringNode(bin, "Portal"),
                        new BinInterpNode(bin.Position, $"Op: ({bin.ReadInt32()} items)")
                        {
                            Items = ReadList(bin.Skip(-4).ReadInt32(), i => MakeStringNode(bin, $"{i}"))
                        },
                        MakeInt32Node(bin, "Port"),
                        new BinInterpNode(bin.Position, $"Valid: {bin.ReadInt32()}")
                    }
                });
                subnodes.Add(MakeEntryNode(bin, "Model"));
                int modelcomponentsCount;
                subnodes.Add(new BinInterpNode(bin.Position, $"ModelComponents: ({modelcomponentsCount = bin.ReadInt32()})")
                {
                    Items = ReadList(modelcomponentsCount, i => MakeEntryNode(bin, $"{i}"))
                });
                int sequencesCount;
                subnodes.Add(new BinInterpNode(bin.Position, $"GameSequences: ({sequencesCount = bin.ReadInt32()})")
                {
                    Items = ReadList(sequencesCount, i => MakeEntryNode(bin, $"{i}"))
                });
                int texToInstCount;
                int streamableTexInstCount;
                subnodes.Add(new BinInterpNode(bin.Position, $"TextureToInstancesMap: ({texToInstCount = bin.ReadInt32()})")
                {
                    Items = ReadList(texToInstCount, i =>
                                         new BinInterpNode(bin.Position, $"{entryRefString(bin)}: ({streamableTexInstCount = bin.ReadInt32()} StreamableTextureInstances)")
                                         {
                                             Items = ReadList(streamableTexInstCount, j => new BinInterpNode(bin.Position, $"{j}")
                                             {
                                                 IsExpanded = true,
                                                 Items =
                            {
                                new BinInterpNode(bin.Position, "BoundingSphere")
                                {
                                    IsExpanded = true,
                                    Items =
                                    {
                                        MakeVectorNode(bin, "Center"),
                                        new BinInterpNode(bin.Position, $"Radius: {bin.ReadSingle()}")
                                    }
                                },
                                new BinInterpNode(bin.Position, $"TexelFactor: {bin.ReadSingle()}")
                            }
                                             })
                                         })
                });
                if (Pcc.Game == MEGame.UDK)
                {
                    subnodes.Add(MakeArrayNode(bin, "MeshesComponentsWithDynamicLighting?",
                                               i => new BinInterpNode(bin.Position, $"{i}: {entryRefString(bin)}, {bin.ReadInt32()}")));
                }

                if (Pcc.Game >= MEGame.ME3)
                {
                    int apexSize;
                    subnodes.Add(new BinInterpNode(bin.Position, $"APEX Size: {apexSize = bin.ReadInt32()}"));
                    //should always be zero, but just in case...
                    if (apexSize > 0)
                    {
                        subnodes.Add(new BinInterpNode(bin.Position, $"APEX mesh?: {apexSize} bytes") { Length = apexSize });
                        bin.Skip(apexSize);
                    }
                }

                int cachedPhysBSPDataSize;
                subnodes.Add(MakeInt32Node(bin, "size of byte"));
                subnodes.Add(new BinInterpNode(bin.Position, $"CachedPhysBSPData Size: {cachedPhysBSPDataSize = bin.ReadInt32()}"));
                if (cachedPhysBSPDataSize > 0)
                {
                    subnodes.Add(new BinInterpNode(bin.Position, $"CachedPhysBSPData: {cachedPhysBSPDataSize} bytes") { Length = cachedPhysBSPDataSize });
                    bin.Skip(cachedPhysBSPDataSize);
                }

                int cachedPhysSMDataMapCount;
                subnodes.Add(new BinInterpNode(bin.Position, $"CachedPhysSMDataMap: ({cachedPhysSMDataMapCount = bin.ReadInt32()})")
                {
                    Items = ReadList(cachedPhysSMDataMapCount, i => new BinInterpNode(bin.Position, $"{entryRefString(bin)}")
                    {
                        Items =
                        {
                            MakeVectorNode(bin, "Scale3D"),
                            new BinInterpNode(bin.Position, $"CachedDataIndex: {bin.ReadInt32()}")
                        }
                    })
                });

                int cachedPhysSMDataStoreCount;
                int cachedConvexElementsCount;
                subnodes.Add(new BinInterpNode(bin.Position, $"CachedPhysSMDataStore: ({cachedPhysSMDataStoreCount = bin.ReadInt32()})")
                {
                    Items = ReadList(cachedPhysSMDataStoreCount, i => new BinInterpNode(bin.Position, $"{i}: CachedConvexElements ({cachedConvexElementsCount = bin.ReadInt32()})")
                    {
                        Items = ReadList(cachedConvexElementsCount, j =>
                        {
                            int size;
                            var item = new BinInterpNode(bin.Position, $"{j}: ConvexElementData (size of byte: {bin.ReadInt32()}) (number of bytes: {size = bin.ReadInt32()})")
                            {
                                Length = size + 8
                            };
                            bin.Skip(size);
                            return item;
                        })
                    })
                });

                int cachedPhysPerTriSMDataMapCount;
                subnodes.Add(new BinInterpNode(bin.Position, $"CachedPhysPerTriSMDataMap: ({cachedPhysPerTriSMDataMapCount = bin.ReadInt32()})")
                {
                    Items = ReadList(cachedPhysPerTriSMDataMapCount, i => new BinInterpNode(bin.Position, $"{entryRefString(bin)}")
                    {
                        Items =
                        {
                            MakeVectorNode(bin, "Scale3D"),
                            new BinInterpNode(bin.Position, $"CachedDataIndex: {bin.ReadInt32()}")
                        }
                    })
                });

                int cachedPhysPerTriSMDataStoreCount;
                subnodes.Add(new BinInterpNode(bin.Position, $"CachedPhysPerTriSMDataStore: ({cachedPhysPerTriSMDataStoreCount = bin.ReadInt32()})")
                {
                    Items = ReadList(cachedPhysPerTriSMDataStoreCount, j =>
                    {
                        int size;
                        var item = new BinInterpNode(bin.Position, $"{j}: CachedPerTriData (size of byte: {bin.ReadInt32()}) (number of bytes: {size = bin.ReadInt32()})")
                        {
                            Length = size + 8
                        };
                        bin.Skip(size);
                        return item;
                    })
                });

                subnodes.Add(MakeInt32Node(bin, "CachedPhysBSPDataVersion"));
                subnodes.Add(MakeInt32Node(bin, "CachedPhysSMDataVersion"));

                int forceStreamTexturesCount;
                subnodes.Add(new BinInterpNode(bin.Position, $"ForceStreamTextures: ({forceStreamTexturesCount = bin.ReadInt32()})")
                {
                    Items = ReadList(forceStreamTexturesCount, i => MakeBoolIntNode(bin, $"Texture: {entryRefString(bin)} | ForceStream"))
                });

                if (Pcc.Game == MEGame.UDK)
                {
                    subnodes.Add(new BinInterpNode(bin.Position, "CachedPhysConvexBSPData")
                    {
                        Items =
                        {
                            new BinInterpNode(bin.Position, $"CachedConvexElements ({cachedConvexElementsCount = bin.ReadInt32()})")
                            {
                                Items = ReadList(cachedConvexElementsCount, j =>
                                {
                                    int size;
                                    var item = new BinInterpNode(bin.Position, $"{j}: ConvexElementData (size of byte: {bin.ReadInt32()}) (number of bytes: {size = bin.ReadInt32()})")
                                    {
                                        Length = size + 8
                                    };
                                    bin.Skip(size);
                                    return item;
                                })
                            }
                        }
                    });
                    subnodes.Add(MakeInt32Node(bin, "CachedPhysConvexBSPVersion"));
                }

                subnodes.Add(MakeEntryNode(bin, "NavListStart"));
                subnodes.Add(MakeEntryNode(bin, "NavListEnd"));
                subnodes.Add(MakeEntryNode(bin, "CoverListStart"));
                subnodes.Add(MakeEntryNode(bin, "CoverListEnd"));
                if (Pcc.Game >= MEGame.ME3)
                {
                    subnodes.Add(MakeEntryNode(bin, "PylonListStart"));
                    subnodes.Add(MakeEntryNode(bin, "PylonListEnd"));
                }
                if (Pcc.Game is MEGame.ME3 or MEGame.LE3 or MEGame.UDK)
                {
                    int guidToIntMapCount;
                    subnodes.Add(new BinInterpNode(bin.Position, $"CrossLevelCoverGuidRefs: ({guidToIntMapCount = bin.ReadInt32()})")
                    {
                        Items = ReadList(guidToIntMapCount, i => MakeInt32Node(bin, $"{bin.ReadGuid()}"))
                    });

                    int coverListCount;
                    subnodes.Add(new BinInterpNode(bin.Position, $"CoverLinkRefs: ({coverListCount = bin.ReadInt32()})")
                    {
                        Items = ReadList(coverListCount, i => MakeEntryNode(bin, $"{i}"))
                    });

                    int intToByteMapCount;
                    subnodes.Add(new BinInterpNode(bin.Position, $"CoverIndexPairs: ({intToByteMapCount = bin.ReadInt32()})")
                    {
                        Items = ReadList(intToByteMapCount, i => new BinInterpNode(bin.Position, $"[{i}] {bin.ReadInt32()}: {bin.ReadByte()}"))
                    });

                    if (Pcc.Game != MEGame.UDK)
                    {
                        // BioWare only

                        int guidToIntMap2Count;
                        subnodes.Add(new BinInterpNode(bin.Position, $"CrossLevelNavGuidRefs: ({guidToIntMap2Count = bin.ReadInt32()})")
                        {
                            Items = ReadList(guidToIntMap2Count, i => MakeInt32Node(bin, $"{bin.ReadGuid()}"))
                        });

                        int navListCount;
                        subnodes.Add(new BinInterpNode(bin.Position, $"NavRefs: ({navListCount = bin.ReadInt32()})")
                        {
                            Items = ReadList(navListCount, i => MakeEntryNode(bin, $"{i}"))
                        });

                        int numbersCount;
                        subnodes.Add(new BinInterpNode(bin.Position,
                            $"NavRefIndices: ({numbersCount = bin.ReadInt32()})")
                        {
                            Items = ReadList(numbersCount, i => MakeInt32Node(bin, $"{i}"))
                        });
                    }
                }

                int crossLevelActorsCount;
                subnodes.Add(new BinInterpNode(bin.Position, $"CrossLevelActors?: ({crossLevelActorsCount = bin.ReadInt32()})")
                {
                    Items = ReadList(crossLevelActorsCount, i => MakeEntryNode(bin, $"{i}"))
                });

                if (Pcc.Game is MEGame.ME1 or MEGame.LE1)
                {
                    subnodes.Add(MakeEntryNode(bin, "BioArtPlaceable 1?"));
                    subnodes.Add(MakeEntryNode(bin, "BioArtPlaceable 2?"));
                }

                if (Pcc.Game >= MEGame.ME3)
                {
                    bool bInitialized;
                    int samplesCount;
                    subnodes.Add(new BinInterpNode(bin.Position, "PrecomputedLightVolume")
                    {
                        Items =
                        {
                            new BinInterpNode(bin.Position, $"bInitialized: ({bInitialized = bin.ReadBoolInt()})"),
                            ListInitHelper.ConditionalAdd(bInitialized, () => new ITreeItem[]
                            {
                                MakeBoxNode(bin, "Bounds"),
                                MakeFloatNode(bin, "SampleSpacing"),
                                new BinInterpNode(bin.Position, $"Samples ({samplesCount = bin.ReadInt32()})")
                                {
                                    Items = ReadList(samplesCount, i => new BinInterpNode(bin.Position, $"{i}")
                                    {
                                        Items =
                                        {
                                            MakeVectorNode(bin, "Position"),
                                            MakeFloatNode(bin, "Radius"),
                                            ListInitHelper.ConditionalAdd(Pcc.Game == MEGame.UDK, () => new ITreeItem[]
                                            {
                                                MakeByteNode(bin, "IndirectDirectionTheta"),
                                                MakeByteNode(bin, "IndirectDirectionPhi"),
                                                MakeByteNode(bin, "EnvironmentDirectionTheta"),
                                                MakeByteNode(bin, "EnvironmentDirectionPhi"),
                                                MakeColorNode(bin, "IndirectRadiance"),
                                                MakeColorNode(bin, "EnvironmentRadiance"),
                                                MakeColorNode(bin, "AmbientRadiance"),
                                                MakeByteNode(bin, "bShadowedFromDominantLights"),
                                            }, () => new []
                                            {
                                                //SirCxyrtyx: This is a color, but is serialized as an FQuantizedSHVectorRGB, a vector of colored, quantized spherical harmonic coefficients.
                                                //Conversion to ARGB is possible, but devilishly tricky. Let me know if this is something that's actually needed
                                                new BinInterpNode(bin.Position, $"Ambient Radiance? : {bin.ReadToBuffer(39)}"){ Length = 39}
                                            })
                                        }
                                    })
                                }
                            })
                        }
                    });
                }
                if (Pcc.Game == MEGame.UDK)
                {
                    BinInterpNode item = new BinInterpNode(bin.Position, "PrecomputedVisibilityHandler")
                    {
                        IsExpanded = true
                    };
                    subnodes.Add(item);
                    item.Items.Add(MakeVector2DNode(bin, "PrecomputedVisibilityCellBucketOriginXY"));
                    item.Items.Add(MakeFloatNode(bin, "PrecomputedVisibilityCellSizeXY"));
                    item.Items.Add(MakeFloatNode(bin, "PrecomputedVisibilityCellSizeZ"));
                    item.Items.Add(MakeInt32Node(bin, "PrecomputedVisibilityCellBucketSizeXY"));
                    item.Items.Add(MakeInt32Node(bin, "PrecomputedVisibilityNumCellBuckets"));

                    item = new BinInterpNode(bin.Position, "PrecomputedVolumeDistanceField")
                    {
                        IsExpanded = true
                    };

                    subnodes.Add(item);
                    item.Items.Add(MakeFloatNode(bin, "VolumeMaxDistance"));
                    item.Items.Add(MakeBoxNode(bin, "VolumeBox"));
                    item.Items.Add(MakeInt32Node(bin, "VolumeSizeX"));
                    item.Items.Add(MakeInt32Node(bin, "VolumeSizeY"));
                    item.Items.Add(MakeInt32Node(bin, "VolumeSizeZ"));
                    item.Items.Add(MakeArrayNode(bin, "Data", x=> MakeColorNode(bin, $"Color[{x}]")));
                    item.Items.Add(MakeInt32Node(bin, "UDKUnknown"));

                }

                binarystart = (int)bin.Position;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private static BinInterpNode MakeBoolIntNode(EndianReader bin, string name) => new BinInterpNode(bin.Position, $"{name}: {bin.ReadBoolInt()}", NodeType.StructLeafBool) { Length = 4 };

        private static BinInterpNode MakeBoolIntNode(EndianReader bin, string name, out bool boolVal)
        {
            return new BinInterpNode(bin.Position, $"{name}: {boolVal = bin.ReadBoolInt()}", NodeType.StructLeafBool) { Length = 4 };
        }

        private static BinInterpNode MakeBoolByteNode(EndianReader bin, string name) => new BinInterpNode(bin.Position, $"{name}: {bin.ReadBoolByte()}") { Length = 1 };

        private static BinInterpNode MakeFloatNode(EndianReader bin, string name) => new BinInterpNode(bin.Position, $"{name}: {bin.ReadFloat()}", NodeType.StructLeafFloat) { Length = 4 };

        private static BinInterpNode MakeFloatNodeConditional(EndianReader bin, string name, bool create)
        {
            if (create)
            {
                return new BinInterpNode(bin.Position, $"{name}: {bin.ReadFloat()}", NodeType.StructLeafFloat) { Length = 4 };
            }
            return null;
        }

        private static BinInterpNode MakeUInt32Node(EndianReader bin, string name) => new BinInterpNode(bin.Position, $"{name}: {bin.ReadUInt32()}") { Length = 4 };

        private static BinInterpNode MakeUInt64Node(EndianReader bin, string name) => new BinInterpNode(bin.Position, $"{name}: {bin.ReadUInt64()}") { Length = 8 };

        private static BinInterpNode MakeInt64Node(EndianReader bin, string name) => new BinInterpNode(bin.Position, $"{name}: {bin.ReadInt64()}") { Length = 8 };

        private static BinInterpNode MakeUInt32HexNode(EndianReader bin, string name) => new BinInterpNode(bin.Position, $"{name}: {bin.ReadUInt32():X8}") { Length = 4 };

        private static BinInterpNode MakeInt32Node(EndianReader bin, string name) => new BinInterpNode(bin.Position, $"{name}: {bin.ReadInt32()}", NodeType.StructLeafInt) { Length = 4 };

        private static BinInterpNode MakeInt32Node(EndianReader bin, string name, out int val)
        {
            return new BinInterpNode(bin.Position, $"{name}: {val = bin.ReadInt32()}", NodeType.StructLeafInt) { Length = 4 };
        }

        private static BinInterpNode MakeUInt16Node(EndianReader bin, string name) => new BinInterpNode(bin.Position, $"{name}: {bin.ReadUInt16()}") { Length = 2 };

        private static BinInterpNode MakeInt16Node(EndianReader bin, string name) => new BinInterpNode(bin.Position, $"{name}: {bin.ReadInt16()}") { Length = 2 };

        private static BinInterpNode MakeByteNode(EndianReader bin, string name) => new BinInterpNode(bin.Position, $"{name}: {bin.ReadByte()}") { Length = 1 };

        private static BinInterpNode MakeSByteNode(EndianReader bin, string name) => new BinInterpNode(bin.Position, $"{name}: {bin.ReadSByte()}") { Length = 1 };

        private BinInterpNode MakeNameNode(EndianReader bin, string name) => new BinInterpNode(bin.Position, $"{name}: {bin.ReadNameReference(Pcc).Instanced}", NodeType.StructLeafName) { Length = 8 };

        private BinInterpNode MakeNameNode(EndianReader bin, string name, out NameReference nameRef) =>
            new BinInterpNode(bin.Position, $"{name}: {nameRef = bin.ReadNameReference(Pcc).Instanced}", NodeType.StructLeafName) { Length = 8 };

        private BinInterpNode MakeEntryNode(EndianReader bin, string name) => new BinInterpNode(bin.Position, $"{name}: {entryRefString(bin)}", NodeType.StructLeafObject) { Length = 4 };

        private static BinInterpNode MakePackedNormalNode(EndianReader bin, string name) =>
            new BinInterpNode(bin.Position, $"{name}: (X: {bin.ReadByte() / 127.5f - 1}, Y: {bin.ReadByte() / 127.5f - 1}, Z: {bin.ReadByte() / 127.5f - 1}, W: {bin.ReadByte() / 127.5f - 1})")
            {
                Length = 4
            };

        private static BinInterpNode MakeVectorNodeEditable(EndianReader bin, string name, bool expanded = false)
        {
            var node = new BinInterpNode(bin.Position, $"{name}: (X: {bin.ReadFloat()}, Y: {bin.ReadFloat()}, Z: {bin.ReadFloat()})") { Length = 12 };
            bin.Position -= 12;
            node.Items.Add(MakeFloatNode(bin, "X"));
            node.Items.Add(MakeFloatNode(bin, "Y"));
            node.Items.Add(MakeFloatNode(bin, "Z"));
            node.IsExpanded = expanded;
            return node;
        }

        private static BinInterpNode MakeVector2DNodeEditable(EndianReader bin, string name, bool expanded = false)
        {
            var node = new BinInterpNode(bin.Position, $"{name}: (X: {bin.ReadFloat()}, Y: {bin.ReadFloat()})") { Length = 8 };
            bin.Position -= 8;
            node.Items.Add(MakeFloatNode(bin, "X"));
            node.Items.Add(MakeFloatNode(bin, "Y"));
            node.IsExpanded = expanded;
            return node;
        }

        private static BinInterpNode MakeVectorNode(EndianReader bin, string name)
        {
            var node = new BinInterpNode(bin.Position, $"{name}: (X: {bin.ReadFloat()}, Y: {bin.ReadFloat()}, Z: {bin.ReadFloat()})") { Length = 12 };
            bin.Position -= 12;
            node.Items.Add(MakeFloatNode(bin, "X"));
            node.Items.Add(MakeFloatNode(bin, "Y"));
            node.Items.Add(MakeFloatNode(bin, "Z"));
            return node;
        }

        private static BinInterpNode MakeQuatNode(EndianReader bin, string name)
        {
            var node = new BinInterpNode(bin.Position, $"{name}: (X: {bin.ReadFloat()}, Y: {bin.ReadFloat()}, Z: {bin.ReadFloat()}, W: {bin.ReadFloat()})") { Length = 16 };
            bin.Position -= 16;
            node.Items.Add(MakeFloatNode(bin, "X"));
            node.Items.Add(MakeFloatNode(bin, "Y"));
            node.Items.Add(MakeFloatNode(bin, "Z"));
            node.Items.Add(MakeFloatNode(bin, "W"));
            return node;
        }

        private static BinInterpNode MakeRotatorNode(EndianReader bin, string name)
        {
            var node = new BinInterpNode(bin.Position, $"{name}: (Pitch: {bin.ReadInt32()}, Yaw: {bin.ReadInt32()}, Roll: {bin.ReadInt32()})") { Length = 12 };
            bin.Position -= 12;
            node.Items.Add(MakeInt32Node(bin, "Pitch"));
            node.Items.Add(MakeInt32Node(bin, "Yaw"));
            node.Items.Add(MakeInt32Node(bin, "Roll"));
            return node;
        }

        private static BinInterpNode MakeBoxNode(EndianReader bin, string name) =>
            new BinInterpNode(bin.Position, name)
            {
                IsExpanded = true,
                Items =
                {
                    MakeVectorNode(bin, "Min"),
                    MakeVectorNode(bin, "Max"),
                    new BinInterpNode(bin.Position, $"IsValid: {bin.ReadBoolByte()}")
                },
                Length = 25
            };

        private static BinInterpNode MakeVector2DNode(EndianReader bin, string name) =>
            new BinInterpNode(bin.Position, $"{name}: (X: {bin.ReadFloat()}, Y: {bin.ReadFloat()})") { Length = 8 };

        private static BinInterpNode MakeVector2DHalfNode(EndianReader bin, string name) =>
            new BinInterpNode(bin.Position, $"{name}: (X: {bin.ReadFloat16()}, Y: {bin.ReadFloat16()})") { Length = 4 };

        private static BinInterpNode MakeColorNode(EndianReader bin, string name)
        {
            return new BinInterpNode(bin.Position, $"{name}")
            {
                Length = 4,
                Items =
                {
                    new BinInterpNode(bin.Position, $"B: {bin.ReadByte()}"),
                    new BinInterpNode(bin.Position, $"G: {bin.ReadByte()}"),
                    new BinInterpNode(bin.Position, $"R: {bin.ReadByte()}"),
                    new BinInterpNode(bin.Position, $"A: {bin.ReadByte()}"),
                }
            };
        }

        private static BinInterpNode MakeBoxSphereBoundsNode(EndianReader bin, string name)
        {
            return new BinInterpNode(bin.Position, $"{name}")
            {
                Items =
                {
                    MakeVectorNode(bin, "Origin"),
                    MakeVectorNode(bin, "BoxExtent"),
                    MakeFloatNode(bin, "SphereRadius")
                }
            };
        }

        private static BinInterpNode MakeMaterialGuidNode(EndianReader bin, string name, Dictionary<Guid, string> materialGuidMap = null)
        {
            var guid = bin.ReadGuid();
            var node = new BinInterpNode(bin.Position - 16, $"{name}: {guid}") { Length = 16 };

#if DEBUG
            if (materialGuidMap != null && materialGuidMap.TryGetValue(guid, out var matName))
            {
                node.Header += " " + matName;
            }
#endif

            node.Tag = NodeType.Guid;
            return node;
        }

        private static BinInterpNode MakeGuidNode(EndianReader bin, string name) => new BinInterpNode(bin.Position, $"{name}: {bin.ReadGuid()}", NodeType.Guid) { Length = 16 };

        private static BinInterpNode MakeArrayNode(EndianReader bin, string name, Func<int, BinInterpNode> selector, bool IsExpanded = false,
                                                   BinInterpNode.ArrayPropertyChildAddAlgorithm arrayAddAlgo = BinInterpNode.ArrayPropertyChildAddAlgorithm.None)
        {
            int count;
            return new BinInterpNode(bin.Position, $"{name} ({count = bin.ReadInt32()})")
            {
                IsExpanded = IsExpanded,
                Items = ReadList(count, selector),
                ArrayAddAlgorithm = arrayAddAlgo,
                Length = 4
            };
        }

        private static BinInterpNode MakeArrayNodeInt16Count(EndianReader bin, string name, Func<int, BinInterpNode> selector, bool IsExpanded = false,
                                           BinInterpNode.ArrayPropertyChildAddAlgorithm arrayAddAlgo = BinInterpNode.ArrayPropertyChildAddAlgorithm.None)
        {
            int count;
            return new BinInterpNode(bin.Position, $"{name} ({count = bin.ReadInt16()})")
            {
                IsExpanded = IsExpanded,
                Items = ReadList(count, selector),
                ArrayAddAlgorithm = arrayAddAlgo,
                Length = 2
            };
        }

        private static BinInterpNode MakeArrayNodeByteCount(EndianReader bin, string name, Func<int, BinInterpNode> selector, bool IsExpanded = false,
                                           BinInterpNode.ArrayPropertyChildAddAlgorithm arrayAddAlgo = BinInterpNode.ArrayPropertyChildAddAlgorithm.None)
        {
            int count;
            return new BinInterpNode(bin.Position, $"{name} ({count = bin.ReadByte()})")
            {
                IsExpanded = IsExpanded,
                Items = ReadList(count, selector),
                ArrayAddAlgorithm = arrayAddAlgo,
                Length = 1
            };
        }

        private static BinInterpNode MakeByteArrayNode(EndianReader bin, string name)
        {
            int pos = (int)bin.Position;
            int count = bin.ReadInt32();
            bin.Skip(count);
            return new BinInterpNode(pos, $"{name} ({count} bytes)");
        }

        private static BinInterpNode MakeArrayNode(int count, EndianReader bin, string name, Func<int, BinInterpNode> selector, bool IsExpanded = false)
        {
            return new BinInterpNode(bin.Position, $"{name} ({count})")
            {
                IsExpanded = IsExpanded,
                Items = ReadList(count, selector)
            };
        }




        [Flags]
        public enum ECoordTransformUsage : uint
        {
            // no transforms used
            UsedCoord_None = 0,
            // local to world used
            UsedCoord_World = 1 << 0,
            // local to view used
            UsedCoord_View = 1 << 1,
            // local to local used
            UsedCoord_Local = 1 << 2,
            // World Position used
            UsedCoord_WorldPos = 1 << 3
        }

        private List<ITreeItem> StartPrefabInstanceScan(byte[] data, ref int binarystart)
        {
            /*
             *  count: 4 bytes 
             *      Prefab ref : 4 bytes
             *      Level Object : 4 bytes
             *  0: 4 bytes
             *  
             */
            var subnodes = new List<ITreeItem>();
            if (!CurrentLoadedExport.HasStack)
            {
                return subnodes;
            }

            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);
                subnodes.Add(MakeArrayNode(bin, "ArchetypeToInstanceMap", i => new BinInterpNode(bin.Position, $"{i}")
                {
                    IsExpanded = true,
                    Items =
                    {
                        MakeEntryNode(bin, "Archetype"),
                        MakeEntryNode(bin, "Instance")
                    }
                }, true));
                subnodes.Add(MakeArrayNode(bin, "PrefabInstance_ObjectMap", i => new BinInterpNode(bin.Position, $"{i}")
                {
                    IsExpanded = true,
                    Items =
                    {
                        MakeEntryNode(bin, "Object:"),
                        MakeInt32Node(bin, "int")
                    }
                }, true));
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }

        private List<ITreeItem> StartSkeletalMeshScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            var game = CurrentLoadedExport.FileRef.Game;
            try
            {
                PackageCache cache = new PackageCache();
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);

                subnodes.Add(MakeBoxSphereBoundsNode(bin, "Bounds"));
                subnodes.Add(MakeArrayNode(bin, "Materials", i =>
                {
                    var matNode = MakeEntryNode(bin, $"{i}");
                    try
                    {
                        var value = bin.Skip(-4).ReadInt32();
                        if (value != 0 && Pcc.GetEntry(value) is ExportEntry matExport)
                        {
                            foreach (IEntry texture in MaterialInstanceConstant.GetTextures(matExport, cache))
                            {
                                matNode.Items.Add(new BinInterpNode(-1, $"#{texture.UIndex} {texture.FileRef.GetEntryString(texture.UIndex)}", NodeType.StructLeafObject) { UIndexValue = texture.UIndex });
                            }
                        }
                    }
                    catch
                    {
                        matNode.Items.Add(new BinInterpNode("Error reading Material!"));
                    }

                    return matNode;
                }, true, BinInterpNode.ArrayPropertyChildAddAlgorithm.FourBytes));
                subnodes.Add(MakeVectorNode(bin, "Origin"));
                subnodes.Add(MakeRotatorNode(bin, "Rotation Origin"));
                subnodes.Add(MakeArrayNode(bin, "RefSkeleton", i => new BinInterpNode(bin.Position, $"{i}: {bin.ReadNameReference(Pcc).Instanced}")
                {
                    Items =
                    {
                        MakeUInt32Node(bin, "Flags"),
                        MakeQuatNode(bin, "Bone Orientation (quaternion)"),
                        MakeVectorNode(bin, "Bone Position"),
                        MakeInt32Node(bin, "NumChildren"),
                        MakeInt32Node(bin, "ParentIndex"),
                        ListInitHelper.ConditionalAddOne<ITreeItem>( Pcc.Game >= MEGame.ME3, () => MakeColorNode(bin, "BoneColor")),
                    }
                }));
                subnodes.Add(MakeInt32Node(bin, "SkeletalDepth"));
                int rawPointIndicesCount;
                bool useFullPrecisionUVs = true;
                int numTexCoords = 1;
                subnodes.Add(MakeArrayNode(bin, "LODModels", i =>
                {
                    BinInterpNode node = new BinInterpNode(bin.Position, $"{i}");
                    try
                    {
                        node.Items.Add(MakeArrayNode(bin, "Sections", j => new BinInterpNode(bin.Position, $"{j}")
                        {
                            Items =
                            {
                                MakeUInt16Node(bin, "MaterialIndex"),
                                MakeUInt16Node(bin, "ChunkIndex"),
                                MakeUInt32Node(bin, "BaseIndex"),
                                ListInitHelper.ConditionalAddOne<ITreeItem>(Pcc.Game >= MEGame.ME3,
                                                                            () => MakeUInt32Node(bin, "NumTriangles"),
                                                                            () => MakeUInt16Node(bin, "NumTriangles")),
                                ListInitHelper.ConditionalAddOne<ITreeItem>(Pcc.Game == MEGame.UDK, () => MakeByteNode(bin, "TriangleSorting"))
                            }
                        }));
                        node.Items.Add(ListInitHelper.ConditionalAdd(Pcc.Game == MEGame.UDK, () => new ITreeItem[]
                        {
                            MakeBoolIntNode(bin, "NeedsCPUAccess"),
                            MakeByteNode(bin, "Datatype size"),
                        }));
                        node.Items.Add(MakeInt32Node(bin, "Index size?"));
                        if (Pcc.Game == MEGame.UDK && bin.Skip(-4).ReadInt32() == 4)
                        {
                            node.Items.Add(MakeArrayNode(bin, "IndexBuffer", j => MakeUInt32Node(bin, $"{j}")));
                        }
                        else
                        {
                            node.Items.Add(MakeArrayNode(bin, "IndexBuffer", j => MakeUInt16Node(bin, $"{j}")));
                        }
                        node.Items.Add(ListInitHelper.ConditionalAddOne<ITreeItem>(Pcc.Game != MEGame.UDK, () => MakeArrayNode(bin, "ShadowIndices", j => MakeUInt16Node(bin, $"{j}"))));
                        node.Items.Add(MakeArrayNode(bin, "ActiveBoneIndices", j => MakeUInt16Node(bin, $"{j}")));
                        node.Items.Add(ListInitHelper.ConditionalAddOne<ITreeItem>(Pcc.Game != MEGame.UDK, () => MakeArrayNode(bin, "ShadowTriangleDoubleSided", j => MakeByteNode(bin, $"{j}"))));
                        node.Items.Add(MakeArrayNode(bin, "Chunks", j => new BinInterpNode(bin.Position, $"{j}")
                        {
                            Items =
                            {
                                MakeUInt32Node(bin, "BaseVertexIndex"),
                                MakeArrayNode(bin, "RigidVertices", k => new BinInterpNode(bin.Position, $"{k}")
                                {
                                    Items =
                                    {
                                        MakeVectorNode(bin, "Position"),
                                        MakePackedNormalNode(bin, "TangentX"),
                                        MakePackedNormalNode(bin, "TangentY"),
                                        MakePackedNormalNode(bin, "TangentZ"),
                                        MakeVector2DNode(bin, "UV"),
                                        ListInitHelper.ConditionalAdd(Pcc.Game == MEGame.UDK, () => new ITreeItem[]
                                        {
                                            MakeVector2DNode(bin, "UV2"),
                                            MakeVector2DNode(bin, "UV3"),
                                            MakeVector2DNode(bin, "UV4"),
                                            MakeColorNode(bin, "BoneColor"),
                                        }),
                                        MakeByteNode(bin, "Bone")
                                    }
                                }),
                                MakeArrayNode(bin, "SoftVertices", k => new BinInterpNode(bin.Position, $"{k}")
                                {
                                    Items =
                                    {
                                        MakeVectorNode(bin, "Position"),
                                        MakePackedNormalNode(bin, "TangentX"),
                                        MakePackedNormalNode(bin, "TangentY"),
                                        MakePackedNormalNode(bin, "TangentZ"),
                                        MakeVector2DNode(bin, "UV"),
                                        ListInitHelper.ConditionalAdd(Pcc.Game == MEGame.UDK, () => new ITreeItem[]
                                        {
                                            MakeVector2DNode(bin, "UV2"),
                                            MakeVector2DNode(bin, "UV3"),
                                            MakeVector2DNode(bin, "UV4"),
                                            MakeColorNode(bin, "BoneColor"),
                                        }),
                                        new ListInitHelper.InitCollection<ITreeItem>(ReadList(4, l => MakeByteNode(bin, $"InfluenceBones[{l}]"))),
                                        new ListInitHelper.InitCollection<ITreeItem>(ReadList(4, l => MakeByteNode(bin, $"InfluenceWeights[{l}]")))
                                    }
                                }),
                                MakeArrayNode(bin, "BoneMap", k => MakeUInt16Node(bin, $"{k}")),
                                MakeInt32Node(bin, "NumRigidVertices"),
                                MakeInt32Node(bin, "NumSoftVertices"),
                                MakeInt32Node(bin, "MaxBoneInfluences"),
                            }
                        }));
                        node.Items.Add(MakeUInt32Node(bin, "Size"));
                        node.Items.Add(MakeUInt32Node(bin, "NumVertices"));
                        node.Items.Add(ListInitHelper.ConditionalAddOne<ITreeItem>(Pcc.Game != MEGame.UDK, () => MakeArrayNode(bin, "Edges", j => new BinInterpNode(bin.Position, $"{j}")
                        {
                            Items =
                            {
                                MakeInt32Node(bin, "Vertices[0]"),
                                MakeInt32Node(bin, "Vertices[1]"),
                                MakeInt32Node(bin, "Faces[0]"),
                                MakeInt32Node(bin, "Faces[1]"),
                            }
                        })));
                        node.Items.Add(MakeArrayNode(bin, "RequiredBones", j => MakeByteNode(bin, $"{j}")));
                        node.Items.Add(MakeUInt32Node(bin, "RawPointIndices BulkDataFlags"));
                        node.Items.Add(new BinInterpNode(bin.Position, $"RawPointIndices Count: {rawPointIndicesCount = bin.ReadInt32()}"));
                        node.Items.Add(MakeUInt32Node(bin, "RawPointIndices size"));
                        node.Items.Add(MakeUInt32Node(bin, "RawPointIndices file offset"));
                        node.Items.Add(ListInitHelper.ConditionalAddOne<ITreeItem>(Pcc.Game == MEGame.UDK,
                                                                                   () => MakeArrayNode(rawPointIndicesCount, bin, "RawPointIndices", k => MakeInt32Node(bin, $"{k}")),
                                                                                   () => MakeArrayNode(rawPointIndicesCount, bin, "RawPointIndices", k => MakeUInt16Node(bin, $"{k}"))));
                        node.Items.Add(ListInitHelper.ConditionalAddOne<ITreeItem>(Pcc.Game == MEGame.UDK, () => MakeInt32Node(bin, "NumTexCoords")));
                        BinInterpNode item = new BinInterpNode(bin.Position, "VertexBufferGPUSkin")
                        {
                            IsExpanded = true
                        };
                        node.Items.Add(item);
                        item.Items.Add(ListInitHelper.ConditionalAdd(Pcc.Game != MEGame.ME1, () => new List<ITreeItem>
                        {
                            ListInitHelper.ConditionalAddOne<ITreeItem>(Pcc.Game == MEGame.UDK, () => MakeInt32Node(bin, "NumTexCoords", out numTexCoords)),
                            MakeBoolIntNode(bin, "bUseFullPrecisionUVs", out useFullPrecisionUVs),
                            ListInitHelper.ConditionalAdd(Pcc.Game >= MEGame.ME3, () => new ITreeItem[]
                            {
                                MakeBoolIntNode(bin, "bUsePackedPosition"),
                                MakeVectorNode(bin, "MeshExtension"),
                                MakeVectorNode(bin, "MeshOrigin"),
                            }),
                        }));
                        item.Items.Add(MakeInt32Node(bin, "vertex size"));
                        item.Items.Add(MakeArrayNode(bin, "VertexData", k => new BinInterpNode(bin.Position, $"{k}")
                        {
                            Items =
                            {
                                ListInitHelper.ConditionalAddOne<ITreeItem>(Pcc.Game <= MEGame.ME2, () => MakeVectorNode(bin, "Position")),
                                MakePackedNormalNode(bin, "TangentX"),
                                ListInitHelper.ConditionalAddOne<ITreeItem>(Pcc.Game == MEGame.ME1, () =>  MakePackedNormalNode(bin, "TangentY")),
                                MakePackedNormalNode(bin, "TangentZ"),
                                ListInitHelper.ConditionalAddOne<ITreeItem>(Pcc.Game == MEGame.ME1, () =>  MakeVector2DNode(bin, "UV")),
                                new ListInitHelper.InitCollection<ITreeItem>(ReadList(4, l => MakeByteNode(bin, $"InfluenceBones[{l}]"))),
                                new ListInitHelper.InitCollection<ITreeItem>(ReadList(4, l => MakeByteNode(bin, $"InfluenceWeights[{l}]"))),
                                ListInitHelper.ConditionalAddOne<ITreeItem>(Pcc.Game >= MEGame.ME3, () => MakeVectorNode(bin, "Position")),
                                ListInitHelper.ConditionalAdd(Pcc.Game != MEGame.ME1,
                                                              () => ListInitHelper.ConditionalAddOne<ITreeItem>(useFullPrecisionUVs,
                                                                                                                () => MakeVector2DNode(bin, "UV"),
                                                                                                                () => MakeVector2DHalfNode(bin, "UV"))),
                                ListInitHelper.ConditionalAddOne<ITreeItem>(numTexCoords > 1, () => MakeArrayNode(numTexCoords - 1, bin, "Additional UVs",
                                                                                                                         i => useFullPrecisionUVs ? MakeVector2DNode(bin, "UV") : MakeVector2DHalfNode(bin, "UV")))
                            }
                        }));
                        int vertexInfluenceSize;
                        node.Items.Add(ListInitHelper.ConditionalAdd(Pcc.Game >= MEGame.ME3, () => new List<ITreeItem>
                        {
                            new BinInterpNode(bin.Position, $"VertexInfluence size: {vertexInfluenceSize = bin.ReadInt32()}", NodeType.StructLeafInt) { Length = 4 },
                            ListInitHelper.ConditionalAdd<ITreeItem>(vertexInfluenceSize > 0, () => new ITreeItem[]
                            {
                                MakeArrayNode(bin, "VertexInfluences", i => MakeInt32Node(bin, $"{i}")),
                                MakeInt32Node(bin, "Unknown")
                            })
                        }));
                        if (Pcc.Game is MEGame.UDK)
                        {
                            node.Items.Add(MakeBoolIntNode(bin, "NeedsCPUAccess"));
                            node.Items.Add(MakeByteNode(bin, "Datatype size"));
                            node.Items.Add(MakeInt32Node(bin, "index size", out int indexSize));
                            if (indexSize == 4)
                            {
                                node.Items.Add(MakeArrayNode(bin, "Second IndexBuffer?", j => MakeUInt32Node(bin, $"{j}")));
                            }
                            else
                            {
                                node.Items.Add(MakeArrayNode(bin, "Second IndexBuffer?", j => MakeUInt16Node(bin, $"{j}")));
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        node.Items.Add(new BinInterpNode { Header = $"Error reading binary data: {e}" });
                    }
                    return node;
                }, true));
                subnodes.Add(MakeArrayNode(bin, "NameIndexMap", i => new BinInterpNode(bin.Position, $"{bin.ReadNameReference(Pcc).Instanced} => {bin.ReadInt32()}")));
                subnodes.Add(MakeArrayNode(bin, "PerPolyBoneKDOPs", i => new BinInterpNode(bin.Position, $"{i}")
                {
                    Items =
                    {
                        MakekDOPTreeNode(bin),
                        MakeArrayNode(bin, "CollisionVerts", j => MakeVectorNode(bin, $"{j}"))
                    }
                }));
                if (Pcc.Game >= MEGame.ME3)
                {
                    subnodes.Add(MakeArrayNode(bin, "BoneBreakNames", i => new BinInterpNode(bin.Position, $"{i}: {bin.ReadUnrealString()}")));
                    subnodes.Add(MakeArrayNode(bin, "ClothingAssets", i => MakeEntryNode(bin, $"{i}")));
                }
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartStaticMeshCollectionActorScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                //get a list of staticmesh stuff from the props.
                var smacitems = new List<ExportEntry>();
                var props = CurrentLoadedExport.GetProperty<ArrayProperty<ObjectProperty>>("StaticMeshComponents");

                foreach (var prop in props)
                {
                    if (prop.Value > 0)
                    {
                        smacitems.Add(Pcc.GetUExport(prop.Value));
                    }
                    else
                    {
                        smacitems.Add(null);
                    }
                }

                //find start of class binary (end of props)
                int start = binarystart;

                //Lets make sure this binary is divisible by 64.
                if ((data.Length - start) % 64 != 0)
                {
                    subnodes.Add(new BinInterpNode
                    {
                        Tag = NodeType.Unknown,
                        Header = $"{start:X4} Binary data is not divisible by 64 ({data.Length - start})! SMCA binary data should be a length divisible by 64.",
                        Offset = start
                    });
                    return subnodes;
                }

                int smcaindex = 0;
                while (start < data.Length && smcaindex < smacitems.Count)
                {
                    BinInterpNode smcanode = new BinInterpNode
                    {
                        Tag = NodeType.Unknown
                    };
                    ExportEntry associatedData = smacitems[smcaindex];
                    string staticmesh = "";
                    string objtext = "Null - unused data";
                    if (associatedData != null)
                    {
                        objtext = $"[Export {associatedData.UIndex}] {associatedData.ObjectName.Instanced}";

                        //find associated static mesh value for display.
                        var smc_data = associatedData.DataReadOnly;
                        int staticmeshstart = 0x4;
                        bool found = false;
                        while (staticmeshstart < smc_data.Length && smc_data.Length - 8 >= staticmeshstart)
                        {
                            ulong nameindex = EndianReader.ToUInt64(smc_data, staticmeshstart, Pcc.Endian);
                            if (nameindex < (ulong)CurrentLoadedExport.FileRef.Names.Count && CurrentLoadedExport.FileRef.Names[(int)nameindex] == "StaticMesh")
                            {
                                //found it
                                found = true;
                                break;
                            }
                            else
                            {
                                staticmeshstart += 1;
                            }
                        }

                        if (found)
                        {
                            int staticmeshexp = EndianReader.ToInt32(smc_data, staticmeshstart + 0x18, Pcc.Endian);
                            if (staticmeshexp > 0 && staticmeshexp < CurrentLoadedExport.FileRef.ExportCount)
                            {
                                staticmesh = Pcc.GetEntry(staticmeshexp).ObjectName.Instanced;
                            }
                        }
                    }

                    smcanode.Header = $"{start:X4} [{smcaindex}] {objtext} {staticmesh}";
                    smcanode.Offset = start;
                    subnodes.Add(smcanode);

                    //Read nodes
                    for (int i = 0; i < 16; i++)
                    {
                        float smcadata = BitConverter.ToSingle(data, start);
                        BinInterpNode node = new BinInterpNode
                        {
                            Tag = NodeType.StructLeafFloat,
                            Header = start.ToString("X4")
                        };

                        //TODO: Figure out what the rest of these mean
                        string label = i.ToString();
                        switch (i)
                        {
                            case 0:
                                label = "X1:";
                                break;
                            case 1:
                                label = "X2: X-scaling-Axis: ";
                                break;
                            case 2:
                                label = "X3:";
                                break;
                            case 3:
                                label = "XT:";
                                break;
                            case 4:
                                label = "Y1: Y-scaling axis";
                                break;
                            case 5:
                                label = "Y2:";
                                break;
                            case 6:
                                label = "Y3:";
                                break;
                            case 7:
                                label = "YT:";
                                break;
                            case 8:
                                label = "Z1:";
                                break;
                            case 9:
                                label = "Z2:";
                                break;
                            case 10:
                                label = "Z3: Z-scaling axis";
                                break;
                            case 11:
                                label = "ZT:";
                                break;
                            case 12:
                                label = "LocX:";
                                break;
                            case 13:
                                label = "LocY:";
                                break;
                            case 14:
                                label = "LocZ:";
                                break;
                            case 15:
                                label = "CameraCollisionDistanceScalar:";
                                break;
                        }

                        node.Header += $" {label} {smcadata}";

                        //TODO: Lookup staticmeshcomponent so we can see what this actually is without changing to the export

                        node.Offset = start;
                        smcanode.Items.Add(node);
                        start += 4;
                    }

                    smcaindex++;
                }
                //topLevelTree.ItemsSource = subnodes;
                binarystart = start;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode() { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }

        private List<ITreeItem> StartStaticLightCollectionActorScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                //get a list of lightcomponents from the props.
                var slcaitems = new List<ExportEntry>();
                var props = CurrentLoadedExport.GetProperty<ArrayProperty<ObjectProperty>>("LightComponents");

                foreach (var prop in props)
                {
                    if (prop.Value > 0)
                    {
                        slcaitems.Add(CurrentLoadedExport.FileRef.GetEntry(prop.Value) as ExportEntry);
                    }
                    else
                    {
                        slcaitems.Add(null);
                    }
                }

                //find start of class binary (end of props)
                int start = binarystart;

                //Lets make sure this binary is divisible by 64.
                if ((data.Length - start) % 64 != 0)
                {
                    subnodes.Add(new BinInterpNode
                    {
                        Tag = NodeType.Unknown,
                        Header = $"{start:X4} Binary data is not divisible by 64 ({data.Length - start})! SLCA binary data should be a length divisible by 64.",
                        Offset = start
                    });
                    return subnodes;
                }

                int slcaindex = 0;
                while (start < data.Length && slcaindex < slcaitems.Count)
                {
                    BinInterpNode slcanode = new BinInterpNode
                    {
                        Tag = NodeType.Unknown
                    };
                    ExportEntry assossiateddata = slcaitems[slcaindex];
                    string objtext = "Null - unused data";
                    if (assossiateddata != null)
                    {
                        objtext = $"[Export {assossiateddata.UIndex}] {assossiateddata.ObjectName.Instanced}";
                    }

                    slcanode.Header = $"{start:X4} [{slcaindex}] {objtext}";
                    slcanode.Offset = start;
                    subnodes.Add(slcanode);

                    //Read nodes
                    for (int i = 0; i < 16; i++)
                    {
                        float slcadata = BitConverter.ToSingle(data, start);
                        BinInterpNode node = new BinInterpNode
                        {
                            Tag = NodeType.StructLeafFloat,
                            Header = start.ToString("X4")
                        };

                        //TODO: Figure out what the rest of these mean
                        string label = i.ToString();
                        switch (i)
                        {
                            case 1:
                                label = "ScalingXorY1:";
                                break;
                            case 12:
                                label = "LocX:";
                                break;
                            case 13:
                                label = "LocY:";
                                break;
                            case 14:
                                label = "LocZ:";
                                break;
                            case 15:
                                label = "CameraLayerDistance?:";
                                break;
                        }

                        node.Header += $" {label} {slcadata}";

                        node.Offset = start;
                        slcanode.Items.Add(node);
                        start += 4;
                    }

                    slcaindex++;
                }

                binarystart = start;
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode() { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }

        [Flags]
        enum EBulkDataFlags
        {
            BULKDATA_None = 0,
            BULKDATA_StoreInSeparateFile = 1 << 0,
            BULKDATA_SerializeCompressedZLIB = 1 << 1,
            BULKDATA_ForceSingleElementSerialization = 1 << 2,
            BULKDATA_SingleUse = 1 << 3,
            BULKDATA_SerializeCompressedLZO = 1 << 4,
            BULKDATA_Unused = 1 << 5,
            BULKDATA_StoreOnlyPayload = 1 << 6,
            BULKDATA_SerializeCompressedLZX = 1 << 7,
            BULKDATA_SerializeCompressed = (BULKDATA_SerializeCompressedZLIB | BULKDATA_SerializeCompressedLZO | BULKDATA_SerializeCompressedLZX),
        }



        private List<ITreeItem> StartBioGestureRulesDataScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();

            if (binarystart >= data.Length)
            {
                return subnodes;
            }

            int pos = binarystart;
            var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
            bin.JumpTo(binarystart);
            try
            {
                var count = bin.ReadInt32();
                bin.Position -= 4; // Set back so the node can be made
                subnodes.Add(MakeInt32Node(bin, "Count"));

                for (int i = 0; i < count; i++)
                {
                    var node = new BinInterpNode(bin.Position, $"Rule {i}");
                    subnodes.Add(node);

                    node.Items.Add(MakeNameNode(bin, "Name"));

                    var subcount = bin.ReadInt32();
                    var subnode = new BinInterpNode(bin.Position - 4, $"Num somethings: {subcount}");
                    node.Items.Add(subnode);

                    for (int j = 0; j < subcount; j++)
                    {
                        // Read name, some integer
                        subnode.Items.Add(MakeNameNode(bin, "SomeName"));
                        subnode.Items.Add(MakeInt32Node(bin, "SomeNum"));
                    }
                }
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode() { Header = $"Error reading binary data: {ex}" });
            }

            return subnodes;
        }

        private List<ITreeItem> StartGenericScan(byte[] data, ref int binarystart)
        {
            binarystart = ByteShiftUpDownValue.Value + binarystart;
            var subnodes = new List<ITreeItem>();

            if (binarystart >= data.Length)
            {
                return subnodes;
            }
            try
            {
                int binarypos = binarystart;

                //binarypos += 0x1C; //Skip ??? and GUID
                //int guid = BitConverter.ToInt32(data, binarypos);
                /*int num1 = BitConverter.ToInt32(data, binarypos);
                TreeNode node = new TreeNode($"0x{binarypos:X4} ???: {num1.ToString());
                subnodes.Add(node);
                binarypos += 4;
                int num2 = BitConverter.ToInt32(data, binarypos);
                node = new TreeNode($"0x{binarypos:X4} Count: {num2.ToString());
                subnodes.Add(node);
                binarypos += 4;
                */
                int datasize = 4;
                if (interpreterMode == InterpreterMode.Names)
                {
                    datasize = 8;
                }

                while (binarypos <= data.Length - datasize)
                {
                    string nodeText = $"0x{binarypos:X4} : ";
                    var node = new BinInterpNode();

                    switch (interpreterMode)
                    {
                        case InterpreterMode.Objects:
                            {
                                int val = BitConverter.ToInt32(data, binarypos);
                                string name = $"0x{binarypos:X6}: {val}";
                                if (CurrentLoadedExport.FileRef.IsEntry(val) && CurrentLoadedExport.FileRef.GetEntry(val) is IEntry ent)
                                {
                                    name += " " + CurrentLoadedExport.FileRef.GetEntryString(val);
                                }

                                nodeText = name;
                                node.Tag = NodeType.StructLeafObject;
                                break;
                            }
                        case InterpreterMode.Names:
                            {
                                int val = BitConverter.ToInt32(data, binarypos);
                                if (val > 0 && val <= CurrentLoadedExport.FileRef.NameCount)
                                {
                                    nodeText += $"{val,-14}{CurrentLoadedExport.FileRef.GetNameEntry(val)}";
                                }
                                else
                                {
                                    nodeText += $"              {val}"; //14 spaces
                                }
                                node.Tag = NodeType.StructLeafName;
                                break;
                            }
                        case InterpreterMode.Floats:
                            {
                                float val = BitConverter.ToSingle(data, binarypos);
                                nodeText += val.ToString();
                                node.Tag = NodeType.StructLeafFloat;
                                break;
                            }
                        case InterpreterMode.Integers:
                            {
                                int val = BitConverter.ToInt32(data, binarypos);
                                nodeText += val.ToString();
                                node.Tag = NodeType.StructLeafInt;
                                break;
                            }
                    }
                    node.Header = nodeText;
                    node.Offset = binarypos;
                    subnodes.Add(node);
                    binarypos += 4;
                }
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode() { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }

        private List<ITreeItem> StartBioMorphFaceScan(byte[] data, ref int binarystart)
        {
            var subnodes = new List<ITreeItem>();
            try
            {
                var bin = new EndianReader(new MemoryStream(data)) { Endian = CurrentLoadedExport.FileRef.Endian };
                bin.JumpTo(binarystart);

                subnodes.Add(MakeArrayNode(bin, "LOD Count:", k => new BinInterpNode(bin.Position, $"{k}")
                {
                    Items =
                    {
                        MakeInt32Node(bin, "Size of Vector"),
                        MakeArrayNode(bin, $"LOD {k} Vertex Positional Data", n => new BinInterpNode(bin.Position, $"{n}")
                        {
                            Items =
                            {
                                MakeVectorNode(bin, "Position"),
                            }
                        }),
                    }
                }, true));
            }
            catch (Exception ex)
            {
                subnodes.Add(new BinInterpNode { Header = $"Error reading binary data: {ex}" });
            }
            return subnodes;
        }

        private static BinInterpNode MakeSHANode(EndianReader bin, string name, out string sha)
        {
            var shaBytes = bin.ReadBytes(20);
            StringBuilder sb = new StringBuilder();
            foreach (var b in shaBytes)
            {
                sb.Append(b.ToString("x2"));
            }

            sha = sb.ToString();
            return new BinInterpNode(bin.Position, $"{name}: {sha}") { Length = 20 };
        }
    }
}
