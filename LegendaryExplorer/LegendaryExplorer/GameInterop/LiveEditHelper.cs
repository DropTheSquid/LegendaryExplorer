﻿using System;
using System.IO;
using System.Numerics;
using LegendaryExplorerCore.GameFilesystem;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.Unreal;
using LegendaryExplorerCore.Unreal.BinaryConverters;

namespace LegendaryExplorer.GameInterop
{
    public static class LiveEditHelper
    {
        public const string LoaderLoadedMessage = "BioP_Global";

        private static string InteropModName(MEGame game) =>
            GameController.GetInteropTargetForGame(game).ModInfo.InteropModName
            ?? throw new ArgumentOutOfRangeException(nameof(game), game, @"No interop mod for game");

        private static string InteropModInstallPath(MEGame game) => Path.Combine(MEDirectories.GetDLCPath(game), InteropModName(game));

        public static string ME3CamPathPccInstallPath => Path.Combine(InteropModInstallPath(MEGame.ME3), MEGame.ME3.CookedDirName(), "ME3LiveEditorCamPath.pcc");

        private static string GetSavedCamFilePath(MEGame game) => Path.Combine(MEDirectories.GetExecutableFolderPath(game), "savedCams");

        public static POV[] ReadSavedCamsFile(MEGame game)
        {
            var povs = new POV[10];

            if (File.Exists(GetSavedCamFilePath(game)))
            {
                using var fs = new FileStream(GetSavedCamFilePath(game), FileMode.Open);

                for (int i = 0; i < 10; i++)
                {
                    povs[i] = new POV
                    {
                        Position = new Vector3(fs.ReadFloat(), fs.ReadFloat(), fs.ReadFloat()),
                        Rotation = new Vector3
                        {
                            Y = (fs.ReadInt32() % 65536).UnrealRotationUnitsToDegrees(),
                            Z = (fs.ReadInt32() % 65536).UnrealRotationUnitsToDegrees(),
                            X = (fs.ReadInt32() % 65536).UnrealRotationUnitsToDegrees()
                        },
                        FOV = fs.ReadFloat(),
                        Index = i
                    };
                }
            }
            else
            {
                for (int i = 0; i < 10; i++)
                {
                    povs[i] = new POV();
                }
            }

            return povs;
        }

        public static void CreateCurveFromSavedCams(ExportEntry export)
        {
            POV[] cams = ReadSavedCamsFile(export.Game);

            var props = export.GetProperties();
            var posTrack = props.GetProp<StructProperty>("PosTrack").GetProp<ArrayProperty<StructProperty>>("Points");
            var rotTrack = props.GetProp<StructProperty>("EulerTrack").GetProp<ArrayProperty<StructProperty>>("Points");
            var lookupTrack = props.GetProp<StructProperty>("LookupTrack").GetProp<ArrayProperty<StructProperty>>("Points");

            posTrack.Clear();
            rotTrack.Clear();

            for (int i = 0; i < cams.Length; i++)
            {
                POV cam = cams[i];
                if (cam.IsZero)
                {
                    break;
                }

                posTrack.Add(new InterpCurvePoint<Vector3>
                {
                    InVal = i * 2,
                    OutVal = cam.Position,
                    InterpMode = EInterpCurveMode.CIM_CurveUser
                }.ToStructProperty(MEGame.ME3));
                rotTrack.Add(new InterpCurvePoint<Vector3>
                {
                    InVal = i * 2,
                    OutVal = cam.Rotation,
                    InterpMode = EInterpCurveMode.CIM_CurveUser
                }.ToStructProperty(MEGame.ME3));
                lookupTrack.Add(new StructProperty("InterpLookupPoint", false, new NameProperty("None", "GroupName"), new FloatProperty(0, "Time")));
            }
            export.WriteProperties(props);
        }

        public static void PadME3CamPathFile()
        {
            InteropHelper.TryPadFile(ME3CamPathPccInstallPath, 10_485_760);
        }
    }

    public class POV
    {
        public Vector3 Position;
        public Vector3 Rotation; // X = Roll, Y = Pitch, Z = Yaw (in degrees)
        public float FOV;

        public int Index { get; set; }
        public bool IsZero => Position.Equals(Vector3.Zero) && Rotation.Equals(Vector3.Zero) && FOV == 0f;

        public string Str => $"Position: {Position}, Rotation: Roll:{Rotation.X}, Pitch:{Rotation.Y}, Yaw:{Rotation.Z}, FOV: {FOV}";
    }
}
